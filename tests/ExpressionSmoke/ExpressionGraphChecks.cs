using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

internal static class ExpressionGraphChecks
{
    // A whole legacy controller fit in one large group. Keep the new boards bounded
    // independently of the number of expressions, outputs, and gesture table rows.
    private const int MaximumBoardNodes = 256;

    public static void CheckMenuColors(Slot expressions)
    {
        if (Descendant(expressions, "Inputs/ContextMenu") == null) return;
        var current = ExpressionTestFields.Reference<Slot>(expressions.FindChild("Internal"), "CurrentExpression");
        bool allow = Value<bool>(expressions.FindChild("Internal"), "AllowHandGestures");
        var green = new colorX(0f, 1f, 0f, 1f, Renderite.Shared.ColorProfile.Linear);
        foreach (var item in expressions.GetComponentsInChildren<ContextMenuItemSource>())
        {
            bool active;
            if (item.Slot.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>() is { } select)
            {
                var expected = select.PressedData.Reference.Target;
                active = current != null && current == expected;
                var driver = item.Slot.GetComponent<ReferenceOptionDescriptionDriver<Slot>>();
                Check(driver != null && driver.Color.IsLinkValid && driver.Color.Target == item.Color,
                    "expression menu Color uses ReferenceOptionDescriptionDriver: " + item.Slot.Name);
                Check(!driver.Label.IsLinkValid && !driver.Sprite.IsLinkValid,
                    "expression color driver preserves label and sprite bindings");
            }
            else if (item.Slot.GetComponent<ButtonDynamicImpulseTrigger>() is { } toggle &&
                toggle.PressedTag.Value == "ResoPon/Expression/ToggleHandGestures")
            {
                var driver = item.Slot.GetComponent<ValueOptionDescriptionDriver<bool>>();
                Check(driver != null && driver.Color.IsLinkValid && driver.Color.Target == item.Color,
                    "permission menu Color uses ValueOptionDescriptionDriver: " + item.Slot.Name);
                var red = new colorX(1f, 0f, 0f, 1f, Renderite.Shared.ColorProfile.Linear);
                Check(item.Color.Value.Equals(allow ? green : red), "hand gesture toggle is green when on and red when off");
                continue;
            }
            else continue;
            Check(item.Color.Value.Equals(active ? green : colorX.White),
                "only the currently selected menu option is green: " + item.Slot.Name);
        }
    }

