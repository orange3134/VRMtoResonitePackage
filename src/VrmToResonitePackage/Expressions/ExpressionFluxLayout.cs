using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;

namespace VrmToResonitePackage.Expressions;

/// <summary>Positions a board from its consumers back toward their ordered inputs.</summary>
internal static class ExpressionFluxLayout
{
    private const float HorizontalGap = 0.12f;
    private const float VerticalGap = 0.04f;

    // Allow for the stock node UI, including nodes with a custom width.
    public static float Width(ProtoFluxNode node) =>
        Math.Max(0.18f, (node.OverrideWidth ?? ProtoFluxNodeVisual.DEFAULT_WIDTH) *
            ProtoFluxNodeVisual.DEFAULT_SCALE * ProtoFluxNodeVisual.NODE_SCALE);

    private static float Height(ProtoFluxNode node)
    {
        int left = node.AllInputs.Count() + node.AllTargetOperations.Count();
        int right = node.AllSourceOutputs.Count() + node.AllImpulses.Count();
        return Math.Max(0.12f, 0.08f + 0.04f *
            (Math.Max(left, right) + node.NodeReferenceCount + node.NodeGlobalRefCount));
    }

    public static Dictionary<ProtoFluxNode, float2> Arrange(ProtoFluxNode[] nodes)
    {
        var order = nodes.Select((node, index) => (node, index)).ToDictionary(p => p.node, p => p.index);
        var outgoing = nodes.ToDictionary(node => node, _ => new HashSet<ProtoFluxNode>());
        var inputs = nodes.ToDictionary(node => node, _ => new List<ProtoFluxNode>());
        void Edge(ProtoFluxNode source, ProtoFluxNode target)
        {
            if (source == null || target == null || source == target ||
                !outgoing.ContainsKey(source) || !outgoing.ContainsKey(target)) return;
            outgoing[source].Add(target);
            if (!inputs[target].Contains(source)) inputs[target].Add(source);
        }
        // Operation connectors precede data inputs in the stock node UI. Data input
        // enumeration preserves fixed-port order and the order of variable-sized lists.
        foreach (var node in nodes)
            foreach (var impulse in node.AllImpulses) Edge(node, Owner(impulse.Target));
        foreach (var node in nodes)
            foreach (var input in node.AllInputs.Concat(node.NodeReferences)) Edge(Owner(input.Target), node);

        var columns = Columns(nodes, outgoing);
        var heights = nodes.ToDictionary(node => node, Height);
        // Give each source one layout consumer. Keeping an input subtree together
        // prevents unrelated modifier inputs from interleaving an eight-key fan-in.
        var consumer = nodes.ToDictionary(n => n, n => outgoing[n].Where(t => columns[t] > columns[n])
            .OrderBy(t => columns[t]).ThenBy(t => order[t]).FirstOrDefault());
        var children = nodes.ToDictionary(n => n, n => inputs[n].Where(s => consumer[s] == n).ToArray());

        Dictionary<ProtoFluxNode, float> Tree(ProtoFluxNode node)
        {
            var result = MergeTrees(children[node].Select(child => (child, Tree(child))).ToArray());
            float center = children[node].Length == 0 ? 0 :
                (result[children[node][0]] + result[children[node][^1]]) / 2;
            result[node] = center;
            return result.ToDictionary(p => p.Key, p => p.Value - center);
        }

        Dictionary<ProtoFluxNode, float> MergeTrees(
            (ProtoFluxNode Root, Dictionary<ProtoFluxNode, float> Rows)[] trees)
        {
            var result = new Dictionary<ProtoFluxNode, float>();
            var bottom = new Dictionary<int, float>();
            ProtoFluxNode previous = null;
            foreach (var tree in trees)
            {
                float shift = previous == null ? 0 :
                    result[previous] + (heights[previous] + heights[tree.Root]) / 2 + VerticalGap;
                // Merge occupied column contours, not whole subtree rectangles.
                // A sender's short inputs can sit beside a tall upstream key list.
                foreach (var column in tree.Rows.GroupBy(p => columns[p.Key]))
                    if (bottom.TryGetValue(column.Key, out float occupied))
                        shift = Math.Max(shift, occupied + VerticalGap -
                            column.Min(p => p.Value - heights[p.Key] / 2));
                foreach (var (node, row) in tree.Rows)
                {
                    result[node] = row + shift;
                    float edge = row + shift + heights[node] / 2;
                    bottom[columns[node]] = Math.Max(bottom.GetValueOrDefault(columns[node], float.NegativeInfinity), edge);
                }
                previous = tree.Root;
            }
            return result;
        }

        var rows = MergeTrees(nodes.Where(n => consumer[n] == null)
            .OrderByDescending(n => columns[n]).ThenBy(n => order[n])
            .Select(n => (n, Tree(n))).ToArray());

        var x = new Dictionary<int, float>();
        float cursor = 0;
        foreach (var column in nodes.GroupBy(n => columns[n]).OrderBy(g => g.Key))
        {
            float width = column.Max(Width);
            x[column.Key] = cursor + width / 2;
            cursor += width + HorizontalGap;
        }
        float top = nodes.Min(n => rows[n] - heights[n] / 2);
        return nodes.ToDictionary(n => n, n => new float2(x[columns[n]], -(rows[n] - top)));
    }

