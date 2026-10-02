using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class HandGesturePermissionChecks
{
    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("Internal");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu");
        var bothToggle = menu.FindChild("Items").FindChild("Hand gestures").GetComponent<ButtonDynamicImpulseTrigger>();
        var toggles = new Dictionary<string, ButtonDynamicImpulseTrigger>();
        foreach (var side in new[] { Chirality.Left, Chirality.Right })
        {
            string hand = side.ToString();
            var item = menu.FindChild(hand + " hand gestures");
            var root = item.GetComponent<RootContextMenuItem>();
            var source = item.GetComponent<ContextMenuItemSource>();
            var toggle = item.GetComponent<ButtonDynamicImpulseTrigger>();
            Check(root.OnlyForSide.Value == side && root.Item.Target == source &&
                toggle.Target.Target == api && toggle.PressedTag.Value == "ResoPon/Expression/ToggleHandGestures/" + hand,
                hand + " toggle is exposed only on that hand's root context menu and targets this avatar");
            Check(item.Parent == menu && menu.FindChild("Items").GetComponentsInChildren<RootContextMenuItem>().Count == 0,
                "individual toggles stay outside the shared submenu");
            var color = item.GetComponent<ValueOptionDescriptionDriver<bool>>();
            var permission = expressions.FindChild("DV").FindChild("AllowHandGestures." + hand).GetComponent<DynamicValueVariable<bool>>();
            Check(color.Value.Target == permission.Value, hand + " menu color follows its own avatar's permission field");
            toggles.Add(hand, toggle);
        }
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
            bothToggle.Pressed(null, default); State(!left, !right); Preserved(); await Frames();
            bothToggle.Pressed(null, default); State(left, right); Preserved();
            toggles["Left"].Pressed(null, default); State(!left, right); Preserved(); await Frames();
            toggles["Right"].Pressed(null, default); State(!left, !right); Preserved(); await Frames();
            bothToggle.Pressed(null, default); State(left, right); Preserved();
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
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        State(true, true); await Frames();
        Console.WriteLine("PASS: per-hand gesture permissions, both/individual toggles, side-filtered root menus and keyboard bypass");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
