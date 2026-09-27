using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;

internal static class ExpressionLayoutChecks
{
    public static void CheckDirection(Slot expressions)
    {
        int dataLinks = 0, impulseLinks = 0, feedbackLinks = 0;
        foreach (var board in expressions.GetComponentsInChildren<ProtoFluxNode>().GroupBy(node => node.Slot.Parent))
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
            // must point strictly right.
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
        CheckKeyboardInputs(expressions);
        CheckControllerInputs(expressions);
        if (dataLinks == 0 || impulseLinks == 0) throw new InvalidOperationException("Direction checks must cover both data and impulse connections");
        Console.WriteLine($"LAYOUT: {dataLinks} data/reference links and {impulseLinks} impulse links point right; {feedbackLinks} feedback links share a layer");
    }

    private static void CheckControllerInputs(Slot expressions)
    {
        var modules = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules");
        foreach (var module in modules.Children)
        foreach (var hand in module.Children.Where(s => s.Name is "Left" or "Right"))
        {
            var board = hand.FindChild("Logic");
            var nodes = board.GetComponentsInChildren<ProtoFluxNode>();
            bool pad = module.Name is "Vive" or "WindowsMR";
            Check(nodes.Count(n => n.GetType().Name == "ComposeBits_byte") == (pad ? 0 : 1) &&
                nodes.Count(n => n.GetType().Name == "ValueMultiplex`1") == 1 &&
                nodes.Count(n => n.GetType().Name == "IndexOfFirstValueMatch`1") == (pad ? 0 : 1),
                module.Name + " has one device-specific classifier");
            Check(nodes.Count(n => n.GetType().Name == "FingerPose") == (module.Name == "Index" ? 5 : 0),
                "only Index reads five finger joint rotations");
            Check(nodes.Count(n => n.GetType().Name == "Atan2_Float") == (pad ? 1 : 0),
                "pad controllers use angular sectors");
        }
        Console.WriteLine("LAYOUT: device-specific controller boards retain valid input flow");
    }

    private static void CheckKeyboardInputs(Slot expressions)
    {
        foreach (var hand in expressions.FindChild("Inputs").FindChild("Keyboard").Children)
        {
            var board = hand.FindChild("Logic");
            var nodes = board.GetComponentsInChildren<ProtoFluxNode>();
            float X(ProtoFluxNode n) => board.GlobalPointToLocal(n.Slot.GlobalPosition).x;
            float Y(ProtoFluxNode n) => board.GlobalPointToLocal(n.Slot.GlobalPosition).y;
            foreach (string type in new[] { "IndexOfFirstValueMatch`1", "AND_Multi_Bool", "ValueEquals`1" })
                foreach (var target in nodes.Where(n => n.GetType().Name == type))
                {
                    var inputs = target.AllInputs.Select(p => Owner(p.Target)).Where(n => n != null).Distinct().ToArray();
                    for (int i = 1; i < inputs.Length; i++)
                        Check(Y(inputs[i - 1]) > Y(inputs[i]) + 0.01f,
                            hand.Name + ": " + type + " inputs follow port order from top to bottom");
                    if (type == "IndexOfFirstValueMatch`1")
                        Check(!nodes.Except(inputs).Any(n => Math.Abs(X(n) - X(inputs[0])) < 0.01f &&
                            Y(n) < inputs.Max(Y) && Y(n) > inputs.Min(Y)),
                            hand.Name + ": unrelated inputs do not split the keyboard fan-in");
                    foreach (var input in inputs)
                        Check(X(target) - X(input) is > 0 and < 0.65f,
                            hand.Name + ": fan-in sources stay in the adjacent input column");
                }
            foreach (var target in nodes.Where(n => n.GetType().Name == "KeyHeld"))
            {
                var input = Owner(target.AllInputs.Single().Target);
                Check(X(target) - X(input) is > 0 and < 0.65f && Math.Abs(Y(target) - Y(input)) < 0.65f,
                    hand.Name + ": key input stays near its KeyHeld consumer");
            }
            var sender = nodes.Single(n => n.GetType().Name.StartsWith("DynamicImpulseTriggerWithValue", StringComparison.Ordinal));
            foreach (string port in new[] { "Tag", "TargetHierarchy", "ExcludeDisabled" })
            {
                var input = Owner(((ISyncRef)ExpressionFlux.Member(sender, port)).Target);
                Check(X(sender) - X(input) is > 0 and < 0.65f && Math.Abs(Y(sender) - Y(input)) < 0.65f,
                    hand.Name + ": sender inputs stay near the sender");
            }
        }
        Console.WriteLine("LAYOUT: keyboard fan-in order and consumer-local key/tag/target inputs verified");
    }

