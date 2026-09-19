using Elements.Assets;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;

// Optional integration check for a converted avatar with 64 assigned, static gesture poses.
// Input packages and their paths are supplied locally and are never part of the fixture.
internal static class ImportedGestureAvatarChecks
{
    public static async Task Run(World world, string package, string artifacts, string baselinePackage = null)
    {
        var avatar = world.LocalUser.Root.Slot.AddSlot("Imported gesture avatar");
        await PackageImporter.ImportPackage(package, avatar);
        await default(ToWorld);
        for (int i = 0; i < 180; i++) await default(NextUpdate);
        var root = avatar.FindChild("Expressions") ?? throw new InvalidOperationException("Missing expression system");
        ExpressionGraphChecks.CheckLayout(root);
        if (baselinePackage != null)
        {
            string current = ExpressionPackageSnapshot.Capture(root, Path.Combine(artifacts, "current-expressions"));
            var baseline = world.LocalUser.Root.Slot.AddSlot("Baseline gesture avatar");
            await PackageImporter.ImportPackage(baselinePackage, baseline);
            await default(ToWorld);
            for (int i = 0; i < 180; i++) await default(NextUpdate);
            var baselineRoot = baseline.FindChild("Expressions");
            Console.WriteLine("BASELINE graph (legacy board boundaries are reported, not asserted):");
            ExpressionGraphChecks.Report(baselineRoot);
            string previous = ExpressionPackageSnapshot.Capture(baselineRoot, Path.Combine(artifacts, "baseline-expressions"));
            Check(current == previous, "Expression semantics differ from baseline; compare current-expressions/expressions.json and baseline-expressions/expressions.json");
            baseline.Destroy();
            Console.WriteLine("PASS: baseline catalog, complete AnimX curves, output bindings and all 64 gesture mappings are unchanged");
        }
        var core = root.FindChild("Core"); var table = root.FindChild("GestureTable");
        var menu = root.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
        var left = menu.FindChild("Left hand").FindChild("Items"); var right = menu.FindChild("Right hand").FindChild("Items");
        foreach (var entry in root.FindChild("Catalog").Children)
        {
            entry.WriteDynamicVariable("Expr/FadeIn", 0f); entry.WriteDynamicVariable("Expr/FadeOut", 0f);
        }

        for (int hand = 0; hand < 2; hand++)
            for (int gesture = 0; gesture < 8; gesture++)
            {
                var button = (hand == 0 ? left : right).Children[gesture].GetComponent<ButtonDynamicImpulseTriggerWithValue<int>>();
                Check(button != null && button.PressedData.Tag.Value == (hand == 0 ? ExpressionSystemSetup.MenuLeftTag : ExpressionSystemSetup.MenuRightTag) &&
                    button.PressedData.Value.Value == gesture,
                    "Saved menu button contains the hand Tag and correct int payload");
            }
        var distinctPoses = new HashSet<string>();
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                // Both clicks run in the same update; each event must resolve the current pair immediately.
                int previousRight = Get<int>(core, "RightGesture");
                left.Children[l].GetComponent<ButtonDynamicImpulseTriggerWithValue<int>>().Pressed(null, default);
                Check(Get<int>(core, "LeftGesture") == l && Get<int>(core, "RightGesture") == previousRight &&
                    Get<int>(core, "PairIndex") == l * 8 + previousRight, "Left menu event did not evaluate immediately");
                right.Children[r].GetComponent<ButtonDynamicImpulseTriggerWithValue<int>>().Pressed(null, default);
                Check(Get<int>(core, "LeftGesture") == l && Get<int>(core, "RightGesture") == r, "Menu did not update both hand states synchronously");
                var mapped = table.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
                    .Single(v => v.VariableName.Value == "Expr/Pair." + (l * 8 + r)).Reference.Target;
                Check(mapped != null && Reference<Slot>(core, "CurrentExpression") == mapped, "Missing or incorrect selected pose");
                Check(Get<int>(core, "PairIndex") == l * 8 + r && Get<int>(core, "SelectionStatus") == 2 && !Get<bool>(core, "AllowExternalInput") &&
                    Reference<Slot>(core, "MappedExpression") == mapped && Reference<Slot>(core, "CandidateExpression") == mapped,
                    "Imported selection diagnostics disagree with the selected gesture pair");
                for (int i = 0; i < 6; i++) await default(NextUpdate);
                var data = mapped.GetComponent<StaticAnimationProvider>().Asset?.Data;
                Check(data != null, "Pose animation asset did not load");
                var values = new List<float>();
                foreach (var output in root.FindChild("Outputs").Children)
                {
                    int index = data.FindTrackIndex("Expression", Get<string>(output, "Id"));
                    float expected = index >= 0 ? ((IAnimationTrack<float>)data[index]).Sample(0) : Get<float>(output, "Base");
                    float actual = Reference<IField<float>>(output, "Target").Value;
                    Check(Math.Abs(expected - actual) < 0.001f, $"Pair {l},{r}: output {Get<string>(output, "Shape")} expected {expected}, got {actual}");
                    values.Add(actual);
                }
                distinctPoses.Add(string.Join(",", values.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine($"PASS: saved menu Left {l}, Right {r} -> {mapped.Name}, {values.Count} output fields checked");
            }
        Check(distinctPoses.Count >= 8, "Gesture menu did not produce eight distinct visible poses");
        var receiverRoot = root.FindChild("API").FindChild("Receivers");
        var mappings = table.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("Expr/Pair.", StringComparison.Ordinal))
            .ToDictionary(v => int.Parse(v.VariableName.Value["Expr/Pair.".Length..]), v => v.Reference.Target);
        int visible = 0;
        foreach (var expression in root.FindChild("Catalog").Children)
        {
            var indices = mappings.Where(pair => pair.Value == expression).Select(pair => pair.Key).ToArray();
            bool available = indices.Length > 0 && Get<bool>(expression, "Enabled") && expression.IsActive;
            Check(expression.GetComponent<ContextMenuItemSource>().Enabled == available,
                "Saved direct menu only shows expressions in the current table");
            if (!available) continue;
            visible++;
            expression.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>().Pressed(null, default);
            Check(!Get<bool>(core, "AllowExternalInput") && Get<int>(core, "PairIndex") == indices.Min() &&
                Reference<Slot>(core, "CurrentExpression") == expression,
                "Saved direct menu updates the normal hand pair and locks ordinary input");
        }
        Check(visible >= 8, "Saved direct menu contains at least eight mapped poses");
        int heldLeft = Get<int>(core, "LeftGesture"), heldRight = Get<int>(core, "RightGesture");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.LeftTag, true, (heldLeft + 1) % 8);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.RightTag, true, (heldRight + 1) % 8);
        Check(Get<int>(core, "LeftGesture") == heldLeft && Get<int>(core, "RightGesture") == heldRight,
            "Saved menu-only mode ignores normal input");
        var enable = menu.FindChild("Allow gestures and keyboard").GetComponent<ButtonDynamicImpulseTriggerWithValue<bool>>();
        Check(enable.PressedData.Tag.Value == ExpressionSystemSetup.InputEnabledTag && enable.PressedData.Value.Value,
            "Saved allow-input button sends true");
        enable.Pressed(null, default);
        Check(Get<bool>(core, "AllowExternalInput") && Get<int>(core, "LeftGesture") == heldLeft && Get<int>(core, "RightGesture") == heldRight,
            "Enabling ordinary input retains the saved menu pair");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.LeftTag, true, 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.RightTag, true, 0);
        Check(Get<int>(core, "PairIndex") == 0, "Saved normal input works after enabling");
        var disable = menu.FindChild("Menu only").GetComponent<ButtonDynamicImpulseTriggerWithValue<bool>>();
        Check(disable.PressedData.Tag.Value == ExpressionSystemSetup.InputEnabledTag && !disable.PressedData.Value.Value,
            "Saved menu-only button sends false");
        disable.Pressed(null, default);
        Check(!Get<bool>(core, "AllowExternalInput"), "Saved menu-only button disables ordinary input");
        Console.WriteLine($"PASS: {visible} mapped direct-menu entries and both input-mode buttons work without Override state");
        var importedMenu = menu.FindChild("Imported menu");
        // Unmapped imported expressions are intentionally absent from the visible menu.
        if (importedMenu != null && !importedMenu.GetComponentsInChildren<ContextMenuItemSource>().Any(item =>
            item.Enabled && item.Slot.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>() != null))
            importedMenu = null;
        if (importedMenu != null)
        {
            var importedButton = importedMenu.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithValue<string>>()
                .FirstOrDefault(button => button.PressedData.Tag.Value == ExpressionSystemSetup.SelectTag && button.Slot.GetComponent<ContextMenuItemSource>().Enabled);
            Check(importedButton != null, "Imported menu contains an actual string selection button");
            var catalog = root.FindChild("Catalog");
            string originalId = importedButton.PressedData.Value.Value;
            var selected = catalog.Children.Single(entry => Get<string>(entry, "Id") == originalId);
            const string editedId = "Smoke.RenamedImportedExpression";
            Check(catalog.Children.All(entry => Get<string>(entry, "Id") != editedId), "Edited test ID is unique");
            Check(selected.WriteDynamicVariable("Expr/Id", editedId) == DynamicVariableWriteResult.Success, "Can edit imported expression ID");
            try
            {
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                Check(importedButton.PressedData.Value.Value == editedId, "Saved imported-menu payload did not follow its expression's edited ID");
                var api = root.FindChild("API").FindChild("Receivers");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.InputEnabledTag, true, true);
                Check(Get<bool>(core, "AllowExternalInput"), "Ordinary input is enabled before imported button verification");
                importedButton.Pressed(null, default);
                Check(!Get<bool>(core, "AllowExternalInput") && Reference<Slot>(core, "CurrentExpression") == selected,
                    "Saved imported-menu button did not select the expression by its edited ID");
                Console.WriteLine("PASS: saved imported-menu button follows edited expression ID and selects it synchronously");
            }
            finally
            {
                Check(selected.WriteDynamicVariable("Expr/Id", originalId) == DynamicVariableWriteResult.Success, "Can restore imported expression ID");
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(root.FindChild("API").FindChild("Receivers"),
                    ExpressionSystemSetup.InputEnabledTag, true, true);
            }
            Check(importedButton.PressedData.Value.Value == originalId, "Imported-menu payload follows the restored expression ID");
        }
        Check(root.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "Invalid imported Flux group");
        Console.WriteLine($"PASS: imported package menu drives all 64 pairs and {distinctPoses.Count} distinct poses");
    }

    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == "Expr/" + name).Value.Value;
    private static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.GetComponents<DynamicReferenceVariable<T>>().Single(v => v.VariableName.Value == "Expr/" + name).Reference.Target;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