    public static void CheckLayout(Slot expressions)
    {
        ExpressionSpaceChecks.Run(expressions);
        CheckMenuColors(expressions);
        Check(expressions.FindChild("GestureTable") == null && expressions.FindChild("DV").FindChild("GestureTable").FindChild("Logic") == null &&
            Descendant(expressions, "Inputs/ContextMenu/Logic") == null,
            "menu availability watchers and refresh board are absent");
        Check(!expressions.GetComponentsInChildren<DynamicValueVariable<bool>>().Any(v =>
            v.VariableName.Value == "ExpressionSystem.Catalog.Clip/MenuAvailable"),
            "menu availability state is absent");
        Check(expressions.GetComponentsInChildren<ContextMenuItemSource>().All(item =>
            item.EnabledField.ActiveLink == null), "menu item Enabled fields have no automatic drivers");
        Check(Descendant(expressions, "Inputs/HandGestures").GetComponent<Comment>()?.Text.Value
            .Contains("Copyright (c) 2022-2025 rhenium, kazu0617, orange") == true,
            "controller source license survives generation, clone and package reload");
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        Report(expressions);
        ExpressionLayoutChecks.CheckDirection(expressions);
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        Check(nodes.All(n => n.GetType().Name is not ("GetActiveUser" or "LocalUser")),
            "expression boards do not substitute LocalUser for the active user");
        bool NeedsUser(ProtoFluxNode node) => node.GetType().Name is "TouchController" or "IndexController" or
            "ViveController" or "WindowsMRController" or "CosmosController" or "UserFingerPoseSource";
        foreach (var sensor in nodes.Where(NeedsUser))
            Check(((ISyncRef)VrmToResonitePackage.Expressions.ExpressionFlux.Member(sensor, "User"))
                .Target?.GetType().Name == "GetActiveUserSelf", "user sensors read GetActiveUserSelf: " + sensor.GetType().Name);
        foreach (var wearer in nodes.Where(n => n.GetType().Name == "GetActiveUserSelf"))
        {
            var consumers = nodes.Where(n => n.AllInputs.Any(input => input.Target == wearer)).ToArray();
            Check(consumers.Length > 0 && consumers.All(NeedsUser),
                "GetActiveUserSelf is used only by sensors requiring a User");
        }
        foreach (string flag in new[] { "AvatarWorn", "AvatarWornLocal" })
            Check(expressions.GetComponentsInChildren<GlobalValue<string>>().Any(v => v.Value.Value == "modular_avatar/" + flag),
                "expression boards read identification flag: " + flag);
        Check(nodes.Count > 0 && nodes.GroupBy(n => n.Slot).All(g => g.Count() == 1), "one Flux node per slot");
        Check(!expressions.GetComponentsInChildren<DynamicValueVariable<int>>().Any(v =>
            v.VariableName.Value == "ExpressionSystem/PairIndex"), "numeric PairIndex state is absent");
        foreach (string path in new[] { "Internal/Selection" })
        {
            var lookupNodes = Descendant(expressions, path).GetComponentsInChildren<ProtoFluxNode>();
            Check(lookupNodes.All(node =>
                !node.GetType().GetGenericArguments().Contains(typeof(int)) ||
                node.GetType().Name is not ("ValueMul`1" or "ValueDiv`1" or "ValueMod`1")),
                "pair lookup does not pack or unpack a numeric table index: " + path);
            Check(lookupNodes.All(node => node.GetType().Name != "ConcatenateString"),
                "pair lookup uses no string Add nodes: " + path);
            var formatter = lookupNodes.OfType<Nodes.Strings.FormatString>().Single(node =>
                (node.Format.Target as Nodes.ValueObjectInput<string>)?.Value.Value ==
                "ExpressionSystem/GestureTable.L{0}R{1}");
            Check(formatter.Parameters.Count == 2 && formatter.Parameters.All(p => p is Nodes.Box<int>),
                "pair lookup formats two gesture integers into the full variable path: " + path);
        }
        Check(Descendant(expressions, "Internal/Selection").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => !n.GetType().Name.StartsWith("FireOnLocal", StringComparison.Ordinal)),
            "gesture selection has no state-change monitors");
        Check(Descendant(expressions, "API/Receivers/Logic/Select").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => n is not Nodes.Strings.FormatString), "direct selection does not look up gesture pairs");
        Check(nodes.All(n => n.Group?.IsValid == true), "all expression Flux groups are valid");
        Check(nodes.All(n => n.Slot.Parent.GetComponents<ProtoFluxNode>().Count == 0), "Flux nodes belong to logic boards, not other nodes");
        Check(nodes.Select(n => n.Slot.GlobalPosition).Distinct().Count() == nodes.Count, "Flux node positions do not overlap across logic boards");

        Slot Board(ProtoFluxNode node) => node.Slot.Parent;
        var updates = nodes.Where(n => n.GetType().Name == "LocalUpdate").ToArray();
        Check(updates.Length == 0, "expression system contains no LocalUpdate");
        Check(nodes.All(n => n.GetType().Name != "ReferenceDrive`1"), "no playback reference drives");
        Check(nodes.Where(n => n.GetType().Name == "ValueFieldDrive`1").All(n =>
            Board(n).Name == "Tracking" && Board(n).Parent.Parent == expressions.FindChild("Outputs")),
            "continuous drives are limited to live tracking");
        Check(nodes.Any(n => n.GetType().Name.StartsWith("FireOnLocal", StringComparison.Ordinal)),
            "state transitions use local change detectors");
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<bool>>()
            .All(v => v.VariableName.Value != "ExpressionSystem.Input.Keyboard/Held"),
            "keyboard edge state is local to its change detector");
        foreach (var output in expressions.FindChild("Outputs").Children)
        {
            Check(!output.ExpressionVariables<DynamicValueVariable<bool>>().Any(v => v.VariableName.Value == "ExpressionSystem.Output/HasPose") &&
                !output.ExpressionVariables<DynamicValueVariable<float>>().Any(v => v.VariableName.Value == "ExpressionSystem.Output/Pose"),
                "outputs keep no presence flag or copied pose value");
            Check(output.ExpressionVariables<DynamicReferenceVariable<Slot>>().Count(v => v.VariableName.Value == "ExpressionSystem.Output/Binding") == 0,
                "outputs retain no per-shape binding references");
            bool tracked = output.ExpressionVariables<DynamicReferenceVariable<ISyncRef>>()
                .Any(v => v.VariableName.Value == "ExpressionSystem.Output/OriginalDriver");
            var outputNodes = output.GetComponentsInChildren<ProtoFluxNode>();
            Check(outputNodes.All(n => !n.GetType().Name.StartsWith("FireOnLocal", StringComparison.Ordinal) &&
                !n.GetType().Name.StartsWith("DynamicImpulse", StringComparison.Ordinal)),
                "outputs have no per-shape change monitors or impulses");
            Check(tracked ? outputNodes.Count(n => n.GetType().Name == "ValueFieldDrive`1") == 1 : outputNodes.Count == 0,
                "only outputs with original tracking have a live mixing graph");
            var result = output.ExpressionVariables<DynamicField<float>>()
                .Single(v => v.VariableName.Value == "ExpressionSystem.Output/Result").TargetField.Target;
            Check((result.ActiveLink != null) == tracked, "only tracked Result fields have a drive");
            Check(output.GetComponent<ValueCopy<float>>() == null,
                "outputs use no ValueCopy: " + output.Name);
            Check(!output.ExpressionVariables<DynamicValueVariable<float>>().Any(v => v.VariableName.Value == "ExpressionSystem.Output/Result"),
                "Result is a field view, not duplicate stored state: " + output.Name);
            Check(output.FindChild("DV").Children.All(s => s.Name is not ("Path" or "Shape" or "Target" or "Baseline")),
                "outputs do not generate unused metadata variables");
            var target = ExpressionTestFields.OutputTarget(output);
            var renderer = target.FindNearestParent<SkinnedMeshRenderer>();
            if (renderer != null)
            {
                var smooth = result.Parent as SmoothValue<float>;
                var entry = smooth?.Value.Target?.Parent as DynamicBlendShapeDriver.BlendShape;
                var meshDriver = entry?.FindNearestParent<DynamicBlendShapeDriver>();
                Check(smooth != null && result == smooth.TargetValue && smooth.Value.IsLinkValid &&
                    !smooth.WriteBack.Value && smooth.Speed.Value > 0 && entry != null && entry.Value == smooth.Value.Target &&
                    meshDriver?.Renderer.Target == renderer && entry._drive.Target == target && entry._drive.IsLinkValid &&
                    renderer.TryGetBlendShape(entry.BlendShapeName.Value) == target,
                    $"Result targets SmoothValue, which drives the correct renderer's named blendshape: {output.Name}");
            }
            else Check(result == target, "standalone field outputs are driven directly");
        }
        var meshDrivers = expressions.GetComponentsInChildren<DynamicBlendShapeDriver>();
        var meshOutputs = expressions.FindChild("Outputs").Children
            .Select(o => ExpressionTestFields.OutputTarget(o).FindNearestParent<SkinnedMeshRenderer>())
            .Where(r => r != null).ToArray();
        Check(meshDrivers.Select(d => d.Renderer.Target).Distinct().Count() == meshDrivers.Count &&
            meshDrivers.Count == meshOutputs.Distinct().Count(), "exactly one driver per output renderer");
        Check(expressions.GetComponentsInChildren<SmoothValue<float>>().Count == meshOutputs.Length,
            "exactly one SmoothValue per mesh output");
        foreach (var meshDriver in meshDrivers)
            Check(meshDriver.BlendShapes.Count == meshOutputs.Count(r => r == meshDriver.Renderer.Target),
                "mesh driver contains only required shape entries");
        Check(nodes.All(n => n.GetType().Name != "GetSlotActive"), "clip state never gates selection or playback");
        Check(!expressions.GetComponentsInChildren<DynamicValueVariable<bool>>().Any(v =>
            v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Enabled") &&
            !expressions.GetComponentsInChildren<DynamicValueVariable<string>>().Any(v =>
                v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Source"),
            "clips and templates contain neither Enabled nor unused Source metadata");
        Check(expressions.GetComponentsInChildren<Nodes.ValueObjectInput<string>>().All(v =>
            v.Value.Value is not "ExpressionSystem.Catalog.Clip/Enabled" and not "ExpressionSystem.Catalog.Clip/Source"),
            "graphs do not read or write removed clip fields");
        Check(nodes.All(n => n.GetType().Name is not "SampleValueAnimationTrack`1" and not "FindAnimationTrackIndex"),
            "no runtime animation samplers or track lookup");
        Check(expressions.GetComponentsInChildren<StaticAnimationProvider>().Count == 0 &&
            expressions.GetComponentsInChildren<AssetLoader<Animation>>().Count == 0 &&
            expressions.GetComponentsInChildren<DynamicReferenceVariable<IAssetProvider<Animation>>>().Count == 0,
            "expressions contain no animation providers, loaders or asset references");
        foreach (var entry in expressions.FindChild("Catalog").Children)
        {
            Check(entry.FindChild("Bindings") == null &&
                !entry.ExpressionVariables<DynamicReferenceVariable<Slot>>().Any(v => v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Bindings"),
                "Clip owns binding values without a separate space or reference");
            Check(entry.FindChild("DV").FindChild("Binding") != null,
                "each clip has a Binding group under DV");
            var keys = expressions.FindChild("Outputs").Children.Select(o =>
                o.ExpressionVariables<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "ExpressionSystem.Output/Id").Value.Value).ToHashSet();
            const string prefix = "ExpressionSystem.Catalog.Clip/Binding.";
            foreach (var binding in entry.ExpressionVariables<DynamicValueVariable<float>>())
                Check(binding.VariableName.Value.StartsWith(prefix, StringComparison.Ordinal) &&
                    binding.Slot.Name == binding.VariableName.Value["ExpressionSystem.Catalog.Clip/".Length..] &&
                    keys.Contains(binding.VariableName.Value[prefix.Length..]) && DynamicVariableHelper.IsValidName(binding.Slot.Name),
                    "each clip-scoped binding matches a readable output key");
        }
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
                new[] { "Tag", "Control", "Shift" }.Concat(Enumerable.Range(0, 10).Select(i => "Key." + i))),
                "keyboard settings expose only the hand tag, shared modifiers and ten indexed keys");
        }
        foreach (var group in nodes.GroupBy(n => n.Group))
        {
            var owners = group.Select(Board).Distinct().ToArray();
            Check(owners.Length == 1, "Flux group stays inside one board: " +
                string.Join(", ", owners.Select(b => RelativePath(expressions, b))));
        }

        foreach (string path in new[] { "Internal/Lifecycle", "Internal/Selection", "Internal/Playback",
            "API/Receivers/Logic/Left", "API/Receivers/Logic/Right",
            "API/Receivers/Logic/KeyboardLeft", "API/Receivers/Logic/KeyboardRight",
            "API/Receivers/Logic/Select", "API/Receivers/Logic/AllowHandGestures", "API/Receivers/Logic/ToggleHandGestures", "API/Receivers/Logic/Reset" })
        {
            var board = Descendant(expressions, path);
            Check(board != null && boards.Any(g => g.Key == board), "independent logic board exists: " + path);
        }

        foreach (var board in boards.OrderBy(b => RelativePath(expressions, b.Key), StringComparer.Ordinal))
        {
            string path = RelativePath(expressions, board.Key);
            Check(board.Key.Children.All(child => child.GetComponents<ProtoFluxNode>().Count == 1),
                "logic boards contain node slots directly without section slots: " + path);
            Check(board.Count() <= MaximumBoardNodes, $"board node budget ({MaximumBoardNodes}): {path} has {board.Count()}");

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
        Check(publicReceivers.Length == 8, "exactly eight public API receivers");
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Name == hand);
            Check(receiver.GetType().Name == "DynamicImpulseReceiverWithValue`1" &&
                receiver.GetType().GetGenericArguments().SequenceEqual(new[] { typeof(int) }),
                hand + " receives int directly");
            Check(receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ResoPon/Expression/Gesture/" + hand,
                hand + " uses the exact hand Tag");
        }
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Name == "Keyboard" + hand);
            Check(receiver.GetType().GetGenericArguments().Single() == typeof(int) &&
                receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ResoPon/Expression/Keyboard/" + hand,
                "keyboard has a separate int receiver: " + hand);
        }
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "Select").GetType().GetGenericArguments().Single() == typeof(Slot),
            "direct Catalog selection receives a Slot");
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "AllowHandGestures").GetType().GetGenericArguments().Single() == typeof(bool),
            "input permission receives a bool");
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "Reset").GetType().Name == "DynamicImpulseReceiver",
            "reset receives an impulse without a payload");
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "ToggleHandGestures").GetType().Name == "DynamicImpulseReceiver",
            "hand gesture toggle receives an impulse without a payload");
        if (Descendant(expressions, "Inputs/ContextMenu/Items") is { } menuItems)
        {
            Check(menuItems.FindChild("Allow hand gestures") == null && menuItems.FindChild("Disable hand gestures") == null,
                "separate allow and disable menu items are absent");
            var toggle = menuItems.FindChild("Hand gestures")?.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(toggle?.Target.Target == Descendant(expressions, "API/Receivers") &&
                toggle.PressedTag.Value == "ResoPon/Expression/ToggleHandGestures" &&
                menuItems.GetComponentsInChildren<ButtonDynamicImpulseTrigger>().Count(b => b.PressedTag.Value == toggle.PressedTag.Value) == 1,
                "one toggle menu targets this avatar's gesture permission API");
            var resetButton = menuItems.FindChild("Reset settings")?.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(resetButton?.Target.Target == Descendant(expressions, "API/Receivers") &&
                resetButton.PressedTag.Value == "ResoPon/Expression/Reset", "reset menu targets this avatar's reset API");
        }
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().All(v => v.VariableName.Value != "ExpressionSystem/Override"),
            "no Override Slot state is generated");
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<User>>().Count == 0,
            "expression state retains no wearer User references");
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<int>>()
            .All(v => !v.VariableName.Value.EndsWith("Revision", StringComparison.Ordinal)),
            "expression state retains no input revisions");
        var core = Descendant(expressions, "Internal");
        Check(core.ExpressionVariables<DynamicValueVariable<int>>().All(v => v.VariableName.Value != "ExpressionSystem/SelectionStatus"),
            "Core contains no SelectionStatus diagnostic variable");
        Check(core.ExpressionVariables<DynamicReferenceVariable<Slot>>().Where(v => !v.VariableName.Value.StartsWith("ExpressionSystem/References.", StringComparison.Ordinal) &&
                !v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal))
            .Select(v => v.VariableName.Value).SequenceEqual(new[] { "ExpressionSystem/CurrentExpression" }),
            "Core stores only the current expression, without intermediate diagnostic references");
        Check(!core.ExpressionVariables<DynamicValueVariable<float>>().Any(v =>
            v.VariableName.Value is "ExpressionSystem/PlaybackStart" or "ExpressionSystem/PlaybackElapsed" or "ExpressionSystem/AnimationTime"),
            "Core has no playback clocks");
        var playback = Descendant(expressions, "Internal/Playback").GetComponentsInChildren<ProtoFluxNode>();
        Check(playback.Single(n => n.GetType().Name.StartsWith("DynamicImpulseReceiver", StringComparison.Ordinal))
            .GetType().GetGenericArguments().SequenceEqual(new[] { typeof(Slot) }), "Playback receives a Slot");
        Check(Descendant(expressions, "API/Receivers/Logic/Select").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => n.GetType().Name != "Children" && !n.GetType().Name.StartsWith("ForEachObject", StringComparison.Ordinal)),
            "Select validates the supplied Slot without scanning Catalog");
        var currentWrites = nodes.Where(n => n.GetType().Name == "WriteDynamicObjectVariable`1" &&
            n.Slot.Parent.GetComponentsInChildren<Nodes.ValueObjectInput<string>>().Any(v =>
                v.Value.Value == "ExpressionSystem/CurrentExpression")).ToArray();
        Check(currentWrites.Length == 1 && currentWrites[0].Slot.Parent.Name == "Playback",
            "Playback is the sole writer of CurrentExpression");
        Check(playback.All(n => n.GetType().Name is not "WorldTimeFloat" and not "ValueMod"), "pose application has no time or loop evaluation");
        Check(playback.All(n => n.GetType().Name != "FireOnLocalObjectChange`1"),
            "CurrentExpression changes require an explicit playback impulse");
        Check(playback.Concat(expressions.FindChild("Outputs").GetComponentsInChildren<ProtoFluxNode>())
            .All(n => n.GetType().Name != "GetSlotActive"),
            "playback and tracking do not gate values on slot activity");
        foreach (var entry in expressions.FindChild("Catalog").Children)
            Check(!entry.ExpressionVariables<DynamicValueVariable<bool>>().Any(v => v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Loop") &&
                !entry.ExpressionVariables<DynamicValueVariable<float>>().Any(v => v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Duration"),
                "Catalog has no playback settings");
    }

    public static void Report(Slot expressions)
    {
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        var boards = nodes.GroupBy(n => n.Slot.Parent).ToArray();
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
        int crossed = groups.Count(g => g.Select(n => n.Slot.Parent).Distinct().Count() > 1);
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

    private static T Value<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
}
