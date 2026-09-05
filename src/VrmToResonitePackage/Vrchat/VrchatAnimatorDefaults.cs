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
        foreach (YamlNode layer in settings?["m_AnimatorLayers"]?.Seq ?? new())
        {
            long machine = layer["m_StateMachine"]?.FileID ?? 0;
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
                foreach (YamlNode reference in state?["m_Transitions"]?.Seq ?? new())
                {
                    YamlNode transition = controller.Doc(reference.FileID ?? 0)?.Root;
                    if (transition == null || transition["m_HasExitTime"]?.AsBool() == true ||
                        !Enabled(transition)) continue;
                    YamlNode next = controller.Doc(transition["m_DstState"]?.FileID ?? 0)?.Root;
                    if (next == null) continue;
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

        YamlNode Entry(long machine, HashSet<long> visited)
        {
            if (machine == 0 || !visited.Add(machine)) return null;
            YamlNode node = controller.Doc(machine)?.Root;
            foreach (YamlNode reference in node?["m_EntryTransitions"]?.Seq ?? new())
            {
                YamlNode transition = controller.Doc(reference.FileID ?? 0)?.Root;
                if (!Enabled(transition)) continue;
                long state = transition["m_DstState"]?.FileID ?? 0;
                return state != 0 ? controller.Doc(state)?.Root : Entry(transition["m_DstStateMachine"]?.FileID ?? 0, visited);
            }
            return controller.Doc(node?["m_DefaultState"]?.FileID ?? 0)?.Root;
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
