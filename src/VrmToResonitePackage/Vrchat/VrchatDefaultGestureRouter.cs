using System.Globalization;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Specializes gated hand selectors at authored defaults, starting from Animator Entry.</summary>
internal static class VrchatDefaultGestureRouter
{
    internal static bool TryProject(UnityPackage package, UnityScene scene, YamlNode machine, YamlNode mask,
        ExpressionLayer original, IReadOnlyDictionary<string, ExpressionParameter> parameters,
        Func<YamlNode, ExpressionClip> readClip, out ExpressionLayer result, out string detail, bool includeHandOnly = false)
    {
        result = null; detail = null;
        if ((machine["m_ChildStateMachines"]?.Seq?.Count ?? 0) != 0 ||
            (machine["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0) return false;
        if ((mask?.FileID ?? 0) != 0)
        {
            var asset = package.ByGuid(mask.Guid);
            if (asset?.HasContent != true || package.ReadScene(asset).Doc(mask.FileID.Value) is not { ClassId: 319 } doc ||
                doc.Root["m_Elements"]?.Seq is not { Count: 0 }) return false;
        }
        bool Hand(string name) => name is "GestureLeft" or "GestureRight";
        var ids = (machine["m_ChildStates"]?.Seq ?? new()).Select(s => s["m_State"]?.FileID ?? 0).ToHashSet();
        IEnumerable<YamlNode> Transitions(YamlNode owner, string key) =>
            VrchatAnimatorDefaults.ActiveTransitions(scene, owner?[key]?.Seq).Select(r => scene.Doc(r.FileID ?? 0)?.Root);
        var used = ids.SelectMany(id => Transitions(scene.Doc(id)?.Root, "m_Transitions"))
            .Concat(Transitions(machine, "m_EntryTransitions")).Concat(Transitions(machine, "m_AnyStateTransitions"))
            .SelectMany(t => t?["m_Conditions"]?.Seq ?? new()).Select(c => c["m_ConditionEvent"]?.AsString()).ToHashSet();
        // Existing hand-only projectors run first; the fallback can also specialize flat Any State selectors.
        if (!used.Any(Hand) || (!includeHandOnly && !used.Any(p => !Hand(p)))) return false;
        if (!used.Any(p => !Hand(p)) &&
            (Transitions(machine, "m_EntryTransitions").Any() || !Transitions(machine, "m_AnyStateTransitions").Any() ||
             ids.SelectMany(id => Transitions(scene.Doc(id)?.Root, "m_Transitions")).Any(t =>
                 t?["m_IsExit"]?.AsBool() != true || (t["m_DstState"]?.FileID ?? 0) != 0 ||
                 (t["m_DstStateMachine"]?.FileID ?? 0) != 0))) return false;
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        bool failed = false;
        int left = 0, right = 0;
        bool Matches(YamlNode t)
        {
            if (t == null) { failed = true; return false; }
            bool matches = true;
            foreach (var c in t["m_Conditions"]?.Seq ?? new())
            {
                string name = c["m_ConditionEvent"]?.AsString();
                int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                float threshold = c["m_EventTreshold"]?.AsFloat() ?? 0;
                if (name == null || mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold))
                { failed = true; return false; }
                float value;
                if (Hand(name)) value = name == "GestureLeft" ? left : right;
                else
                {
                    if (!parameters.TryGetValue(name, out var p) || p.Type is not (1 or 3 or 4) || !float.IsFinite(p.Default))
                    { failed = true; return false; }
                    value = p.Default; defaults[name] = value;
                }
                matches &= new ExpressionCondition(name, mode, threshold).Matches(value);
            }
            return matches;
        }
        bool ValidTransition(YamlNode t) => (t["m_DstStateMachine"]?.FileID ?? 0) == 0 &&
            t["m_HasExitTime"]?.AsBool() != true && (t["m_TransitionOffset"]?.AsFloat() ?? 0) == 0 &&
            (t["m_InterruptionSource"]?.AsInt() ?? 0) == 0 &&
            float.IsFinite(t["m_TransitionDuration"]?.AsFloat() ?? 0);
        bool SafeBehaviours(YamlNode state)
        {
            foreach (var r in state["m_StateMachineBehaviours"]?.Seq ?? new())
            {
                var b = scene.Doc(r.FileID ?? 0)?.Root; var script = b?["m_Script"];
                if (script?.Guid != VrchatConstants.AvatarDescriptorScriptGuid) return false;
                if (script.FileID == -706344726)
                {
                    // Non-hand parameters stay at defaults; do not execute driver side effects.
                    if (b["parameters"]?.Seq == null || b["parameters"].Seq.Any(p =>
                        string.IsNullOrEmpty(p["name"]?.AsString()) || Hand(p["name"].AsString()))) return false;
                }
                else if (script.FileID == -646210727)
                {
                    foreach (string key in new[] { "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip",
                        "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers" })
                        if ((b[key]?.AsInt() ?? 0) != 0) return false;
                    foreach (string key in new[] { "trackingEyes", "trackingMouth" })
                        if ((b[key]?.AsInt(-1) ?? -1) is < 0 or > 2) return false;
                }
                else return false;
            }
            return true;
        }
        var projected = new ExpressionLayer { Id = original.Id, Name = original.Name, Weight = original.Weight };
        var selectedStates = new Dictionary<long, int>();
        var missingDefaults = new HashSet<string>(StringComparer.Ordinal);
        for (left = 0; left < 8; left++) for (right = 0; right < 8; right++)
        {
            long Entry()
            {
                var entry = Transitions(machine, "m_EntryTransitions").FirstOrDefault(Matches);
                if (entry == null) return machine["m_DefaultState"]?.FileID ?? 0;
                if (!ValidTransition(entry) || entry["m_IsExit"]?.AsBool() == true) { failed = true; return 0; }
                return entry["m_DstState"]?.FileID ?? 0;
            }
            long id = Entry();
            var visited = new HashSet<long>();
            while (!failed)
            {
                if (!ids.Contains(id) || !visited.Add(id) || scene.Doc(id)?.Root is not { } state || !SafeBehaviours(state))
                    return false;
                var transition = Transitions(machine, "m_AnyStateTransitions")
                    .FirstOrDefault(t => Matches(t) && ((t["m_DstState"]?.FileID ?? 0) != id || t["m_CanTransitionToSelf"]?.AsBool() == true))
                    ?? Transitions(state, "m_Transitions").FirstOrDefault(Matches);
                if (failed) return false;
                if (transition != null)
                {
                    if (!ValidTransition(transition)) return false;
                    long next = transition["m_IsExit"]?.AsBool() == true ? Entry() : transition["m_DstState"]?.FileID ?? 0;
                    if (failed) return false;
                    if (next != id) { id = next; continue; }
                }
                var motion = state["m_Motion"]; var clip = readClip(motion);
                if ((motion?.FileID ?? 0) != 0 && clip == null)
                {
                    // A missing neutral/default asset must not discard every valid hand pose.
                    // Never reinterpret an existing unsupported clip, local motion or active hand pose.
                    if (id != (machine["m_DefaultState"]?.FileID ?? 0) || motion.Guid == null ||
                        package.ByGuid(motion.Guid) != null || used.Where(Hand).Any(p => (p == "GestureLeft" ? left : right) != 0))
                        return false;
                    missingDefaults.Add(motion.Guid + ":" + motion.FileID);
                }
                if (!selectedStates.TryGetValue(id, out int index))
                {
                    if (transition != null && (clip?.Curves.Count ?? 0) == 0) return false;
                    float speed = state["m_Speed"]?.AsFloat(1) ?? 1;
                    string time = state["m_TimeParameterActive"]?.AsBool() == true ? state["m_TimeParameter"]?.AsString() ?? "" : null;
                    if (!float.IsFinite(speed) || speed <= 0 || (time != null && time is not ("GestureLeftWeight" or "GestureRightWeight")) ||
                        new[] { "m_SpeedParameterActive", "m_CycleOffsetParameterActive", "m_MirrorParameterActive" }.Any(k => state[k]?.AsBool() == true) ||
                        (state["m_CycleOffset"]?.AsFloat() ?? 0) != 0) return false;
                    index = projected.States.Count; selectedStates[id] = index;
                    // The projected pose starts from the lower/base stream, not a previous state's retained values.
                    projected.States.Add(new(state["m_Name"]?.AsString() ?? "State", clip?.Id, speed, true, time));
                }
                var route = new ExpressionTransition { Destination = index, CanTransitionToSelf = true };
                route.Conditions.Add(new("GestureLeft", 6, left)); route.Conditions.Add(new("GestureRight", 6, right));
                projected.Transitions.Add(route);
                break;
            }
            if (failed) return false;
        }
        result = projected;
        detail = string.Join(", ", defaults.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
        if (detail.Length == 0) detail = "hand inputs only";
        if (missingDefaults.Count > 0) detail += "; missing neutral/default motion(s) use lower-layer/base stream: " +
            string.Join(", ", missingDefaults.OrderBy(id => id, StringComparer.Ordinal));
        return true;
    }
}
