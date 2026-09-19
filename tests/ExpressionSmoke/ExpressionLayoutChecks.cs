using FrooxEngine;
using FrooxEngine.ProtoFlux;

internal static class ExpressionLayoutChecks
{
    public static void CheckDirection(Slot expressions)
    {
        int dataLinks = 0, impulseLinks = 0, feedbackLinks = 0;
        foreach (var board in expressions.GetComponentsInChildren<ProtoFluxNode>().GroupBy(node => node.Slot.Parent.Parent))
        {
            var nodes = board.ToHashSet();
            var edges = new HashSet<(ProtoFluxNode Source, ProtoFluxNode Target)>();
            void Add(ProtoFluxNode source, ProtoFluxNode target, bool impulse)
            {
                if (source == null || target == null || !nodes.Contains(source) || !nodes.Contains(target)) return;
                edges.Add((source, target));
                if (impulse) impulseLinks++; else dataLinks++;
            }
            foreach (var node in nodes)
            {
                foreach (var input in node.AllInputs.Concat(node.NodeReferences)) Add(Owner(input.Target), node, false);
                foreach (var impulse in node.AllImpulses) Add(node, Owner(impulse.Target), true);
            }

            // Reachability independently identifies feedback paths. Every other connection
            // must point strictly right, including connections crossing visual sections.
            var outgoing = nodes.ToDictionary(node => node, node => edges.Where(edge => edge.Source == node).Select(edge => edge.Target).ToArray());
            var reachable = new Dictionary<ProtoFluxNode, HashSet<ProtoFluxNode>>();
            foreach (var node in nodes)
            {
                var seen = new HashSet<ProtoFluxNode>();
                var pending = new Stack<ProtoFluxNode>(outgoing[node]);
                while (pending.TryPop(out var next))
                    if (seen.Add(next)) foreach (var target in outgoing[next]) pending.Push(target);
                reachable.Add(node, seen);
            }
            foreach (var (source, target) in edges)
            {
                if (reachable[target].Contains(source)) { feedbackLinks++; continue; }
                float sourceX = board.Key.GlobalPointToLocal(source.Slot.GlobalPosition).x;
                float targetX = board.Key.GlobalPointToLocal(target.Slot.GlobalPosition).x;
                if (sourceX + 0.01f >= targetX)
                    throw new InvalidOperationException($"Flux link must run left to right in {board.Key.Name}: " +
                        $"{source.Slot.Name} ({sourceX}) -> {target.Slot.Name} ({targetX})");
            }
        }
        if (dataLinks == 0 || impulseLinks == 0) throw new InvalidOperationException("Direction checks must cover both data and impulse connections");
        Console.WriteLine($"LAYOUT: {dataLinks} data/reference links and {impulseLinks} impulse links point right; {feedbackLinks} feedback links share a layer");
    }

    private static ProtoFluxNode Owner(IWorldElement element)
    {
        for (; element != null; element = element.Parent)
            if (element is ProtoFluxNode node) return node;
        return null;
    }
}
