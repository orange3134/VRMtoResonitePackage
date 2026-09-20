using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;

internal static class ExpressionDynamicInputChecks
{
    public static void CheckBindings(Slot root)
    {
        var modules = root.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules");
        foreach (var module in modules.Children)
        {
            var settings = module.GetComponents<DynamicValueVariable<float>>();
            foreach (string hand in new[] { "Left", "Right" })
            {
                var inputs = module.FindChild(hand).GetComponentsInChildren<DynamicVariableValueInput<float>>()
                    .Where(node => Name(node).StartsWith("ExpressionGestureSettings/", StringComparison.Ordinal)).ToArray();
                int expected = module.Name is "Vive" or "WindowsMR" ? 3 : 5;
                Check(inputs.Length == expected, "all used ancestor settings use Dynamic Inputs: " + module.Name + "/" + hand);
                foreach (var input in inputs)
                {
                    var proxy = input.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<float>>();
                    var setting = settings.Single(value => value.VariableName.Value == Name(input));
                    Check(proxy != null && proxy.HasValue && proxy.DynamicValue == setting.Value.Value,
                        "ancestor input follows its own module: " + module.Name + "/" + hand + "/" + Name(input));
                }
            }
        }
        foreach (string board in new[] { "Selection", "Playback" })
        {
            var logic = root.FindChild("Core").FindChild("Logic").FindChild(board);
            var current = logic.GetComponentsInChildren<DynamicVariableObjectInput<Slot>>()
                .Single(node => Name(node) == "ExpressionCore/CurrentExpression");
            var proxy = current.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<Slot>>();
            var field = root.FindChild("Core").GetComponents<DynamicReferenceVariable<Slot>>()
                .Single(variable => variable.VariableName.Value == "ExpressionCore/CurrentExpression");
            Check(proxy != null && proxy.HasValue && proxy.DynamicValue == field.Reference.Target,
                "Core object input follows this avatar's selected expression: " + board);
            Check(logic.GetComponentsInChildren<DynamicVariableValueInput<int>>().Count > 0 || board == "Playback",
                "Selection uses Dynamic Inputs for hand values");
        }
    }

    public static async Task CheckEdits(Slot root)
    {
        var modules = root.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules");
        var module = modules.Children.First();
        var field = module.GetComponents<DynamicValueVariable<float>>()
            .Single(value => value.VariableName.Value == "ExpressionGestureSettings/StabilitySeconds");
        float original = field.Value.Value;
        try
        {
            Check(module.WriteDynamicVariable("ExpressionGestureSettings/StabilitySeconds", original + 0.137f) == DynamicVariableWriteResult.Success,
                "can edit ancestor setting");
            for (int i = 0; i < 3; i++) await default(NextUpdate);
            CheckBindings(root);
        }
        finally
        {
            field.Value.Value = original;
        }
        for (int i = 0; i < 3; i++) await default(NextUpdate);
        CheckBindings(root);
        Console.WriteLine("INPUTS: ancestor edits update both hands without affecting other modules; Core object inputs are bound");
    }

    private static string Name(ProtoFluxNode node) => node.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
