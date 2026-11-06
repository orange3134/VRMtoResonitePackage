using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;

internal static class ExpressionDynamicInputChecks
{
    public static void CheckBindings(Slot root)
    {
        CheckSlotReferences(root);
        var modules = root.FindChild("Inputs").FindChild("HandGestures");
        foreach (var module in modules.Children)
        {
            var settings = module.ExpressionVariables<DynamicValueVariable<float>>();
            foreach (string hand in new[] { "Left", "Right" })
            {
                var inputs = module.FindChild(hand).GetComponentsInChildren<DynamicVariableValueInput<float>>()
                    .Where(node => Name(node).StartsWith("ExpressionSystem.Input.HandGestures/", StringComparison.Ordinal)).ToArray();
                int expected = module.Name == "Index" ? 5 : 1;
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
        foreach (var hand in root.FindChild("Inputs").FindChild("Keyboard").Children)
        {
            var inputs = hand.GetComponentsInChildren<DynamicVariableValueInput<Renderite.Shared.Key>>();
            var settings = hand.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<Renderite.Shared.Key>>();
            Check(inputs.Count == 11 && settings.Count == 11, "each keyboard hand has a modifier and ten key bindings");
            foreach (var input in inputs)
            {
                var proxy = input.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<Renderite.Shared.Key>>();
                var setting = settings.Single(value => value.VariableName.Value == Name(input));
                Check(proxy != null && proxy.HasValue && proxy.DynamicValue == setting.Value.Value,
                    "keyboard key input follows its own hand: " + hand.Name + "/" + Name(input));
            }
        }
        foreach (string board in new[] { "ApplyExpression" })
        {
            var logic = root.FindChild("API").FindChild("Internal").FindChild(board);
            var current = logic.GetComponentsInChildren<DynamicVariableObjectInput<Slot>>()
                .Single(node => Name(node) == "ExpressionSystem/State.CurrentExpression");
            var proxy = current.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<Slot>>();
            var field = root.FindChild("API").FindChild("Internal").ExpressionVariables<DynamicReferenceVariable<Slot>>()
                .Single(variable => variable.VariableName.Value == "ExpressionSystem/State.CurrentExpression");
            Check(proxy != null && proxy.HasValue && proxy.DynamicValue == field.Reference.Target,
                "Core object input follows this avatar's selected expression: " + board);
            Check(root.FindChild("API").FindChild("Internal").FindChild("UpdateSelection").GetComponentsInChildren<DynamicVariableValueInput<int>>().Count > 0,
                "Selection uses Dynamic Inputs for hand values");
        }
    }

    public static async Task CheckEdits(Slot root)
    {
        CheckSlotReferences(root);
        var modules = root.FindChild("Inputs").FindChild("HandGestures");
        var module = modules.Children.First();
        var field = module.ExpressionVariables<DynamicValueVariable<float>>()
            .Single(value => value.VariableName.Value == "ExpressionSystem.Input.HandGestures/StabilitySeconds");
        var key = root.FindChild("Inputs").FindChild("Keyboard").FindChild("Left").FindChild("DV").FindChild("Key.1")
            .GetComponent<DynamicValueVariable<Renderite.Shared.Key>>();
        var originalKey = key.Value.Value;
        var modifier = root.FindChild("Inputs").FindChild("Keyboard").FindChild("Left").FindChild("DV").FindChild("Modifier")
            .GetComponent<DynamicValueVariable<Renderite.Shared.Key>>();
        var originalModifier = modifier.Value.Value;
        float original = field.Value.Value;
        var receiver = root.FindChild("DV").FindChild("Reference.API").GetComponent<DynamicReferenceVariable<Slot>>();
        var originalReceiver = receiver.Reference.Target;
        var alternateReceiver = root.FindChild("API").FindChild("Internal");
        try
        {
            receiver.Reference.Target = alternateReceiver;
            key.Value.Value = Renderite.Shared.Key.Keypad7;
            modifier.Value.Value = Renderite.Shared.Key.Alt;
            Check(module.WriteDynamicVariable("ExpressionSystem.Input.HandGestures/StabilitySeconds", original + 0.137f) == DynamicVariableWriteResult.Success,
                "can edit ancestor setting");
            for (int i = 0; i < 3; i++) await default(NextUpdate);
            CheckBindings(root);
        }
        finally
        {
            receiver.Reference.Target = originalReceiver;
            field.Value.Value = original;
            key.Value.Value = originalKey;
            modifier.Value.Value = originalModifier;
        }
        for (int i = 0; i < 3; i++) await default(NextUpdate);
        CheckBindings(root);
        Console.WriteLine("INPUTS: ancestor edits update both hands without affecting other modules; Core object inputs are bound");
    }

    private static void CheckSlotReferences(Slot root)
    {
        Check(root.FindChild("DV").FindChild("GestureTable").GetComponent<DynamicReferenceVariable<Slot>>() == null &&
            root.FindChild("DV").FindChild("Reference.GestureTable") == null &&
            root.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().All(v =>
                v.VariableName.Value is not ("ExpressionSystem/GestureTable" or "ExpressionSystem/Reference.DV.GestureTable" or "ExpressionSystem/Reference.GestureTable")) &&
            root.GetComponentsInChildren<DynamicVariableObjectInput<Slot>>().All(n =>
                Name(n) is not ("ExpressionSystem/GestureTable" or "ExpressionSystem/Reference.DV.GestureTable" or "ExpressionSystem/Reference.GestureTable")),
            "GestureTable needs no Slot reference variable or input");
        var tableRead = root.FindChild("API").FindChild("Internal").FindChild("UpdateSelection")
            .GetComponentInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.ReadDynamicObjectVariable<Slot>>();
        Check(tableRead != null && tableRead.Source.Target == null,
            "Selection reads table rows from its ancestor space without a Source input");
        Check(root.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.RefObjectInput<Slot>>().Count == 0,
            "no fixed Slot reference inputs are exported");
        Check(!root.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<Slot>>().Any(v =>
            v.VariableName.Value.StartsWith("ExpressionSystem/Reference.Inputs.HandGestures.", StringComparison.Ordinal)),
            "hand-module references are absent from the system space");
        foreach (var module in root.FindChild("Inputs").FindChild("HandGestures").Children)
        {
            var local = module.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<Slot>>();
            Check(local.Count == 0, "local writes need no hand references: " + module.Name);
        }
        var apiReferences = root.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value == "ExpressionSystem/Reference.API" ||
                v.VariableName.Value.StartsWith("ExpressionSystem/Reference.API.", StringComparison.Ordinal)).ToArray();
        Check(apiReferences.Length == 1 && apiReferences[0].VariableName.Value == "ExpressionSystem/Reference.API",
            "public and internal impulses share one API root reference");
        Check(root.FindChild("DV").FindChild("Reference.Internal") == null &&
            root.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().All(v =>
                !v.VariableName.Value.StartsWith("ExpressionSystem/Reference.Internal", StringComparison.Ordinal)) &&
            root.GetComponentsInChildren<DynamicVariableObjectInput<Slot>>().All(n =>
                !Name(n).StartsWith("ExpressionSystem/Reference.Internal", StringComparison.Ordinal)),
            "removed Internal reference has no variable, slot or inputs");
        foreach (var trigger in root.GetComponentsInChildren<ProtoFluxNode>().Where(n =>
            n.GetType().Name.StartsWith("DynamicImpulseTrigger", StringComparison.Ordinal)))
        {
            var tag = ((ISyncRef)VrmToResonitePackage.Expressions.ExpressionFlux.Member(trigger, "Tag")).Target
                as FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueObjectInput<string>;
            if (tag?.Value.Value?.StartsWith("ExpressionSystem/", StringComparison.Ordinal) != true) continue;
            var target = ((ISyncRef)VrmToResonitePackage.Expressions.ExpressionFlux.Member(trigger, "TargetHierarchy")).Target;
            while (target != null && target is not ProtoFluxNode) target = target.Parent;
            Check(target is DynamicVariableObjectInput<Slot> input && Name(input) == "ExpressionSystem/Reference.API",
                "expression impulse targets the shared API hierarchy: " + tag.Value.Value);
        }
        foreach (var write in root.GetComponentsInChildren<ProtoFluxNode>().Where(n =>
            n.GetType().Name is "WriteDynamicValueVariable`1" or "WriteDynamicObjectVariable`1"))
        {
            var path = (FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueObjectInput<string>)
                ((ISyncRef)VrmToResonitePackage.Expressions.ExpressionFlux.Member(write, "Path")).Target;
            var target = (ISyncRef)VrmToResonitePackage.Expressions.ExpressionFlux.Member(write, "Target");
            Check((target.Target != null) == (path.Value.Value == "ExpressionSystem.Output/Result"),
                "only traversed Output writes require a Target: " + path.Value.Value);
        }
        var references = root.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/Reference.", StringComparison.Ordinal) ||
                v.VariableName.Value.StartsWith("ExpressionSystem.Input.HandGestures/Reference.", StringComparison.Ordinal)).ToArray();
        Check(references.All(v => v.VariableName.Value != "ExpressionSystem/Reference.None"),
            "null constants generate no reference variable");
        var inputs = root.GetComponentsInChildren<DynamicVariableObjectInput<Slot>>();
        Check(inputs.All(n => Name(n) != "ExpressionSystem/Reference.None"),
            "null constants generate no dynamic input node");
        foreach (var variable in references)
        {
            var space = variable.Slot.GetComponentInParents<DynamicVariableSpace>();
            var consumers = inputs.Where(n => Name(n) == variable.VariableName.Value &&
                n.Slot.GetComponentInParents<DynamicVariableSpace>(s => s.SpaceName.Value == space.SpaceName.Value) == space).ToArray();
            // Editing a generated avatar may delete a module and all its consumers.
            Check(consumers.Length > 0 || variable.Reference.Target == null,
                "only deleted targets may leave an unused reference: " + variable.VariableName.Value);
            Check(variable.Reference.Target == null || variable.Reference.Target == root || variable.Reference.Target.IsChildOf(root),
                "fixed references stay inside their own avatar after clone/reload");
            foreach (var input in consumers)
            {
                var proxy = input.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<Slot>>();
                Check(proxy != null && proxy.HasValue && proxy.DynamicValue == variable.Reference.Target,
                    "Slot input binds to its own variable: " + variable.VariableName.Value);
            }
        }
    }

    private static string Name(ProtoFluxNode node) => node.Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
