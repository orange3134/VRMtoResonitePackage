using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionResetChecks
{
    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("Core");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var table = expressions.FindChild("DV").FindChild("GestureTable");
        var outputs = expressions.FindChild("Outputs").Children.ToArray();
        var expression = expressions.FindChild("Catalog").Children.First(c =>
            c.IsActive && Get<bool>(c, "Enabled") && c.FindChild("Bindings").ChildrenCount > 0);
        var mappings = table.ExpressionVariables<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal))
            .ToDictionary(v => v, v => v.Reference.Target);
        var neutral = mappings.Keys.Single(v => v.VariableName.Value == "ExpressionSystem/GestureTable.L0R0");
        var button = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Reset settings")
            .GetComponent<ButtonDynamicImpulseTrigger>();
        try
        {
            // Reset must return to Base even if neutral has an assigned expression.
            neutral.Reference.Target = expression;
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.KeyboardLeftTag, true, 5);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.KeyboardRightTag, true, 6);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, Get<string>(expression, "Id"));
            Check(Get<int>(core, "LeftGesture") == 5 && Get<int>(core, "RightGesture") == 6 &&
                !Get<bool>(core, "AllowHandGestures") && Reference<Slot>(core, "CurrentExpression") == expression,
                "reset starts with both hands non-neutral, a selected expression and gestures disabled");
            var toggle = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Hand gestures")
                .GetComponent<ButtonDynamicImpulseTrigger>();
            void Toggle(bool expected)
            {
                toggle.Pressed(null, default);
                Check(Get<bool>(core, "AllowHandGestures") == expected && Get<int>(core, "LeftGesture") == 5 &&
                    Get<int>(core, "RightGesture") == 6 && Reference<Slot>(core, "CurrentExpression") == expression,
                    "gesture toggle changes only permission and preserves the selected expression and hands");
            }
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            Toggle(true);
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            Toggle(false); Toggle(true); Toggle(false); // Each press reads current state, even in one frame.
            await Frames(2); ExpressionGraphChecks.CheckMenuColors(expressions);
            button.Pressed(null, default);
            void CheckReset()
            {
                Check(Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 0 &&
                    Get<string>(core, "PairKey") == "L0R0" && Get<bool>(core, "AllowHandGestures") &&
                    Reference<Slot>(core, "CurrentExpression") == null,
                    "reset button synchronously clears expression and hands and enables hand gestures");
                Check(outputs.All(o => Reference<Slot>(o, "Binding") == null), "reset clears every resolved output binding");
            }
            CheckReset();
            button.Pressed(null, default); // Repeated reset also works while gestures are already enabled.
            CheckReset();
            await Frames(30);
            CheckReset();
            foreach (var output in outputs)
                Check(Math.Abs(Get<float>(output, "Result") - Get<float>(output, "Base")) < 0.001f,
                    "reset restores Base: " + output.Name);
            Check(neutral.Reference.Target == expression && mappings.All(pair => pair.Key == neutral || pair.Key.Reference.Target == pair.Value),
                "reset preserves gesture mappings, including the neutral entry");
            ExpressionGraphChecks.CheckMenuColors(expressions);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.RightTag, true, 0);
            Check(Reference<Slot>(core, "CurrentExpression") == expression,
                "next gesture event resumes selection using the retained neutral mapping");
        }
        finally
        {
            neutral.Reference.Target = mappings[neutral];
            button.Pressed(null, default);
        }
        await Frames(5);
        Console.WriteLine("RESET: actual menu button resets both hands, expression and permission; Base and mappings verified");
    }

    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicVariableBase<T>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, name)).DynamicValue;
    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
