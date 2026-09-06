using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Resolves deterministic startup states; does not emulate timed animations or user input.</summary>
internal static class VrchatAnimatorDefaults
{
    public static Dictionary<long, YamlNode> Resolve(UnityScene controller, YamlNode settings,
        Dictionary<string, float> parameters)
    {
        var states = new Dictionary<long, YamlNode>();
        var entered = new HashSet<YamlNode>();
        var owners = new Dictionary<YamlNode, long>();
        var parents = new Dictionary<long, long>();
        foreach (YamlNode layer in settings?["m_AnimatorLayers"]?.Seq ?? new())
        {
            long machine = layer["m_StateMachine"]?.FileID ?? 0;
            IndexMachine(machine, 0);
            states[machine] = Entry(machine, new());
        }
        // Parameter drivers also run on zero-weight control layers. Only Set is deterministic.
        for (int pass = 0; pass < 32; pass++)
        {
            bool changed = false;
            foreach (YamlNode state in states.Values.Where(s => s != null))
            {
                if (!entered.Add(state)) continue;
                foreach (YamlNode reference in state["m_StateMachineBehaviours"]?.Seq ?? new())
                {
                    YamlNode behaviour = controller.Doc(reference.FileID ?? 0)?.Root;
                    YamlNode script = behaviour?["m_Script"];
                    if (script?.Guid != VrchatConstants.AvatarDescriptorScriptGuid || script.FileID != -706344726 ||
                        behaviour["m_Enabled"]?.AsBool() == false) continue;
                    foreach (YamlNode parameter in behaviour["parameters"]?.Seq ?? new())
                    {
                        string name = parameter["name"]?.AsString();
                        if (name == null) continue;
                        if (parameter["type"]?.AsInt() == 0)
                            parameters[name] = parameter["value"]?.AsFloat() ?? 0;
                        else parameters.Remove(name);
                    }
                }
            }
            foreach (long machine in states.Keys.ToArray())
            {
                YamlNode state = states[machine];
                if (state == null) continue;
                // Only the active state machine and its ancestors contribute Any State
                // transitions. Sibling machines must not affect the current expression.
                var path = new List<long>();
                long current = owners.GetValueOrDefault(state, machine);
                while (current != 0 && !path.Contains(current))
                {
                    path.Add(current);
                    current = parents.GetValueOrDefault(current);
                }
                path.Reverse();
                var anyState = path.SelectMany(id =>
                    controller.Doc(id)?.Root?["m_AnyStateTransitions"]?.Seq ?? new()).ToList();
                foreach (YamlNode reference in anyState.Concat(state["m_Transitions"]?.Seq ?? new()))
                {
                    YamlNode transition = controller.Doc(reference.FileID ?? 0)?.Root;
                    if (!Enabled(transition)) continue;
                    // An enabled timed departure means this expression will not settle here.
                    // Do not install its source motion as a permanent blink, including when
                    // the destination is Exit rather than another state in this machine.
                    if (transition["m_HasExitTime"]?.AsBool() == true)
                    {
                        states[machine] = null;
                        changed = true;
                        break;
                    }
                    YamlNode next = controller.Doc(transition["m_DstState"]?.FileID ?? 0)?.Root;
                    if (next == null && (transition["m_DstStateMachine"]?.FileID ?? 0) != 0)
                        next = Entry(transition["m_DstStateMachine"].FileID.Value, new());
                    if (next == null) continue;
                    if (next == state && anyState.Contains(reference) &&
                        transition["m_CanTransitionToSelf"]?.AsBool() != true) continue;
                    // A cycle cannot be reduced to one permanent startup expression.
                    states[machine] = entered.Contains(next) ? null : next;
                    changed = true;
                    break;
                }
            }
            if (!changed) return states;
        }
        // A startup graph that fails to settle is not safe to turn into a permanent face driver.
        return new();

        void IndexMachine(long id, long parent)
        {
            if (id == 0 || !parents.TryAdd(id, parent)) return;
            var node = controller.Doc(id)?.Root;
            foreach (var child in node?["m_ChildStates"]?.Seq ?? new())
            {
                var state = controller.Doc(child["m_State"]?.FileID ?? 0)?.Root;
                if (state != null) owners[state] = id;
            }
            foreach (var child in node?["m_ChildStateMachines"]?.Seq ?? new())
                IndexMachine(child["m_StateMachine"]?.FileID ?? 0, id);
        }

        YamlNode Entry(long machine, HashSet<long> visited)
        {
            if (machine == 0 || !visited.Add(machine)) return null;
            YamlNode node = controller.Doc(machine)?.Root;
            foreach (YamlNode reference in node?["m_EntryTransitions"]?.Seq ?? new())
            {
                YamlNode transition = controller.Doc(reference.FileID ?? 0)?.Root;
                if (!Enabled(transition)) continue;
                long state = transition["m_DstState"]?.FileID ?? 0;
                if (state != 0) return OwnedState(state, machine);
                long child = transition["m_DstStateMachine"]?.FileID ?? 0;
                IndexMachine(child, machine);
                return Entry(child, visited);
            }
            return OwnedState(node?["m_DefaultState"]?.FileID ?? 0, machine);
        }

        YamlNode OwnedState(long id, long machine)
        {
            var state = controller.Doc(id)?.Root;
            if (state != null) owners.TryAdd(state, machine);
            return state;
        }

        bool Enabled(YamlNode transition) => transition != null && transition["m_Mute"]?.AsBool() != true &&
            (transition["m_Conditions"]?.Seq ?? new()).All(Matches);

        bool Matches(YamlNode condition)
        {
            if (!parameters.TryGetValue(condition["m_ConditionEvent"]?.AsString() ?? "", out float value)) return false;
            float threshold = condition["m_EventTreshold"]?.AsFloat() ?? 0;
            return condition["m_ConditionMode"]?.AsInt() switch
            {
                1 => value != 0, 2 => value == 0, 3 => value > threshold, 4 => value < threshold,
                6 => value == threshold, 7 => value != threshold, _ => false,
            };
        }
    }
}
