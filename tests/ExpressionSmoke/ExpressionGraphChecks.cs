using FrooxEngine;
using FrooxEngine.ProtoFlux;

internal static class ExpressionGraphChecks
{
    // A whole legacy controller fit in one large group. Keep the new boards bounded
    // independently of the number of expressions, outputs, and gesture table rows.
    private const int MaximumBoardNodes = 256;

    public static void CheckLayout(Slot expressions)
    {
        ExpressionSpaceChecks.Run(expressions);
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        Report(expressions);
        ExpressionLayoutChecks.CheckDirection(expressions);
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        Check(nodes.Count > 0 && nodes.GroupBy(n => n.Slot).All(g => g.Count() == 1), "one Flux node per slot");
        Check(nodes.All(n => n.Group?.IsValid == true), "all expression Flux groups are valid");
        Check(nodes.All(n => n.Slot.Parent.GetComponents<ProtoFluxNode>().Count == 0), "Flux nodes belong to sections, not other nodes");
        Check(nodes.Select(n => n.Slot.GlobalPosition).Distinct().Count() == nodes.Count, "Flux node positions do not overlap across logic boards");

        Slot Board(ProtoFluxNode node) => node.Slot.Parent.Parent;
        var updates = nodes.Where(n => n.GetType().Name == "LocalUpdate").ToArray();
        Check(updates.Length == 1 && Board(updates[0]) == Descendant(expressions, "Core/Logic/Playback"), "one shared playback update");
        Check(nodes.All(n => n.GetType().Name is not "ValueFieldDrive`1" and not "ReferenceDrive`1"), "no continuous playback Drive nodes");
        Check(nodes.Any(n => n.GetType().Name.StartsWith("FireOnLocal", StringComparison.Ordinal)),
            "state transitions use local change detectors");
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<bool>>()
            .All(v => v.VariableName.Value != "ExpressionSystem.Input.Keyboard/Held"),
            "keyboard edge state is local to its change detector");
        foreach (var output in expressions.FindChild("Outputs").Children)
        {
            Check(output.FindChild("Logic") == null, "outputs share playback without individual Flux graphs");
            var result = output.GetComponents<DynamicField<float>>()
                .Single(v => v.VariableName.Value == "ExpressionOutput/Result").TargetField.Target;
            Check(result.ActiveLink == null, "Result is writable, without a Flux drive");
            Check(output.GetComponent<ValueCopy<float>>() == null,
                "outputs use no ValueCopy: " + output.Name);
            Check(!output.GetComponents<DynamicValueVariable<float>>().Any(v => v.VariableName.Value == "ExpressionOutput/Result"),
                "Result is a field view, not duplicate stored state: " + output.Name);
            var target = ExpressionTestFields.Reference<IField<float>>(output, "Target");
            var renderer = target.FindNearestParent<SkinnedMeshRenderer>();
            if (renderer != null)
            {
                var meshDriver = result.FindNearestParent<DynamicBlendShapeDriver>();
                var entry = result.Parent as DynamicBlendShapeDriver.BlendShape;
                Check(meshDriver != null && entry != null && meshDriver.Renderer.Target == renderer && entry.Value == result &&
                    entry._drive.Target == target && entry._drive.IsLinkValid && renderer.TryGetBlendShape(entry.BlendShapeName.Value) == target,
                    $"Result references the correct renderer's named blendshape entry: {output.Name}; " +
                    $"renderer={meshDriver?.Renderer.Target == renderer}, value={entry?.Value == result}, " +
                    $"target={entry?._drive.Target == target}, linked={entry?._drive.IsLinkValid}, " +
                    $"name={entry?.BlendShapeName.Value}, shapes={renderer.MeshBlendshapeCount}");
            }
            else Check(result == target, "standalone field outputs are driven directly");
        }
        var meshDrivers = expressions.GetComponentsInChildren<DynamicBlendShapeDriver>();
        var meshOutputs = expressions.FindChild("Outputs").Children
            .Select(o => ExpressionTestFields.Reference<IField<float>>(o, "Target").FindNearestParent<SkinnedMeshRenderer>())
            .Where(r => r != null).ToArray();
        Check(meshDrivers.Select(d => d.Renderer.Target).Distinct().Count() == meshDrivers.Count &&
            meshDrivers.Count == meshOutputs.Distinct().Count(), "exactly one driver per output renderer");
        foreach (var meshDriver in meshDrivers)
            Check(meshDriver.BlendShapes.Count == meshOutputs.Count(r => r == meshDriver.Renderer.Target),
                "mesh driver contains only required shape entries");
        Check(Descendant(expressions, "Core/Logic/Playback").GetComponentsInChildren<ProtoFluxNode>()
            .Count(n => n.GetType().Name == "SampleValueAnimationTrack`1") == 1,
            "one sampler serves the shared output loop");
        var boards = nodes.GroupBy(Board).ToArray();
        var keyboard = Descendant(expressions, "Inputs/Keyboard");
        Check(keyboard.Children.All(hand => hand.FindChild("DV").GetComponentsInChildren<ProtoFluxNode>().Count == 0),
            "keyboard binding records contain no per-key Flux");
        var keyboardBoards = keyboard.GetComponentsInChildren<ProtoFluxNode>().GroupBy(Board).ToArray();
        Check(keyboardBoards.Length == 2 &&
            keyboardBoards.Select(b => b.Key).ToHashSet().SetEquals(new[] {
                Descendant(keyboard, "Left/Logic"), Descendant(keyboard, "Right/Logic") }),
            "keyboard has exactly two logic boards, Left and Right");
        foreach (var board in keyboardBoards)
        {
            var detector = board.Single(n => n.GetType().Name == "FireOnLocalValueChange`1");
            Check(detector.GetType().GetGenericArguments().Single() == typeof(bool) &&
                board.Count(n => n.GetType().Name.StartsWith("DynamicImpulseTriggerWithValue", StringComparison.Ordinal)) == 1 &&
                board.Count(n => n.GetType().Name == "IndexOfFirstValueMatch`1") == 1,
                "each keyboard hand has one bool change detector, first-match selector and sender");
            Check(board.All(n => n.GetType().Name is not "GetActiveUserSelf" and not "IsLocalUser" and not "For" and not "ComposeBits_byte"),
                "keyboard uses the shared avatar worn variable and no per-key send loop");
            var names = board.SelectMany(n => n.Slot.GetComponentsInChildren<GlobalValue<string>>()).Select(v => v.Value.Value).ToArray();
            Check(names.Count(n => n == "modular_avatar/AvatarWornLocal") == 1,
                "each keyboard hand binds AvatarWornLocal once");
        }
        foreach (var hand in keyboard.Children)
        {
            var data = hand.FindChild("DV");
            Check(data.Children.Select(s => s.Name).ToHashSet().SetEquals(
                new[] { "Tag", "Control", "Shift" }.Concat(Enumerable.Range(0, 8).Select(i => "Key." + i))),
                "keyboard settings expose only the hand tag, shared modifiers and eight indexed keys");
        }
        foreach (var group in nodes.GroupBy(n => n.Group))
        {
            var owners = group.Select(Board).Distinct().ToArray();
            Check(owners.Length == 1, "Flux group stays inside one board: " +
                string.Join(", ", owners.Select(b => RelativePath(expressions, b))));
        }

