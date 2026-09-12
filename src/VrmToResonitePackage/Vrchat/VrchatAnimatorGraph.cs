using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Shared conservative reachability for the supported Animator subset. State inventories
/// are used only to index ownership; face inference and competing bindings use this graph.
/// Unknown non-Viseme parameters keep every potentially reachable route.
/// </summary>
internal sealed class VrchatAnimatorGraph
{
    private readonly UnityScene _controller;
    private readonly Dictionary<long, HashSet<long>> _layers = new();
    public Dictionary<YamlNode, YamlNode> Owners { get; } = new();

    public VrchatAnimatorGraph(UnityScene controller)
    {
        _controller = controller;
        foreach (var document in controller.Documents.Values.Where(d => d.ClassId == 1107))
            foreach (string key in new[] { "m_ChildStates", "m_ChildStateMachines" })
                foreach (var child in document.Root?[key]?.Seq ?? new())
                {
                    var node = controller.Doc(child[key == "m_ChildStates" ? "m_State" : "m_StateMachine"]?.FileID ?? 0)?.Root;
                    if (node != null) Owners[node] = document.Root;
                }
    }

    public IReadOnlySet<long> Reachable(long machine)
    {
        if (_layers.TryGetValue(machine, out var cached)) return cached;
        var reachable = new HashSet<long>();
        var reachedValues = new Dictionary<long, HashSet<int>>();
        Gather(machine);
        _layers.Add(machine, reachable);
        return reachable;

        void Gather(long id, IEnumerable<int> eligibleValues = null)
        {
            if (id == 0) return;
            var incoming = (eligibleValues ?? Enumerable.Range(0, 15)).ToHashSet();
            if (!reachedValues.TryGetValue(id, out var seen)) reachedValues[id] = seen = new();
            incoming.ExceptWith(seen);
            if (incoming.Count == 0) return;
            seen.UnionWith(incoming);
            reachable.Add(id);
            YamlNode node = _controller.Doc(id)?.Root;
            foreach (string key in new[] { "m_DstState", "m_DstStateMachine" })
                Gather(node?[key]?.FileID ?? 0, incoming);
            foreach (string key in new[] { "m_Transitions", "m_EntryTransitions", "m_AnyStateTransitions" })
            {
                // Entry preserves the incoming value; subsequent transitions can observe
                // another phoneme. Priority and Solo/Mute apply within each list only.
                var remaining = key == "m_EntryTransitions" ? incoming.ToHashSet() : Enumerable.Range(0, 15).ToHashSet();
                foreach (var transition in VrchatAnimatorDefaults.ActiveTransitions(_controller, node?[key]?.Seq))
                {
                    var candidate = _controller.Doc(transition.FileID ?? 0)?.Root;
                    var conditions = candidate?["m_Conditions"]?.Seq ?? new();
                    var eligible = remaining.Where(value => conditions.All(c =>
                        c["m_ConditionEvent"]?.AsString() != "Viseme" || Matches(c, value))).ToArray();
                    if (eligible.Length == 0) continue;
                    if (conditions.All(c => c["m_ConditionEvent"]?.AsString() == "Viseme") &&
                        candidate?["m_HasExitTime"]?.AsBool() != true) remaining.ExceptWith(eligible);
                    Gather(transition.FileID ?? 0, eligible);
                }
                if (key == "m_EntryTransitions" && remaining.Count > 0)
                    Gather(node?["m_DefaultState"]?.FileID ?? 0, remaining);
            }
        }
    }

    internal static bool Matches(YamlNode condition, float value)
    {
        float threshold = condition["m_EventTreshold"]?.AsFloat() ?? 0;
        return condition["m_ConditionMode"]?.AsInt() switch
        {
            1 => value != 0, 2 => value == 0, 3 => value > threshold, 4 => value < threshold,
            6 => value == threshold, 7 => value != threshold, _ => false,
        };
    }
}
