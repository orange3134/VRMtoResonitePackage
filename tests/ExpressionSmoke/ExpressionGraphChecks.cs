using FrooxEngine;
using FrooxEngine.ProtoFlux;

internal static class ExpressionGraphChecks
{
    // A whole legacy controller fit in one large group. Keep the new boards bounded
    // independently of the number of expressions, outputs, and gesture table rows.
    private const int MaximumBoardNodes = 256;

    public static void CheckLayout(Slot expressions)
    {
        Report(expressions);
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        Check(nodes.Count > 0 && nodes.GroupBy(n => n.Slot).All(g => g.Count() == 1), "one Flux node per slot");
        Check(nodes.All(n => n.Group?.IsValid == true), "all expression Flux groups are valid");
        Check(nodes.All(n => n.Slot.Parent.GetComponents<ProtoFluxNode>().Count == 0), "Flux nodes belong to sections, not other nodes");
        Check(nodes.Select(n => n.Slot.GlobalPosition).Distinct().Count() == nodes.Count, "Flux node positions do not overlap across logic boards");

        Slot Board(ProtoFluxNode node) => node.Slot.Parent.Parent;
        var boards = nodes.GroupBy(Board).ToArray();
        foreach (var group in nodes.GroupBy(n => n.Group))
        {
            var owners = group.Select(Board).Distinct().ToArray();
            Check(owners.Length == 1, "Flux group stays inside one board: " +
                string.Join(", ", owners.Select(b => RelativePath(expressions, b))));
        }

        foreach (string path in new[] { "Core/Logic/Lifecycle", "Core/Logic/Selection", "Core/Logic/Playback",
            "API/Receivers/Logic/Gesture", "API/Receivers/Logic/Select", "API/Receivers/Logic/Automatic" })
        {
            var board = Descendant(expressions, path);
            Check(board != null && boards.Any(g => g.Key == board), "independent logic board exists: " + path);
        }

        foreach (var board in boards.OrderBy(b => RelativePath(expressions, b.Key), StringComparer.Ordinal))
        {
            string path = RelativePath(expressions, board.Key);
            Check(board.Count() <= MaximumBoardNodes, $"board node budget ({MaximumBoardNodes}): {path} has {board.Count()}");
            var diagnostics = Descendant(expressions, "Diagnostics/Graph modules");
            var record = diagnostics?.Children.SingleOrDefault(s => Value<string>(s, "Path") == path);
            Check(record != null && Value<int>(record, "NodeCount") == board.Count(), "module diagnostics match the actual board: " + path);
        }
        foreach (var module in Descendant(expressions, "Inputs/HandGestures/Modules").Children)
            foreach (string hand in new[] { "Left", "Right" })
                Check(boards.Any(b => b.Key == Descendant(module, hand + "/Logic")),
                    "controller hand has an independent board: " + module.Name + "/" + hand);
        var core = Descendant(expressions, "Core");
        foreach (string name in new[] { "PlaybackElapsed", "FadeWeight" })
        {
            var field = core.GetComponents<DynamicValueVariable<float>>()
                .Single(v => v.VariableName.Value == "Expr/" + name).Value;
            Check(field.ActiveLink != null, "playback diagnostics have native local drivers: " + name);
        }
    }

    public static void Report(Slot expressions)
    {
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        var boards = nodes.GroupBy(n => n.Slot.Parent.Parent).ToArray();
        var groups = nodes.GroupBy(n => n.Group).ToArray();
        foreach (var board in boards.OrderBy(b => RelativePath(expressions, b.Key), StringComparer.Ordinal))
            Console.WriteLine($"BOARD: {RelativePath(expressions, board.Key)}: {board.Count()} nodes, " +
                $"{board.Select(n => n.Group).Distinct().Count()} groups");
        int crossed = groups.Count(g => g.Select(n => n.Slot.Parent.Parent).Distinct().Count() > 1);
        Console.WriteLine($"GRAPH: {nodes.Count} nodes, {groups.Length} groups, {boards.Length} boards, " +
            $"largest board {boards.Select(b => b.Count()).DefaultIfEmpty().Max()} nodes, " +
            $"largest group {groups.Select(g => g.Count()).DefaultIfEmpty().Max()} nodes, {crossed} groups crossing boards");
    }

    private static Slot Descendant(Slot root, string path)
    {
        foreach (string name in path.Split('/'))
        {
            root = root?.Children.SingleOrDefault(child => child.Name == name);
            if (root == null) return null;
        }
        return root;
    }

    private static string RelativePath(Slot root, Slot slot)
    {
        var names = new Stack<string>();
        for (; slot != null && slot != root; slot = slot.Parent) names.Push(slot.Name);
        if (slot != root) throw new InvalidOperationException("Flux board is outside the expression system");
        return string.Join("/", names);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Value<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == "Expr/" + name).Value.Value;
}
