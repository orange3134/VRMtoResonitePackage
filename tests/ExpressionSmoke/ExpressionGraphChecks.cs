using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

internal static class ExpressionGraphChecks
{
    // A whole legacy controller fit in one large group. Keep the new boards bounded
    // independently of the number of expressions, outputs, and gesture table rows.
    private const int MaximumBoardNodes = 256;

    public static void CheckMenuColors(Slot expressions)
    {
        if (Descendant(expressions, "Inputs/ContextMenu") == null) return;
        var current = ExpressionTestFields.Reference<Slot>(expressions.FindChild("API").FindChild("Internal"), "CurrentExpression");
        var green = new colorX(0f, 1f, 0f, 1f, Renderite.Shared.ColorProfile.Linear);
        foreach (var item in expressions.GetComponentsInChildren<ContextMenuItemSource>())
        {
            bool active;
            if (item.Slot.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>() is { } select)
            {
                var payload = item.Slot.GetComponent<BooleanReferenceDriver<Slot>>();
                var expected = payload.FalseTarget.Target;
                active = current != null && current == expected;
                Check(payload.TargetReference.Target == select.PressedData.Reference && payload.TrueTarget.Target == null &&
                    select.PressedData.Reference.Target == (active ? null : expected),
                    "selected menu sends null; other entries send their expression: " + item.Slot.Name);
                var driver = item.Slot.GetComponent<ReferenceOptionDescriptionDriver<Slot>>();
                Check(driver != null && driver.Color.IsLinkValid && driver.Color.Target == item.Color,
                    "expression menu Color uses ReferenceOptionDescriptionDriver: " + item.Slot.Name);
                Check(!driver.Label.IsLinkValid && !driver.Sprite.IsLinkValid,
                    "expression color driver preserves label and sprite bindings");
            }
            else if (item.Slot.GetComponent<ButtonToggle>() is { } direct)
            {
                CheckPermissionColor(item, direct.TargetValue.Target.Value);
                continue;
            }
            else if (item.Slot.GetComponent<ButtonDynamicImpulseTrigger>() is { } toggle &&
                toggle.PressedTag.Value.StartsWith("ExpressionSystem/Method.ToggleHandGestures", StringComparison.Ordinal))
            {
                bool allow = toggle.PressedTag.Value == "ExpressionSystem/Method.ToggleHandGestures"
                    ? ExpressionTestFields.BothHandsAllowed(expressions)
                    : ExpressionTestFields.HandGesturesAllowed(expressions, toggle.PressedTag.Value.Split('/').Last());
                CheckPermissionColor(item, allow);
                continue;
            }
            else continue;
            Check(item.Color.Value.Equals(active ? green : colorX.White),
                "only the currently selected menu option is green: " + item.Slot.Name);
        }
        void CheckPermissionColor(ContextMenuItemSource item, bool allow)
        {
            var driver = item.Slot.GetComponent<ValueOptionDescriptionDriver<bool>>();
            Check(driver != null && driver.Color.IsLinkValid && driver.Color.Target == item.Color,
                "permission menu Color uses ValueOptionDescriptionDriver: " + item.Slot.Name);
            var red = new colorX(1f, 0f, 0f, 1f, Renderite.Shared.ColorProfile.Linear);
            Check(item.Color.Value.Equals(allow ? green : red), "hand gesture toggle is green when on and red when off");
        }
    }

