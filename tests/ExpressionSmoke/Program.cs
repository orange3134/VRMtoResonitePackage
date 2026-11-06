using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage.Expressions;
using User = FrooxEngine.User;
using static ExpressionTestFields;

string resonite = Environment.GetEnvironmentVariable("RESONITE_PATH") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(resonite, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
string artifacts = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp_verify/expression-smoke/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
string importedPackage = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
string baselinePackage = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
try { await Run(resonite, artifacts, importedPackage, baselinePackage); Console.WriteLine("Expression smoke checks passed."); Environment.Exit(0); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Run(string resonite, string artifacts, string importedPackage, string baselinePackage)
{
    VrmToResonitePackage.ResoniteLocator.InstallAssemblyResolver(resonite);
    Environment.CurrentDirectory = resonite;
    Directory.CreateDirectory(artifacts);
    ParserChecks.Run(artifacts);
    CompilerChecks.Run();
    var runner = new StandaloneFrooxEngineRunner();
    await runner.Initialize(new LaunchOptions { DataDirectory = Path.Combine(artifacts, "Data"), CacheDirectory = Path.Combine(artifacts, "Cache"),
        LogsDirectory = Path.Combine(artifacts, "Logs"), DoNotAutoLoadHome = true, StartInvisible = true, NeverSaveSettings = true,
        NeverSaveDash = true, DisablePlatformInterfaces = true });
    var world = await Userspace.OpenWorld(new WorldStartSettings { AutoFocus = true, CreateLoadIndicator = false, InitWorld = delegate { } });
    await world.Coroutines.StartTask(async () =>
    {
        await default(ToWorld);
        world.ForceFullUpdateCycle = true;
        for (int i = 0; i < 30; i++) await default(NextUpdate);
        world.LocalUser.Root ??= world.AddSlot("Wearer").AttachComponent<UserRoot>();
        await default(NextUpdate);
        await NamedShapeRepairChecks.Run(world.LocalUser.Root.Slot);
        await FaceExpressionDetectionChecks.RunCatalog(world.LocalUser.Root.Slot);
        if (importedPackage != null) { await ImportedGestureAvatarChecks.Run(world, importedPackage, artifacts, baselinePackage); return; }
        await GeneratedAvatarChecks.Run(world.LocalUser.Root.Slot, artifacts);
        await ExpressionBindingNameChecks.Run(world.LocalUser.Root.Slot, artifacts);
        await ExpressionOutputWriteChecks.Run(world.LocalUser.Root.Slot);
        await ExpressionBlinkChecks.Run(world.LocalUser.Root.Slot, artifacts);
        await ExpressionMeshDriverChecks.Run(world.LocalUser.Root.Slot, artifacts);
        ExpressionLayoutChecks.CheckFixtures(world.LocalUser.Root.Slot);
        var avatar = world.LocalUser.Root.Slot.AddSlot("Expression smoke avatar");
        var field = avatar.AttachComponent<ValueField<float>>().Value; field.Value = 0.2f;
        var tracking = avatar.AddSlot("Tracking").AttachComponent<ValueField<float>>(); tracking.Value.Value = 0.2f;
        var trackingDriver = avatar.AttachComponent<ValueCopy<float>>();
        trackingDriver.Source.Target = tracking.Value; trackingDriver.Target.Target = field;
        var model = new ExpressionModel();
        model.Diagnostics.Add("Synthetic warning for expression diagnostics logging coverage.");
        foreach (string hand in new[] { "Left", "Right" })
        {
            model.Parameters["Gesture" + hand] = new("Gesture" + hand, 3, 0);
            model.Parameters["Gesture" + hand + "Weight"] = new("Gesture" + hand + "Weight", 1, 0);
        }
        foreach (var (name, value) in new[] { ("Smile", 1f), ("Angry", 0.7f) })
        {
            var clip = new ExpressionClip { Id = name, Name = name, Duration = 1 };
            var curve = new ExpressionCurve { Binding = new("Face", "Smile") };
            curve.Keys.Add(new(0, value, 0, 0)); curve.Keys.Add(new(1, value, 0, 0)); clip.Curves.Add(curve); model.Clips.Add(clip);
        }
        var layerModel = new ExpressionLayer { Id = "hands", Name = "Hands", DefaultState = 0 };
        layerModel.States.Add(new("Neutral", null, 1, true));
        layerModel.States.Add(new("Left", "Smile", 1, true));
        layerModel.States.Add(new("Both", "Angry", 1, true));
        foreach (var (left, right, destination) in new[] { (1, 1, 2), (1, 0, 1), (0, 0, 0), (0, 1, 0) })
        {
            var transition = new ExpressionTransition { Destination = destination, FixedDuration = true, Duration = 0.05f };
            transition.Conditions.Add(new("GestureLeft", 6, left)); transition.Conditions.Add(new("GestureRight", 6, right));
            layerModel.Transitions.Add(transition);
        }
        model.Layers.Add(layerModel);
        var menuLayer = new ExpressionLayer { Id = "menu", Name = "Menu" };
        menuLayer.States.Add(new("Neutral", null, 1, true));
        menuLayer.States.Add(new("Angry", "Angry", 1, true));
        foreach (var (mode, destination) in new[] { (6, 1), (7, 0) })
        {
            var transition = new ExpressionTransition { Destination = destination };
            transition.Conditions.Add(new("FaceMenu", mode, 1));
            menuLayer.Transitions.Add(transition);
        }
        model.Layers.Add(menuLayer);
        model.Menu.Add(new ExpressionMenuControl { Name = "Imported angry", Parameter = "FaceMenu", Value = 1, Type = 1 });
        var animatedClip = new ExpressionClip { Id = "Animated", Name = "Animated", Duration = 10 };
        var animatedCurve = new ExpressionCurve { Binding = new("Face", "Smile") };
        animatedCurve.Keys.Add(new(0, 0, 0.1f, 0.1f)); animatedCurve.Keys.Add(new(10, 1, 0.1f, 0.1f));
        animatedClip.Curves.Add(animatedCurve); model.Clips.Add(animatedClip);
        var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, _ => field);
        EquipAvatar(avatar);
        Console.WriteLine("Built graph");
        for (int i = 0; i < 90; i++) await default(NextUpdate);
        Check(expressions.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "all generated ProtoFlux groups are valid");
        ExpressionLayoutChecks.SaveKeyboardLayout(expressions, Path.Combine(artifacts, "keyboard-layout.json"));
        ExpressionGraphChecks.CheckLayout(expressions);
        var directGroups = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
        var expressionGroups = directGroups.Children.Where(group => group.Name is "Hand sign expressions" or "Other expressions").ToArray();
        var mappedExpressions = expressions.FindChild("DV").FindChild("GestureTable")
            .GetComponentsInChildren<DynamicReferenceVariable<Slot>>().Select(row => row.Reference.Target)
            .Where(target => target != null).ToHashSet();
        var menuTargets = new HashSet<Slot>();
        Check(expressionGroups.Length == 2, "mixed Clips offers separate hand sign and other submenus directly under Expressions");
        foreach (var group in expressionGroups)
        foreach (var entry in group.FindChild("Items").Children.Where(entry => entry.Name != "Back"))
        {
            var target = entry.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>().PressedData.Reference.Target;
            Check(menuTargets.Add(target), "direct selection lists each expression once despite repeated gesture mappings");
            Check(mappedExpressions.Contains(target) == (group.Name == "Hand sign expressions"),
                "direct menu classification matches the final 64 gesture assignments");
        }
        Check(menuTargets.SetEquals(expressions.FindChild("Clips").Children),
            "both submenus together retain every Clips expression");
        await ExpressionDynamicInputChecks.CheckEdits(expressions);
        await HandGesturePermissionChecks.Run(expressions);
        await KeyboardShortcutChecks.Run(expressions);
        await ExpressionInputEventChecks.Run(expressions);
        var core = expressions.FindChild("API").FindChild("Internal"); var api = expressions.FindChild("API");
        var catalog = expressions.FindChild("Clips"); var table = expressions.FindChild("DV").FindChild("GestureTable");
        string[] gestureNames = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };
        void Request(string tag, int gesture)
        {
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, gesture) == 1,
                "exactly one int gesture receiver for " + tag);
        }
        void Gesture(int hand, int gesture) => Request(hand == 0 ? ExpressionSystemSetup.TrySetGestureLeftTag : ExpressionSystemSetup.TrySetGestureRightTag, gesture);
        void Apply(string name)
        {
            // Prepare neutral hand state for playback checks without selecting a table row.
            Set(core, "State.Gesture.Left", 0); Set(core, "State.Gesture.Right", 0);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                "ExpressionSystem/Internal.ApplyExpression", true, catalog.FindChild(name));
        }
        void AllowInput(bool enabled = true) => SetHandGesturesAllowed(core, enabled);
        async Task Frames(int count = 30) { for (int i = 0; i < count; i++) await default(NextUpdate); }
        Check(core.FindChild("SourceState") == null && core.FindChild("ParameterState") == null && expressions.FindChild("Rules") == null,
            "generic source arbitration and Animator graph are absent");
        Console.WriteLine($"Flux nodes: {expressions.GetComponentsInChildren<ProtoFluxNode>().Count}; Core: {core.GetComponentsInChildren<ProtoFluxNode>().Count}");
        Check(core.ExpressionVariables<DynamicReferenceVariable<Slot>>().All(v => v.VariableName.Value is not "ExpressionSystem/LeftInput" and not "ExpressionSystem/RightInput"),
            "int requests retain no input Slot references");
        foreach (string tag in new[] { ExpressionSystemSetup.TrySetGestureLeftTag, ExpressionSystemSetup.TrySetGestureRightTag })
        {
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, tag, true) == 0 &&
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, "Left.Fist") == 0 &&
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<string>(api, tag, true, null) == 0 &&
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, 1f) == 0 &&
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, catalog) == 0,
                "gesture API requires int arguments: " + tag);
        }
        foreach (string tag in new[] { "Left", "Right", "left", "right", "Left.Fist", "ResoPon/Expression/v3/Gesture" })
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, 1) == 0,
                "unknown or legacy gesture Tag is rejected: " + tag);
        for (int hand = 0; hand < 2; hand++)
            for (int gesture = 0; gesture < gestureNames.Length; gesture++)
            {
                int otherBefore = Get<int>(core, hand == 0 ? "State.Gesture.Right" : "State.Gesture.Left");
                Gesture(hand, gesture);
                Check(Get<int>(core, hand == 0 ? "State.Gesture.Left" : "State.Gesture.Right") == gesture &&
                    Get<int>(core, hand == 0 ? "State.Gesture.Right" : "State.Gesture.Left") == otherBefore &&
                    Reference<Slot>(core, "State.CurrentExpression") == Reference<Slot>(table, $"L{Get<int>(core, "State.Gesture.Left")}R{Get<int>(core, "State.Gesture.Right")}"),
                    $"{(hand == 0 ? "Left" : "Right")}.{gestureNames[gesture]} updates only its hand and evaluates the pair synchronously");
            }
        var pairVariables = table.ExpressionVariables<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal)).ToArray();
        var expectedKeys = (from left in Enumerable.Range(0, 8) from right in Enumerable.Range(0, 8)
                            select $"ExpressionSystem/GestureTable.L{left}R{right}").ToHashSet();
        Check(pairVariables.Length == 64 && expectedKeys.SetEquals(pairVariables.Select(v => v.VariableName.Value)),
            "all 64 generated table keys explicitly name their left and right gestures");
        AllowInput();
        for (int left = 0; left < 8; left++)
        for (int right = 0; right < 8; right++)
        {
            Gesture(0, left); Gesture(1, right);
            Check(Gestures(core) == (left, right) &&
                Reference<Slot>(core, "State.CurrentExpression") == Reference<Slot>(table, $"L{left}R{right}"),
                $"L{left}R{right} directly selects its named table entry");
        }
        var previousHighPair = Reference<Slot>(table, "L6R7");
        var decoy = table.AddSlot("Legacy numeric key (ignored)");
        var legacyPair = decoy.AttachComponent<DynamicReferenceVariable<Slot>>();
        legacyPair.VariableName.Value = "ExpressionSystem/GestureTable.Pair.55";
        legacyPair.Reference.Target = catalog.FindChild("Angry");
        Set(table, "L6R7", catalog.FindChild("Animated")); await Frames();
        Gesture(0, 6); Gesture(1, 7);
        Check(Gestures(core) == (6, 7) && Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated"),
            "selection ignores a conflicting legacy numeric key");
        Gesture(0, 0); Gesture(1, 0); Apply("Animated");
        Check(Get<int>(core, "State.Gesture.Left") == 0 && Get<int>(core, "State.Gesture.Right") == 0 && Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated"),
            "direct selection preserves both gestures");
        Set(table, "L6R7", previousHighPair); decoy.Destroy();

        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        Gesture(0, 1);
        Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile"),
            "left event selects Smile before the next frame");
        Gesture(1, 1);
        Check(Gestures(core) == (1, 1) && Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry"),
            "back-to-back right event immediately evaluates the latest state of both hands");
        Gesture(1, 0); await Frames();
        Check(Get<int>(core, "State.Gesture.Left") == 1 && Get<int>(core, "State.Gesture.Right") == 0 && Math.Abs(field.Value - 1) < 0.01,
            "left gesture selects Smile without modifying the right hand");
        Check(Gestures(core) == (1, 0) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile"),
            "validated table entry is the current expression");
        Gesture(0, 1);
        Check(Get<int>(core, "State.Gesture.Left") == 1,
            "same gesture preserves the hand value without restarting playback");
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "same expression retains its fixed pose");
        Gesture(1, 1); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "both-hand table entry selects Angry");
        foreach (int extended in new[] { int.MinValue, -1, 8, 255, int.MaxValue })
        {
            int right = extended == 8 ? 255 : extended;
            string key = $"L{extended}R{right}";
            Gesture(0, extended); Gesture(1, right);
            Check(Get<int>(core, "State.Gesture.Left") == extended && Get<int>(core, "State.Gesture.Right") == right &&
                Reference<Slot>(core, "State.CurrentExpression") == null &&
                BothHandsAllowed(core),
                "extended gestures are retained even without a table entry: " + key);
            var row = table.AddSlot(key);
            var mapping = row.AttachComponent<DynamicReferenceVariable<Slot>>();
            mapping.VariableName.Value = "ExpressionSystem/GestureTable." + key;
            mapping.Reference.Target = catalog.FindChild("Angry");
            await Frames();
            Check(Reference<Slot>(core, "State.CurrentExpression") == null, "adding a row waits for a gesture event");
            Gesture(1, right); await Frames();
            Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry") &&
                Math.Abs(field.Value - 0.7f) < 0.01,
                "next gesture event plays the externally added extended pair: " + key);
        }
        Gesture(0, 1); Gesture(1, 1);
        await Frames();
        Check(BothHandsAllowed(core), "ordinary input is initially enabled");
        Apply("Smile"); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Math.Abs(field.Value - 1) < 0.01 && BothHandsAllowed(core) &&
            Gestures(core) == (0, 0),
            "playback applies the prepared pose without changing permissions or holding input");
        AllowInput(false);
        var foreignParent = avatar.AddSlot("Foreign catalog");
        var foreign = catalog.FindChild("Smile").Duplicate(foreignParent);
        var inactive = catalog.FindChild("Smile").Duplicate(catalog); inactive.ActiveSelf = false;
        try
        {
            await Frames(3);
            Set(core, "State.Gesture.Left", 3); Set(core, "State.Gesture.Right", 4);
            foreach (Slot invalid in new[] { avatar, catalog, foreign })
            {
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<Slot>(api, ExpressionSystemSetup.MenuSelectTag, true, invalid);
                Check(NoHandsAllowed(core) && Gestures(core) == (3, 4) &&
                    Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile"), "invalid Slot preserves menu selection");
            }
            foreach (bool catalogActive in new[] { true, false })
            {
                catalog.ActiveSelf = catalogActive;
                await Frames(3);
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument<Slot>(api, ExpressionSystemSetup.MenuSelectTag, true, inactive);
                Check(Reference<Slot>(core, "State.CurrentExpression") == inactive && NoHandsAllowed(core) &&
                    Gestures(core) == (0, 0), "direct selection accepts an inactive clip even under an inactive Clips");
                await Frames();
                Check(Math.Abs(field.Value - 1) < 0.01, "inactive direct selection applies its pose");
            }
            catalog.ActiveSelf = true;
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
            Apply("Smile");
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, "Smile") == 0,
                "menu selection does not accept string payloads");
        }
        finally { catalog.ActiveSelf = true; foreignParent.Destroy(); inactive.Destroy(); }
        Gesture(0, 0); Gesture(1, 0); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01 && Gestures(core) == (0, 0) &&
            NoHandsAllowed(core),
            "menu-only mode ignores normal input without altering direct selection");
        foreach (string hand in new[] { "Left", "Right" })
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                "ResoPon/Expression/Menu/" + hand, true, 8) == 0, "removed hand-menu API is absent: " + hand);
        // Neither data edits nor changing input permission can overwrite a direct selection.
        Set(core, "State.Gesture.Left", 6); Set(core, "State.Gesture.Right", 7);
        Set(table, "L6R7", catalog.FindChild("Angry"));
        AllowInput(false); AllowInput(); await Frames();
        Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile") &&
            Gestures(core) == (6, 7), "data and permission edits do not reevaluate gesture selection");
        Gesture(1, 7);
        Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry") &&
            Gestures(core) == (6, 7), "next gesture event replaces direct selection using both stored gestures");
        Set(table, "L6R7", previousHighPair);
        Gesture(0, 1); Gesture(1, 1); Apply("Smile");
        AllowInput(); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(BothHandsAllowed(core) && Gestures(core) == (0, 0) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile"),
            "enabling ordinary input retains direct selection until the next gesture");
        Gesture(0, 0); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01 && Gestures(core) == (0, 0), "ordinary input changes expressions after enabling");
        AllowInput(false);
        Gesture(0, 1);
        Check(Gestures(core) == (0, 0) && NoHandsAllowed(core), "bool API can explicitly disable ordinary input");
        AllowInput();

        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "unmapped expression keeps its menu item enabled");

        Apply("Animated");
        Check(Math.Abs(SelectedValue(expressions.FindChild("Outputs").FindChild("Smile")) - 1) < 0.001f,
            "animated clip stores its final key before the next frame");
        await Frames(40);
        Check(Math.Abs(field.Value - 1) < 0.001f, "animated clip remains fixed without playback");
        catalog.FindChild("Animated").ActiveSelf = false;
        Set(table, "L0R2", catalog.FindChild("Animated"));
        AllowInput(); Gesture(1, 2);
        Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated") &&
            Math.Abs(SelectedValue(expressions.FindChild("Outputs").FindChild("Smile")) - 1) < 0.001f,
            "inactive mapped expression is selected synchronously");
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "inactive mapped expression applies its pose");
        Set(table, "L0R2", (Slot)null); Gesture(1, 2); await Frames();
        Check(Reference<Slot>(core, "State.CurrentExpression") == null && Math.Abs(field.Value - 0.2f) < 0.01,
            "unassigned mapping still clears the expression and restores Base");
        catalog.FindChild("Animated").ActiveSelf = true;
        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "removing the last mapping keeps the expression menu item enabled");
        AllowInput(); Gesture(0, 1); Gesture(1, 1);

        // Table keys are stable dynamic names; deleting/reordering rows cannot shift other mappings.
        Set(table, "L1R1", catalog.FindChild("Smile"));
        await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "editing a table reference waits for input");
        Gesture(1, 1); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Math.Abs(field.Value - 1) < 0.01, "next gesture applies the edited table reference");
        Gesture(1, 0); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "different pair sharing the same clip retains its pose");
        table.FindChild("L1R0").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "deleting a row preserves selection until input");
        Gesture(1, 0); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "next gesture falls back to base for the deleted row");
        Gesture(1, 1); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Math.Abs(field.Value - 1) < 0.01, "deleting row 8 does not shift row 9");

        var touch = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Touch");

        await Frames();
        Check(Get<int>(core, "State.Gesture.Left") == 1 && Get<int>(core, "State.Gesture.Right") == 1 &&
            Math.Abs(field.Value - 1) < 0.01,
            "inactive controllers retain the last accepted hand values and expression without restarting playback");

        Gesture(0, 2); await Frames();
        Check(Get<int>(core, "State.Gesture.Left") == 2 && Get<int>(core, "State.Gesture.Right") == 1,
            "the next accepted input replaces only its hand while controllers remain inactive");
        Gesture(0, 0); await Frames();
        Check(Get<int>(core, "State.Gesture.Left") == 0 && Get<int>(core, "State.Gesture.Right") == 1,
            "explicit Neutral clears only the requested hand");
        Gesture(0, 1); await Frames();
        touch.Destroy(); await Frames();
        Check(Get<int>(core, "State.Gesture.Left") == 1, "deleting a controller module preserves the last accepted int state");
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
        Check(menu.FindChild("Left hand") == null && menu.FindChild("Right hand") == null,
            "context menu has no hand submenus");
        var menuButton = directGroups.FindChild("Hand sign expressions").FindChild("Items").FindChild("Smile")
            .GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        Gesture(0, 0); await Frames(2);
        menuButton.Pressed(null, default);
        Check(Gestures(core) == (0, 0) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Smile"),
            "actual expression menu button clears both gestures and selects directly");
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "actual menu button applies the selected pose");
        var otherButton = directGroups.FindChild("Other expressions").FindChild("Items").FindChild("Animated")
            .GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        Set(core, "State.Gesture.Left", 5); Set(core, "State.Gesture.Right", 6);
        otherButton.Pressed(null, default);
        Check(Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated") &&
            Gestures(core) == (0, 0) && BothHandsAllowed(core) && ContextMenuSelected(core),
            "other submenu clears both gestures and selects an unmapped expression");
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
        Check(keyboard.Children.Count == 2, "keyboard exposes settings for each hand");
        var shortcut = keyboard.FindChild("Right").FindChild("DV").FindChild("Tag");
        Request(Get<string>(shortcut, "Tag"), 7); await Frames();
        Check(Get<int>(core, "State.Gesture.Right") == 7 && BothHandsAllowed(core) && ContextMenuSelected(core) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated"),
            "keyboard binding cannot overwrite a menu selection");
        AllowInput();
        Request(Get<string>(shortcut, "Tag"), 7); await Frames();
        Check(Get<int>(core, "State.Gesture.Right") == 7 && ContextMenuSelected(core) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Animated"), "enabling gestures does not release the menu hold");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        Request(Get<string>(shortcut, "Tag"), 7);
        Check(Get<int>(core, "State.Gesture.Right") == 7, "keyboard binding resumes after reset");

        var directExpression = catalog.FindChild("Smile");
        var directButton = directExpression.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        var directItem = directExpression.GetComponent<ContextMenuItemSource>();
        string originalLabel = directItem.Label.Value;
        string originalName = directExpression.Name;
        Check(directItem.Label.ActiveLink == null && originalLabel == originalName,
            "menu label is initialized directly from the expression name");
        directExpression.Name = "Renamed expression slot";
        await Frames(2);
        Check(directItem.Label.Value == originalLabel, "renaming the Slot does not overwrite its menu label");
        directItem.Label.Value = "Edited expression label";
        await Frames(2);
        Check(directItem.Label.Value == "Edited expression label" && directButton.PressedData.Reference.Target == directExpression,
            "menu label is editable without changing the Slot selection target");
        Check(menuButton.Slot.GetComponent<ContextMenuItemSource>().Label.Value == "Edited expression label",
            "Clips label edits also update the visible grouped menu");
        AllowInput();
        directButton.Pressed(null, default);
        Check(BothHandsAllowed(core) && ContextMenuSelected(core) && Reference<Slot>(core, "State.CurrentExpression") == directExpression,
            "direct menu selects by Slot without Clip metadata");
        directExpression.Name = originalName;
        directItem.Label.Value = originalLabel;
        await Frames(2);

        var addedExpression = catalog.Children.First().Duplicate(catalog);
        addedExpression.Name = "Added expression from Clips";
        addedExpression.GetComponent<ContextMenuItemSource>().Label.Value = "Added expression";
        await Frames(30);
        var addedButton = addedExpression.GetComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        Check(addedButton.PressedData.Tag.Value == ExpressionSystemSetup.MenuSelectTag && addedButton.PressedData.Reference.Target == addedExpression,
            "copied Clips menu reference remaps to the new entry");
        AllowInput();
        addedButton.Pressed(null, default);
        Check(BothHandsAllowed(core) && ContextMenuSelected(core) && Reference<Slot>(core, "State.CurrentExpression") == addedExpression,
            "copied Clips entry is directly selectable without a GestureTable mapping");
        var addedBinding = addedExpression.ExpressionVariables<DynamicValueVariable<float>>().First();
        Check(addedBinding.Slot.WriteDynamicVariable(addedBinding.VariableName.Value, 0.65f) == DynamicVariableWriteResult.Success, "write the named copied value");
        await Frames();
        Check(addedButton.PressedData.Reference.Target == null, "copied selected entry sends null");
        addedButton.Pressed(null, default); await Frames();
        Check(Reference<Slot>(core, "State.CurrentExpression") == null && Math.Abs(field.Value - 0.2f) < 0.01,
            "pressing the copied entry again clears its pose");
        addedButton.Pressed(null, default); await Frames();
        Check(Math.Abs(field.Value - 0.65f) < 0.01, "selecting the copied entry again applies edited bindings");
        ExpressionGraphChecks.CheckMenuColors(expressions);
        addedExpression.Destroy(); AllowInput(); await Frames();

        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        AllowInput(false);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, catalog.FindChild("Smile"));
        await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        var clone = avatar.Duplicate(avatar.Parent); EquipAvatar(clone); await Frames(90);
        ExpressionGraphChecks.CheckLayout(clone.FindChild("Expressions"));
        Check(Math.Abs(clone.GetComponent<ValueField<float>>().Value.Value - 0.2f) < 0.01, "cloning resets transient selection");
        Check(Math.Abs(field.Value - 1f) < 0.01, "cloning does not reset original");
        await ExpressionDynamicInputChecks.CheckEdits(clone.FindChild("Expressions"));
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        ExpressionGraphChecks.CheckMenuColors(clone.FindChild("Expressions"));
        ExpressionGraphChecks.CheckMenuColors(expressions);
        var cloneCore = clone.FindChild("Expressions").FindChild("API").FindChild("Internal");
        Check(Gestures(cloneCore) == (0, 0) && NoHandsAllowed(cloneCore) && !ContextMenuSelected(cloneCore) && ContextMenuSelected(core),
            "clone resets hand inputs and preserves disabled ordinary input");
        SetHandGesturesAllowed(cloneCore, true);
        var cloneApi = clone.FindChild("Expressions").FindChild("API");
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(cloneApi,
            ExpressionSystemSetup.TrySetGestureLeftTag, true, 1) == 1, "clone has its own int request receiver");
        Check(Get<int>(cloneCore, "State.Gesture.Left") == 1 && Get<int>(cloneCore, "State.Gesture.Right") == 0 &&
            Gestures(core) == (0, 0) && NoHandsAllowed(core),
            "clone int requests update only the clone and leave the original hand state and input mode unchanged");
        await KeyboardShortcutChecks.Run(clone.FindChild("Expressions"));
        await HandGesturePermissionChecks.Run(clone.FindChild("Expressions"));
        await ExpressionResetChecks.Run(clone.FindChild("Expressions"));
        await ExpressionMenuToggleChecks.Run(clone.FindChild("Expressions"));
        Check(Gestures(core) == (0, 0) && NoHandsAllowed(core),
            "clone reset leaves the original input state unchanged");
        clone.Destroy();
        catalog.FindChild("Smile").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting selected expression restores base");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        Set(table, "L0R0", (Slot)null);
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        tracking.Value.Value = 0.4f; await Frames();
        Check(Math.Abs(field.Value - 0.4f) < 0.01, "existing tracking driver continues through proxy");
        Set(table, "L0R2", catalog.FindChild("Angry"));
        Apply("Angry");
        Check(Math.Abs(SelectedValue(expressions.FindChild("Outputs").FindChild("Smile")) - 0.7f) < 0.001f,
            "selection immediately writes the tracked pose");
        await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.001f, "tracking applies the final pose without interpolation");
        var wearer = avatar.Parent;
        // Departure retains state but suppresses the pose, even with mapped Neutral.
        Apply("Angry");
        AllowInput(false);
        Set(core, "State.Gesture.Left", 3); Set(core, "State.Gesture.Right", 4);
        Set(table, "L0R0", catalog.FindChild("Angry"));
        avatar.Parent = world.RootSlot;
        await Frames();
        Check(NoHandsAllowed(core) && Gestures(core) == (3, 4) &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry") &&
            Math.Abs(field.Value - 0.4f) < 0.01,
            "wearer departure retains state while the output returns to Base");
        Set(table, "L0R0", (Slot)null);
        AllowInput();
        Gesture(0, 1); Gesture(1, 2);
        Check(Get<int>(core, "State.Gesture.Left") == 1 && Get<int>(core, "State.Gesture.Right") == 2 &&
            Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry"),
            "unworn gesture APIs update both hands while Internal Selection retains its wearer gate");
        Apply("Animated");
        Check(BothHandsAllowed(core) && Gestures(core) == (0, 0) && Reference<Slot>(core, "State.CurrentExpression") == catalog.FindChild("Angry"),
            "unworn Playback rejects the pose with prepared neutral hand state");
        AllowInput(false);
        Gesture(0, 7); Gesture(1, 7);
        Check(Gestures(core) == (0, 0),
            "unworn TrySetGesture still checks each hand permission");
        Request(ExpressionSystemSetup.SetGestureLeftTag, 3); Request(ExpressionSystemSetup.SetGestureRightTag, 4);
        Check(Get<int>(core, "State.Gesture.Left") == 3 && Get<int>(core, "State.Gesture.Right") == 4 && NoHandsAllowed(core),
            "unworn SetGesture updates both hands independently of permission");
        expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Hand gestures")
            .GetComponent<ButtonDynamicImpulseTrigger>().Pressed(null, default);
        Check(BothHandsAllowed(core), "unworn combined toggle enables both hands");
        expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Hand gestures")
            .GetComponent<ButtonDynamicImpulseTrigger>().Pressed(null, default);
        Check(NoHandsAllowed(core), "unworn combined toggle disables both hands");
        // Saved/manual state may still contain a pose while the avatar is unworn.
        Set(core, "State.CurrentExpression", catalog.FindChild("Angry"));
        Set(table, "L0R0", catalog.FindChild("Angry"));
        expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Reset settings")
            .GetComponent<ButtonDynamicImpulseTrigger>().Pressed(null, default);
        await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(NoHandsAllowed(core) && Gestures(core) == (0, 0) &&
            Reference<Slot>(core, "State.CurrentExpression") == null && Math.Abs(field.Value - 0.4f) < 0.01,
            "unworn Reset clears hands and saved selection, preserves permissions and restores Base without selecting Neutral");
        Set(table, "L0R0", (Slot)null);
        Request(ExpressionSystemSetup.SetGestureLeftTag, 3); Request(ExpressionSystemSetup.SetGestureRightTag, 4);
        AllowInput(false);
        // Selection still requires a wearer; ResetState now accepts unworn requests.
        var outputState = expressions.FindChild("Outputs").Children.Single();
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, "ExpressionSystem/Internal.UpdateSelection", true) == 1,
            "shared API hierarchy reaches exactly one UpdateSelection receiver");
        await Frames();
        Check(Gestures(core) == (3, 4) && NoHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == null &&
            Math.Abs(Get<float>(outputState, "Result") - 0.4f) < 0.01f,
            "UpdateSelection still rejects updates without a local wearer");
        Set(core, "State.CurrentExpression", catalog.FindChild("Angry"));
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, "ExpressionSystem/Internal.ResetState", true) == 1,
            "shared API hierarchy reaches exactly one ResetState receiver");
        Check(Gestures(core) == (0, 0) && NoHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == null,
            "direct ResetState synchronously resets an unworn avatar");
        Request(ExpressionSystemSetup.SetGestureLeftTag, 3); Request(ExpressionSystemSetup.SetGestureRightTag, 4);
        AllowInput(false);
        // A loaded/cloned instance with no wearer must not mutate shared state just
        // because its local initialization flag starts false.
        var unwornClone = avatar.Duplicate(world.RootSlot);
        await Frames();
        var unwornCore = unwornClone.FindChild("Expressions").FindChild("API").FindChild("Internal");
        Check(Gestures(unwornCore) == (3, 4) && Reference<Slot>(unwornCore, "State.CurrentExpression") == null && NoHandsAllowed(unwornCore),
            "an unworn clone does not initialize or repeatedly clear stored state");
        unwornClone.Parent = wearer;
        EquipAvatar(unwornClone);
        await Frames();
        Check(Gestures(unwornCore) == (0, 0) && NoHandsAllowed(unwornCore) &&
            Math.Abs(unwornClone.GetComponent<ValueField<float>>().Value.Value - 0.4f) < 0.01,
            "first wear initializes a previously unworn clone and restores Base while preserving permissions");
        unwornClone.Destroy();
        avatar.Parent = wearer;
        await Frames();
        Check(Gestures(core) == (0, 0) && NoHandsAllowed(core) && Reference<Slot>(core, "State.CurrentExpression") == null,
            "rewear resets retained gestures and expression while preserving disabled permissions");
        AllowInput();
        Gesture(0, 1); Gesture(1, 1);
        Check(Get<int>(core, "State.Gesture.Left") == 1 && Get<int>(core, "State.Gesture.Right") == 1 &&
            BothHandsAllowed(core),
            "first events after AvatarWornLocal is restored retain both hand requests");
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        await Frames();
        Check(BothHandsAllowed(core) &&
            Gestures(core) == (0, 0) && Math.Abs(field.Value - 0.4f) < 0.01,
            "reattaching initializes hand state, selection and tracking");
        AllowInput(false);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, catalog.FindChild("Angry"));
        await Frames();
        var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
        var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, world.LocalUser.MachineID, null);
        string packagePath = Path.Combine(artifacts, "Expression.resonitepackage");
        await default(ToBackground);
        using (var stream = File.Create(packagePath)) await PackageCreator.BuildPackage(world.Engine, record, graph, stream, includeVariants: false);
        await default(ToWorld);
        var restored = avatar.Parent.AddSlot("Restored");
        await PackageImporter.ImportPackage(packagePath, restored);
        await default(ToWorld);
        EquipAvatar(restored);
        for (int i = 0; i < 120; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.4f) < 0.01, "package reload discards active requests and restores tracking");
        Check(restored.GetComponentsInChildren<StaticAnimationProvider>().Count == 0, "saved poses reload without animation assets");
        var restoredExpressions = restored.FindChild("Expressions");
        ExpressionGraphChecks.CheckLayout(restoredExpressions);
        await ExpressionDynamicInputChecks.CheckEdits(restoredExpressions);
        var restoredCore = restoredExpressions.FindChild("API").FindChild("Internal");
        Check(Gestures(restoredCore) == (0, 0) &&
            Reference<Slot>(restoredCore, "State.CurrentExpression") == null && NoHandsAllowed(restoredCore) &&
            !ContextMenuSelected(restoredCore) && ContextMenuSelected(core),
            "package reload resets hand inputs and selection while preserving saved permissions");
        SetHandGesturesAllowed(restoredCore, true);
        Set(restoredExpressions.FindChild("DV").FindChild("GestureTable"), "L0R2", restoredExpressions.FindChild("Clips").FindChild("Angry"));
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredExpressions.FindChild("API"),
            ExpressionSystemSetup.TrySetGestureRightTag, true, 2) == 1, "reloaded int request receiver is connected");
        Check(Gestures(restoredCore) == (0, 2) &&
            Reference<Slot>(restoredCore, "State.CurrentExpression") == restoredExpressions.FindChild("Clips").FindChild("Angry"),
            "reloaded receiver evaluates the pair before the next frame");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.7f) < 0.01, "reloaded stock ProtoFlux applies a fixed pose without converter callbacks");
        var restoredApi = restoredExpressions.FindChild("API");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.TrySetGestureLeftTag, true, 8);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.TrySetGestureRightTag, true, 255);
        Check(Gestures(restoredCore) == (8, 255) &&
            Reference<Slot>(restoredCore, "State.CurrentExpression") == restoredExpressions.FindChild("Clips").FindChild("Angry"),
            "saved external pair and unrestricted gesture receivers work after package reload");
        SetHandGesturesAllowed(restoredCore, false);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.TrySetGestureRightTag, true, 0);
        Check(Get<int>(restoredCore, "State.Gesture.Right") == 255, "reloaded gesture permission blocks gesture API");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.SetGestureLeftTag, true, 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.SetGestureRightTag, true, 2);
        Check(Gestures(restoredCore) == (0, 2) && NoHandsAllowed(restoredCore) &&
            Reference<Slot>(restoredCore, "State.CurrentExpression") == restoredExpressions.FindChild("Clips").FindChild("Angry"),
            "reloaded keyboard API selects a pose without enabling hand gestures");
        await ExpressionResetChecks.Run(expressions);
        await HandGesturePermissionChecks.Run(restoredExpressions);
        await KeyboardShortcutChecks.Run(restoredExpressions);
        await ExpressionResetChecks.Run(restoredExpressions);
        await ExpressionMenuToggleChecks.Run(expressions);
        await ExpressionMenuToggleChecks.Run(restoredExpressions);
        // Curves with equal endpoints may still have a tangent excursion.
        var testCurve = new ExpressionCurve { Binding = new("Face", "Curve") };
        testCurve.Keys.Add(new(0, 0, 0, 4)); testCurve.Keys.Add(new(1, 0, -4, 0));
        Check(Math.Abs(testCurve.Sample(0.5f) - 1) < 0.0001, "source Hermite curve remains available for compile-time composition");
    });
}

static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicVariableBase<T>>().Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).DynamicValue;

static void Set<T>(Slot slot, string name, T value)
{
    var result = slot.WriteDynamicVariable(ExpressionTestFields.VariablePath(slot, name), value);
    if (result != DynamicVariableWriteResult.Success) throw new InvalidOperationException("Cannot write " + name + ": " + result);
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
