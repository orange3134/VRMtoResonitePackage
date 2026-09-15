using Elements.Assets;
using FrooxEngine;
using FrooxEngine.ProtoFlux;

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
                var button = (hand == 0 ? left : right).Children[gesture].GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
                var command = button.PressedData.Reference.Target;
                Check(Get<int>(command, "Hand") == hand && Get<int>(command, "Gesture") == gesture && Get<bool>(command, "Available"),
                    "Saved menu command contains correct fixed values");
            }
        var distinctPoses = new HashSet<string>();
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                // Exercise the actual button receivers without patching Command fields.
                left.Children[l].GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>().Pressed(null, default);
                right.Children[r].GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>().Pressed(null, default);
                for (int i = 0; i < 6; i++) await default(NextUpdate);
                Check(Get<int>(core, "LeftGesture") == l && Get<int>(core, "RightGesture") == r, "Menu did not update both hand states");
                var mapped = table.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
                    .Single(v => v.VariableName.Value == "Expr/Pair." + (l * 8 + r)).Reference.Target;
                Check(mapped != null && Reference<Slot>(core, "CurrentExpression") == mapped, "Missing or incorrect selected pose");
                Check(Get<int>(core, "PairIndex") == l * 8 + r && Get<int>(core, "SelectionStatus") == 1 &&
                    Reference<Slot>(core, "MappedExpression") == mapped && Reference<Slot>(core, "CandidateExpression") == mapped,
                    "Imported selection diagnostics disagree with the selected gesture pair");
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
        Check(root.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "Invalid imported Flux group");
        Console.WriteLine($"PASS: imported package menu drives all 64 pairs and {distinctPoses.Count} distinct poses");
    }

    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == "Expr/" + name).Value.Value;
    private static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.GetComponents<DynamicReferenceVariable<T>>().Single(v => v.VariableName.Value == "Expr/" + name).Reference.Target;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