    private static Dictionary<ProtoFluxNode, int> Columns(ProtoFluxNode[] nodes,
        Dictionary<ProtoFluxNode, HashSet<ProtoFluxNode>> outgoing)
    {
        // Collapse feedback cycles before layering: only edges within a cycle may
        // remain in the same column.
        var indices = new Dictionary<ProtoFluxNode, int>();
        var low = new Dictionary<ProtoFluxNode, int>();
        var component = new Dictionary<ProtoFluxNode, int>();
        var stack = new Stack<ProtoFluxNode>();
        var active = new HashSet<ProtoFluxNode>();
        int nextIndex = 0, componentCount = 0;
        void Visit(ProtoFluxNode node)
        {
            indices[node] = low[node] = nextIndex++;
            stack.Push(node); active.Add(node);
            foreach (var target in outgoing[node])
            {
                if (!indices.ContainsKey(target)) { Visit(target); low[node] = Math.Min(low[node], low[target]); }
                else if (active.Contains(target)) low[node] = Math.Min(low[node], indices[target]);
            }
            if (low[node] != indices[node]) return;
            ProtoFluxNode member;
            do { member = stack.Pop(); active.Remove(member); component[member] = componentCount; } while (member != node);
            componentCount++;
        }
        foreach (var node in nodes) if (!indices.ContainsKey(node)) Visit(node);

        var incoming = Enumerable.Range(0, componentCount).Select(_ => new HashSet<int>()).ToArray();
        var successors = Enumerable.Range(0, componentCount).Select(_ => new HashSet<int>()).ToArray();
        foreach (var source in nodes)
            foreach (var target in outgoing[source])
                if (component[source] != component[target])
                {
                    incoming[component[target]].Add(component[source]);
                    successors[component[source]].Add(component[target]);
                }
        var earliest = new int?[componentCount];
        int Earliest(int group) => earliest[group] ??= incoming[group].Select(parent => Earliest(parent) + 1).DefaultIfEmpty(0).Max();
        var latest = new int?[componentCount];
        // Place inputs as late as possible, immediately before their first consumer,
        // instead of pinning every literal and variable input to the far-left column.
        int Latest(int group) => latest[group] ??= successors[group].Select(child => Latest(child) - 1)
            .DefaultIfEmpty(Earliest(group)).Min();
        return nodes.ToDictionary(node => node, node => Latest(component[node]));
    }

    private static ProtoFluxNode Owner(IWorldElement element)
    {
        for (; element != null; element = element.Parent)
            if (element is ProtoFluxNode node) return node;
        return null;
    }
}
