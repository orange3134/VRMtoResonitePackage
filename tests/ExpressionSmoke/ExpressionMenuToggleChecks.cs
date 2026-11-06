using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionMenuToggleChecks
{
    public static async Task Run(Slot expressions)
    {
        var api = expressions.FindChild("API");
        var core = api.FindChild("Internal");
        var catalog = expressions.FindChild("Clips");
        var outputs = expressions.FindChild("Outputs").Children.ToArray();
        var buttons = expressions.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithReference<Slot>>()
            .Where(button => button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target != null).ToArray();
        Check(buttons.Any(button => button.Slot.Name == "Imported angry"), "imported expression menu is exercised");
        var reset = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items")
            .FindChild("Reset settings").GetComponent<ButtonDynamicImpulseTrigger>();
        try
        {
            reset.Pressed(null, default);
            await Frames();
            for (int permissions = 0; permissions < 4; permissions++)
            foreach (var button in buttons)
            {
                bool left = (permissions & 1) != 0, right = (permissions & 2) != 0;
                SetHandGesturesAllowed(core, left, "Left");
                SetHandGesturesAllowed(core, right, "Right");
                bool Held() => ContextMenuSelected(core);
                void PermissionsPreserved() => Check(HandGesturesAllowed(core, "Left") == left &&
                    HandGesturesAllowed(core, "Right") == right, "menu operations preserve each hand permission");
                var target = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
                Check(target.Parent == catalog && button.PressedData.Reference.Target == target,
                    "unselected menu payload references the local Clips");
                void SetHands()
                {
                    Check(core.WriteDynamicVariable("ExpressionSystem/State.Gesture.Left", 5) == DynamicVariableWriteResult.Success &&
                        core.WriteDynamicVariable("ExpressionSystem/State.Gesture.Right", 6) == DynamicVariableWriteResult.Success,
                        "prepare non-neutral hand values without changing the selected expression");
                }
                SetHands();
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "State.CurrentExpression") == target && Held() && Gestures(core) == (0, 0),
                    "menu press clears both hand values and holds the selected expression");
                PermissionsPreserved();
                await Frames();
                var pose = outputs.Select(SelectedValue).ToArray();
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, catalog);
                Check(Held() && Reference<Slot>(core, "State.CurrentExpression") == target,
                    "invalid menu payload cannot release or replace the held pose");
                foreach (string tag in new[] { ExpressionSystemSetup.TrySetGestureLeftTag, ExpressionSystemSetup.TrySetGestureRightTag })
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, 7);
                Check(Gestures(core) == (left ? 7 : 0, right ? 7 : 0) && Held() &&
                    Reference<Slot>(core, "State.CurrentExpression") == target,
                    "gesture inputs update permitted hands while ApplyExpression holds the pose");
                foreach (string tag in new[] { ExpressionSystemSetup.SetGestureLeftTag, ExpressionSystemSetup.SetGestureRightTag })
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, 6);
                Check(Gestures(core) == (6, 6) && Held() && Reference<Slot>(core, "State.CurrentExpression") == target,
                    "keyboard API updates both hands without applying a different pose");
                foreach (string tag in new[] { "ExpressionSystem/Method.SelectExpression", "ExpressionSystem/Internal.ApplyExpression" })
                foreach (var candidate in new[] { (Slot)null, catalog.Children.First(c => c != target) })
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<Slot>(api, tag, true, candidate);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, "ExpressionSystem/Internal.UpdateSelection", true);
                await Frames();
                Check(Held() && Gestures(core) == (6, 6) && Reference<Slot>(core, "State.CurrentExpression") == target &&
                    pose.SequenceEqual(outputs.Select(SelectedValue)), "removed Select API does nothing and ApplyExpression rejects non-menu pose changes while held");
                PermissionsPreserved();
                ExpressionGraphChecks.CheckMenuColors(expressions);
                Check(button.PressedData.Reference.Target == null, "selected menu trigger carries null");
                SetHands();
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "State.CurrentExpression") == null && !Held() && Gestures(core) == (0, 0),
                    "second menu press clears selection, hold and both hand values");
                PermissionsPreserved();
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.TrySetGestureLeftTag, true, 7);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.TrySetGestureRightTag, true, 6);
                Check(Gestures(core) == (left ? 7 : 0, right ? 6 : 0), "release resumes only the enabled hands");
                reset.Pressed(null, default);
                await Frames();
                Check(outputs.All(output => !TryReadSelectedValue(output, out _)), "cleared menu leaves no selected pose bindings");
                ExpressionGraphChecks.CheckMenuColors(expressions);
                Check(button.PressedData.Reference.Target == target, "cleared menu restores its expression payload");
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "State.CurrentExpression") == target, "third press selects the expression again");
                // The removed API must not leave a receiver behind.
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, "ExpressionSystem/Method.SelectExpression", true, target) == 0 &&
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<Slot>(api, "ExpressionSystem/Method.SelectExpression", true, null) == 0,
                    "removed Select API has no receiver for either a pose or null");
                Check(Reference<Slot>(core, "State.CurrentExpression") == target && Held(), "removed Select API cannot clear a held pose");
                foreach (string tag in new[] { ExpressionSystemSetup.ResetTag, "ExpressionSystem/Internal.ResetState" })
                {
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, tag, true);
                    Check(!Held() && Reference<Slot>(core, "State.CurrentExpression") == null && Gestures(core) == (0, 0),
                        "public and internal reset release the menu hold");
                    PermissionsPreserved();
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SetGestureLeftTag, true, 4);
                    Check(Gestures(core).Left == 4, "keyboard API resumes after reset regardless of gesture permissions");
                    ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, target);
                }
                reset.Pressed(null, default);
                await Frames();
            }
        }
        finally { reset.Pressed(null, default); }
        await Frames();
        Console.WriteLine($"MENU TOGGLE: {buttons.Length} Clips, shared and imported menu buttons hold/release all input paths with all four permission combinations");
    }

    private static async Task Frames() { for (int i = 0; i < 5; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
