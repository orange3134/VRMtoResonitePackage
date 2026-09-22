using FrooxEngine;
using InputKey = Renderite.Shared.Key;

internal static class ExpressionSpaceChecks
{
    public static void Run(Slot root)
    {
        var expected = new Dictionary<Slot, string>
        {
            [root] = "ExpressionSystem",
            [root.FindChild("Core")] = "ExpressionCore",
            [root.FindChild("GestureTable")] = "ExpressionGestureTable"
        };
        void Records(Slot parent, string name)
        {
            foreach (var child in parent.Children) expected.Add(child, name);
        }
        void Clips(Slot parent)
        {
            Records(parent, "ExpressionClip");
            foreach (var clip in parent.Children) Records(clip.FindChild("Bindings"), "ExpressionBinding");
        }
        Clips(root.FindChild("Catalog"));
        Clips(root.FindChild("API").FindChild("Templates"));
        Records(root.FindChild("Outputs"), "ExpressionOutput");
        var inputs = root.FindChild("Inputs");
        Records(inputs.FindChild("Keyboard"), "ExpressionSystem.Input.Keyboard");
        var modules = inputs.FindChild("HandGestures").FindChild("Modules");
        Records(modules, "ExpressionGestureSettings");
        foreach (var module in modules.Children) Records(module, "ExpressionGestureHand");
        var diagnostics = root.FindChild("Diagnostics");
        Records(diagnostics.FindChild("Graph modules"), "ExpressionGraphModule");
        foreach (var warning in diagnostics.Children.Where(s => s.Name == "Import warning"))
            expected.Add(warning, "ExpressionImportWarning");

        var spaces = root.GetComponentsInChildren<DynamicVariableSpace>();
        Check(spaces.Count == expected.Count, "every record has exactly one explicit schema space");
        foreach (var space in spaces)
        {
            Check(expected.TryGetValue(space.Slot, out string name) && space.SpaceName.Value == name,
                "space matches record schema: " + space.Slot.Name);
            Check(space.OnlyDirectBinding.Value == (name != "ExpressionGestureTable"),
                "only the gesture table permits unqualified binding");
        }
        // Check prefixes independently of the generator's helpers, including child table
        // variables and optional output drivers, before and after package serialization.
        string Prefix(Slot slot) => slot.GetComponentInParents<DynamicVariableSpace>().SpaceName.Value + "/";
        void Values<T>()
        {
            foreach (var variable in root.GetComponentsInChildren<DynamicVariableBase<T>>())
                Check(variable.VariableName.Value.StartsWith(Prefix(variable.Slot), StringComparison.Ordinal),
                    "value belongs to its record's space: " + variable.VariableName.Value);
        }
        void References<T>() where T : class, IWorldElement
        {
            foreach (var variable in root.GetComponentsInChildren<DynamicReferenceVariable<T>>())
                Check(variable.VariableName.Value.StartsWith(Prefix(variable.Slot), StringComparison.Ordinal),
                    "reference belongs to its record's space: " + variable.VariableName.Value);
        }
        Values<int>(); Values<float>(); Values<bool>(); Values<string>(); Values<InputKey>();
        References<Slot>(); References<IField<float>>(); References<ISyncRef>();
        Check(root.GetComponents<DynamicValueVariable<int>>().Single(v => v.VariableName.Value == "ExpressionSystem/Version").Value.Value == 13,
            "mesh driver outputs are identified by package version 10");
        var core = root.FindChild("Core");
        Check(core.WriteDynamicVariable("Expr/AllowExternalInput", false) != DynamicVariableWriteResult.Success,
            "legacy shared-space writes cannot modify the new Core");
        Console.WriteLine("SPACES: explicit record schemas and variable prefixes verified");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
