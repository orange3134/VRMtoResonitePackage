using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Resolves cascaded, zero-weight Set selectors at fixed hand inputs and authored defaults.</summary>
internal static class VrchatDrivenGestureRouter
{
    internal static bool TryProject(UnityPackage package, UnityScene scene, YamlNode controller, YamlNode machine,
        ExpressionLayer original, IReadOnlyDictionary<string, ExpressionParameter> parameters,
        Func<YamlNode, ExpressionClip> readClip, out ExpressionLayer result)
    {
        result = null;
        var controls = (controller["m_AnimatorLayers"]?.Seq ?? new()).Skip(1)
            .Where(l => l["m_DefaultWeight"]?.AsFloat() == 0 && (l["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) < 0).ToArray();
        if (controls.Length == 0) return false;
        var graph = new VrchatAnimatorGraph(scene);
        var inventories = controls.ToDictionary(l => l, l => graph.Reachable(l["m_StateMachine"]?.FileID ?? 0));
        var driverIds = inventories.ToDictionary(p => p.Key, p => p.Value.SelectMany(id =>
            scene.Doc(id)?.Root?["m_StateMachineBehaviours"]?.Seq ?? new()).Select(r => r.FileID ?? 0).ToHashSet());
        var written = driverIds.ToDictionary(p => p.Key, p => p.Value.SelectMany(id =>
            scene.Doc(id)?.Root?["parameters"]?.Seq ?? new()).Select(w => w["name"]?.AsString()).Where(n => n != null).ToHashSet());
        // This fallback is for a cascade of distinct parameter producers. Shared writers
        // require the explicit neutral/active priority rules of the indirect router.
        if (written.Values.SelectMany(v => v).GroupBy(n => n).Any(g => g.Count() > 1) ||
            !controls.Any(l => inventories[l].SelectMany(id => scene.Doc(id)?.Root?["m_Conditions"]?.Seq ?? new())
                .Any(c => written.Where(p => p.Key != l).Any(p => p.Value.Contains(c["m_ConditionEvent"]?.AsString() ?? ""))))) return false;
        var projected = new ExpressionLayer { Id = original.Id, Name = original.Name, Weight = original.Weight };
        var indices = new Dictionary<long, int>();
        bool dependsOnHand = false;
        for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
        {
            var values = parameters.Where(p => p.Value.Type is 1 or 3 or 4 && float.IsFinite(p.Value.Default))
                .ToDictionary(p => p.Key, p => p.Value.Default);
            values["GestureLeft"] = left; values["GestureRight"] = right;
            values["GestureLeftWeight"] = values["GestureRightWeight"] = values["IsLocal"] = 1;
            values["AFK"] = 0;
            var unknown = new HashSet<string>();
            var allConsumed = new HashSet<string>();
            bool stable = false;
            for (int pass = 0; pass < 32; pass++)
            {
                var before = new Dictionary<string, float>(values);
                var nextUnknown = new HashSet<string>();
                foreach (var layer in controls)
                {
                    var root = scene.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root;
                    long id = Select(root);
                    var state = scene.Doc(id)?.Root;
                    if (state == null || !ControlMotion(state["m_Motion"]) || !Writes(state, out var writes))
                    {
                        if (state != null && !ControlMotion(state["m_Motion"])) nextUnknown.UnionWith(parameters.Keys);
                        // An unresolved writer must not silently become an authored default.
                        foreach (long node in inventories[layer])
                            foreach (var r in scene.Doc(node)?.Root?["m_StateMachineBehaviours"]?.Seq ?? new())
                                foreach (var w in scene.Doc(r.FileID ?? 0)?.Root?["parameters"]?.Seq ?? new())
                                    if (w["name"]?.AsString() is string name) nextUnknown.Add(name);
                        continue;
                    }
                    foreach (var (name, value) in writes) values[name] = value;
                }
                bool sameUnknown = unknown.SetEquals(nextUnknown); unknown = nextUnknown;
                if (sameUnknown && before.Count == values.Count && before.All(p => values.GetValueOrDefault(p.Key, float.NaN) == p.Value))
                { stable = true; break; }
            }
            if (!stable) return false;
            long selected = Select(machine);
            var poseState = scene.Doc(selected)?.Root;
            if (poseState == null || !Writes(poseState, out var sideEffects)) return false;
            // Consumer side effects may control tracking, but cannot change its own selection.
            var consumed = new HashSet<string>();
            Select(machine, consumed);
            if (sideEffects.Keys.Any(allConsumed.Contains)) return false;
            foreach (var other in (controller["m_AnimatorLayers"]?.Seq ?? new()).Except(controls))
            {
                var otherRoot = scene.Doc(other["m_StateMachine"]?.FileID ?? 0)?.Root;
                if (otherRoot == machine) continue;
                bool writesInput = graph.Reachable(other["m_StateMachine"]?.FileID ?? 0)
                    .SelectMany(id => scene.Doc(id)?.Root?["m_StateMachineBehaviours"]?.Seq ?? new())
                    .SelectMany(r => scene.Doc(r.FileID ?? 0)?.Root?["parameters"]?.Seq ?? new())
                    .Any(w => allConsumed.Contains(w["name"]?.AsString() ?? ""));
                if (!writesInput) continue;
                var otherState = scene.Doc(Select(otherRoot))?.Root;
                if (otherState == null || !Writes(otherState, out var otherWrites) || otherWrites.Keys.Any(allConsumed.Contains)) return false;
            }
            var motion = poseState["m_Motion"]; var clip = readClip(motion);
            if ((motion?.FileID ?? 0) != 0 && clip == null) return false;
            string time = poseState["m_TimeParameterActive"]?.AsBool() == true ? poseState["m_TimeParameter"]?.AsString() : null;
            float speed = poseState["m_Speed"]?.AsFloat(1) ?? 1;
            if (!ExpressionState.SupportsSpeed(speed, time) || time != null && time is not ("GestureLeftWeight" or "GestureRightWeight") ||
                new[] { "m_SpeedParameterActive", "m_CycleOffsetParameterActive", "m_MirrorParameterActive" }.Any(k => poseState[k]?.AsBool() == true) ||
                (poseState["m_CycleOffset"]?.AsFloat() ?? 0) != 0) return false;
            if (!indices.TryGetValue(selected, out int index))
            {
                index = projected.States.Count; indices[selected] = index;
                projected.States.Add(new(poseState["m_Name"]?.AsString() ?? "State", clip?.Id, speed, true, time));
            }
            var route = new ExpressionTransition { Destination = index, CanTransitionToSelf = true };
            route.Conditions.Add(new("GestureLeft", 6, left)); route.Conditions.Add(new("GestureRight", 6, right));
            projected.Transitions.Add(route);

            long Select(YamlNode root, HashSet<string> consumed = null)
            {
                var visited = new HashSet<long>();
                bool failed = false;
                bool Matches(YamlNode t)
                {
                    if (t == null) { failed = true; return false; }
                    bool match = true;
                    foreach (var c in t["m_Conditions"]?.Seq ?? new())
                    {
                        string name = c["m_ConditionEvent"]?.AsString();
                        int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                        float threshold = c["m_EventTreshold"]?.AsFloat(float.NaN) ?? 0;
                        if (name == null || unknown.Contains(name) || !values.TryGetValue(name, out float value) ||
                            mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold)) { failed = true; return false; }
                        consumed?.Add(name); allConsumed.Add(name);
                        dependsOnHand |= name is "GestureLeft" or "GestureRight";
                        match &= new ExpressionCondition(name, mode, threshold).Matches(value);
                    }
                    return match;
                }
                IEnumerable<YamlNode> Transitions(YamlNode n, string key) => VrchatAnimatorDefaults.ActiveTransitions(scene, n?[key]?.Seq)
                    .Select(r => scene.Doc(r.FileID ?? 0)?.Root);
                long Enter(YamlNode owner)
                {
                    if (owner == null || (owner["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0) return 0;
                    var entry = Transitions(owner, "m_EntryTransitions").FirstOrDefault(Matches);
                    return entry == null ? owner["m_DefaultState"]?.FileID ?? 0 : Destination(entry, owner);
                }
                long Destination(YamlNode t, YamlNode owner)
                {
                    if (t["m_HasExitTime"]?.AsBool() == true || t["m_IsExit"]?.AsBool() == true ||
                        (t["m_TransitionOffset"]?.AsFloat() ?? 0) != 0) return 0;
                    long child = t["m_DstStateMachine"]?.FileID ?? 0;
                    long state = t["m_DstState"]?.FileID ?? 0;
                    if (child == 0) return state;
                    if (state != 0 || !visited.Add(child) || scene.Doc(child) is not { ClassId: 1107 } d ||
                        !graph.Owners.TryGetValue(d.Root, out var parent) || parent != owner) return 0;
                    return Enter(d.Root);
                }
                long id = Enter(root);
                while (!failed && id != 0 && visited.Add(id))
                {
                    if (scene.Doc(id) is not { ClassId: 1102 } doc || !graph.Owners.TryGetValue(doc.Root, out var owner)) return 0;
                    var path = new List<YamlNode>(); var parent = owner;
                    while (parent != null && !path.Contains(parent)) { path.Add(parent); parent = graph.Owners.GetValueOrDefault(parent); }
                    if (!path.Contains(root)) return 0;
                    if (path.Any(m => (m["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0)) return 0;
                    path.Reverse();
                    var t = path.SelectMany(m => Transitions(m, "m_AnyStateTransitions"))
                        .FirstOrDefault(t => Matches(t) && ((t["m_DstState"]?.FileID ?? 0) != id || t["m_CanTransitionToSelf"]?.AsBool() == true))
                        ?? Transitions(doc.Root, "m_Transitions").FirstOrDefault(Matches);
                    if (failed) return 0;
                    if (t == null) return id;
                    if (!Writes(doc.Root, out var transientWrites) || transientWrites.Count != 0) return 0;
                    long next = Destination(t, owner);
                    if (next == id) return id;
                    id = next;
                }
                return 0;
            }
            bool Writes(YamlNode state, out Dictionary<string, float> writes)
            {
                writes = new();
                foreach (var r in state["m_StateMachineBehaviours"]?.Seq ?? new())
                {
                    var b = scene.Doc(r.FileID ?? 0)?.Root;
                    if (b?["m_Enabled"]?.AsBool() == false) continue;
                    if (b?["m_Script"]?.Guid != VrchatConstants.AvatarDescriptorScriptGuid) return false;
                    if (b["m_Script"]?.FileID == -646210727)
                    {
                        if (new[] { "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers" }.Any(k => (b[k]?.AsInt() ?? 0) != 0)) return false;
                        if (new[] { "trackingEyes", "trackingMouth" }.Any(k => (b[k]?.AsInt() ?? 0) is < 0 or > 2)) return false;
                        continue;
                    }
                    if (b["m_Script"]?.FileID != -706344726 || b["parameters"]?.Seq == null) return false;
                    foreach (var w in b["parameters"].Seq)
                    {
                        string name = w["name"]?.AsString(); float value = w["value"]?.AsFloat(float.NaN) ?? float.NaN;
                        if (name == null || name is "GestureLeft" or "GestureRight" or "GestureLeftWeight" or "GestureRightWeight" or "IsLocal" or "AFK" ||
                            w["type"]?.AsInt(-1) != 0 || !string.IsNullOrEmpty(w["source"]?.AsString()) || !float.IsFinite(value) ||
                            !parameters.TryGetValue(name, out var p) || p.Type is not (1 or 3 or 4) ||
                            p.Type == 3 && (value != MathF.Truncate(value) || value < int.MinValue || value >= 2147483648f) || p.Type == 4 && value is not (0 or 1)) return false;
                        writes[name] = value;
                    }
                }
                return true;
            }
        }
        if (!dependsOnHand || indices.Count < 2 || projected.States.All(s => s.ClipId == null)) return false;
        result = projected; return true;

        bool ControlMotion(YamlNode reference)
        {
            if ((reference?.FileID ?? 0) == 0) return true;
            if (reference.Guid != null && package.ByGuid(reference.Guid) == null) return true; // Missing zero-weight motion is null in Unity.
            var motionScene = reference.Guid == null ? scene : package.ByGuid(reference.Guid) is { HasContent: true } a ? package.ReadScene(a) : null;
            var motion = motionScene?.Doc(reference.FileID.Value);
            // Zero-weight clips cannot deform the avatar, but animated Animator parameters are unsafe.
            return motion is { ClassId: 74 } && !(motion.Root["m_FloatCurves"]?.Seq?.Any(c => c["classID"]?.AsInt() == 95) ?? false) &&
                (motion.Root["m_Events"]?.Seq?.Count ?? 0) == 0;
        }
    }
}