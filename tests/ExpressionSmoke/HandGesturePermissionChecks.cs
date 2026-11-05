using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

internal static class HandGesturePermissionChecks
{
    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("API").FindChild("Internal");
        var api = expressions.FindChild("API");
        CheckToggleGraph(api.FindChild("Method").FindChild("ToggleHandGestures"));
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu");
        var bothToggles = new List<ButtonDynamicImpulseTrigger>();
        var toggles = new Dictionary<string, ButtonToggle>();
        Check(menu.GetComponentsInChildren<RootContextMenuItem>().Count == 2 &&
            menu.GetComponent<RootContextMenuItem>() == null,
            "only the two side-filtered Expressions entries are root menu items");
        foreach (var side in new[] { Chirality.Left, Chirality.Right })
        {
            string hand = side.ToString();
            var handMenu = menu.FindChild(hand);
            var root = handMenu.GetComponent<RootContextMenuItem>();
            var source = handMenu.GetComponent<ContextMenuItemSource>();
            var items = handMenu.FindChild("Items");
            var item = items.FindChild(hand + " hand gestures");
            var toggle = item.GetComponent<ButtonToggle>();
            var permission = expressions.FindChild("DV").FindChild("AllowHandGestures." + hand).GetComponent<DynamicValueVariable<bool>>();
            Check(root.OnlyForSide.Value == side && root.Item.Target == source && source.Label.Value == "Expressions" &&
                handMenu.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == items &&
                toggle != null && toggle.TargetValue.Target == permission.Value &&
                item.GetComponent<ButtonDynamicImpulseTrigger>() == null,
                hand + " Expressions submenu toggles its own avatar permission field directly");
            Check(items.FindChild((side == Chirality.Left ? "Right" : "Left") + " hand gestures") == null &&
                items.GetComponentsInChildren<RootContextMenuItem>().Count == 0,
                "individual toggles stay inside their own submenu without extra root entries");
            bothToggles.Add(items.FindChild("Hand gestures").GetComponent<ButtonDynamicImpulseTrigger>());
            var color = item.GetComponent<ValueOptionDescriptionDriver<bool>>();
            Check(color.Value.Target == permission.Value, hand + " menu color follows its own avatar's permission field");
            toggles.Add(hand, toggle);
        }
        string[] Contents(Slot slot) => slot.GetComponentsInChildren<ContextMenuItemSource>()
            .Select(item => item.Label.Value).Where(label => label is not ("Left hand gestures" or "Right hand gestures"))
            .OrderBy(label => label, StringComparer.Ordinal).ToArray();
        Check(Contents(menu.FindChild("Left")).SequenceEqual(Contents(menu.FindChild("Right"))),
            "both hand menus contain the complete expression, imported and settings menus");
        void Allow(string hand, bool enabled) => SetHandGesturesAllowed(core, enabled, hand);
        void Keyboard(string hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, value);
        int Gesture(string hand) => core.ExpressionVariables<DynamicValueVariable<int>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/" + hand + "Gesture").Value.Value;
        void State(bool left, bool right) => Check(HandGesturesAllowed(core, "Left") == left && HandGesturesAllowed(core, "Right") == right,
            $"independent permissions: Left={left}, Right={right}");
        async Task Frames() { for (int i = 0; i < 3; i++) await default(NextUpdate); ExpressionGraphChecks.CheckMenuColors(expressions); }
        for (int combination = 0; combination < 4; combination++)
        {
            bool left = (combination & 1) != 0, right = (combination & 2) != 0;
            Allow("Left", left); Allow("Right", right); State(left, right);
            Keyboard("Left", 6); Keyboard("Right", 7);
            var selected = Reference<Slot>(core, "CurrentExpression");
            void Preserved() => Check(Gesture("Left") == 6 && Gesture("Right") == 7 && Reference<Slot>(core, "CurrentExpression") == selected,
                "permission toggles retain both hand values and the selected expression");
            foreach (var bothToggle in bothToggles)
            {
                Allow("Left", left); Allow("Right", right);
                bool enabled = !(left && right);
                bothToggle.Pressed(null, default); State(enabled, enabled); Preserved(); await Frames();
                bothToggle.Pressed(null, default); State(!enabled, !enabled); Preserved();
                bothToggle.Pressed(null, default); State(enabled, enabled); Preserved(); await Frames();
            }
            Allow("Left", left); Allow("Right", right);
            toggles["Left"].Pressed(null, default); State(!left, right); Preserved(); await Frames();
            toggles["Right"].Pressed(null, default); State(!left, !right); Preserved(); await Frames();
            bothToggles[0].Pressed(null, default); State(left || right, left || right); Preserved(); await Frames();
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
        State(false, false);
        bothToggles[0].Pressed(null, default);
        State(true, true);
        Check(Reference<Slot>(core, "CurrentExpression") == expression, "global toggle resumes both hands without replacing the selected expression");
        await CheckApiBeforeInitialization(expressions);
        SetHandGesturesAllowed(core, true);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        State(true, true); await Frames();
        Console.WriteLine("PASS: per-hand gesture permissions, both/individual toggles, complete side-filtered submenus and keyboard bypass");
    }

