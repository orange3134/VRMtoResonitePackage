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
        var core = expressions.FindChild("Internal");
        var api = expressions.FindChild("API").FindChild("Receivers");
        CheckToggleGraph(api.FindChild("Logic").FindChild("ToggleHandGestures"));
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu");
        var bothToggles = new List<ButtonDynamicImpulseTrigger>();
        var toggles = new Dictionary<string, ButtonDynamicImpulseTrigger>();
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
            var toggle = item.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(root.OnlyForSide.Value == side && root.Item.Target == source && source.Label.Value == "Expressions" &&
                handMenu.GetComponent<ContextMenuSubmenu>().ItemsRoot.Target == items &&
                toggle.Target.Target == api && toggle.PressedTag.Value == "ResoPon/Expression/ToggleHandGestures/" + hand,
                hand + " Expressions submenu contains its own toggle and targets this avatar");
            Check(items.FindChild((side == Chirality.Left ? "Right" : "Left") + " hand gestures") == null &&
                items.GetComponentsInChildren<RootContextMenuItem>().Count == 0,
                "individual toggles stay inside their own submenu without extra root entries");
            bothToggles.Add(items.FindChild("Hand gestures").GetComponent<ButtonDynamicImpulseTrigger>());
            var color = item.GetComponent<ValueOptionDescriptionDriver<bool>>();
            var permission = expressions.FindChild("DV").FindChild("AllowHandGestures." + hand).GetComponent<DynamicValueVariable<bool>>();
            Check(color.Value.Target == permission.Value, hand + " menu color follows its own avatar's permission field");
            toggles.Add(hand, toggle);
        }
        string[] Contents(Slot slot) => slot.GetComponentsInChildren<ContextMenuItemSource>()
            .Select(item => item.Label.Value).Where(label => label is not ("Left hand gestures" or "Right hand gestures"))
            .OrderBy(label => label, StringComparer.Ordinal).ToArray();
        Check(Contents(menu.FindChild("Left")).SequenceEqual(Contents(menu.FindChild("Right"))),
            "both hand menus contain the complete expression, imported and settings menus");
        void Allow(string hand, bool enabled) => Check(
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                ExpressionSystemSetup.HandGesturesEnabledHandTag(hand), true, enabled) == 1,
            "exactly one permission receiver for " + hand);
        void Keyboard(string hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, hand == "Left" ? ExpressionSystemSetup.KeyboardLeftTag : ExpressionSystemSetup.KeyboardRightTag, true, value);
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
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    ExpressionSystemSetup.HandGesturesEnabledHandTag(hand), true, 1) == 0,
                    "per-hand permission rejects non-bool payloads");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, hand == "Left" ? 1 : 2);
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
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.HandGesturesEnabledTag, true, true);
        State(true, true);
        Check(Reference<Slot>(core, "CurrentExpression") == expression, "global permission API resumes both hands without replacing the selected expression");
        await CheckGestureBeforeInitialization(expressions);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        State(true, true); await Frames();
        Console.WriteLine("PASS: per-hand gesture permissions, both/individual toggles, complete side-filtered submenus and keyboard bypass");
    }

    private static async Task CheckGestureBeforeInitialization(Slot expressions)
    {
        var core = expressions.FindChild("Internal");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var initialized = core.GetComponentsInChildren<Nodes.StoredValue<bool>>().Single();
        int Gesture(string hand) => core.ExpressionVariables<DynamicValueVariable<int>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/" + hand + "Gesture").Value.Value;
        string Pair() => core.ExpressionVariables<DynamicValueVariable<string>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/PairKey").Value.Value;
        var fixture = expressions.Parent.AddSlot("Temporary uninitialized gesture fixture");
        try
        {
            // Reproduce a fresh local lifecycle with saved false permissions,
            // without waiting for OnStart/wearer notifications to initialize it.
            var g = new ExpressionFlux(fixture);
            const string prepareTag = "Test/PrepareUninitializedGesture";
            ExpressionFlux.Link(g.Receiver(prepareTag, false), "OnTriggered", g.Set<bool>(initialized, g.Constant(false)));
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
                var previous = (Gesture("Left"), Gesture("Right"), Pair(), Reference<Slot>(core, "CurrentExpression"));
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, 7);
                Check(NoHandsAllowed(core) && previous ==
                    (Gesture("Left"), Gesture("Right"), Pair(), Reference<Slot>(core, "CurrentExpression")),
                    hand + " early gesture with false permission is dropped without initialization or mutation");
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(core,
                    "ResoPon/Expression/Internal/Initialize", true) == 1,
                    "the lifecycle initialization stage receives its impulse");
                Check(BothHandsAllowed(core) && Gesture("Left") == 0 && Gesture("Right") == 0 && Pair() == "L0R0",
                    "initialization resets state without replaying the dropped gesture");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    hand == "Left" ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, 7);
                Check(Gesture(hand) == 7 && Pair() == (hand == "Left" ? "L7R0" : "L0R7"),
                    hand + " subsequent gesture selects synchronously after initialization");
            }
        }
        finally { fixture.Destroy(); }
        Console.WriteLine("PASS: both gesture APIs drop early disabled input and resume after lifecycle initialization");
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
        Check(board.GetComponentsInChildren<Nodes.If>().Count == 1,
            "shared toggle keeps only the wearer gate, without duplicated true/false write branches");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
