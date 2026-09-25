using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>FaceEmo's SYNC_EM_EMOTE import: 14 entries per set, right hand before left.</summary>
internal static class VrchatCacExpressionImporter
{
    public static bool TryRead(UnityScene scene, YamlNode controller, string controllerId,
        Func<YamlNode, ExpressionClip> readClip, out List<ExpressionLayer> sets)
    {
        sets = null;
        int order = 0;
        foreach (var layer in controller?["m_AnimatorLayers"]?.Seq ?? new())
        {
            int layerIndex = order++;
            var root = scene.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root;
            var entries = (root?["m_ChildStateMachines"]?.Seq ?? new())
                .SelectMany(child => scene.Doc(child["m_StateMachine"]?.FileID ?? 0)?.Root?["m_EntryTransitions"]?.Seq ?? new())
                .Select(r => scene.Doc(r.FileID ?? 0)?.Root).Where(t => t != null).ToArray();
            // Match FaceEmo GetCacLayer: the first such layer takes precedence over normal import.
            if (!entries.Any(t => t["m_Conditions"]?.Seq?.FirstOrDefault()?["m_ConditionEvent"]?.AsString() == "SYNC_EM_EMOTE")) continue;
            sets = new();
            var branches = new List<(int Index, YamlNode State, ExpressionClip Clip)>();
            foreach (var transition in entries)
            {
                var conditions = transition["m_Conditions"]?.Seq;
                if (transition["m_Mute"]?.AsBool() == true || conditions?.Count != 1 ||
                    conditions[0]["m_ConditionEvent"]?.AsString() != "SYNC_EM_EMOTE" ||
                    conditions[0]["m_ConditionMode"]?.AsInt() != 6) continue;
                float threshold = conditions[0]["m_EventTreshold"]?.AsFloat() ?? 0;
                if (!float.IsFinite(threshold) || threshold < 1 || threshold >= int.MaxValue || threshold != MathF.Truncate(threshold)) continue;
                if (scene.Doc(transition["m_DstState"]?.FileID ?? 0) is not { ClassId: 1102 } state) continue;
                var clip = LastMotion(state.Root["m_Motion"], new());
                if (clip?.Curves.Any(c => !VrchatExpressionDetection.IsViseme(c.Binding.Shape)) != true) continue;
                branches.Add(((int)threshold - 1, state.Root, clip));
            }
            foreach (var group in branches.OrderBy(b => b.Index).GroupBy(b => b.Index / 14))
            {
                var set = new ExpressionLayer { Id = controllerId + ":cac:" + layerIndex + ":" + group.Key,
                    Name = "FaceEmo expression set " + (group.Key + 1), DefaultState = 0, EmptyStatesUseBaseStream = true };
                set.States.Add(new("Neutral (authored baseline)", null, 1, true));
                foreach (var branch in group)
                {
                    int offset = branch.Index % 14, gesture = offset % 7 + 1;
                    string hand = offset < 7 ? "GestureRight" : "GestureLeft";
                    string time = gesture == 1 && branch.State["m_TimeParameterActive"]?.AsBool() == true ? hand + "Weight" : null;
                    int index = set.States.Count;
                    set.States.Add(new(branch.State["m_Name"]?.AsString() ?? branch.Clip.Name, branch.Clip.Id, 1, true, time));
                    // Self transitions stop the first matching branch from falling through to the other hand.
                    var transition = new ExpressionTransition { Destination = index, CanTransitionToSelf = true };
                    transition.Conditions.Add(new(hand, 6, gesture));
                    set.Transitions.Add(transition);
                }
                set.Transitions.Add(new() { Destination = 0, CanTransitionToSelf = true });
                sets.Add(set);
            }
            return true;
        }
        return false;

        ExpressionClip LastMotion(YamlNode motion, HashSet<long> visited)
        {
            if (motion?.Guid != null) return readClip(motion);
            long id = motion?.FileID ?? 0;
            if (id == 0 || !visited.Add(id) || scene.Doc(id) is not { ClassId: 206 } tree) return null;
            return LastMotion(tree.Root["m_Childs"]?.Seq?.LastOrDefault()?["m_Motion"], visited);
        }
    }

    public static void SelectFirstSet(ExpressionModel model, bool pruneClips)
    {
        var clips = model.Clips.ToDictionary(c => c.Id);
        bool HasFace(ExpressionState state) => state.ClipId != null &&
            clips.TryGetValue(state.ClipId, out var clip) && clip.Curves.Count > 0;
        var selected = model.ImportedGestureSets.FirstOrDefault(set => set.States.Any(HasFace));
        model.Layers.Clear();
        model.Menu.Clear();
        model.DetectedExpressions.Clear();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        if (selected != null)
        {
            var layer = new ExpressionLayer { Id = selected.Id, Name = selected.Name, DefaultState = 0, EmptyStatesUseBaseStream = true };
            layer.States.Add(selected.States[0]);
            // Actual renderer resolution can remove non-face branches. They must not shadow a valid other-hand pose.
            foreach (var source in selected.Transitions.Where(t => t.Destination > 0))
            {
                var state = selected.States[source.Destination];
                if (!HasFace(state)) continue;
                var transition = new ExpressionTransition { Destination = layer.States.Count, CanTransitionToSelf = true };
                transition.Conditions.AddRange(source.Conditions);
                layer.States.Add(state); layer.Transitions.Add(transition); retained.Add(state.ClipId);
            }
            layer.Transitions.Add(new() { Destination = 0, CanTransitionToSelf = true });
            model.Layers.Add(layer);
            model.DetectedExpressions.AddRange(model.Clips.Where(c => retained.Contains(c.Id)));
        }
        if (pruneClips)
        {
            model.Clips.RemoveAll(c => !retained.Contains(c.Id));
            string message = selected == null ? "FaceEmo CAC: no set contains resolved face curves." :
                $"FaceEmo CAC: selected first set ({selected.Name}), {model.Layers[0].States.Count - 1} branches; " +
                "right-hand 1..7 precede left-hand 1..7. Other sets, locks and parameter-driver side effects are not imported.";
            model.Diagnostics.Add(message); UniLog.Log(message);
        }
    }
}
