using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

// Optional integration check for every gesture cell, including neutral/release cells.
// Input packages and their paths are supplied locally and are never part of the fixture.
internal static class ImportedGestureAvatarChecks
{
    public static async Task Run(World world, string package, string artifacts, string baselinePackage = null)
    {
        var avatar = world.LocalUser.Root.Slot.AddSlot("Imported gesture avatar");
        await PackageImporter.ImportPackage(package, avatar);
        await default(ToWorld);
        ExpressionTestFields.EquipAvatar(avatar);
        for (int i = 0; i < 180; i++) await default(NextUpdate);
        var root = avatar.FindChild("Expressions") ?? throw new InvalidOperationException("Missing expression system");
        await WaitForExpressionMeshes(root);
        await ImportedBlinkChecks.Run(avatar, root);
        ImportedInitialShapeChecks.Run(avatar);
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
            float frozenBase = ExpressionPackageSnapshot.AuthoredBase(output) ?? Get<float>(output, "Base");
            Check(output.WriteDynamicVariable("ExpressionSystem.Output/Base", frozenBase) == DynamicVariableWriteResult.Success,
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
            await WaitForExpressionMeshes(baselineRoot);
            Console.WriteLine("BASELINE graph (legacy board boundaries are reported, not asserted):");
            ExpressionGraphChecks.Report(baselineRoot);
            string previous = ExpressionPackageSnapshot.Capture(baselineRoot, Path.Combine(artifacts, "baseline-expressions"));
            Check(current == previous, "Expression semantics differ from baseline; compare current-expressions/expressions.json and baseline-expressions/expressions.json");
            baseline.Destroy();
            Console.WriteLine("PASS: baseline catalog, final pose values, output bindings and all gesture mappings are unchanged");
        }
        await KeyboardShortcutChecks.Run(root);
        var core = root.FindChild("API").FindChild("Internal"); var table = root.FindChild("DV").FindChild("GestureTable");
        var menu = root.FindChild("Inputs").FindChild("ContextMenu");
        Check(menu.FindChild("Left hand") == null && menu.FindChild("Right hand") == null, "Saved menu has no hand submenus");
        var receiverRoot = root.FindChild("API");
        SetHandGesturesAllowed(core, true);
        var distinctPoses = new HashSet<string>();
        int gestureCount = (int)Math.Sqrt(table.GetComponentsInChildren<DynamicReferenceVariable<Slot>>().Count);
        for (int l = 0; l < gestureCount; l++)
            for (int r = 0; r < gestureCount; r++)
            {
                // Both gesture events run in the same update and resolve the pair immediately.
                int previousRight = Get<int>(core, "State.Gesture.Right");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureLeftTag, true, l);
                Check(Get<int>(core, "State.Gesture.Left") == l && Get<int>(core, "State.Gesture.Right") == previousRight &&
                    Reference<Slot>(core, "State.CurrentExpression") == Reference<Slot>(table, $"L{l}R{previousRight}"), "Left gesture event did not evaluate immediately");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureRightTag, true, r);
                Check(Get<int>(core, "State.Gesture.Left") == l && Get<int>(core, "State.Gesture.Right") == r, "Gestures did not update both hand states synchronously");
                var mapped = table.ExpressionVariables<DynamicReferenceVariable<Slot>>()
                    .Single(v => v.VariableName.Value == $"ExpressionSystem/GestureTable.L{l}R{r}").Reference.Target;
                Check(Reference<Slot>(core, "State.CurrentExpression") == mapped, "Incorrect selected pose or release");
                Check(BothHandsAllowed(core),
                    "Imported pair or input mode disagrees with the selected gesture pair");
                for (int i = 0; i < 6; i++) await default(NextUpdate);
                var pose = mapped == null ? new SortedDictionary<string, float>() : ExpressionPackageSnapshot.Pose(mapped);
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
                    float expected = pose.TryGetValue(ExpressionPackageSnapshot.OutputIdentity(output), out float fixedValue) ? fixedValue : baseValue;
                    expected += (baseValue - expected) * Math.Clamp(Get<float>(output, "TrackingWeight"), 0, 1);
                    expected = Get<int>(output, "BlinkMode") switch
                    {
                        1 => Math.Max(expected, baseValue), 2 => Math.Min(expected, baseValue), _ => expected
                    };
                    float actual = ExpressionTestFields.OutputTarget(output).Value;
                    Check(Math.Abs(expected - actual) < 0.001f, $"Pair {l},{r}: output {Get<string>(output, "Id")} expected {expected}, got {actual}");
                    values.Add(actual);
                }
                ExpressionGraphChecks.CheckMenuColors(root);
                distinctPoses.Add(string.Join(",", values.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine($"PASS: saved gesture Left {l}, Right {r} -> {mapped?.Name ?? "(release)"}, {values.Count} output fields checked");
            }
        string expectedCount = Environment.GetEnvironmentVariable("RESOPON_TEST_EXPECTED_DISTINCT_POSES");
        if (expectedCount != null)
        {
            Check(int.TryParse(expectedCount, out int expected) && expected >= 2 && expected <= gestureCount * gestureCount,
                "Expected distinct pose count must be between 2 and the gesture table size");
            Check(distinctPoses.Count == expected, $"Expected {expected} distinct visible poses, got {distinctPoses.Count}");
        }
        else Check(distinctPoses.Count >= 8, "Gesture inputs did not produce eight distinct visible poses");
        var directButtons = ExpressionMenuGroupChecks.CheckMenu(root);
        int visible = 0;
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(receiverRoot, ExpressionSystemSetup.ResetTag, true);
        for (int i = 0; i < 5; i++) await default(NextUpdate);
        foreach (var button in directButtons)
        {
            var expression = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
            Check(expression.GetComponent<ContextMenuItemSource>().EnabledField.Value &&
                expression.GetComponent<ContextMenuItemSource>().EnabledField.ActiveLink == null,
                "Saved direct menu is enabled independently of table membership and expression state");
            visible++;
            button.Pressed(null, default);
            Check(BothHandsAllowed(core) && ContextMenuSelected(core) && Gestures(core) == (0, 0) &&
                Reference<Slot>(core, "State.CurrentExpression") == expression,
                "Saved direct menu clears gestures and selects the Clips expression");
            for (int i = 0; i < 2; i++) await default(NextUpdate);
            ExpressionGraphChecks.CheckMenuColors(root);
        }
        Check(visible >= table.GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Select(v => v.Reference.Target).Where(target => target != null).Distinct().Count(),
            "Saved direct menu exposes every distinct mapped clip; release needs no button");
        int heldLeft = Get<int>(core, "State.Gesture.Left"), heldRight = Get<int>(core, "State.Gesture.Right");
        var heldExpression = Reference<Slot>(core, "State.CurrentExpression");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureLeftTag, true, (heldLeft + 1) % gestureCount);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureRightTag, true, (heldRight + 1) % gestureCount);
        heldLeft = (heldLeft + 1) % gestureCount; heldRight = (heldRight + 1) % gestureCount;
        Check(Get<int>(core, "State.Gesture.Left") == heldLeft && Get<int>(core, "State.Gesture.Right") == heldRight &&
            Reference<Slot>(core, "State.CurrentExpression") == heldExpression && ContextMenuSelected(core),
            "Saved menu hold permits hand updates while blocking pose application");
        HandGesturePermissionChecks.PressOption(menu, "Both hands disabled");
        Check(NoHandsAllowed(core) && ContextMenuSelected(core), "permission option preserves the menu hold");
        HandGesturePermissionChecks.PressOption(menu, "Both hands enabled");
        Check(BothHandsAllowed(core) && Get<int>(core, "State.Gesture.Left") == heldLeft && Get<int>(core, "State.Gesture.Right") == heldRight,
            "Enabling ordinary input retains the saved menu pair");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(receiverRoot, ExpressionSystemSetup.ResetTag, true);
        Check(!ContextMenuSelected(core), "saved reset releases menu hold");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureLeftTag, true, 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(receiverRoot, ExpressionSystemSetup.TrySetGestureRightTag, true, 0);
        Check(Gestures(core) == (0, 0), "Saved normal input works after enabling");
        HandGesturePermissionChecks.PressOption(menu, "Both hands disabled");
        Check(NoHandsAllowed(core), "Saved menu-only button disables ordinary input");
        for (int i = 0; i < 2; i++) await default(NextUpdate);
        ExpressionGraphChecks.CheckMenuColors(root);
        Console.WriteLine($"PASS: {visible} Clips direct-menu entries and the hand gesture toggle work without Override state");
        {
            var groupedButton = directButtons[0];
            var selected = groupedButton.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
            var selectedItem = selected.GetComponent<ContextMenuItemSource>();
            string originalLabel = selectedItem.Label.Value;
            const string editedLabel = "Edited grouped expression label";
            selectedItem.Label.Value = editedLabel;
            Check(selectedItem.Label.ActiveLink == null && selectedItem.Label.Value == editedLabel, "Can edit grouped menu label directly");
            try
            {
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(receiverRoot, ExpressionSystemSetup.ResetTag, true);
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                Check(groupedButton.PressedData.Reference.Target == selected, "Saved grouped-menu Slot reference survives a label edit");
                SetHandGesturesAllowed(core, true);
                Check(BothHandsAllowed(core), "Ordinary input is enabled before grouped button verification");
                groupedButton.Pressed(null, default);
                Check(BothHandsAllowed(core) && ContextMenuSelected(core) && Reference<Slot>(core, "State.CurrentExpression") == selected,
                    "Saved grouped-menu button selects by Slot independently of its label");
                Console.WriteLine("PASS: saved grouped-menu button selects its Slot synchronously despite label edits");
            }
            finally
            {
                selectedItem.Label.Value = originalLabel;
                for (int i = 0; i < 2; i++) await default(NextUpdate);
                SetHandGesturesAllowed(core, true);
            }
            Check(groupedButton.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target == selected &&
                groupedButton.PressedData.Reference.Target == null, "Selected grouped menu retains its expression but sends null after label edits");
        }
        Check(root.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "Invalid imported Flux group");
        await ExpressionResetChecks.Run(root);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(receiverRoot, ExpressionSystemSetup.ResetTag, true);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        ImportedInitialShapeChecks.Run(avatar);
        Console.WriteLine($"PASS: imported package gesture API drives all {gestureCount * gestureCount} pairs and {distinctPoses.Count} distinct poses");
    }

    private static async Task WaitForExpressionMeshes(Slot root)
    {
        // Both imported packages restore fields before their mesh assets finish loading.
        // Without this wait, baseline shape names can still be null in the snapshot.
        var renderers = root.GetComponentsInChildren<DynamicBlendShapeDriver>()
            .Select(driver => driver.Renderer.Target).Distinct().ToArray();
        for (int i = 0; i < 7200 && renderers.Any(renderer => renderer.MeshBlendshapeCount == 0); i++)
            await default(NextUpdate);
        Check(renderers.All(renderer => renderer.MeshBlendshapeCount > 0), "Expression driver meshes finished loading");
    }

    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
    private static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.ExpressionVariables<DynamicReferenceVariable<T>>().Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Reference.Target;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