    public static void CheckFixtures(Slot parent)
    {
        var root = parent.AddSlot("Temporary layout fixtures");
        try
        {
            var board = root.AddSlot("Board");
            var g = new ExpressionFlux(board);
            // Creation order is intentionally different from input order.
            var second = g.Constant(20);
            var first = g.Constant(10);
            var sum = g.Binary<int>("ValueAdd", first, second);
            var local = g.Constant(3);
            var result = g.Binary<int>("ValueMul", sum, local);
            var cycleA = g.Node("NOT_Bool");
            var cycleB = g.Node("NOT_Bool", null, ("A", cycleA));
            ExpressionFlux.Link(cycleA, "A", cycleB);
            ExpressionFlux.Arrange(root);
            Elements.Core.float3 Position(IWorldElement n) => board.GlobalPointToLocal(Owner(n).Slot.GlobalPosition);
            Check(Position(first).y > Position(second).y, "fan-in order is independent of node creation order");
            Check(Position(local).x > Position(first).x && Position(result).x - Position(local).x < 0.65f,
                "a shallow input moves next to its deep consumer");
            Check(Math.Abs(Position(sum).y - Position(result).y) < 0.65f,
                "connected operations stay near each other");
            Check(Math.Abs(Position(cycleA).x - Position(cycleB).x) < 0.001f,
                "feedback cycles remain in one column");
            var nodes = root.GetComponentsInChildren<ProtoFluxNode>();
            var before = nodes.ToDictionary(n => n, n => n.Slot.GlobalPosition);
            ExpressionFlux.Arrange(root);
            Check(nodes.All(n => (n.Slot.GlobalPosition - before[n]).Magnitude < 0.0001f),
                "arranging a graph twice keeps the same positions");
        }
        finally { root.Destroy(); }
        Console.WriteLine("LAYOUT: shuffled inputs, shallow dependencies, cycles and repeatability verified");
    }

    public static void SaveKeyboardLayout(Slot expressions, string path)
    {
        var board = expressions.FindChild("Inputs").FindChild("Keyboard").FindChild("Left").FindChild("Logic");
        var nodes = board.GetComponentsInChildren<ProtoFluxNode>().ToArray();
        var ids = nodes.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(nodes.Select(n =>
        {
            var position = board.GlobalPointToLocal(n.Slot.GlobalPosition);
            return new {
                id = ids[n], name = n.Slot.Name,
                variable = n.Slot.GetComponentsInChildren<GlobalValue<string>>().FirstOrDefault()?.Value.Value,
                x = position.x, y = position.y, width = ExpressionFluxLayout.Width(n),
                sources = n.AllInputs.Concat(n.NodeReferences).Select(p => Owner(p.Target))
                    .Where(p => p != null && ids.ContainsKey(p)).Select(p => ids[p]).ToArray(),
                calls = n.AllImpulses.Select(p => Owner(p.Target))
                    .Where(p => p != null && ids.ContainsKey(p)).Select(p => ids[p]).ToArray()
            };
        }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ProtoFluxNode Owner(IWorldElement element)
    {
        for (; element != null; element = element.Parent)
            if (element is ProtoFluxNode node) return node;
        return null;
    }
}
