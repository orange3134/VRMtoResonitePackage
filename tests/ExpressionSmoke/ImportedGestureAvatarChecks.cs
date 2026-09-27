using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;

// Optional integration check for a converted avatar with 64 assigned gesture poses (including animated clips).
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
        // Package import restores fields before the mesh assets finish loading.
        var renderers = root.GetComponentsInChildren<DynamicBlendShapeDriver>()
            .Select(driver => driver.Renderer.Target).Distinct().ToArray();
        for (int i = 0; i < 7200 && renderers.Any(renderer => renderer.MeshBlendshapeCount == 0); i++)
            await default(NextUpdate);
        Check(renderers.All(renderer => renderer.MeshBlendshapeCount > 0), "Expression driver meshes finished loading");
        // Keep the exported mixer enabled, but freeze external tracking inputs for
        // this fixed-pose oracle. User/eye updates can otherwise race the sampled
        // Base and Result in the headless frame. ExpressionBlinkChecks separately
        // exercises live native tracking, including cloned and reloaded packages.
        foreach (var output in root.FindChild("Outputs").Children)
        {
            var original = output.ExpressionVariables<DynamicReferenceVariable<ISyncRef>>()
                .SingleOrDefault(v => v.VariableName.Value == "ExpressionSystem.Output/OriginalDriver")?.Reference.Target;
            if (original == null) continue;
            original.Target = null;
            Check(output.WriteDynamicVariable("ExpressionSystem.Output/Base", Get<float>(output, "Baseline")) == DynamicVariableWriteResult.Success,
                "can stabilize tracking Base for fixed-pose comparison");
        }
        for (int i = 0; i < 6; i++) await default(NextUpdate);
        ExpressionGraphChecks.CheckLayout(root);
        string current = ExpressionPackageSnapshot.Capture(root, Path.Combine(artifacts, "current-expressions"));
        if (baselinePackage != null)
        {
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
            Console.WriteLine("PASS: baseline catalog, final pose values, output bindings and all 64 gesture mappings are unchanged");
        }
        string expectedHand = Environment.GetEnvironmentVariable("RESOPON_TEST_KEYBOARD_PRIMARY_HAND");
        await KeyboardPriorityChecks.Run(root, expectedHand == null ? null : int.Parse(expectedHand));
        var core = root.FindChild("Core"); var table = root.FindChild("GestureTable");
        var menu = root.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
        Check(menu.FindChild("Left hand") == null && menu.FindChild("Right hand") == null, "Saved menu has no hand submenus");
        var receiverRoot = root.FindChild("API").FindChild("Receivers");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.InputEnabledTag, true, true);
        var distinctPoses = new HashSet<string>();
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                // Both gesture events run in the same update and resolve the pair immediately.
                int previousRight = Get<int>(core, "RightGesture");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.LeftTag, true, l);
                Check(Get<int>(core, "LeftGesture") == l && Get<int>(core, "RightGesture") == previousRight &&
                    Get<string>(core, "PairKey") == $"L{l}R{previousRight}", "Left gesture event did not evaluate immediately");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.RightTag, true, r);
                Check(Get<int>(core, "LeftGesture") == l && Get<int>(core, "RightGesture") == r, "Gestures did not update both hand states synchronously");
                var mapped = table.ExpressionVariables<DynamicReferenceVariable<Slot>>()
                    .Single(v => v.VariableName.Value == $"ExpressionSystem/GestureTable.Pair.L{l}R{r}").Reference.Target;
                Check(mapped != null && Reference<Slot>(core, "CurrentExpression") == mapped, "Missing or incorrect selected pose");
                Check(Get<string>(core, "PairKey") == $"L{l}R{r}" && Get<bool>(core, "AllowExternalInput"),
                    "Imported pair or input mode disagrees with the selected gesture pair");
                for (int i = 0; i < 6; i++) await default(NextUpdate);
                var pose = ExpressionPackageSnapshot.Pose(mapped);
                var smoothing = root.GetComponentsInChildren<SmoothValue<float>>();
                for (int frame = 0; frame < 600 && smoothing.Any(s => !s.Value.IsLinkValid ||
                    Math.Abs(s.Value.Target.Value - s.TargetValue.Value) > 0.00001f); frame++)
                    await default(NextUpdate);
                Check(smoothing.All(s => s.Value.IsLinkValid && Math.Abs(s.Value.Target.Value - s.TargetValue.Value) <= 0.00001f),
                    "SmoothValue outputs converge to their current targets");
                // The named mesh driver applies the newly settled value on its own update.
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                var values = new List<float>();
                foreach (var output in root.FindChild("Outputs").Children)
                {
                    float baseValue = Get<float>(output, "Base");
                    float expected = pose.TryGetValue(Get<string>(output, "Id"), out float fixedValue) ? fixedValue : baseValue;
                    expected += (baseValue - expected) * Math.Clamp(Get<float>(output, "TrackingWeight"), 0, 1);
                    expected = Get<int>(output, "BlinkMode") switch
                    {
                        1 => Math.Max(expected, baseValue), 2 => Math.Min(expected, baseValue), _ => expected
                    };
                    float actual = Reference<IField<float>>(output, "Target").Value;
                    Check(Math.Abs(expected - actual) < 0.001f, $"Pair {l},{r}: output {Get<string>(output, "Shape")} expected {expected}, got {actual}");
                    values.Add(actual);
                }
                distinctPoses.Add(string.Join(",", values.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine($"PASS: saved gesture Left {l}, Right {r} -> {mapped.Name}, {values.Count} output fields checked");
            }
        string expectedCount = Environment.GetEnvironmentVariable("RESOPON_TEST_EXPECTED_DISTINCT_POSES");
        if (expectedCount != null)
        {
            Check(int.TryParse(expectedCount, out int expected) && expected is >= 2 and <= 64,
                "Expected distinct pose count must be between 2 and 64");
            Check(distinctPoses.Count == expected, $"Expected {expected} distinct visible poses, got {distinctPoses.Count}");
        }
        else Check(distinctPoses.Count >= 8, "Gesture inputs did not produce eight distinct visible poses");
        int visible = 0;
        foreach (var expression in root.FindChild("Catalog").Children)
        {
            bool available = Get<bool>(expression, "Enabled") && expression.IsActive;
            Check(expression.GetComponent<ContextMenuItemSource>().EnabledField.Value &&
                expression.GetComponent<ContextMenuItemSource>().EnabledField.ActiveLink == null,
                "Saved direct menu is enabled independently of table membership and expression state");
            if (!available) continue;
            visible++;
            int leftBefore = Get<int>(core, "LeftGesture"), rightBefore = Get<int>(core, "RightGesture");
            string pairBefore = Get<string>(core, "PairKey");
            expression.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>().Pressed(null, default);
            Check(!Get<bool>(core, "AllowExternalInput") && Get<string>(core, "PairKey") == pairBefore &&
                Get<int>(core, "LeftGesture") == leftBefore && Get<int>(core, "RightGesture") == rightBefore &&
                Reference<Slot>(core, "CurrentExpression") == expression,
                "Saved direct menu preserves gestures and selects the Catalog expression");
        }
        Check(visible >= distinctPoses.Count, "Saved direct menu exposes every distinct mapped pose");
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
        Check(Get<string>(core, "PairKey") == "L0R0", "Saved normal input works after enabling");
        var disable = menu.FindChild("Menu only").GetComponent<ButtonDynamicImpulseTriggerWithValue<bool>>();
        Check(disable.PressedData.Tag.Value == ExpressionSystemSetup.InputEnabledTag && !disable.PressedData.Value.Value,
            "Saved menu-only button sends false");
        disable.Pressed(null, default);
        Check(!Get<bool>(core, "AllowExternalInput"), "Saved menu-only button disables ordinary input");
        Console.WriteLine($"PASS: {visible} Catalog direct-menu entries and both input-mode buttons work without Override state");
        var importedMenu = menu.FindChild("Imported menu");
        // All menu items stay enabled; valid Catalog IDs can select an expression.
        var mappedIds = root.FindChild("Catalog").Children.Where(expression => expression.IsActive && Get<bool>(expression, "Enabled"))
            .Select(expression => Get<string>(expression, "Id")).ToHashSet();
        if (importedMenu != null && !importedMenu.GetComponentsInChildren<ContextMenuItemSource>().Any(item =>
            item.Enabled && mappedIds.Contains(item.Slot.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>()?.PressedData.Value.Value)))
            importedMenu = null;
        if (importedMenu != null)
        {
            var importedButton = importedMenu.GetComponentsInChildren<ButtonDynamicImpulseTriggerWithValue<string>>()
                .FirstOrDefault(button => button.PressedData.Tag.Value == ExpressionSystemSetup.SelectTag && mappedIds.Contains(button.PressedData.Value.Value) &&
                    button.Slot.GetComponent<ContextMenuItemSource>().Enabled);
            Check(importedButton != null, "Imported menu contains an actual string selection button");
            var catalog = root.FindChild("Catalog");
            string originalId = importedButton.PressedData.Value.Value;
            var selected = catalog.Children.Single(entry => Get<string>(entry, "Id") == originalId);
            const string editedId = "Smoke.RenamedImportedExpression";
            Check(catalog.Children.All(entry => Get<string>(entry, "Id") != editedId), "Edited test ID is unique");
            Check(selected.WriteDynamicVariable("ExpressionSystem.Catalog.Clip/Id", editedId) == DynamicVariableWriteResult.Success, "Can edit imported expression ID");
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
                Check(selected.WriteDynamicVariable("ExpressionSystem.Catalog.Clip/Id", originalId) == DynamicVariableWriteResult.Success, "Can restore imported expression ID");
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(root.FindChild("API").FindChild("Receivers"),
                    ExpressionSystemSetup.InputEnabledTag, true, true);
            }
            Check(importedButton.PressedData.Value.Value == originalId, "Imported-menu payload follows the restored expression ID");
        }
        Check(root.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "Invalid imported Flux group");
        Console.WriteLine($"PASS: imported package gesture API drives all 64 pairs and {distinctPoses.Count} distinct poses");
    }

    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
    private static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.ExpressionVariables<DynamicReferenceVariable<T>>().Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Reference.Target;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
