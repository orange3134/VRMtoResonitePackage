using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Validates flat Entry/Exit hand selectors before exporting their discrete poses.</summary>
internal static class VrchatEntryGestureRouter
{
    internal static bool TryProject(UnityPackage package, UnityScene scene, YamlNode machine, YamlNode mask,
        ExpressionLayer original, IReadOnlyList<long> ids, Func<YamlNode, ExpressionClip> readClip,
        out ExpressionLayer result)
    {
        result = null;
        // A humanoid-only mask cannot filter these non-transform blendshape curves.
        // Transform masks and missing mask assets remain unsupported.
        if ((mask?.FileID ?? 0) != 0)
        {
            var asset = package.ByGuid(mask.Guid);
            if (asset?.HasContent != true) return false;
            var document = package.ReadScene(asset).Doc(mask.FileID.Value);
            if (document?.ClassId != 319 || document.Root["m_Elements"]?.Seq is not { Count: 0 }) return false;
        }
        if (original.Entry.Count == 0 || original.States.Count != ids.Count ||
            (machine["m_AnyStateTransitions"]?.Seq?.Count ?? 0) != 0) return false;
        bool Hand(string name) => name is "GestureLeft" or "GestureRight";
        var entries = original.Entry;
        if (entries.Any(t => t.HasExitTime || t.Offset != 0 || t.Conditions.Count == 0 ||
            t.Conditions.Any(c => !Hand(c.Parameter)))) return false;
        var exits = new List<ExpressionTransition>[ids.Count];
        HashSet<ExpressionBinding> bindings = null;
        for (int i = 0; i < ids.Count; i++)
        {
            var state = scene.Doc(ids[i])?.Root;
            var motion = state?["m_Motion"];
            var clip = readClip(motion);
            if ((motion?.FileID ?? 0) != 0 && clip == null) return false;
            if (clip?.Curves.Count > 0)
            {
                var set = clip.Curves.Select(c => c.Binding).ToHashSet();
                if (bindings != null && !bindings.SetEquals(set)) return false;
                bindings = set;
            }
            foreach (var reference in state?["m_StateMachineBehaviours"]?.Seq ?? new())
            {
                var behaviour = scene.Doc(reference.FileID ?? 0)?.Root;
                var script = behaviour?["m_Script"];
                // Only recognized eye/mouth tracking controls are projected away. They do not
                // change selection; the exported system retains its own blink/viseme cooperation.
                if (script?.Guid != VrchatConstants.AvatarDescriptorScriptGuid || script.FileID != -646210727)
                    return false;
                foreach (string key in new[] { "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip",
                    "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers" })
                    if ((behaviour[key]?.AsInt() ?? 0) != 0) return false;
                foreach (string key in new[] { "trackingEyes", "trackingMouth" })
                    if ((behaviour[key]?.AsInt(-1) ?? -1) is < 0 or > 2) return false;
            }
            exits[i] = new();
            foreach (var reference in VrchatAnimatorDefaults.ActiveTransitions(scene, state?["m_Transitions"]?.Seq))
            {
                var t = scene.Doc(reference.FileID ?? 0)?.Root;
                if (t?["m_IsExit"]?.AsBool() != true || (t["m_DstState"]?.FileID ?? 0) != 0 ||
                    (t["m_DstStateMachine"]?.FileID ?? 0) != 0 || t["m_HasExitTime"]?.AsBool() == true ||
                    (t["m_TransitionOffset"]?.AsFloat() ?? 0) != 0 ||
                    (t["m_InterruptionSource"]?.AsInt() ?? 0) != 0) return false;
                var exit = new ExpressionTransition();
                foreach (var c in t["m_Conditions"]?.Seq ?? new())
                {
                    string parameter = c["m_ConditionEvent"]?.AsString();
                    int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                    float threshold = c["m_EventTreshold"]?.AsFloat() ?? 0;
                    if (!Hand(parameter) || mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold)) return false;
                    exit.Conditions.Add(new(parameter, mode, threshold));
                }
                exits[i].Add(exit);
            }
        }
        if (bindings == null) return false;
        var projected = new ExpressionLayer { Id = original.Id, Name = original.Name, Weight = original.Weight,
            DefaultState = original.DefaultState, EmptyStatesUseBaseStream = true };
        projected.States.AddRange(original.States);
        for (int left = 0; left < 8; left++)
            for (int right = 0; right < 8; right++)
            {
                bool Matches(ExpressionTransition t) => t.Conditions.All(c => c.Matches(c.Parameter == "GestureLeft" ? left : right));
                int selected = entries.FirstOrDefault(Matches)?.Destination ?? original.DefaultState;
                if (selected < 0 || selected >= ids.Count) return false;
                // Every prior state must exit towards Entry unless it is the selected stable state.
                // Do not turn latched states or Entry/Exit cycles into apparently stateless poses.
                for (int i = 0; i < ids.Count; i++)
                    if (exits[i].Any(Matches) == (i == selected)) return false;
                var transition = new ExpressionTransition { Destination = selected, CanTransitionToSelf = true };
                transition.Conditions.Add(new("GestureLeft", 6, left));
                transition.Conditions.Add(new("GestureRight", 6, right));
                projected.Transitions.Add(transition);
            }
        result = projected;
        return true;
    }
}
