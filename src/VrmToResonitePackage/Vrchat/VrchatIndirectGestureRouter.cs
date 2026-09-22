using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Compiles empty hand-input layers that Set a parameter consumed by an Entry selector.</summary>
internal static class VrchatIndirectGestureRouter
{
    private sealed record Source(string Hand, string Parameter, float[] Values, HashSet<long> Behaviours);

    internal static bool TryProject(UnityScene scene, YamlNode controller, YamlNode machine,
        ExpressionLayer original, IReadOnlyDictionary<string, ExpressionParameter> parameters,
        Func<YamlNode, ExpressionClip> readClip, out ExpressionLayer result, out string detail)
    {
        result = null; detail = null;
        var sources = new List<Source>();
        // State behaviours run even on zero-weight layers. Their motions must be empty:
        // these are parameter inputs, not additional animated face layers.
        foreach (var layer in controller["m_AnimatorLayers"]?.Seq ?? new())
            if ((layer["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) < 0 &&
                TrySource(scene, scene.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root, parameters, readClip) is { } source)
                sources.Add(source);
        var aliases = new Dictionary<string, float[]>();
        foreach (var group in sources.GroupBy(s => s.Parameter))
        {
            if (group.Select(s => s.Hand).Distinct().Count() != group.Count() ||
                group.Select(s => s.Values[0]).Distinct().Count() != 1) continue;
            var known = group.SelectMany(s => s.Behaviours).ToHashSet();
            // An unrecognized writer could change this parameter. Do not silently ignore it.
            if (scene.Documents.Values.Any(d => d.ClassId == 114 && !known.Contains(d.FileId) &&
                d.Root["m_Script"]?.Guid == VrchatConstants.AvatarDescriptorScriptGuid &&
                d.Root["m_Script"]?.FileID == -706344726 &&
                (d.Root["parameters"]?.Seq?.Any(p => p["name"]?.AsString() == group.Key) ?? false))) continue;
            var values = new float[64];
            for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
            {
                float value = group.First().Values[0];
                // Canonical static pose: active hands override neutral, later controller
                // layers win simultaneous non-neutral hands. Last-changed-hand history is not exported.
                foreach (var source in group)
                {
                    int gesture = source.Hand == "GestureLeft" ? l : r;
                    if (gesture != 0) value = source.Values[gesture];
                }
                values[l * 8 + r] = value;
            }
            aliases[group.Key] = values;
        }
        if (aliases.Count == 0) return false;
        var usedAliases = new HashSet<string>();
        var usedDefaults = new HashSet<string>();
        var projected = new ExpressionLayer { Id = original.Id, Name = original.Name, Weight = original.Weight };
        var states = new Dictionary<long, int>();
        for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
        {
            float? Value(string name)
            {
                if (name == "GestureLeft") return l;
                if (name == "GestureRight") return r;
                if (aliases.TryGetValue(name, out var values)) { usedAliases.Add(name); return values[l * 8 + r]; }
                if (!parameters.TryGetValue(name, out var p) || p.Type is not (1 or 3 or 4) || !float.IsFinite(p.Default)) return null;
                usedDefaults.Add(name); return p.Default;
            }
            var visited = new HashSet<YamlNode>();
            long Enter(YamlNode owner)
            {
                if (owner == null || !visited.Add(owner) || Has(owner, "m_StateMachineBehaviours") ||
                    Has(owner, "m_AnyStateTransitions")) return 0;
                long id = owner["m_DefaultState"]?.FileID ?? 0;
                foreach (var t in Transitions(scene, owner, "m_EntryTransitions"))
                {
                    if (!Matches(t, Value, out bool matches)) return 0;
                    if (!matches) continue;
                    if (!Untimed(t) || t["m_IsExit"]?.AsBool() == true) return 0;
                    long child = t["m_DstStateMachine"]?.FileID ?? 0;
                    id = t["m_DstState"]?.FileID ?? 0;
                    if (child != 0)
                    {
                        if (id != 0 || !(owner["m_ChildStateMachines"]?.Seq?.Any(c => c["m_StateMachine"]?.FileID == child) ?? false) ||
                            scene.Doc(child) is not { ClassId: 1107 } doc) return 0;
                        return Enter(doc.Root);
                    }
                    break;
                }
                if (!(owner["m_ChildStates"]?.Seq?.Any(c => c["m_State"]?.FileID == id) ?? false)) return 0;
                return id;
            }
            long selected = Enter(machine);
            if (scene.Doc(selected) is not { ClassId: 1102 } stateDoc) return false;
            var state = stateDoc.Root;
            if (Has(state, "m_StateMachineBehaviours")) return false;
            // At the fixed inputs the selected state must be stable. Exit routing and
            // timed/active transitions are deliberately not approximated by this projector.
            foreach (var t in Transitions(scene, state, "m_Transitions"))
                if (!Matches(t, Value, out bool matches) || matches) return false;
            var motion = state["m_Motion"];
            var clip = readClip(motion);
            if ((motion?.FileID ?? 0) != 0 && clip == null) return false;
            float speed = state["m_Speed"]?.AsFloat(1) ?? 1;
            string time = state["m_TimeParameterActive"]?.AsBool() == true ? state["m_TimeParameter"]?.AsString() ?? "" : null;
            if (!ExpressionState.SupportsSpeed(speed, time) || (time != null && time is not ("GestureLeftWeight" or "GestureRightWeight")) ||
                (state["m_CycleOffset"]?.AsFloat() ?? 0) != 0 ||
                new[] { "m_SpeedParameterActive", "m_CycleOffsetParameterActive", "m_MirrorParameterActive" }
                    .Any(k => state[k]?.AsBool() == true)) return false;
            if (!states.TryGetValue(selected, out int index))
            {
                index = projected.States.Count; states[selected] = index;
                projected.States.Add(new(state["m_Name"]?.AsString() ?? "State", clip?.Id, speed, true, time));
            }
            var route = new ExpressionTransition { Destination = index, CanTransitionToSelf = true };
            route.Conditions.Add(new("GestureLeft", 6, l)); route.Conditions.Add(new("GestureRight", 6, r));
            projected.Transitions.Add(route);
        }
        if (usedAliases.Count == 0 || projected.States.All(s => s.ClipId == null)) return false;
        result = projected;
        detail = string.Join(", ", usedAliases.Order()) + "; active hands override neutral; later input layers win simultaneous hands";
        if (usedDefaults.Count > 0) detail += "; other conditions use authored defaults: " + string.Join(", ", usedDefaults.Order());
        return true;
    }

    private static Source TrySource(UnityScene scene, YamlNode machine,
        IReadOnlyDictionary<string, ExpressionParameter> parameters, Func<YamlNode, ExpressionClip> readClip)
    {
        if (machine == null || Has(machine, "m_ChildStateMachines") || Has(machine, "m_StateMachineBehaviours") ||
            Has(machine, "m_AnyStateTransitions") || !Has(machine, "m_EntryTransitions")) return null;
        var entries = Transitions(scene, machine, "m_EntryTransitions").ToArray();
        var hands = entries.SelectMany(t => t?["m_Conditions"]?.Seq ?? new()).Select(c => c["m_ConditionEvent"]?.AsString()).Distinct().ToArray();
        if (hands.Length != 1 || hands[0] is not ("GestureLeft" or "GestureRight")) return null;
        string hand = hands[0], target = null;
        var values = new float[8]; var selected = new HashSet<long>(); var behaviours = new HashSet<long>();
        var children = (machine["m_ChildStates"]?.Seq ?? new()).Select(c => c["m_State"]?.FileID ?? 0).ToHashSet();
        for (int g = 0; g < 8; g++)
        {
            float? Value(string name) => name == hand ? g : null;
            long id = machine["m_DefaultState"]?.FileID ?? 0;
            foreach (var t in entries)
            {
                if (!Untimed(t) || t["m_IsExit"]?.AsBool() == true || (t["m_DstStateMachine"]?.FileID ?? 0) != 0 ||
                    !Matches(t, Value, out bool matches)) return null;
                if (matches) { id = t["m_DstState"]?.FileID ?? 0; break; }
            }
            if (!children.Contains(id) || !selected.Add(id) || scene.Doc(id) is not { ClassId: 1102 } doc) return null;
            var state = doc.Root; var motion = state["m_Motion"]; var clip = readClip(motion);
            if ((motion?.FileID ?? 0) != 0 && clip == null || (clip?.Curves.Count ?? 0) != 0) return null;
            foreach (var t in Transitions(scene, state, "m_Transitions"))
                if (!Untimed(t) || t["m_IsExit"]?.AsBool() != true || (t["m_DstState"]?.FileID ?? 0) != 0 ||
                    (t["m_DstStateMachine"]?.FileID ?? 0) != 0 || !Matches(t, Value, out bool matches) || matches) return null;
            if (state["m_StateMachineBehaviours"]?.Seq is not { Count: 1 } refs) return null;
            long bid = refs[0].FileID ?? 0;
            var b = scene.Doc(bid)?.Root;
            if (b?["m_Script"]?.Guid != VrchatConstants.AvatarDescriptorScriptGuid || b["m_Script"]?.FileID != -706344726 ||
                b["m_Enabled"]?.AsBool() == false || b["parameters"]?.Seq is not { Count: 1 } writes) return null;
            var write = writes[0]; string name = write["name"]?.AsString();
            float value = write["value"]?.AsFloat(float.NaN) ?? float.NaN;
            if (name == null || name is "GestureLeft" or "GestureRight" || write["type"]?.AsInt(-1) != 0 ||
                !string.IsNullOrEmpty(write["source"]?.AsString()) || !float.IsFinite(value) ||
                !parameters.TryGetValue(name, out var parameter) || parameter.Type is not (1 or 3) ||
                parameter.Type == 3 && (value != MathF.Truncate(value) || value < int.MinValue || value >= 2147483648f) ||
                target != null && target != name) return null;
            target = name; values[g] = value; behaviours.Add(bid);
        }
        return selected.SetEquals(children) ? new(hand, target, values, behaviours) : null;
    }

    private static bool Has(YamlNode node, string key) => (node?[key]?.Seq?.Count ?? 0) != 0;
    private static IEnumerable<YamlNode> Transitions(UnityScene scene, YamlNode node, string key) =>
        VrchatAnimatorDefaults.ActiveTransitions(scene, node?[key]?.Seq).Select(r => scene.Doc(r.FileID ?? 0)?.Root);
    private static bool Untimed(YamlNode t) => t != null && t["m_HasExitTime"]?.AsBool() != true &&
        (t["m_TransitionOffset"]?.AsFloat() ?? 0) == 0 && (t["m_InterruptionSource"]?.AsInt() ?? 0) == 0 &&
        float.IsFinite(t["m_TransitionDuration"]?.AsFloat() ?? 0);
    private static bool Matches(YamlNode transition, Func<string, float?> value, out bool matches)
    {
        matches = true;
        if (transition == null) return false;
        foreach (var c in transition["m_Conditions"]?.Seq ?? new())
        {
            string name = c["m_ConditionEvent"]?.AsString();
            int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
            float threshold = c["m_EventTreshold"]?.AsFloat() ?? 0;
            if (name == null || mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold) || value(name) is not float v) return false;
            matches &= new ExpressionCondition(name, mode, threshold).Matches(v);
        }
        return true;
    }
}