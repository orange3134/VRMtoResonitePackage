using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Recovers hand conditions for FaceEmo candidates selected through constant Parameter Driver writes.</summary>
internal sealed class FaceEmoGestureConditions
{
    private sealed record Source(string Hand, string Parameter, float[] Values, HashSet<long> Behaviours);
    internal sealed record Assignment(long State, int Left, int Right);
    private readonly UnityScene _scene;
    private readonly Dictionary<string, float[]> _values = new(StringComparer.Ordinal);

    internal FaceEmoGestureConditions(UnityScene scene, YamlNode controller, Func<YamlNode, bool> emptyMotion,
        IEnumerable<UnityScene> controllers)
    {
        _scene = scene;
        var sources = new List<Source>();
        foreach (var layer in controller?["m_AnimatorLayers"]?.Seq ?? new())
            if ((layer["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) < 0 &&
                ReadSource(scene.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root, controller, emptyMotion) is { } source)
                sources.Add(source);
        foreach (var group in sources.GroupBy(s => s.Parameter))
        {
            if (group.Select(s => s.Hand).Distinct().Count() != group.Count() ||
                group.Select(s => s.Values[0]).Distinct().Count() != 1) continue;
            var known = group.SelectMany(s => s.Behaviours).ToHashSet();
            // Include appended FX controllers: an unknown writer invalidates this inference.
            if ((controllers ?? new[] { scene }).Any(s => s.Documents.Values.Any(d => IsDriver(d.Root) &&
                (s != scene || !known.Contains(d.FileId)) &&
                (d.Root["parameters"]?.Seq?.Any(p => p["name"]?.AsString() == group.Key) ?? false)))) continue;
            var values = new float[64];
            for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
            {
                float value = group.First().Values[0];
                // Fixed-pose convention: active hands override neutral; later input layers win.
                // Parameter Driver execution history is deliberately not exported.
                foreach (var source in group)
                {
                    int gesture = source.Hand == "GestureLeft" ? l : r;
                    if (gesture != 0) value = source.Values[gesture];
                }
                values[l * 8 + r] = value;
            }
            _values[group.Key] = values;
        }
    }

    internal IReadOnlyList<Assignment> ForLayer(YamlNode layer)
    {
        var assignments = new List<Assignment>();
        if (_values.Count == 0 || (layer["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) >= 0) return assignments;
        bool usedAlias = false;
        for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
        {
            float? Value(string name)
            {
                if (name == "GestureLeft") return l;
                if (name == "GestureRight") return r;
                if (!_values.TryGetValue(name, out var values)) return null;
                usedAlias = true; return values[l * 8 + r];
            }
            var visited = new HashSet<long>();
            long Enter(long machine)
            {
                if (!visited.Add(machine) || _scene.Doc(machine) is not { ClassId: 1107 } doc) return 0;
                var owner = doc.Root;
                if (Has(owner, "m_StateMachineBehaviours") || Has(owner, "m_AnyStateTransitions")) return 0;
                long id = owner["m_DefaultState"]?.FileID ?? 0;
                foreach (var t in Transitions(owner, "m_EntryTransitions"))
                {
                    if (!Matches(t, Value, out bool matches)) return 0;
                    if (!matches) continue;
                    if (!Untimed(t) || t["m_IsExit"]?.AsBool() == true) return 0;
                    long child = t["m_DstStateMachine"]?.FileID ?? 0;
                    id = t["m_DstState"]?.FileID ?? 0;
                    if (child != 0)
                        return id == 0 && (owner["m_ChildStateMachines"]?.Seq?.Any(c => c["m_StateMachine"]?.FileID == child) ?? false)
                            ? Enter(child) : 0;
                    break;
                }
                return (owner["m_ChildStates"]?.Seq?.Any(c => c["m_State"]?.FileID == id) ?? false) ? id : 0;
            }
            long selected = Enter(layer["m_StateMachine"]?.FileID ?? 0);
            if (_scene.Doc(selected) is not { ClassId: 1102 } state || Has(state.Root, "m_StateMachineBehaviours")) return Array.Empty<Assignment>();
            // Unknown gates, a timed departure or an active exit cannot define a stable hand assignment.
            foreach (var t in Transitions(state.Root, "m_Transitions"))
                if (!Matches(t, Value, out bool matches) || matches) return Array.Empty<Assignment>();
            assignments.Add(new(selected, l, r));
        }
        return usedAlias ? assignments : Array.Empty<Assignment>();
    }

    private Source ReadSource(YamlNode machine, YamlNode controller, Func<YamlNode, bool> emptyMotion)
    {
        if (machine == null || Has(machine, "m_ChildStateMachines") || Has(machine, "m_StateMachineBehaviours") ||
            Has(machine, "m_AnyStateTransitions") || !Has(machine, "m_EntryTransitions")) return null;
        var entries = Transitions(machine, "m_EntryTransitions").ToArray();
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
            if (!children.Contains(id) || !selected.Add(id) || _scene.Doc(id) is not { ClassId: 1102 } doc) return null;
            var state = doc.Root; var motion = state["m_Motion"];
            if ((motion?.FileID ?? 0) != 0 && emptyMotion?.Invoke(motion) != true) return null;
            var exits = Transitions(state, "m_Transitions").ToArray();
            foreach (var t in exits)
                if (!Untimed(t) || t["m_IsExit"]?.AsBool() != true || (t["m_DstState"]?.FileID ?? 0) != 0 ||
                    (t["m_DstStateMachine"]?.FileID ?? 0) != 0 || !Matches(t, Value, out bool matches) || matches) return null;
            // The input layer must update after every change, not stay in its initial state.
            for (int other = 0; other < 8; other++)
                if (other != g && !exits.Any(t => Matches(t, n => n == hand ? other : null, out bool m) && m)) return null;
            if (state["m_StateMachineBehaviours"]?.Seq is not { Count: 1 } refs) return null;
            long bid = refs[0].FileID ?? 0;
            var b = _scene.Doc(bid)?.Root;
            if (!IsDriver(b) || b["m_Enabled"]?.AsBool() == false || b["parameters"]?.Seq is not { Count: 1 } writes) return null;
            var write = writes[0]; string name = write["name"]?.AsString();
            float value = write["value"]?.AsFloat(float.NaN) ?? float.NaN;
            var parameters = (controller["m_AnimatorParameters"]?.Seq ?? new()).Where(p => p["m_Name"]?.AsString() == name).ToArray();
            if (name == null || name is "GestureLeft" or "GestureRight" || write["type"]?.AsInt(-1) != 0 ||
                !string.IsNullOrEmpty(write["source"]?.AsString()) || !float.IsFinite(value) || parameters.Length != 1 ||
                parameters[0]["m_Type"]?.AsInt() is not (1 or 3) ||
                parameters[0]["m_Type"]?.AsInt() == 3 && (value != MathF.Truncate(value) || value < int.MinValue || value >= 2147483648f) ||
                target != null && target != name) return null;
            target = name; values[g] = value; behaviours.Add(bid);
        }
        return selected.SetEquals(children) ? new(hand, target, values, behaviours) : null;
    }

    private static bool IsDriver(YamlNode b) => b?["m_Script"]?.Guid == VrchatConstants.AvatarDescriptorScriptGuid && b["m_Script"]?.FileID == -706344726;
    private static bool Has(YamlNode node, string key) => (node?[key]?.Seq?.Count ?? 0) != 0;
    private IEnumerable<YamlNode> Transitions(YamlNode node, string key) =>
        VrchatAnimatorDefaults.ActiveTransitions(_scene, node?[key]?.Seq).Select(r => _scene.Doc(r.FileID ?? 0)?.Root);
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