        foreach (string path in new[] { "Core/Logic/Lifecycle", "Core/Logic/Selection", "Core/Logic/Playback",
            "API/Receivers/Logic/Left", "API/Receivers/Logic/Right", "API/Receivers/Logic/MenuLeft", "API/Receivers/Logic/MenuRight",
            "API/Receivers/Logic/Select", "API/Receivers/Logic/AllowExternalInput", "Inputs/ContextMenu/Logic" })
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
        var pending = new Stack<Slot>(); pending.Push(expressions);
        while (pending.TryPop(out var slot))
        {
            Check(slot.Name != "Command", "expression API contains no Command slots");
            foreach (var child in slot.Children) pending.Push(child);
        }
        var publicReceivers = Descendant(expressions, "API/Receivers").GetComponentsInChildren<ProtoFluxNode>()
            .Where(node => node.GetType().Name.StartsWith("DynamicImpulseReceiver", StringComparison.Ordinal)).ToArray();
        Check(publicReceivers.Length == 6, "exactly six public API receivers");
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Parent.Name == hand);
            Check(receiver.GetType().Name == "DynamicImpulseReceiverWithValue`1" &&
                receiver.GetType().GetGenericArguments().SequenceEqual(new[] { typeof(int) }),
                hand + " receives int directly");
            Check(receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ResoPon/Expression/Gesture/" + hand,
                hand + " uses the exact hand Tag");
        }
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Parent.Name == "Menu" + hand);
            Check(receiver.GetType().GetGenericArguments().Single() == typeof(int) &&
                receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ResoPon/Expression/Menu/" + hand,
                "menu has a separate int path while ordinary input is disabled: " + hand);
        }
        Check(publicReceivers.Single(node => node.Slot.Parent.Parent.Name == "Select").GetType().GetGenericArguments().Single() == typeof(string),
            "mapped expression selection receives an ID");
        Check(publicReceivers.Single(node => node.Slot.Parent.Parent.Name == "AllowExternalInput").GetType().GetGenericArguments().Single() == typeof(bool),
            "input permission receives a bool");
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().All(v => v.VariableName.Value != "ExpressionCore/Override"),
            "no Override Slot state is generated");
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<User>>().Count == 0,
            "expression state retains no wearer User references");
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<int>>()
            .All(v => !v.VariableName.Value.EndsWith("Revision", StringComparison.Ordinal)),
            "expression state retains no input revisions");
        var core = Descendant(expressions, "Core");
        Check(core.GetComponents<DynamicValueVariable<int>>().All(v => v.VariableName.Value != "ExpressionCore/SelectionStatus"),
            "Core contains no SelectionStatus diagnostic variable");
        Check(core.GetComponents<DynamicReferenceVariable<Slot>>().Select(v => v.VariableName.Value)
            .SequenceEqual(new[] { "ExpressionCore/CurrentExpression" }),
            "Core stores only the current expression, without intermediate diagnostic references");
        foreach (string name in new[] { "PlaybackElapsed", "AnimationTime" })
        {
            var field = core.GetComponents<DynamicValueVariable<float>>()
                .Single(v => v.VariableName.Value == "ExpressionCore/" + name).Value;
            Check(field.ActiveLink == null, "playback diagnostics are written without Drive: " + name);
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
        int Count(string name) => nodes.Count(node => node.GetType().Name.Split('`')[0] == name);
        Console.WriteLine($"NATIVE: DynamicVariableValueInput={Count("DynamicVariableValueInput")}, " +
            $"DynamicVariableObjectInput={Count("DynamicVariableObjectInput")}, " +
            $"ReadDynamicValueVariable={Count("ReadDynamicValueVariable")}, ReadDynamicObjectVariable={Count("ReadDynamicObjectVariable")}, " +
            $"Children={Count("Children")}, ForEachObject={Count("ForEachObject")}, For={Count("For")}, GetChild={Count("GetChild")}, " +
            $"GetActiveUserSelf={Count("GetActiveUserSelf")}, GetActiveUser={Count("GetActiveUser")}");
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
        .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
}
