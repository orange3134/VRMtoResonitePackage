using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Projects an empty dispatcher and complete face poses onto the discrete gesture table.</summary>
internal static class VrchatGestureRouter
{
    internal static bool TryProject(UnityScene scene, YamlNode machine, ExpressionLayer original,
        Func<YamlNode, ExpressionClip> readClip, out ExpressionLayer result)
    {
        result = null;
        bool Hand(string name) => name is "GestureLeft" or "GestureRight";
        var states = (machine["m_ChildStates"]?.Seq ?? new()).Select(n => n["m_State"]?.FileID ?? 0).ToHashSet();
        long dispatcherId = machine["m_DefaultState"]?.FileID ?? 0;
        var dispatcher = scene.Doc(dispatcherId)?.Root;
        var empty = readClip(dispatcher?["m_Motion"]);
        if (!states.Contains(dispatcherId) || empty == null || empty.Curves.Count != 0 ||
            (machine["m_EntryTransitions"]?.Seq?.Count ?? 0) != 0) return false;

        IEnumerable<YamlNode> Transitions(YamlNode node, string key) =>
            VrchatAnimatorDefaults.ActiveTransitions(scene, node?[key]?.Seq).Select(r => scene.Doc(r.FileID ?? 0)?.Root);
        var selectionParameters = states.SelectMany(id => Transitions(scene.Doc(id)?.Root, "m_Transitions"))
            .Concat(Transitions(machine, "m_AnyStateTransitions"))
            .SelectMany(t => t?["m_Conditions"]?.Seq ?? new())
            .Select(c => c["m_ConditionEvent"]?.AsString()).ToHashSet();
        bool SafeBehaviours(YamlNode state)
        {
            foreach (var reference in state?["m_StateMachineBehaviours"]?.Seq ?? new())
            {
                var behaviour = scene.Doc(reference.FileID ?? 0)?.Root;
                var script = behaviour?["m_Script"];
                // Ignore only known parameter drivers that cannot change this layer's selection.
                if (script?.Guid != VrchatConstants.AvatarDescriptorScriptGuid || script.FileID != -706344726 ||
                    behaviour["parameters"]?.Seq == null) return false;
                foreach (var parameter in behaviour["parameters"].Seq)
                    if (string.IsNullOrEmpty(parameter["name"]?.AsString()) ||
                        selectionParameters.Contains(parameter["name"].AsString()) ||
                        Hand(parameter["name"].AsString())) return false;
            }
            return true;
        }
        // External reactions are explicitly excluded, never mistaken for hand routes.
        foreach (var transition in Transitions(machine, "m_AnyStateTransitions"))
            if (transition == null || (transition["m_Conditions"]?.Seq?.Count ?? 0) == 0 ||
                transition["m_Conditions"].Seq.Any(c => Hand(c["m_ConditionEvent"]?.AsString()))) return false;

        if (!SafeBehaviours(dispatcher)) return false;
        var projected = new ExpressionLayer { Id = original.Id, Name = original.Name, Weight = original.Weight };
        var routes = new List<(List<ExpressionCondition> Conditions, int Destination)>();
        var destinations = new Dictionary<long, int>();
        HashSet<ExpressionBinding> bindings = null;
        foreach (var transition in Transitions(dispatcher, "m_Transitions"))
        {
            if (transition == null || transition["m_IsExit"]?.AsBool() == true ||
                transition["m_HasExitTime"]?.AsBool() == true ||
                (transition["m_TransitionOffset"]?.AsFloat() ?? 0) != 0 ||
                (transition["m_DstStateMachine"]?.FileID ?? 0) != 0) return false;
            var conditions = new List<ExpressionCondition>();
            foreach (var c in transition["m_Conditions"]?.Seq ?? new())
            {
                string name = c["m_ConditionEvent"]?.AsString();
                int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                float value = c["m_EventTreshold"]?.AsFloat() ?? 0;
                if (!Hand(name) || mode is not (3 or 4 or 6 or 7) || !float.IsFinite(value)) return false;
                conditions.Add(new(name, mode, value));
            }
            if (conditions.Count == 0) return false;
            long id = transition["m_DstState"]?.FileID ?? 0;
            if (!states.Contains(id) || id == dispatcherId) return false;
            if (!destinations.TryGetValue(id, out int index))
            {
                var state = scene.Doc(id)?.Root;
                var clip = readClip(state?["m_Motion"]);
                if (clip == null || clip.Curves.Count == 0 || !SafeBehaviours(state)) return false;
                var set = clip.Curves.Select(c => c.Binding).ToHashSet();
                if (bindings != null && !bindings.SetEquals(set)) return false;
                bindings = set;
                var exits = Transitions(state, "m_Transitions").ToArray();
                if (exits.Length == 0 || exits.Any(t => t == null || t["m_IsExit"]?.AsBool() != true ||
                    t["m_HasExitTime"]?.AsBool() == true || (t["m_TransitionOffset"]?.AsFloat() ?? 0) != 0 ||
                    (t["m_DstState"]?.FileID ?? 0) != 0 || (t["m_DstStateMachine"]?.FileID ?? 0) != 0 ||
                    (t["m_Conditions"]?.Seq?.Count ?? 0) == 0 ||
                    t["m_Conditions"].Seq.Any(c => !Hand(c["m_ConditionEvent"]?.AsString())))) return false;
                var parsed = new ExpressionState(state["m_Name"]?.AsString() ?? "State", clip.Id,
                    state["m_Speed"]?.AsFloat(1) ?? 1, state["m_WriteDefaultValues"]?.AsBool() == true,
                    state["m_TimeParameterActive"]?.AsBool() == true ? state["m_TimeParameter"]?.AsString() ?? "" : null);
                index = projected.States.Count;
                destinations.Add(id, index);
                projected.States.Add(parsed);
            }
            routes.Add((conditions, index));
        }
        // Require complete coverage: an empty routing state cannot retain a previous pose safely.
        for (int left = 0; left < 8; left++)
            for (int right = 0; right < 8; right++)
            {
                int route = routes.FindIndex(r => r.Conditions.All(c => c.Matches(c.Parameter == "GestureLeft" ? left : right)));
                if (route < 0) return false;
                var transition = new ExpressionTransition { Destination = routes[route].Destination, CanTransitionToSelf = true };
                transition.Conditions.Add(new("GestureLeft", 6, left));
                transition.Conditions.Add(new("GestureRight", 6, right));
                projected.Transitions.Add(transition);
            }
        result = projected;
        return true;
    }
}
