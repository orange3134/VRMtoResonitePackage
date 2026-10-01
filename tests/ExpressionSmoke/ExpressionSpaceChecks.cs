using FrooxEngine;
using InputKey = Renderite.Shared.Key;

internal static class ExpressionSpaceChecks
{
    public static void Run(Slot root)
    {
        var expected = new Dictionary<Slot, string>
        {
            [root] = "ExpressionSystem"
        };
        void Records(Slot parent, string name)
        {
            foreach (var child in parent.Children.Where(child => child.Name != "DV")) expected.Add(child, name);
        }
        void Clips(Slot parent)
        {
            Records(parent, "ExpressionSystem.Catalog.Clip");
        }
        Clips(root.FindChild("Catalog"));
        Clips(root.FindChild("API").FindChild("Templates"));
        Records(root.FindChild("Outputs"), "ExpressionSystem.Output");
        var inputs = root.FindChild("Inputs");
        Records(inputs.FindChild("Keyboard"), "ExpressionSystem.Input.Keyboard");
        var modules = inputs.FindChild("HandGestures").FindChild("Modules");
        Records(modules, "ExpressionSystem.Input.HandGestures");
        foreach (var module in modules.Children)
        foreach (string side in new[] { "Left", "Right" })
        {
            var hand = module.FindChild(side);
            Check(hand.FindChild("DV") == null && hand.GetComponentsInChildren<DynamicVariableSpace>().Count == 0,
                "controller hands keep transient state inside Flux: " + module.Name + "/" + side);
            Check(hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.DelayWithValueSecondsFloat<int>>().Count == 1 &&
                hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Async.StartAsyncTask>().Count == 1,
                "each controller hand schedules delayed input snapshots");
            var delayed = hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.DelayWithValueSecondsFloat<int>>().Single();
            var start = hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Async.StartAsyncTask>().Single();
            var timeout = hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Actions.LocalImpulseTimeoutSeconds>().Single();
            Check(start.TaskStart.Target == timeout.Trigger && timeout.Next.Target == delayed &&
                timeout.Timeout.Target == delayed.Duration.Target,
                "async tasks pass through the per-hand timeout before capturing input, with the same duration");
            Check(!hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.ProtoFluxNode>().Any(node =>
                node.AllImpulses.Any(port => port.Target == timeout.Reset)),
                "input changes and departure do not reset the authored timeout");
            Check(!hand.GetComponentsInChildren<FrooxEngine.ProtoFlux.ProtoFluxNode>().Any(node =>
                node.GetType().Name.StartsWith("StoredValue") || node.GetType().Name == "ElapsedTimeFloat"),
                "controller hands need no candidate or last-sent stores and no elapsed timer");
        }
        Check(root.FindChild("Diagnostics") == null, "diagnostics are logged rather than exported into the avatar");

        var internalSlot = root.FindChild("Internal");
        Check(root.FindChild("Core") == null && internalSlot != null &&
            internalSlot.Children.Select(child => child.Name).OrderBy(name => name)
                .SequenceEqual(new[] { "Lifecycle", "Playback", "Selection" }),
            "Internal directly contains exactly the three logic boards");
        Check(root.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/", StringComparison.Ordinal))
            .All(v => v.VariableName.Value == "ExpressionSystem/CurrentExpression" ||
                v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal) ||
                v.VariableName.Value.StartsWith("ExpressionSystem/References.", StringComparison.Ordinal)),
            "fixed system Slot references use References; current expression and gesture mappings retain their keys");
        Check(root.FindChild("DV").Children.All(child => !child.Name.StartsWith("Core.", StringComparison.Ordinal)),
            "state variable slots have no Core prefix");

        var spaces = root.GetComponentsInChildren<DynamicVariableSpace>();
        Check(spaces.Count == expected.Count, "every record has exactly one explicit schema space");
        foreach (var space in spaces)
        {
            Check(expected.TryGetValue(space.Slot, out string name) && space.SpaceName.Value == name,
                "space matches record schema: " + space.Slot.Name);
            Check(space.OnlyDirectBinding.Value == (name != "ExpressionSystem"),
                "the shared system space binds Core fields and child table rows");
        }
        // Check prefixes independently of the generator's helpers, including child table
        // variables and optional output drivers, before and after package serialization.
        string Prefix(Slot slot) => ExpressionTestFields.VariablePath(slot, "");
        var occupied = new HashSet<Slot>();
        void Placement(Component variable, string path)
        {
            var space = variable.Slot.GetComponentInParents<DynamicVariableSpace>();
            var data = space.Slot.FindChild("DV");
            bool tableRow = path.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal);
            var container = tableRow ? data?.FindChild("GestureTable") : data;
            Check(data != null && data.Parent == space.Slot && variable.Slot.Parent == container,
                "variable lives in its DV container or GestureTable group: " + path);
            Check(variable.Slot.Name == (tableRow ? path["ExpressionSystem/GestureTable.".Length..] : path[(path.IndexOf('/') + 1)..]),
                "variable slot is named after its key: " + path);
            Check(occupied.Add(variable.Slot), "one variable per slot: " + path);
        }
        void Values<T>()
        {
            // Drivers consume a variable at their target; only definitions belong in DV.
            foreach (var variable in root.GetComponentsInChildren<DynamicVariableBase<T>>()
                .Where(variable => variable is not DynamicValueVariableDriver<T>))
            {
                Placement(variable, variable.VariableName.Value);
                Check(variable.VariableName.Value.StartsWith(Prefix(variable.Slot), StringComparison.Ordinal),
                    "value belongs to its record's space: " + variable.VariableName.Value);
            }
        }
        void References<T>() where T : class, IWorldElement
        {
            foreach (var variable in root.GetComponentsInChildren<DynamicReferenceVariable<T>>())
            {
                Placement(variable, variable.VariableName.Value);
                Check(variable.VariableName.Value.StartsWith(Prefix(variable.Slot), StringComparison.Ordinal),
                    "reference belongs to its record's space: " + variable.VariableName.Value);
            }
        }
        Values<int>(); Values<float>(); Values<bool>(); Values<string>(); Values<InputKey>();
        References<Slot>(); References<IField<float>>(); References<ISyncRef>();
        Check(root.ExpressionVariables<DynamicValueVariable<int>>().Single(v => v.VariableName.Value == "ExpressionSystem/Version").Value.Value == 55,
            "Touch without a redundant Neutral override is identified by package version 55");
        Check(root.GetComponent<DynamicVariableSpace>().TryReadValue<int>("LeftGesture", out _),
            "Core fields are readable from the system root");
        Check(root.GetComponent<DynamicVariableSpace>().TryReadValue<Slot>("GestureTable.L0R0", out _),
            "table rows are readable from the system root");
        var core = root.FindChild("Internal");
        Check(core.WriteDynamicVariable("Expr/AllowExternalInput", false) != DynamicVariableWriteResult.Success,
            "legacy shared-space writes cannot modify the new Core");
        Console.WriteLine("SPACES: explicit record schemas and variable prefixes verified");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