    public static void CheckLayout(Slot expressions)
    {
        ExpressionSpaceChecks.Run(expressions);
        CheckMenuColors(expressions);
        foreach (string hand in new[] { "Left", "Right" })
        if (Descendant(expressions, "Inputs/ContextMenu/" + hand + "/Items") is { } groups)
        {
            Check(groups.FindChild("Direct selection") == null,
                "expression groups open directly from Expressions without an intermediate selection menu");
            foreach (var group in groups.Children.Where(group => group.Name is "Hand sign expressions" or "Other expressions"))
            {
                var entries = group.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target;
                Check(entries == group.FindChild("Items") && entries.Children.Count > 1,
                    "expression groups use their own nonempty item roots");
                var back = entries.FindChild("Back");
                Check(back?.GetComponent<ContextMenuItemSource>()?.Label.Value == "Back" &&
                    back.GetComponent<ContextMenuSubmenu>()?.ItemsRoot.Target == groups &&
                    !back.GetComponent<ContextMenuSubmenu>().SearchWholeHierarchy.Value,
                    "Back uses a Submenu referencing the local Expressions items after cloning and reload");
                Check(back.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>() == null,
                    "Back navigates without selecting an expression");
                foreach (var item in entries.Children.Where(item => item != back))
                {
                    var target = item.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
                    Check(target == null || target.Parent == expressions.FindChild("Catalog"),
                        "grouped menu buttons reference the local Catalog after cloning and reload");
                    var labelCopy = item.GetComponent<ValueCopy<string>>();
                    Check(labelCopy.Source.Target == target?.GetComponent<ContextMenuItemSource>()?.Label &&
                        labelCopy.Target.Target == item.GetComponent<ContextMenuItemSource>().Label,
                        "grouped menu labels follow their local Catalog entries");
                }
            }
        }
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
        Check(expressions.FindChild("Inputs").GetComponentsInChildren<ProtoFluxNode>().All(n => n.GetType().Name != "OnStart"),
            "input modules run on changes without OnStart impulses");
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        Report(expressions);
        ExpressionLayoutChecks.CheckDirection(expressions);
        var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<string>>().All(v =>
                v.VariableName.Value != "ExpressionSystem/PairKey") &&
            expressions.GetComponentsInChildren<GlobalValue<string>>().All(v => v.Value.Value != "ExpressionSystem/PairKey") &&
            nodes.OfType<Nodes.ValueObjectInput<string>>().All(v => v.Value.Value != "ExpressionSystem/PairKey"),
            "PairKey variable and graph references are absent after generation, clone and reload");
        Check(nodes.OfType<Nodes.ValueObjectInput<string>>()
            .All(node => node.Value.Value != "ResoPon/Expression/Internal/Initialize") &&
            expressions.GetComponentsInChildren<GlobalValue<string>>()
                .All(value => value.Value.Value != "ResoPon/Expression/Internal/Initialize"),
            "removed Initialize API has no senders or receivers, including clones and package reloads");
        Check(nodes.OfType<Nodes.If>().All(node =>
            node.Condition.Target is not Nodes.ValueInput<bool> constant || !constant.Value.Value),
            "generated Flux has no If branches with a constant true condition, including clones and package reloads");
        Check(nodes.All(n => n.GetType().Name is not ("GetActiveUser" or "LocalUser")),
            "expression boards do not substitute LocalUser for the active user");
        bool NeedsUser(ProtoFluxNode node) => node.GetType().Name is "TouchController" or "IndexController" or
            "ViveController" or "WindowsMRController" or "CosmosController" or "UserFingerPoseSource" or "UserVR_Active";
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
        {
            Check(expressions.GetComponentsInChildren<GlobalValue<string>>().Any(v => v.Value.Value == "modular_avatar/" + flag),
                "expression boards read identification flag: " + flag);
            Check(Descendant(expressions, "API/Method").GetComponentsInChildren<GlobalValue<string>>()
                .All(value => value.Value.Value != "modular_avatar/" + flag),
                "API contains no wearer flag binding after generation, clone and reload: " + flag);
        }
        Check(nodes.Count > 0 && nodes.GroupBy(n => n.Slot).All(g => g.Count() == 1), "one Flux node per slot");
        Check(!expressions.GetComponentsInChildren<DynamicValueVariable<int>>().Any(v =>
            v.VariableName.Value == "ExpressionSystem/PairIndex"), "numeric PairIndex state is absent");
        foreach (string path in new[] { "API/Internal/UpdateSelection" })
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
        Check(Descendant(expressions, "API/Internal/UpdateSelection").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => !n.GetType().Name.StartsWith("FireOnLocal", StringComparison.Ordinal)),
            "gesture selection has no state-change monitors");
        Check(Descendant(expressions, "API/Method/SelectExpression").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => n is not Nodes.Strings.FormatString), "direct selection does not look up gesture pairs");
        Check(nodes.All(n => n.Group?.IsValid == true), "all expression Flux groups are valid");
        Check(nodes.All(n => n.Slot.Parent.GetComponents<ProtoFluxNode>().Count == 0), "Flux nodes belong to logic boards, not other nodes");
        Check(nodes.GroupBy(n => n.Slot.Parent).All(board => board.Key.LocalPosition == float3.Zero &&
            board.Select(n => n.Slot.LocalPosition).Distinct().Count() == board.Count()),
            "Flux logic parents stay at local zero and node positions remain distinct within each board");

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
                var entry = (smooth?.Value.Target ?? result).Parent as DynamicBlendShapeDriver.BlendShape;
                var meshDriver = entry?.FindNearestParent<DynamicBlendShapeDriver>();
                var original = output.ExpressionVariables<DynamicReferenceVariable<ISyncRef>>()
                    .SingleOrDefault(v => v.VariableName.Value == "ExpressionSystem.Output/OriginalDriver")?.Reference.Target;
                bool blink = original?.Parent is EyeLinearDriver.Eye eye && original == eye.OpenCloseTarget;
                Check(blink ? smooth == null && entry?.Value == result :
                    smooth != null && result == smooth.TargetValue && smooth.Value.IsLinkValid &&
                    !smooth.WriteBack.Value && smooth.Speed.Value > 0 && entry?.Value == smooth.Value.Target,
                    $"Only blink mesh outputs bypass smoothing: {output.Name}");
                Check(entry != null &&
                    meshDriver?.Renderer.Target == renderer && entry._drive.Target == target && entry._drive.IsLinkValid &&
                    renderer.TryGetBlendShape(entry.BlendShapeName.Value) == target,
                    $"Result drives the correct renderer's named blendshape: {output.Name}");
            }
            else Check(result == target, "standalone field outputs are driven directly");
        }
        var meshDrivers = expressions.GetComponentsInChildren<DynamicBlendShapeDriver>();
        var meshOutputs = expressions.FindChild("Outputs").Children
            .Select(o => ExpressionTestFields.OutputTarget(o).FindNearestParent<SkinnedMeshRenderer>())
            .Where(r => r != null).ToArray();
        Check(meshDrivers.Select(d => d.Renderer.Target).Distinct().Count() == meshDrivers.Count &&
            meshDrivers.Count == meshOutputs.Distinct().Count(), "exactly one driver per output renderer");
        int directMeshOutputs = expressions.FindChild("Outputs").Children.Count(o =>
            o.ExpressionVariables<DynamicField<float>>().Single(v => v.VariableName.Value == "ExpressionSystem.Output/Result")
                .TargetField.Target.Parent is DynamicBlendShapeDriver.BlendShape);
        Check(expressions.GetComponentsInChildren<SmoothValue<float>>().Count == meshOutputs.Length - directMeshOutputs,
            "exactly one SmoothValue per non-blink mesh output");
        foreach (var meshDriver in meshDrivers)
            Check(meshDriver.BlendShapes.Count == meshOutputs.Count(r => r == meshDriver.Renderer.Target),
                "mesh driver contains only required shape entries");
        Check(nodes.All(n => n.GetType().Name != "GetSlotActive"), "clip state never gates selection or playback");
        Check(!expressions.GetComponentsInChildren<DynamicValueVariable<bool>>().Any(v =>
            v.VariableName.Value == "ExpressionSystem.Catalog.Clip/Enabled") &&
            !expressions.GetComponentsInChildren<DynamicValueVariable<string>>().Any(v =>
                v.VariableName.Value is "ExpressionSystem.Catalog.Clip/Source" or "ExpressionSystem.Catalog.Clip/Id" or "ExpressionSystem.Catalog.Clip/DisplayName"),
            "clips contain no metadata variables");
        Check(expressions.GetComponentsInChildren<Nodes.ValueObjectInput<string>>().All(v =>
            v.Value.Value is not "ExpressionSystem.Catalog.Clip/Enabled" and not "ExpressionSystem.Catalog.Clip/Source" and
                not "ExpressionSystem.Catalog.Clip/Id" and not "ExpressionSystem.Catalog.Clip/DisplayName"),
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
            Check(entry.FindChild("DV") != null && entry.FindChild("DV").FindChild("Binding") == null,
                "each clip stores values directly under DV without a Binding group");
            Check(entry.GetComponent<ContextMenuItemSource>() is not { } menuItem || menuItem.Label.ActiveLink == null,
                "clip menu labels are directly editable without a variable driver");
            var keys = expressions.FindChild("Outputs").Children.Select(o =>
                o.ExpressionVariables<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "ExpressionSystem.Output/Id").Value.Value).ToHashSet();
            const string prefix = "ExpressionSystem.Catalog.Clip/";
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
                new[] { "Tag", "Modifier" }.Concat(Enumerable.Range(0, 10).Select(i => "Key." + i))),
                "keyboard settings expose only the hand tag, modifier key and ten indexed keys");
        }
        foreach (string side in new[] { "Left", "Right" })
        {
            var indexBoard = Descendant(expressions, "Inputs/HandGestures/Index/" + side + "/Logic");
            var selector = indexBoard.GetComponentsInChildren<Nodes.Utility.IndexOfFirstValueMatch<bool>>().Single();
            var neutral = indexBoard.GetComponentsInChildren<Nodes.Operators.NOR_Multi_Bool>().Single();
            Check(selector.Values.Count == 8 && selector.Values[0] == neutral && neutral.Operands.Count == 7,
                "Index selector has explicit Neutral followed by the seven hand shapes");
            var bits = indexBoard.GetComponentsInChildren<ProtoFluxNode>().Single(node => node.GetType().Name == "ComposeBits_ushort");
            string[] fingers = { "IndexFinger", "MiddleFinger", "RingFinger", "Pinky", "Thumb" };
            for (int finger = 0; finger < 5; finger++)
            for (int state = 0; state < 2; state++)
                Check(((ISyncRef)ExpressionFlux.Member(bits, "Bit" + (2 * finger + state))).Target is ProtoFluxNode comparison &&
                    comparison.Slot.Name.EndsWith(fingers[finger] + (state == 0 ? " Closed" : " Open"), StringComparison.Ordinal),
                    "Index open/closed comparisons connect directly to all ten packed bits");
            Check(Enumerable.Range(10, 6).All(bit => ((ISyncRef)ExpressionFlux.Member(bits, "Bit" + bit)).Target == null),
                "Index leaves the upper six ushort bits unused");
            Check(neutral.Operands.SequenceEqual(selector.Values.Skip(1)),
                "Index Neutral reuses the seven packed gesture comparisons");
        }
        foreach (var group in nodes.GroupBy(n => n.Group))
        {
            var owners = group.Select(Board).Distinct().ToArray();
            Check(owners.Length == 1, "Flux group stays inside one board: " +
                string.Join(", ", owners.Select(b => RelativePath(expressions, b))));
        }

        foreach (string path in new[] { "API/Internal/ResetState", "API/Internal/UpdateSelection", "API/Internal/ApplyExpression",
            "API/Method/TrySetGesture.Left", "API/Method/TrySetGesture.Right",
            "API/Method/SetGesture.Left", "API/Method/SetGesture.Right",
            "API/Method/SelectExpression", "API/Method/ToggleHandGestures", "API/Method/Reset" })
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
        foreach (var module in Descendant(expressions, "Inputs/HandGestures").Children)
            foreach (string hand in new[] { "Left", "Right" })
                Check(boards.Any(b => b.Key == Descendant(module, hand + "/Logic")),
                    "controller hand has an independent board: " + module.Name + "/" + hand);
        var pending = new Stack<Slot>(); pending.Push(expressions);
        while (pending.TryPop(out var slot))
        {
            Check(slot.Name != "Command", "expression API contains no Command slots");
            foreach (var child in slot.Children) pending.Push(child);
        }
        var method = Descendant(expressions, "API/Method");
        Check(ExpressionTestFields.Reference<Slot>(expressions, "References.API") == expressions.FindChild("API"),
            "shared API reference targets this avatar's API root after generation, clone and reload");
        Check(expressions.FindChild("Method") == null && expressions.FindChild("Internal") == null &&
            expressions.FindChild("API").Children.Select(child => child.Name).OrderBy(name => name)
                .SequenceEqual(new[] { "Internal", "Method" }) &&
            method.FindChild("Receivers") == null && method.Children.Count == 7,
            "API contains Internal and Method, with seven direct public operation boards");
        var publicReceivers = method.GetComponentsInChildren<ProtoFluxNode>()
            .Where(node => node.GetType().Name.StartsWith("DynamicImpulseReceiver", StringComparison.Ordinal)).ToArray();
        Check(publicReceivers.Length == 7, "exactly seven public API receivers");
        foreach (string removed in new[] { "AllowHandGestures", "AllowHandGesturesLeft", "AllowHandGesturesRight",
            "ToggleHandGesturesLeft", "ToggleHandGesturesRight" })
            Check(Descendant(expressions, "API/Method/" + removed) == null,
                "unused permission API board is absent: " + removed);
        var tags = expressions.GetComponentsInChildren<GlobalValue<string>>().Select(value => value.Value.Value)
            .Concat(nodes.OfType<Nodes.ValueObjectInput<string>>().Select(node => node.Value.Value));
        Check(tags.All(tag => tag == null ||
            (!tag.StartsWith("ResoPon/Expression/AllowHandGestures", StringComparison.Ordinal) &&
             !tag.StartsWith("ExpressionSystem/Method.ToggleHandGestures.", StringComparison.Ordinal))),
            "removed permission APIs have no senders or receivers after generation, clone and reload");
        Check(tags.All(tag => tag?.StartsWith("ResoPon/Expression/", StringComparison.Ordinal) != true),
            "legacy expression tags are absent after generation, clone and reload");
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Name == "TrySetGesture." + hand);
            Check(receiver.GetType().Name == "DynamicImpulseReceiverWithValue`1" &&
                receiver.GetType().GetGenericArguments().SequenceEqual(new[] { typeof(int) }),
                hand + " receives int directly");
            Check(receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ExpressionSystem/Method.TrySetGesture." + hand,
                hand + " uses the exact hand Tag");
        }
        foreach (string hand in new[] { "Left", "Right" })
        {
            var receiver = publicReceivers.Single(node => node.Slot.Parent.Name == "SetGesture." + hand);
            Check(receiver.GetType().GetGenericArguments().Single() == typeof(int) &&
                receiver.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value == "ExpressionSystem/Method.SetGesture." + hand,
                "SetGesture has a shared int receiver independent of the input source: " + hand);
        }
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "SelectExpression").GetType().GetGenericArguments().Single() == typeof(Slot),
            "direct Catalog selection receives a Slot");
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "Reset").GetType().Name == "DynamicImpulseReceiver",
            "reset receives an impulse without a payload");
        Check(publicReceivers.Single(node => node.Slot.Parent.Name == "ToggleHandGestures").GetType().Name == "DynamicImpulseReceiver",
            "hand gesture toggle receives an impulse without a payload");
        foreach (string hand in new[] { "Left", "Right" })
        if (Descendant(expressions, "Inputs/ContextMenu/" + hand + "/Items") is { } menuItems)
        {
            Check(menuItems.FindChild("Allow hand gestures") == null && menuItems.FindChild("Disable hand gestures") == null,
                "separate allow and disable menu items are absent");
            var toggle = menuItems.FindChild("Hand gestures")?.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(toggle?.Target.Target == Descendant(expressions, "API") &&
                toggle.PressedTag.Value == "ExpressionSystem/Method.ToggleHandGestures" &&
                menuItems.GetComponentsInChildren<ButtonDynamicImpulseTrigger>().Count(b => b.PressedTag.Value == toggle.PressedTag.Value) == 1,
                "one toggle menu targets this avatar's gesture permission API");
            var resetButton = menuItems.FindChild("Reset settings")?.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(resetButton?.Target.Target == Descendant(expressions, "API") &&
                resetButton.PressedTag.Value == "ExpressionSystem/Method.Reset", "reset menu targets this avatar's reset API");
        }
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().All(v => v.VariableName.Value != "ExpressionSystem/Override"),
            "no Override Slot state is generated");
        Check(expressions.GetComponentsInChildren<DynamicReferenceVariable<User>>().Count == 0,
            "expression state retains no wearer User references");
        Check(expressions.GetComponentsInChildren<DynamicValueVariable<int>>()
            .All(v => !v.VariableName.Value.EndsWith("Revision", StringComparison.Ordinal)),
            "expression state retains no input revisions");
        var core = Descendant(expressions, "API/Internal");
        Check(core.ExpressionVariables<DynamicValueVariable<int>>().All(v => v.VariableName.Value != "ExpressionSystem/SelectionStatus"),
            "Core contains no SelectionStatus diagnostic variable");
        Check(core.ExpressionVariables<DynamicReferenceVariable<Slot>>().Where(v => !v.VariableName.Value.StartsWith("ExpressionSystem/References.", StringComparison.Ordinal) &&
                !v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal))
            .Select(v => v.VariableName.Value).SequenceEqual(new[] { "ExpressionSystem/CurrentExpression" }),
            "Core stores only the current expression, without intermediate diagnostic references");
        Check(!core.ExpressionVariables<DynamicValueVariable<float>>().Any(v =>
            v.VariableName.Value is "ExpressionSystem/PlaybackStart" or "ExpressionSystem/PlaybackElapsed" or "ExpressionSystem/AnimationTime"),
            "Core has no playback clocks");
        var playback = Descendant(expressions, "API/Internal/ApplyExpression").GetComponentsInChildren<ProtoFluxNode>();
        Check(playback.Single(n => n.GetType().Name.StartsWith("DynamicImpulseReceiver", StringComparison.Ordinal))
            .GetType().GetGenericArguments().SequenceEqual(new[] { typeof(Slot) }), "Playback receives a Slot");
        Check(Descendant(expressions, "API/Method/SelectExpression").GetComponentsInChildren<ProtoFluxNode>()
            .All(n => n.GetType().Name != "Children" && !n.GetType().Name.StartsWith("ForEachObject", StringComparison.Ordinal)),
            "Select validates the supplied Slot without scanning Catalog");
        var currentWrites = nodes.Where(n => n.GetType().Name == "WriteDynamicObjectVariable`1" &&
            n.Slot.Parent.GetComponentsInChildren<Nodes.ValueObjectInput<string>>().Any(v =>
                v.Value.Value == "ExpressionSystem/CurrentExpression")).ToArray();
        Check(currentWrites.Length == 1 && currentWrites[0].Slot.Parent.Name == "ApplyExpression",
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
