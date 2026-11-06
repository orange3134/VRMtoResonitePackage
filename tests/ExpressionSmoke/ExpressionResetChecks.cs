using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionResetChecks
{
    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("API").FindChild("Internal");
        var api = expressions.FindChild("API");
        var table = expressions.FindChild("DV").FindChild("GestureTable");
        var outputs = expressions.FindChild("Outputs").Children.ToArray();
        var expression = expressions.FindChild("Clips").Children.First(c =>
            c.ExpressionVariables<DynamicValueVariable<float>>().Any(v => v.VariableName.Value.StartsWith("ExpressionSystem.Clip/", StringComparison.Ordinal)));
        var mappings = table.ExpressionVariables<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal))
            .ToDictionary(v => v, v => v.Reference.Target);
        var neutral = mappings.Keys.Single(v => v.VariableName.Value == "ExpressionSystem/GestureTable.L0R0");
        var button = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Reset settings")
            .GetComponent<ButtonDynamicImpulseTrigger>();
        void PrepareSelection()
        {
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, expression);
            // Direct state edits keep a pose selected while preparing non-neutral reset input.
            Check(core.WriteDynamicVariable("ExpressionSystem/State.Gesture.Left", 5) == DynamicVariableWriteResult.Success &&
                core.WriteDynamicVariable("ExpressionSystem/State.Gesture.Right", 6) == DynamicVariableWriteResult.Success,
                "prepare hand values without reevaluating the selected pose");
        }
        try
        {
            // Reset must return to Base even if neutral has an assigned expression.
            neutral.Reference.Target = expression;
            SetHandGesturesAllowed(core, false);
            PrepareSelection();
            Check(Get<int>(core, "State.Gesture.Left") == 5 && Get<int>(core, "State.Gesture.Right") == 6 &&
                NoHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == expression,
                "reset starts with both hands non-neutral, a selected expression and gestures disabled");
            var toggle = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Hand gestures")
                .GetComponent<ButtonDynamicImpulseTrigger>();
            void Toggle(bool expected)
            {
                toggle.Pressed(null, default);
                Check(BothHandsAllowed(core) == expected && Get<int>(core, "State.Gesture.Left") == 5 &&
                    Get<int>(core, "State.Gesture.Right") == 6 && Reference<Slot>(core, "State.CurrentExpression") == expression,
                    "gesture toggle changes only permission and preserves the selected expression and hands");
            }
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            Toggle(true);
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            Toggle(false); Toggle(true); Toggle(false); // Each press reads current state, even in one frame.
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            button.Pressed(null, default);
            void CheckReset(bool left = false, bool right = false)
            {
                Check(Get<int>(core, "State.Gesture.Left") == 0 && Get<int>(core, "State.Gesture.Right") == 0 &&
                    HandGesturesAllowed(core, "Left") == left && HandGesturesAllowed(core, "Right") == right &&
                    Reference<Slot>(core, "State.CurrentExpression") == null && !ContextMenuSelected(core),
                    "reset synchronously clears expression and hands while preserving each hand permission");
                Check(outputs.All(o => !TryReadSelectedValue(o, out _)), "reset clears every resolved output binding");
            }
            CheckReset();
            button.Pressed(null, default); // Repeated reset also preserves disabled gestures.
            CheckReset();
            await Frames(30);
            CheckReset();
            foreach (var output in outputs)
                Check(Math.Abs(Get<float>(output, "Result") - Get<float>(output, "Base")) < 0.001f,
                    "reset restores Base: " + output.Name);
            Check(neutral.Reference.Target == expression && mappings.All(pair => pair.Key == neutral || pair.Key.Reference.Target == pair.Value),
                "reset preserves gesture mappings, including the neutral entry");
            ExpressionGraphChecks.CheckMenuColors(expressions);
            for (int combination = 0; combination < 4; combination++)
            {
                bool left = (combination & 1) != 0, right = (combination & 2) != 0;
                PrepareSelection();
                SetHandGesturesAllowed(core, left, "Left");
                SetHandGesturesAllowed(core, right, "Right");
                button.Pressed(null, default);
                CheckReset(left, right);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, "ExpressionSystem/Internal.ResetState", true);
                CheckReset(left, right);
                await Frames(2);
                CheckReset(left, right);
                ExpressionGraphChecks.CheckMenuColors(expressions);
            }
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.TrySetGestureRightTag, true, 0);
            Check(Reference<Slot>(core, "State.CurrentExpression") == expression,
                "next gesture event resumes selection using the retained neutral mapping");
        }
        finally
        {
            neutral.Reference.Target = mappings[neutral];
            button.Pressed(null, default);
        }
        await Frames(5);
        Console.WriteLine("RESET: menu and direct reset clear hands and expression, preserve all four permission combinations, Base and mappings");
    }

    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicVariableBase<T>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, name)).DynamicValue;
    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
