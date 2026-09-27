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
        Console.WriteLine("Built graph");
        for (int i = 0; i < 90; i++) await default(NextUpdate);
        Check(expressions.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "all generated ProtoFlux groups are valid");
        ExpressionLayoutChecks.SaveKeyboardLayout(expressions, Path.Combine(artifacts, "keyboard-layout.json"));
        ExpressionGraphChecks.CheckLayout(expressions);
        await ExpressionDynamicInputChecks.CheckEdits(expressions);
        await KeyboardPriorityChecks.Run(expressions, 0);
        await ExpressionInputEventChecks.Run(expressions);
        var core = expressions.FindChild("Core"); var api = expressions.FindChild("API").FindChild("Receivers");
        var catalog = expressions.FindChild("Catalog"); var table = expressions.FindChild("GestureTable");
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
            ExpressionSystemSetup.InputEnabledTag, true, enabled);
        void MenuGesture(int hand, int gesture) => Request(hand == 0 ? ExpressionSystemSetup.MenuLeftTag : ExpressionSystemSetup.MenuRightTag, gesture);
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
                    Get<int>(core, "PairIndex") == Get<int>(core, "LeftGesture") * 8 + Get<int>(core, "RightGesture"),
                    $"{(hand == 0 ? "Left" : "Right")}.{gestureNames[gesture]} updates only its hand and evaluates the pair synchronously");
            }
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        Gesture(0, 1);
        Check(Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "left event selects Smile before the next frame");
        Gesture(1, 1);
        Check(Get<int>(core, "PairIndex") == 9 && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry"),
            "back-to-back right event immediately evaluates the latest state of both hands");
        Gesture(1, 0); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0 && Math.Abs(field.Value - 1) < 0.01,
            "left gesture selects Smile without modifying the right hand");
        Check(Get<int>(core, "PairIndex") == 8 &&
            Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "validated table entry is the current expression");
        Gesture(0, 1);
        Check(Get<int>(core, "LeftGesture") == 1,
            "same gesture preserves the hand value without restarting playback");
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "same expression retains its fixed pose");
        Gesture(1, 1); await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "both-hand table entry selects Angry");
        foreach (int malformed in new[] { int.MinValue, -1, 8, 255, int.MaxValue })
        {
            Gesture(0, malformed);
            Gesture(1, malformed);
            Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 &&
                Get<int>(core, "PairIndex") == 9 && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry") &&
                Get<bool>(core, "AllowExternalInput"),
                "malformed gesture leaves hand state and playback unchanged: " + malformed);
        }
        await Frames();
        Check(Get<bool>(core, "AllowExternalInput"), "ordinary input is initially enabled");
        Select("Smile"); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01 && !Get<bool>(core, "AllowExternalInput") &&
            Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0 && Get<int>(core, "PairIndex") == 8,
            "menu selection disables ordinary input and updates the normal hand pair");
        foreach (string invalidId in new string[] { null, "", "Missing expression", "smile", "Left.Fist", "Animated" })
        {
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, invalidId);
            Check(!Get<bool>(core, "AllowExternalInput") && Get<int>(core, "PairIndex") == 8 &&
                Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"), "invalid or unmapped ID preserves menu selection");
        }
        Gesture(0, 0); Gesture(1, 1); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01 && Get<int>(core, "PairIndex") == 8 &&
            Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0 &&
            !Get<bool>(core, "AllowExternalInput"),
            "menu-only mode ignores normal input without altering hand values");
        MenuGesture(1, 1);
        Check(!Get<bool>(core, "AllowExternalInput") && Get<int>(core, "PairIndex") == 9 &&
            Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Angry"),
            "menu can update the other hand while ordinary input is disabled");
        MenuGesture(0, -1); MenuGesture(1, 8);
        Check(Get<int>(core, "PairIndex") == 9 && !Get<bool>(core, "AllowExternalInput"), "invalid menu values leave pair and mode unchanged");
        AllowInput();
        Check(Get<bool>(core, "AllowExternalInput") && Get<int>(core, "PairIndex") == 9,
            "enabling ordinary input retains the selected pair");
        Gesture(0, 0); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01 && Get<int>(core, "PairIndex") == 1, "ordinary input changes expressions after enabling");
        AllowInput(false);
        Gesture(0, 1);
        Check(Get<int>(core, "PairIndex") == 1 && !Get<bool>(core, "AllowExternalInput"), "bool API can explicitly disable ordinary input");
        AllowInput();

        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "unmapped expression keeps its menu item enabled");
        Set(table, "Pair.2", catalog.FindChild("Animated")); await Frames();
        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "assigning an expression keeps its menu item enabled");
        Select("Animated");
        Check(Math.Abs(Get<float>(expressions.FindChild("Outputs").FindChild("Smile"), "Pose") - 1) < 0.001f,
            "animated clip stores its final key before the next frame");
        await Frames(40);
        Check(Math.Abs(field.Value - 1) < 0.001f, "animated clip remains fixed without playback");
        Set(catalog.FindChild("Animated"), "Enabled", false);
        AllowInput();
        Check(Reference<Slot>(core, "CurrentExpression") == null && !Get<bool>(expressions.FindChild("Outputs").FindChild("Smile"), "HasPose"),
            "invalid selection immediately clears the tracked pose");
        await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "disabled mapped expression restores base output");
        Set(table, "Pair.2", (Slot)null); await Frames();
        Check(catalog.FindChild("Animated").GetComponent<ContextMenuItemSource>().Enabled,
            "removing the last mapping keeps a disabled expression menu item enabled");
        AllowInput(); Gesture(0, 1); Gesture(1, 1);

        // Table keys are stable dynamic names; deleting/reordering rows cannot shift other mappings.
        Set(table, "Pair.9", catalog.FindChild("Smile"));
        await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "editing table reference takes effect");
        Gesture(1, 0); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "different pair sharing the same clip retains its pose");
        expressions.FindChild("DV").FindChild("GestureTable.Pair.8").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleted table row falls back to base");
        Gesture(1, 1); await Frames();
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
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Left hand").FindChild("Items");
        var menuButton = menu.Children[1].GetComponent<ButtonDynamicImpulseTriggerWithValue<int>>();
        Check(menuButton.PressedData.Tag.Value == ExpressionSystemSetup.MenuLeftTag && menuButton.PressedData.Value.Value == 1,
            "menu button contains the hand Tag and authored int payload");
        Gesture(0, 0);
        menuButton.Pressed(null, default);
        Check(Get<int>(core, "LeftGesture") == 1 && Reference<Slot>(core, "CurrentExpression") == catalog.FindChild("Smile"),
            "actual menu button evaluates its int request synchronously");
        await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Math.Abs(field.Value - 1) < 0.01,
            "actual menu button trigger updates hand state and expression using its int payload");
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
        Check(keyboard.Children.Count == 2, "keyboard exposes settings for each hand");
        var shortcut = keyboard.FindChild("Right").FindChild("DV").FindChild("Tag");
        Request(Get<string>(shortcut, "Tag"), 7); await Frames();
        Check(Get<int>(core, "RightGesture") == 1, "keyboard binding is ignored in menu-only mode");
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
        Check(!Get<bool>(core, "AllowExternalInput") && Reference<Slot>(core, "CurrentExpression") == directExpression,
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
        Check(Get<bool>(core, "AllowExternalInput"), "unmapped template selection does not lock input");
        Set(table, "Pair.3", addedExpression); await Frames();
        Check(addedExpression.GetComponent<ContextMenuItemSource>().Enabled, "mapped template becomes selectable");
        addedButton.Pressed(null, default);
        Check(!Get<bool>(core, "AllowExternalInput") && Reference<Slot>(core, "CurrentExpression") == addedExpression,
            "copied template's actual string button selects the newly enabled Catalog entry");
        addedExpression.Destroy(); Set(table, "Pair.3", (Slot)null); AllowInput(); await Frames();

        Select("Smile"); await Frames();
        var clone = avatar.Duplicate(avatar.Parent); await Frames(90);
        Check(Math.Abs(clone.GetComponent<ValueField<float>>().Value.Value - 0.2f) < 0.01, "cloning resets transient selection");
        Check(Math.Abs(field.Value - 1f) < 0.01, "cloning does not reset original");
        await ExpressionDynamicInputChecks.CheckEdits(clone.FindChild("Expressions"));
        ExpressionDynamicInputChecks.CheckBindings(expressions);
        var cloneCore = clone.FindChild("Expressions").FindChild("Core");
        Check(Get<int>(cloneCore, "PairIndex") == 0 && Get<bool>(cloneCore, "AllowExternalInput"),
            "clone diagnostics reflect reset hand inputs and ordinary input enabled");
        var cloneApi = clone.FindChild("Expressions").FindChild("API").FindChild("Receivers");
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(cloneApi,
            ExpressionSystemSetup.LeftTag, true, 1) == 1, "clone has its own int request receiver");
        Check(Get<int>(cloneCore, "LeftGesture") == 1 && Get<int>(cloneCore, "RightGesture") == 0 && Get<int>(cloneCore, "PairIndex") == 8 &&
            Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 && !Get<bool>(core, "AllowExternalInput"),
            "clone int requests update only the clone and leave the original hand state and input mode unchanged");
        clone.Destroy();
        catalog.FindChild("Smile").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting selected expression restores base");
        Set(table, "Pair.0", (Slot)null);
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        tracking.Value.Value = 0.4f; await Frames();
        Check(Math.Abs(field.Value - 0.4f) < 0.01, "existing tracking driver continues through proxy");
        Set(table, "Pair.2", catalog.FindChild("Angry"));
        Select("Angry");
        Check(Math.Abs(Get<float>(expressions.FindChild("Outputs").FindChild("Smile"), "Pose") - 0.7f) < 0.001f,
            "selection immediately writes the tracked pose");
        await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.001f, "tracking applies the final pose without interpolation");
        var wearer = avatar.Parent;
        // Departure must clear output directly, even when Neutral maps to a clip.
        Set(table, "Pair.0", catalog.FindChild("Angry"));
        avatar.Parent = world.RootSlot;
        await Frames();
        Check(Get<bool>(core, "AllowExternalInput") && Get<int>(core, "PairIndex") == 0 &&
            Reference<Slot>(core, "CurrentExpression") == null &&
            Math.Abs(field.Value - 0.4f) < 0.01,
            "wearer departure restores base instead of playing the mapped Neutral expression");
        Set(table, "Pair.0", (Slot)null);
        Gesture(0, 1); Select("Angry"); await Frames();
        Check(Get<int>(core, "LeftGesture") == 0 && Get<bool>(core, "AllowExternalInput"),
            "gesture and select requests are ignored without a local wearer");
        Set(core, "AllowExternalInput", false);
        AllowInput(); await Frames();
        Check(!Get<bool>(core, "AllowExternalInput"),
            "input-mode requests are ignored without a local wearer");
        // Sentinel selection state proves wearer-only private stages did no work.
        var outputState = expressions.FindChild("Outputs").Children.Single();
        Set(core, "PairIndex", -42);
        foreach (var (board, tag) in new[] { ("Selection", "ResoPon/Expression/Internal/Selection"),
            ("Lifecycle", "ResoPon/Expression/Internal/Initialize") })
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(core.FindChild("Logic").FindChild(board), tag, true) == 1,
                "private stage receiver remains discoverable: " + board);
        await Frames();
        Check(Get<int>(core, "PairIndex") == -42 &&
            Math.Abs(Get<float>(outputState, "Result") - 0.4f) < 0.01f,
            "private stages also reject updates without a local wearer");
        // A loaded/cloned instance with no wearer must not mutate shared state just
        // because its local initialization flag starts false.
        var unwornClone = avatar.Duplicate(world.RootSlot);
        await Frames();
        var unwornCore = unwornClone.FindChild("Expressions").FindChild("Core");
        Check(Get<int>(unwornCore, "PairIndex") == -42 && !Get<bool>(unwornCore, "AllowExternalInput"),
            "an unworn clone does not initialize or repeatedly clear stored state");
        unwornClone.Parent = wearer;
        await Frames();
        Check(Get<int>(unwornCore, "PairIndex") == 0 && Get<bool>(unwornCore, "AllowExternalInput") &&
            Math.Abs(unwornClone.GetComponent<ValueField<float>>().Value.Value - 0.4f) < 0.01,
            "first wear initializes a previously unworn clone and restores its base output");
        unwornClone.Destroy();
        avatar.Parent = wearer;
        Gesture(0, 1); Gesture(1, 1);
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1 && Get<int>(core, "PairIndex") == 9 &&
            Get<bool>(core, "AllowExternalInput"),
            "first events after reattachment initialize once and retain both hand requests before the wearer-change event");
        AllowInput(); Gesture(0, 0); Gesture(1, 0);
        await Frames();
        Check(Get<bool>(core, "AllowExternalInput") &&
            Get<int>(core, "PairIndex") == 0 && Math.Abs(field.Value - 0.4f) < 0.01,
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
        for (int i = 0; i < 120; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.4f) < 0.01, "package reload discards active requests and restores tracking");
        Check(restored.GetComponentsInChildren<StaticAnimationProvider>().Count == 0, "saved poses reload without animation assets");
        var restoredExpressions = restored.FindChild("Expressions");
        ExpressionGraphChecks.CheckLayout(restoredExpressions);
        await ExpressionDynamicInputChecks.CheckEdits(restoredExpressions);
        var restoredCore = restoredExpressions.FindChild("Core");
        Check(Get<int>(restoredCore, "PairIndex") == 0 &&
            Reference<Slot>(restoredCore, "CurrentExpression") == null && Get<bool>(restoredCore, "AllowExternalInput"),
            "package reload recomputes diagnostics from reset inputs and the edited empty table row");
        Set(restoredExpressions.FindChild("GestureTable"), "Pair.2", restoredExpressions.FindChild("Catalog").FindChild("Angry"));
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredExpressions.FindChild("API").FindChild("Receivers"),
            ExpressionSystemSetup.RightTag, true, 2) == 1, "reloaded int request receiver is connected");
        Check(Get<int>(restoredCore, "RightGesture") == 2 && Get<int>(restoredCore, "PairIndex") == 2 &&
            Reference<Slot>(restoredCore, "CurrentExpression") == restoredExpressions.FindChild("Catalog").FindChild("Angry"),
            "reloaded receiver evaluates the pair before the next frame");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.7f) < 0.01, "reloaded stock ProtoFlux applies a fixed pose without converter callbacks");
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
