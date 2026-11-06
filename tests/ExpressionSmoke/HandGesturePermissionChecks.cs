using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

internal static class HandGesturePermissionChecks
{
    private static readonly (string Label, bool Left, bool Right)[] Options =
    {
        ("Both hands enabled", true, true), ("Left hand only", true, false),
        ("Right hand only", false, true), ("Both hands disabled", false, false)
    };

    private static void PressOption(Slot items, string label)
    {
        foreach (var set in items.FindChild("Hand gesture options").FindChild("Items").FindChild(label)
            .GetComponents<ButtonValueSet<bool>>()) set.Pressed(null, default);
    }

    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("API").FindChild("Internal");
        var api = expressions.FindChild("API");
        CheckToggleGraph(api.FindChild("Method").FindChild("ToggleHandGestures"));
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu");
        var root = menu.GetComponent<RootContextMenuItem>();
        var source = menu.GetComponent<ContextMenuItemSource>();
        var items = menu.FindChild("Items");
        Check(menu.GetComponentsInChildren<RootContextMenuItem>().Count == 1 && root != null &&
            menu.FindChild("Left") == null && menu.FindChild("Right") == null,
            "one shared Expressions root replaces both hand-specific menu trees");
        Check(root.OnlyForSide.Value == null && !root.ExcludePrimaryHand.Value && !root.ExcludeSecondaryHand.Value &&
            root.Item.Target == source && source.Label.Value == "Expressions" &&
            menu.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == items,
            "both hands use the same Expressions item and submenu without side restrictions");
        Check(items.FindChild("Left hand gestures") == null && items.FindChild("Right hand gestures") == null &&
            items.GetComponentsInChildren<RootContextMenuItem>().Count == 0,
            "individual toggles and extra root entries are absent");
        var group = items.FindChild("Hand gesture options");
        var options = group.FindChild("Items");
        Check(group.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == options &&
            options.Children.Count == 5 && options.FindChild("Back").GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == items,
            "gesture submenu contains four choices and a Back entry");
        foreach (var (label, left, right) in Options)
        {
            var setters = options.FindChild(label).GetComponents<ButtonValueSet<bool>>();
            Check(setters.Count == 2, "each option sets both hand permissions");
            foreach (var (targetHand, enabled) in new[] { ("Left", left), ("Right", right) })
            {
                var permission = expressions.FindChild("DV").FindChild("Config.AllowHandGesture." + targetHand)
                    .GetComponent<DynamicValueVariable<bool>>();
                Check(setters.Count(set => set.TargetValue.Target == permission.Value && set.SetValue.Value == enabled) == 1,
                    label + " sets the local avatar's " + targetHand + " permission");
            }
        }
        var bothToggle = items.FindChild("Hand gestures").GetComponent<ButtonDynamicImpulseTrigger>();
        await CheckVisibility(menu);
        void Allow(string hand, bool enabled) => SetHandGesturesAllowed(core, enabled, hand);
        void Keyboard(string hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, value);
        int Gesture(string hand) => core.ExpressionVariables<DynamicValueVariable<int>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/State." + hand + "Gesture").Value.Value;
        void State(bool left, bool right) => Check(HandGesturesAllowed(core, "Left") == left && HandGesturesAllowed(core, "Right") == right,
            $"independent permissions: Left={left}, Right={right}");
        async Task Frames() { for (int i = 0; i < 3; i++) await default(NextUpdate); ExpressionGraphChecks.CheckMenuColors(expressions); }
        for (int combination = 0; combination < 4; combination++)
        {
            bool left = (combination & 1) != 0, right = (combination & 2) != 0;
            Allow("Left", left); Allow("Right", right); State(left, right);
            Keyboard("Left", 6); Keyboard("Right", 7);
            var selected = Reference<Slot>(core, "State.CurrentExpression");
            void Preserved() => Check(Gesture("Left") == 6 && Gesture("Right") == 7 && Reference<Slot>(core, "State.CurrentExpression") == selected,
                "permission toggles retain both hand values and the selected expression");
            bool enabled = !(left && right);
            bothToggle.Pressed(null, default); State(enabled, enabled); Preserved(); await Frames();
            bothToggle.Pressed(null, default); State(!enabled, !enabled); Preserved();
            bothToggle.Pressed(null, default); State(enabled, enabled); Preserved(); await Frames();
            foreach (var (label, expectedLeft, expectedRight) in Options)
            {
                Allow("Left", left); Allow("Right", right);
                PressOption(items, label); State(expectedLeft, expectedRight); Preserved(); await Frames();
                PressOption(items, label); State(expectedLeft, expectedRight); Preserved(); await Frames();
            }
            Allow("Left", left); Allow("Right", right);
            foreach (string hand in new[] { "Left", "Right" })
            {
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, true, hand == "Left" ? 1 : 2);
            }
            Check(Gesture("Left") == (left ? 1 : 6) && Gesture("Right") == (right ? 2 : 7),
                "Gesture API gates each hand independently");
            Keyboard("Left", 4); Keyboard("Right", 5); State(left, right);
            Check(Gesture("Left") == 4 && Gesture("Right") == 5, "Keyboard API bypasses both independent permission flags");
            await Frames();
        }
        var expression = expressions.FindChild("Catalog").Children.First();
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, expression);
        State(true, true);
        SetHandGesturesAllowed(core, false);
        State(false, false);
        bothToggle.Pressed(null, default);
        State(true, true);
        Check(Reference<Slot>(core, "State.CurrentExpression") == expression, "global toggle resumes both hands without replacing the selected expression");
        await CheckApiBeforeInitialization(expressions);
        SetHandGesturesAllowed(core, true);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        State(true, true); await Frames();
        Console.WriteLine("PASS: shared menu for both hands, four gesture permission choices, desktop toggle, VR visibility and keyboard bypass");
    }

    private static async Task CheckVisibility(Slot menu)
    {
        var fixture = menu.Parent.AddSlot("Temporary menu VR sensor");
        var sensor = fixture.AttachComponent<Nodes.ValueInput<bool>>();
        var restore = new List<(ISyncRef Port, IWorldElement Target)>();
        try
        {
            var board = menu.FindChild("Gesture menu visibility");
            var vrNode = board.GetComponentsInChildren<Nodes.FrooxEngine.Users.UserVR_Active>().Single();
            foreach (var port in board.GetComponentsInChildren<ProtoFluxNode>().SelectMany(node => node.AllInputs))
            if (port.Target == vrNode)
            {
                restore.Add((port, port.Target)); port.Target = sensor;
            }
            Check(restore.Count == 2, "one visibility board drives the submenu and inverse toggle");
            foreach (bool vr in new[] { false, true, false, true })
            {
                sensor.Value.Value = vr;
                for (int i = 0; i < 5; i++) await default(NextUpdate);
                var items = menu.FindChild("Items");
                var group = items.FindChild("Hand gesture options");
                var toggle = items.FindChild("Hand gestures");
                Check((group.GetComponent<ContextMenuItemSource>(excludeDisabled: true) != null) == vr &&
                    (toggle.GetComponent<ContextMenuItemSource>(excludeDisabled: true) != null) == !vr,
                    "menu collection shows only the VR submenu or desktop toggle");
                Check(items.Children.Where(slot => slot != group && slot != toggle)
                    .All(slot => slot.GetComponent<ContextMenuItemSource>(excludeDisabled: true) != null),
                    "mode switching retains expression and reset menus");
            }
        }
        finally
        {
            foreach (var (port, target) in restore) port.Target = target;
            fixture.Destroy();
        }
        for (int i = 0; i < 5; i++) await default(NextUpdate);
    }

    private static async Task CheckApiBeforeInitialization(Slot expressions)
    {
        var core = expressions.FindChild("API").FindChild("Internal");
        var api = expressions.FindChild("API");
        var events = api.FindChild("Event").FindChild("OnAvatarWorn");
        var initialized = events.GetComponentsInChildren<Nodes.StoredValue<bool>>().Single();
        int Gesture(string hand) => core.ExpressionVariables<DynamicValueVariable<int>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/State." + hand + "Gesture").Value.Value;
        var fixture = expressions.Parent.AddSlot("Temporary uninitialized gesture fixture");
        try
        {
            // Reproduce a fresh local lifecycle with saved false permissions,
            // without waiting for OnStart/wearer notifications to initialize it.
            var g = new ExpressionFlux(fixture);
            const string prepareTag = "Test/PrepareUninitializedGesture";
            ExpressionFlux.Link(g.Receiver(prepareTag, false), "OnTriggered", g.Set<bool>(initialized, g.Constant(false)));
            // Invoke the real lifecycle event entry point without a production Initialize API.
            var onStart = events.GetComponentsInChildren<ProtoFluxNode>()
                .Single(node => node.GetType().Name == "OnStart");
            var update = ((ISyncRef)ExpressionFlux.Member(onStart, "Trigger")).Target;
            const string updateTag = "Test/UpdateLifecycle";
            ExpressionFlux.Link(g.Receiver(updateTag, false), "OnTriggered", update);
            for (int i = 0; i < 5; i++) await default(NextUpdate);
            foreach (string hand in new[] { "Left", "Right" })
            {
                foreach (string side in new[] { "Left", "Right" })
                    core.ExpressionVariables<DynamicValueVariable<bool>>()
                        .Single(v => v.VariableName.Value == "ExpressionSystem/Config.AllowHandGesture." + side).Value.Value = false;
                // Direct field edits notify Dynamic Inputs on subsequent updates.
                // Settle saved permissions before resetting the local lifecycle flag.
                for (int i = 0; i < 3; i++) await default(NextUpdate);
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(fixture, prepareTag, true) == 1,
                    "the fresh-lifecycle fixture receives its preparation impulse");
                Check(NoHandsAllowed(core), "fresh lifecycle starts with saved false permission values");
                var previous = (Gesture("Left"), Gesture("Right"), Reference<Slot>(core, "State.CurrentExpression"));
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, true, 7);
                Check(NoHandsAllowed(core) && previous ==
                    (Gesture("Left"), Gesture("Right"), Reference<Slot>(core, "State.CurrentExpression")),
                    hand + " early gesture with false permission is dropped without initialization or mutation");
                string other = hand == "Left" ? "Right" : "Left";
                int otherGesture = Gesture(other);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, 6);
                Check(NoHandsAllowed(core) && Gesture(hand) == 6 && Gesture(other) == otherGesture,
                    hand + " early keyboard request updates only that hand without initializing permissions or opposite state");
                var earlyPair = (Gesture("Left"), Gesture("Right"));
                void Preserved() => Check(earlyPair == (Gesture("Left"), Gesture("Right")),
                    "early permission APIs retain both hand values");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ToggleHandGesturesTag, true);
                Check(BothHandsAllowed(core), "early global toggle enables saved false permissions without an implicit reset");
                Preserved();
                var selected = expressions.FindChild("Catalog").Children.First();
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, selected);
                Check(BothHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == selected &&
                    Gesture("Left") == 0 && Gesture("Right") == 0,
                    "early direct selection clears hand values and selects without lifecycle initialization");
                earlyPair = (0, 0);
                Preserved();
                var items = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
                PressOption(items, hand + " hand only");
                Check(HandGesturesAllowed(core, hand) && !HandGesturesAllowed(core, other),
                    "early menu selects the requested hand without initialization");
                Preserved();
                PressOption(items, "Both hands disabled");
                Check(NoHandsAllowed(core), "early menu disables both permissions without resetting");
                Preserved();
                // ButtonValueSet writes the field; let bound Dynamic Inputs observe it.
                for (int i = 0; i < 3; i++) await default(NextUpdate);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ToggleHandGesturesTag, true);
                Check(BothHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == selected,
                    "early global toggle preserves the selected expression");
                Preserved();
                SetHandGesturesAllowed(core, false, other);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, 6);
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(fixture, updateTag, true) == 1,
                    "the fixture invokes the lifecycle event entry point");
                Check(HandGesturesAllowed(core, hand) && !HandGesturesAllowed(core, other) &&
                    Gesture("Left") == 0 && Gesture("Right") == 0,
                    "lifecycle initialization clears early input while preserving asymmetric permissions");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, true, 7);
                Check(Gesture(hand) == 7 && Gesture(other) == 0 &&
                    Reference<Slot>(core, "State.CurrentExpression") == Reference<Slot>(expressions.FindChild("DV").FindChild("GestureTable"), hand == "Left" ? "L7R0" : "L0R7"),
                    hand + " subsequent gesture selects synchronously after initialization");
            }
        }
        finally { fixture.Destroy(); }
        Console.WriteLine("PASS: public APIs do not initialize implicitly; lifecycle clears early input and preserves permissions");
    }
    private static void CheckToggleGraph(Slot board)
    {
        var local = board.GetComponentsInChildren<Nodes.LocalValue<bool>>().Single();
        var capture = board.GetComponentsInChildren<Nodes.ValueWrite<bool>>().Single();
        var nand = board.GetComponentsInChildren<Nodes.Operators.NAND_Bool>().Single();
        var writes = board.GetComponentsInChildren<Nodes.FrooxEngine.Variables.WriteDynamicValueVariable<bool>>();
        Check(capture.Variable.Target == local && capture.Value.Target == nand &&
            writes.Count == 2 && writes.All(write => write.Value.Target == local),
            "both hand writes consume the same per-impulse NAND snapshot");
        var sequence = capture.OnWritten.Target as Nodes.Sequence;
        Check(sequence != null && sequence.Calls.Count == 2 &&
            sequence.Calls.All(call => writes.Any(write => call == write)),
            "permission writes run only after the local snapshot is captured");
        Check(board.GetComponentsInChildren<Nodes.If>().Count == 0,
            "shared toggle writes the NAND snapshot without wearer or duplicated true/false branches");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
