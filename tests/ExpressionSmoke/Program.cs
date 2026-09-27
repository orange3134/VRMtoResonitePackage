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
        model.Diagnostics.Add("Synthetic warning for expression record scope coverage.");
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
        await ExpressionDynamicInputChecks.CheckEdits(expressions);
        await KeyboardPriorityChecks.Run(expressions, 0);
        await ExpressionInputEventChecks.Run(expressions);
        var core = expressions.FindChild("Core"); var api = expressions.FindChild("API").FindChild("Receivers");
        var catalog = expressions.FindChild("Catalog"); var table = expressions.FindChild("DV").FindChild("GestureTable");
        string[] gestureNames = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };
        void Request(string tag, int gesture)
        {
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, tag, true, gesture) == 1,
                "exactly one int gesture receiver for " + tag);
        }
        void Gesture(int hand, int gesture) => Request(hand == 0 ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, gesture);
        void Select(string name) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
            ExpressionSystemSetup.SelectTag, true, Get<string>(catalog.FindChild(name), "Id"));
        void AllowInput(bool enabled = true) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
            ExpressionSystemSetup.HandGesturesEnabledTag, true, enabled);
        async Task Frames(int count = 30) { for (int i = 0; i < count; i++) await default(NextUpdate); }
        Check(core.FindChild("SourceState") == null && core.FindChild("ParameterState") == null && expressions.FindChild("Rules") == null,
            "generic source arbitration and Animator graph are absent");
        Console.WriteLine($"Flux nodes: {expressions.GetComponentsInChildren<ProtoFluxNode>().Count}; Core: {core.GetComponentsInChildren<ProtoFluxNode>().Count}");
        Check(core.ExpressionVariables<DynamicReferenceVariable<Slot>>().All(v => v.VariableName.Value is not "ExpressionSystem/Core.LeftInput" and not "ExpressionSystem/Core.RightInput"),
            "int requests retain no input Slot references");
        foreach (string tag in new[] { ExpressionSystemSetup.LeftTag, ExpressionSystemSetup.RightTag })
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
                int otherBefore = Get<int>(core, hand == 0 ? "RightGesture" : "LeftGesture");
                Gesture(hand, gesture);
                Check(Get<int>(core, hand == 0 ? "LeftGesture" : "RightGesture") == gesture &&
                    Get<int>(core, hand == 0 ? "RightGesture" : "LeftGesture") == otherBefore &&
                    Get<string>(core, "PairKey") == $"L{Get<int>(core, "LeftGesture")}R{Get<int>(core, "RightGesture")}",
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
            Check(Get<string>(core, "PairKey") == $"L{left}R{right}" &&
                Reference<Slot>(core, "CurrentExpression") == Reference<Slot>(table, $"L{left}R{right}"),
                $"L{left}R{right} directly selects its named table entry");
        }
        var previousHighPair = Reference<Slot>(table, "L6R7");
        var decoy = table.AddSlot("Legacy numeric key (ignored)");
        var legacyPair = decoy.AttachComponent<DynamicReferenceVariable<Slot>>();
        legacyPair.VariableName.Value = "ExpressionSystem/GestureTable.Pair.55";
        legacyPair.Reference.Target = catalog.FindChild("Angry");
        Set(table, "L6R7", catalog.FindChild("Animated")); await Frames();
        Gesture(0, 6); Gesture(1, 7);
        Check(Get<string>(core, "PairKey") == "L6R7" && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Animated"),
            "selection ignores a conflicting legacy numeric key");
        Gesture(0, 0); Gesture(1, 0); Select("Animated");
        Check(Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 0 &&
            Get<string>(core, "PairKey") == "L0R0" && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Animated"),
            "direct selection preserves both gestures and the last evaluated pair key");
        Set(table, "L6R7", previousHighPair); decoy.Destroy();

        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        Gesture(0, 1);
        Check(Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "left event selects Smile before the next frame");
        Gesture(1, 1);
        Check(Get<string>(core, "PairKey") == "L1R1" && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry"),
            "back-to-back right event immediately evaluates the latest state of both hands");
        Gesture(1, 0); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0 && Math.Abs(field.Value - 1) < 0.01,
            "left gesture selects Smile without modifying the right hand");
        Check(Get<string>(core, "PairKey") == "L1R0" &&
            Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "validated table entry is the current expression");
        Gesture(0, 1);
        Check(Get<int>(core, "LeftGesture") == 1,
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
            Check(Get<int>(core, "LeftGesture") == extended && Get<int>(core, "RightGesture") == right &&
                Get<string>(core, "PairKey") == key && Reference<Slot>(core, "CurrentExpression") == null &&
                Get<bool>(core, "AllowHandGestures"),
                "extended gestures are retained even without a table entry: " + key);
            var row = table.AddSlot(key);
            var mapping = row.AttachComponent<DynamicReferenceVariable<Slot>>();
            mapping.VariableName.Value = "ExpressionSystem/GestureTable." + key;
            mapping.Reference.Target = catalog.FindChild("Angry");
            await Frames();
            Check(Reference<Slot>(core, "CurrentExpression") == null, "adding a row waits for a gesture event");
            Gesture(1, right); await Frames();
            Check(Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry") &&
                Math.Abs(field.Value - 0.7f) < 0.01,
                "next gesture event plays the externally added extended pair: " + key);
        }
        Gesture(0, 1); Gesture(1, 1);
        await Frames();
        Check(Get<bool>(core, "AllowHandGestures"), "ordinary input is initially enabled");
        Select("Smile"); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Math.Abs(field.Value - 1) < 0.01 && !Get<bool>(core, "AllowHandGestures") &&
            Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 && Get<string>(core, "PairKey") == "L1R1",
            "direct selection disables ordinary input without changing either gesture");
        foreach (string invalidId in new string[] { null, "", "Missing expression", "smile", "Left.Fist" })
        {
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, invalidId);
            Check(!Get<bool>(core, "AllowHandGestures") && Get<string>(core, "PairKey") == "L1R1" &&
                Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"), "invalid ID preserves menu selection");
        }
        Gesture(0, 0); Gesture(1, 0); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01 && Get<string>(core, "PairKey") == "L1R1" &&
            Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 &&
            !Get<bool>(core, "AllowHandGestures"),
            "menu-only mode ignores normal input without altering direct selection");
        foreach (string hand in new[] { "Left", "Right" })
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                "ResoPon/Expression/Menu/" + hand, true, 8) == 0, "removed hand-menu API is absent: " + hand);
        // Neither data edits nor changing input permission can overwrite a direct selection.
        Set(core, "LeftGesture", 6); Set(core, "RightGesture", 7);
        Set(table, "L6R7", catalog.FindChild("Angry"));
        AllowInput(false); AllowInput(); await Frames();
        Check(Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile") &&
            Get<string>(core, "PairKey") == "L1R1", "data and permission edits do not reevaluate gesture selection");
        Gesture(1, 7);
        Check(Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry") &&
            Get<string>(core, "PairKey") == "L6R7", "next gesture event replaces direct selection using both stored gestures");
        Set(table, "L6R7", previousHighPair);
        Gesture(0, 1); Gesture(1, 1); Select("Smile");
        AllowInput(); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(Get<bool>(core, "AllowHandGestures") && Get<string>(core, "PairKey") == "L1R1" &&
            Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "enabling ordinary input retains direct selection until the next gesture");
        Gesture(0, 0); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01 && Get<string>(core, "PairKey") == "L0R1", "ordinary input changes expressions after enabling");
        AllowInput(false);
        Gesture(0, 1);
        Check(Get<string>(core, "PairKey") == "L0R1" && !Get<bool>(core, "AllowHandGestures"), "bool API can explicitly disable ordinary input");
        AllowInput();

        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "unmapped expression keeps its menu item enabled");

        Select("Animated");
        Check(Math.Abs(Get<float>(expressions.FindChild("Outputs").FindChild("Smile"), "Pose") - 1) < 0.001f,
            "animated clip stores its final key before the next frame");
        await Frames(40);
        Check(Math.Abs(field.Value - 1) < 0.001f, "animated clip remains fixed without playback");
        Set(catalog.FindChild("Animated"), "Enabled", false);
        Set(table, "L0R2", catalog.FindChild("Animated"));
        AllowInput(); Gesture(1, 2);
        Check(Reference<Slot>(core, "CurrentExpression") == null && !Get<bool>(expressions.FindChild("Outputs").FindChild("Smile"), "HasPose"),
            "invalid selection immediately clears the tracked pose");
        await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "disabled mapped expression restores base output");
        Set(table, "L0R2", (Slot)null); await Frames();
        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "removing the last mapping keeps a disabled expression menu item enabled");
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

        var touch = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules").FindChild("Touch");

        await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 &&
            Math.Abs(field.Value - 1) < 0.01,
            "inactive controllers retain the last accepted hand values and expression without restarting playback");

        Gesture(0, 2); await Frames();
        Check(Get<int>(core, "LeftGesture") == 2 && Get<int>(core, "RightGesture") == 1,
            "the next accepted input replaces only its hand while controllers remain inactive");
        Gesture(0, 0); await Frames();
        Check(Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 1,
            "explicit Neutral clears only the requested hand");
        Gesture(0, 1); await Frames();
        touch.Destroy(); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1, "deleting a controller module preserves the last accepted int state");
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items");
        Check(menu.FindChild("Left hand") == null && menu.FindChild("Right hand") == null,
            "context menu has no hand submenus");
        var menuButton = catalog.FindChild("Smile").GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>();
        Gesture(0, 0);
        menuButton.Pressed(null, default);
        Check(Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 1 &&
            Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "actual expression menu button selects directly without changing gestures");
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "actual menu button applies the selected pose");
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
        Check(keyboard.Children.Count == 2, "keyboard exposes settings for each hand");
        var shortcut = keyboard.FindChild("Right").FindChild("DV").FindChild("Tag");
        Request(Get<string>(shortcut, "Tag"), 7); await Frames();
        Check(Get<int>(core, "RightGesture") == 7 && !Get<bool>(core, "AllowHandGestures"),
            "keyboard binding remains accepted after direct menu selection disables hand gestures");
        AllowInput();
        Request(Get<string>(shortcut, "Tag"), 7); await Frames();
        Check(Get<int>(core, "RightGesture") == 7, "keyboard binding works after enabling ordinary input");

        var directExpression = catalog.FindChild("Smile");
        var directButton = directExpression.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>();
        string originalId = Get<string>(directExpression, "Id");
        Set(directExpression, "Id", "Smoke.RenamedSmile"); await Frames(2);
        Check(directButton.PressedData.Value.Value == "Smoke.RenamedSmile", "direct menu payload follows the expression's edited ID");
        AllowInput();
        directButton.Pressed(null, default);
        Check(!Get<bool>(core, "AllowHandGestures") && Reference<Slot>(core, "CurrentExpression") == directExpression,
            "direct menu selects the expression by its edited ID without renaming the Slot");
        Set(directExpression, "Id", originalId); await Frames(2);
        Check(directButton.PressedData.Value.Value == originalId, "direct menu payload follows the restored expression ID");

        var template = expressions.FindChild("API").FindChild("Templates").Children.Single();
        var addedExpression = template.Duplicate(catalog);
        addedExpression.Name = "Added expression from template";
        Set(addedExpression, "Id", "Smoke.AddedTemplate"); Set(addedExpression, "Enabled", true);
        await Frames(30);
        var addedButton = addedExpression.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>();
        Check(addedButton.PressedData.Tag.Value == ExpressionSystemSetup.SelectTag && addedButton.PressedData.Value.Value == "Smoke.AddedTemplate",
            "copied template menu payload follows the new Catalog entry's unique ID");
        AllowInput();
        addedButton.Pressed(null, default);
        Check(!Get<bool>(core, "AllowHandGestures") && Reference<Slot>(core, "CurrentExpression") == addedExpression,
            "copied template is directly selectable without a GestureTable mapping");
        var addedBinding = addedExpression.FindChild("Bindings").Children.First();
        Set(addedBinding, "Value", 0.65f);
        addedButton.Pressed(null, default); await Frames();
        Check(Math.Abs(field.Value - 0.65f) < 0.01, "reselecting the same expression refreshes edited bindings");
        ExpressionGraphChecks.CheckMenuColors(expressions);
        addedExpression.Destroy(); AllowInput(); await Frames();

        Select("Smile"); await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        var clone = avatar.Duplicate(avatar.Parent); EquipAvatar(clone); await Frames(90);
        Check(Math.Abs(clone.GetComponent<ValueField<float>>().Value.Value - 0.2f) < 0.01, "cloning resets transient selection");
        Check(Math.Abs(field.Value - 1f) < 0.01, "cloning does not reset original");
        await ExpressionDynamicInputChecks.CheckEdits(clone.FindChild("Expressions"));
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        ExpressionGraphChecks.CheckMenuColors(clone.FindChild("Expressions"));
        ExpressionGraphChecks.CheckMenuColors(expressions);
        var cloneCore = clone.FindChild("Expressions").FindChild("Core");
        Check(Get<string>(cloneCore, "PairKey") == "L0R0" && Get<bool>(cloneCore, "AllowHandGestures"),
            "clone diagnostics reflect reset hand inputs and ordinary input enabled");
        var cloneApi = clone.FindChild("Expressions").FindChild("API").FindChild("Receivers");
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(cloneApi,
            ExpressionSystemSetup.LeftTag, true, 1) == 1, "clone has its own int request receiver");
        Check(Get<int>(cloneCore, "LeftGesture") == 1 && Get<int>(cloneCore, "RightGesture") == 0 && Get<string>(cloneCore, "PairKey") == "L1R0" &&
            Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 7 && !Get<bool>(core, "AllowHandGestures"),
            "clone int requests update only the clone and leave the original hand state and input mode unchanged");
        await ExpressionResetChecks.Run(clone.FindChild("Expressions"));
        Check(Get<int>(core, "RightGesture") == 7 && !Get<bool>(core, "AllowHandGestures"),
            "clone reset leaves the original input state unchanged");
        clone.Destroy();
        catalog.FindChild("Smile").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting selected expression restores base");
        Set(table, "L0R0", (Slot)null);
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        tracking.Value.Value = 0.4f; await Frames();
        Check(Math.Abs(field.Value - 0.4f) < 0.01, "existing tracking driver continues through proxy");
        Set(table, "L0R2", catalog.FindChild("Angry"));
        Select("Angry");
        Check(Math.Abs(Get<float>(expressions.FindChild("Outputs").FindChild("Smile"), "Pose") - 0.7f) < 0.001f,
            "selection immediately writes the tracked pose");
        await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.001f, "tracking applies the final pose without interpolation");
        var wearer = avatar.Parent;
        // Departure must clear output directly, even when Neutral maps to a clip.
        Set(table, "L0R0", catalog.FindChild("Angry"));
        avatar.Parent = world.RootSlot;
        await Frames();
        Check(Get<bool>(core, "AllowHandGestures") && Get<string>(core, "PairKey") == "L0R0" &&
            Reference<Slot>(core, "CurrentExpression") == null &&
            Math.Abs(field.Value - 0.4f) < 0.01,
            "wearer departure restores base instead of playing the mapped Neutral expression");
        Set(table, "L0R0", (Slot)null);
        Gesture(0, 1); Select("Angry"); await Frames();
        Check(Get<int>(core, "LeftGesture") == 0 && Get<bool>(core, "AllowHandGestures"),
            "gesture and select requests are ignored without a local wearer");
        Set(core, "AllowHandGestures", false);
        expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Hand gestures")
            .GetComponent<ButtonDynamicImpulseTrigger>().Pressed(null, default);
        AllowInput();
        expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Reset settings")
            .GetComponent<ButtonDynamicImpulseTrigger>().Pressed(null, default);
        await Frames();
        ExpressionGraphChecks.CheckMenuColors(expressions);
        Check(!Get<bool>(core, "AllowHandGestures"),
            "input-mode, toggle and reset requests are ignored without a local wearer");
        // Sentinel selection state proves wearer-only private stages did no work.
        var outputState = expressions.FindChild("Outputs").Children.Single();
        Set(core, "PairKey", "__unchanged");
        foreach (var (board, tag) in new[] { ("Selection", "ResoPon/Expression/Internal/Selection"),
            ("Lifecycle", "ResoPon/Expression/Internal/Initialize") })
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(core.FindChild("Logic").FindChild(board), tag, true) == 1,
                "private stage receiver remains discoverable: " + board);
        await Frames();
        Check(Get<string>(core, "PairKey") == "__unchanged" &&
            Math.Abs(Get<float>(outputState, "Result") - 0.4f) < 0.01f,
            "private stages also reject updates without a local wearer");
        // A loaded/cloned instance with no wearer must not mutate shared state just
        // because its local initialization flag starts false.
        var unwornClone = avatar.Duplicate(world.RootSlot);
        await Frames();
        var unwornCore = unwornClone.FindChild("Expressions").FindChild("Core");
        Check(Get<string>(unwornCore, "PairKey") == "__unchanged" && !Get<bool>(unwornCore, "AllowHandGestures"),
            "an unworn clone does not initialize or repeatedly clear stored state");
        unwornClone.Parent = wearer;
        EquipAvatar(unwornClone);
        await Frames();
        Check(Get<string>(unwornCore, "PairKey") == "L0R0" && Get<bool>(unwornCore, "AllowHandGestures") &&
            Math.Abs(unwornClone.GetComponent<ValueField<float>>().Value.Value - 0.4f) < 0.01,
            "first wear initializes a previously unworn clone and restores its base output");
        unwornClone.Destroy();
        avatar.Parent = wearer;
        await Frames();
        Gesture(0, 1); Gesture(1, 1);
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 && Get<string>(core, "PairKey") == "L1R1" &&
            Get<bool>(core, "AllowHandGestures"),
            "first events after AvatarWornLocal is restored retain both hand requests");
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        await Frames();
        Check(Get<bool>(core, "AllowHandGestures") &&
            Get<string>(core, "PairKey") == "L0R0" && Math.Abs(field.Value - 0.4f) < 0.01,
            "reattaching initializes hand state, selection, diagnostics and tracking");
        Select("Angry"); await Frames();
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
        var restoredCore = restoredExpressions.FindChild("Core");
        Check(Get<string>(restoredCore, "PairKey") == "L0R0" &&
            Reference<Slot>(restoredCore, "CurrentExpression") == null && Get<bool>(restoredCore, "AllowHandGestures"),
            "package reload recomputes diagnostics from reset inputs and the edited empty table row");
        Set(restoredExpressions.FindChild("DV").FindChild("GestureTable"), "L0R2", restoredExpressions.FindChild("Catalog").FindChild("Angry"));
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredExpressions.FindChild("API").FindChild("Receivers"),
            ExpressionSystemSetup.RightTag, true, 2) == 1, "reloaded int request receiver is connected");
        Check(Get<int>(restoredCore, "RightGesture") == 2 && Get<string>(restoredCore, "PairKey") == "L0R2" &&
            Reference<Slot>(restoredCore, "CurrentExpression") == restoredExpressions.FindChild("Catalog").FindChild("Angry"),
            "reloaded receiver evaluates the pair before the next frame");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.7f) < 0.01, "reloaded stock ProtoFlux applies a fixed pose without converter callbacks");
        var restoredApi = restoredExpressions.FindChild("API").FindChild("Receivers");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.LeftTag, true, 8);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.RightTag, true, 255);
        Check(Get<string>(restoredCore, "PairKey") == "L8R255" &&
            Reference<Slot>(restoredCore, "CurrentExpression") == restoredExpressions.FindChild("Catalog").FindChild("Angry"),
            "saved external pair and unrestricted gesture receivers work after package reload");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.HandGesturesEnabledTag, true, false);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.RightTag, true, 0);
        Check(Get<int>(restoredCore, "RightGesture") == 255, "reloaded gesture permission blocks gesture API");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.KeyboardLeftTag, true, 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredApi, ExpressionSystemSetup.KeyboardRightTag, true, 2);
        Check(Get<string>(restoredCore, "PairKey") == "L0R2" && !Get<bool>(restoredCore, "AllowHandGestures") &&
            Reference<Slot>(restoredCore, "CurrentExpression") == restoredExpressions.FindChild("Catalog").FindChild("Angry"),
            "reloaded keyboard API selects a pose without enabling hand gestures");
        await ExpressionResetChecks.Run(expressions);
        await ExpressionResetChecks.Run(restoredExpressions);
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
