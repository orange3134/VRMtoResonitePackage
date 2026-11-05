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
        var catalog = expressions.FindChild("Catalog");
        var outputs = expressions.FindChild("Outputs").Children.ToArray();
        var buttons = expressions.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithReference<Slot>>()
            .Where(button => button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target != null).ToArray();
        Check(buttons.Any(button => button.Slot.Name == "Imported angry"), "imported expression menu is exercised");
        var reset = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Left").FindChild("Items")
            .FindChild("Reset settings").GetComponent<ButtonDynamicImpulseTrigger>();
        try
        {
            reset.Pressed(null, default);
            await Frames();
            foreach (var button in buttons)
            {
                var target = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
                Check(target.Parent == catalog && button.PressedData.Reference.Target == target,
                    "unselected menu payload references the local Catalog");
                void SetHands()
                {
                    Check(core.WriteDynamicVariable("ExpressionSystem/LeftGesture", 5) == DynamicVariableWriteResult.Success &&
                        core.WriteDynamicVariable("ExpressionSystem/RightGesture", 6) == DynamicVariableWriteResult.Success,
                        "prepare non-neutral hand values without changing the selected expression");
                }
                SetHands();
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "CurrentExpression") == target && NoHandsAllowed(core) && Gestures(core) == (0, 0),
                    "menu press clears both hand values, selects the expression and disables gestures");
                await Frames();
                ExpressionGraphChecks.CheckMenuColors(expressions);
                Check(button.PressedData.Reference.Target == null, "selected menu trigger carries null");
                SetHands();
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "CurrentExpression") == null && NoHandsAllowed(core) && Gestures(core) == (0, 0),
                    "second menu press clears selection and both hand values without enabling gestures");
                await Frames();
                Check(outputs.All(output => !TryReadSelectedValue(output, out _)), "cleared menu leaves no selected pose bindings");
                ExpressionGraphChecks.CheckMenuColors(expressions);
                Check(button.PressedData.Reference.Target == target, "cleared menu restores its expression payload");
                button.Pressed(null, default);
                Check(Reference<Slot>(core, "CurrentExpression") == target, "third press selects the expression again");
                // API calls remain explicit selections; only menu payloads toggle.
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, target);
                Check(Reference<Slot>(core, "CurrentExpression") == target, "repeating a non-null Select API call keeps its pose");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<Slot>(api, ExpressionSystemSetup.SelectTag, true, null);
                Check(Reference<Slot>(core, "CurrentExpression") == null, "Select API accepts null to clear the pose");
                await Frames();
            }
        }
        finally { reset.Pressed(null, default); }
        await Frames();
        Console.WriteLine($"MENU TOGGLE: {buttons.Length} Catalog, left/right and imported menu buttons select, clear and reselect");
    }

    private static async Task Frames() { for (int i = 0; i < 5; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
