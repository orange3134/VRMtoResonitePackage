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
                Check(button != null && button.PressedData.Tag.Value == (hand == 0 ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag) &&
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
                Check(Get<int>(core, "PairIndex") == l * 8 + r && Get<int>(core, "SelectionStatus") == 1 &&
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
        var importedMenu = menu.FindChild("Imported menu");
        if (importedMenu != null)
        {
            var importedButton = importedMenu.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithValue<string>>()
                .FirstOrDefault(button => button.PressedData.Tag.Value == ExpressionSystemSetup.SelectTag);
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
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.AutomaticTag, true, "");
                Check(Reference<Slot>(core, "Override") == null, "Automatic clears the override before imported button verification");
                importedButton.Pressed(null, default);
                Check(Reference<Slot>(core, "Override") == selected && Reference<Slot>(core, "CurrentExpression") == selected,
                    "Saved imported-menu button did not select the expression by its edited ID");
                Console.WriteLine("PASS: saved imported-menu button follows edited expression ID and selects it synchronously");
            }
            finally
            {
                Check(selected.WriteDynamicVariable("Expr/Id", originalId) == DynamicVariableWriteResult.Success, "Can restore imported expression ID");
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(root.FindChild("API").FindChild("Receivers"),
                    ExpressionSystemSetup.AutomaticTag, true, "");
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