    private static async Task CheckApiBeforeInitialization(Slot expressions)
    {
        var core = expressions.FindChild("API").FindChild("Internal");
        var api = expressions.FindChild("API");
        var events = api.FindChild("Event").FindChild("OnAvatarWorn");
        var initialized = events.GetComponentsInChildren<Nodes.StoredValue<bool>>().Single();
        int Gesture(string hand) => core.ExpressionVariables<DynamicValueVariable<int>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/" + hand + "Gesture").Value.Value;
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
                        .Single(v => v.VariableName.Value == "ExpressionSystem/AllowHandGestures." + side).Value.Value = false;
                // Direct field edits notify Dynamic Inputs on subsequent updates.
                // Settle saved permissions before resetting the local lifecycle flag.
                for (int i = 0; i < 3; i++) await default(NextUpdate);
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(fixture, prepareTag, true) == 1,
                    "the fresh-lifecycle fixture receives its preparation impulse");
                Check(NoHandsAllowed(core), "fresh lifecycle starts with saved false permission values");
                var previous = (Gesture("Left"), Gesture("Right"), Reference<Slot>(core, "CurrentExpression"));
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, true, 7);
                Check(NoHandsAllowed(core) && previous ==
                    (Gesture("Left"), Gesture("Right"), Reference<Slot>(core, "CurrentExpression")),
                    hand + " early gesture with false permission is dropped without initialization or mutation");
                string other = hand == "Left" ? "Right" : "Left";
                int otherGesture = Gesture(other);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, 6);
                Check(NoHandsAllowed(core) && Gesture(hand) == 6 && Gesture(other) == otherGesture,
                    hand + " early keyboard request updates only that hand without initializing permissions or opposite state");
                var earlyPair = (Gesture("Left"), Gesture("Right"));
                void Preserved() => Check(earlyPair == (Gesture("Left"), Gesture("Right")),
                    "early permission and selection APIs retain both hand values");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ToggleHandGesturesTag, true);
                Check(BothHandsAllowed(core), "early global toggle enables saved false permissions without an implicit reset");
                Preserved();
                var selected = expressions.FindChild("Catalog").Children.First();
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, selected);
                Check(NoHandsAllowed(core) && Reference<Slot>(core, "CurrentExpression") == selected,
                    "early direct selection disables gestures and selects without resetting hand state");
                Preserved();
                var toggle = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild(hand)
                    .FindChild("Items").FindChild(hand + " hand gestures").GetComponent<ButtonToggle>();
                toggle.Pressed(null, default);
                Check(HandGesturesAllowed(core, hand) && !HandGesturesAllowed(core, other),
                    "early per-hand menu changes only the requested permission");
                Preserved();
                toggle.Pressed(null, default);
                Check(NoHandsAllowed(core), "early per-hand toggle reads the current permission without resetting");
                Preserved();
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ToggleHandGesturesTag, true);
                Check(BothHandsAllowed(core) && Reference<Slot>(core, "CurrentExpression") == selected,
                    "early global toggle preserves the selected expression");
                Preserved();
                SetHandGesturesAllowed(core, false, other);
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(fixture, updateTag, true) == 1,
                    "the fixture invokes the lifecycle event entry point");
                Check(HandGesturesAllowed(core, hand) && !HandGesturesAllowed(core, other) &&
                    Gesture("Left") == 0 && Gesture("Right") == 0,
                    "lifecycle initialization clears early input while preserving asymmetric permissions");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, true, 7);
                Check(Gesture(hand) == 7 && Gesture(other) == 0 &&
                    Reference<Slot>(core, "CurrentExpression") == Reference<Slot>(expressions.FindChild("DV").FindChild("GestureTable"), hand == "Left" ? "L7R0" : "L0R7"),
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
