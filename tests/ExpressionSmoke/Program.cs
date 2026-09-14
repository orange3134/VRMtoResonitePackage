using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage.Expressions;

string resonite = Environment.GetEnvironmentVariable("RESONITE_PATH") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(resonite, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
string artifacts = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp_verify/expression-smoke/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
try { await Run(resonite, artifacts); Console.WriteLine("Expression smoke checks passed."); Environment.Exit(0); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Run(string resonite, string artifacts)
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
        var avatar = world.LocalUser.Root.Slot.AddSlot("Expression smoke avatar");
        var field = avatar.AttachComponent<ValueField<float>>().Value; field.Value = 0.2f;
        var tracking = avatar.AddSlot("Tracking").AttachComponent<ValueField<float>>(); tracking.Value.Value = 0.2f;
        var trackingDriver = avatar.AttachComponent<ValueCopy<float>>();
        trackingDriver.Source.Target = tracking.Value; trackingDriver.Target.Target = field;
        var model = new ExpressionModel();
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
        CheckLayout(expressions);
        var core = expressions.FindChild("Core"); var api = expressions.FindChild("API").FindChild("Receivers");
        var command = expressions.FindChild("API").FindChild("Examples").Children.Single();
        var catalog = expressions.FindChild("Catalog"); var table = expressions.FindChild("GestureTable");
        void Gesture(int hand, int gesture, Slot input = null, bool available = true)
        {
            input ??= command; Set(input, "Hand", hand); Set(input, "Gesture", gesture); Set(input, "Available", available);
            Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.RequestTag, true, input) == 1,
                "exactly one gesture receiver");
        }
        void Select(string name) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
            ExpressionSystemSetup.SelectTag, true, catalog.FindChild(name));
        void Automatic() => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.AutomaticTag, true);
        async Task Frames(int count = 30) { for (int i = 0; i < count; i++) await default(NextUpdate); }
        Check(core.FindChild("SourceState") == null && core.FindChild("ParameterState") == null && expressions.FindChild("Rules") == null,
            "generic source arbitration and Animator graph are absent");
        Console.WriteLine($"Flux nodes: {expressions.GetComponentsInChildren<ProtoFluxNode>().Count}; Core: {core.GetComponentsInChildren<ProtoFluxNode>().Count}");
        Gesture(0, 1); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0 && Math.Abs(field.Value - 1) < 0.01,
            "left gesture selects Smile without modifying the right hand");
        float start = Get<float>(core, "PlaybackStart");
        Gesture(0, 1); await Frames();
        Check(Get<float>(core, "PlaybackStart") == start, "same expression does not restart playback");
        Gesture(1, 1); await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "both-hand table entry selects Angry");
        Gesture(1, 8); Gesture(2, 0); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 1, "invalid hand and out-of-range gesture are ignored");
        Select("Smile"); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "direct selection overrides gesture table");
        Gesture(0, 0); Automatic(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01 && Get<int>(core, "RightGesture") == 1, "return to gestures uses current two-hand state");
        Select("Animated"); await Frames(20);
        float firstSample = field.Value;
        await Frames(20);
        Check(field.Value > firstSample && field.Value < 1, "real AnimX output advances with playback time");
        Set(catalog.FindChild("Animated"), "Enabled", false); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "disabled override restores gesture selection");
        Automatic();

        // Table keys are stable dynamic names; deleting/reordering rows cannot shift other mappings.
        Set(table, "Pair.9", catalog.FindChild("Smile"));
        Gesture(0, 1); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "editing table reference takes effect");
        start = Get<float>(core, "PlaybackStart");
        Gesture(1, 0); await Frames();
        Check(Get<float>(core, "PlaybackStart") == start, "different pair sharing the same clip does not restart");
        table.Children.First(s => s.Name.StartsWith("08 ")).Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleted table row falls back to base");
        Gesture(1, 1); await Frames();
        Check(Math.Abs(field.Value - 1) < 0.01, "deleting row 8 does not shift row 9");

        var touch = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules").FindChild("Touch");
        var hardware = touch.FindChild("Left").FindChild("Command");
        Gesture(0, 0, hardware); await Frames();
        Gesture(0, 1); Gesture(0, 0, hardware, false); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1, "old hardware disconnect preserves newer manual event");
        Gesture(0, 1, hardware); await Frames();
        touch.Destroy(); await Frames();
        Check(Get<int>(core, "LeftGesture") == 0, "deleting controller module clears its last input");
        var menu = expressions.FindChild("Inputs").FindChild("ContextMenu").FindChild("Items").FindChild("Left hand").FindChild("Items");
        var menuCommand = menu.Children[1].FindChild("Command");
        Gesture(0, 1, menuCommand); await Frames();
        Check(Get<int>(core, "LeftGesture") == 1, "context-menu command updates common hand state");
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard").FindChild("Bindings");
        Check(keyboard.Children.Count == 16, "keyboard exposes eight gestures for each hand");
        var shortcut = keyboard.Children.First(s => Get<int>(s, "Hand") == 1 && Get<int>(s, "Gesture") == 7);
        Gesture(1, 7, shortcut); await Frames();
        Check(Get<int>(core, "RightGesture") == 7, "keyboard binding uses the same gesture protocol");

        Select("Smile"); await Frames();
        var clone = avatar.Duplicate(avatar.Parent); await Frames(90);
        Check(Math.Abs(clone.GetComponent<ValueField<float>>().Value.Value - 0.2f) < 0.01, "cloning resets transient selection");
        Check(Math.Abs(field.Value - 1f) < 0.01, "cloning does not reset original");
        clone.Destroy();
        catalog.FindChild("Smile").Destroy(); await Frames();
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting selected expression restores base");
        Set(table, "Pair.0", (Slot)null);
        Gesture(0, 0); Gesture(1, 0);
        tracking.Value.Value = 0.4f; await Frames();
        Check(Math.Abs(field.Value - 0.4f) < 0.01, "existing tracking driver continues through proxy");
        Select("Angry"); await Frames();
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "animation drives shared tracking output while selected");
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
        Check(restored.GetComponentsInChildren<StaticAnimationProvider>().All(p => p.Asset != null), "packaged AnimX assets reload");
        var restoredExpressions = restored.FindChild("Expressions");
        CheckLayout(restoredExpressions);
        var restoredCommand = restoredExpressions.FindChild("API").FindChild("Examples").Children.Single();
        Set(restoredCommand, "Hand", 1); Set(restoredCommand, "Gesture", 2); Set(restoredCommand, "Available", true);
        Set(restoredExpressions.FindChild("GestureTable"), "Pair.2", restoredExpressions.FindChild("Catalog").FindChild("Angry"));
        Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(restoredExpressions.FindChild("API").FindChild("Receivers"),
            ExpressionSystemSetup.RequestTag, true, restoredCommand) == 1, "reloaded request receiver is connected");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(restored.GetComponent<ValueField<float>>().Value.Value - 0.7f) < 0.01, "reloaded stock ProtoFlux plays animation without converter callbacks");
        // Curves with equal endpoints may still have a tangent excursion.
        var testCurve = new ExpressionCurve { Binding = new("Face", "Curve") };
        testCurve.Keys.Add(new(0, 0, 0, 4)); testCurve.Keys.Add(new(1, 0, -4, 0));
        var testClip = new ExpressionClip { Name = "Tangent", Duration = 1 }; testClip.Curves.Add(testCurve);
        var animation = ExpressionAnimationConverter.ConvertClip(testClip);
        Check(Math.Abs(((Elements.Assets.IAnimationTrack<float>)animation[0]).Sample(0.5f) - 1) < 0.0001, "Hermite tangent excursion preserved");
    });
}

static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == "Expr/" + name).Value.Value;
static void Set<T>(Slot slot, string name, T value)
{
    var result = slot.WriteDynamicVariable("Expr/" + name, value);
    if (result != DynamicVariableWriteResult.Success) throw new InvalidOperationException("Cannot write " + name + ": " + result);
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }

static void CheckLayout(Slot expressions)
{
    var nodes = expressions.GetComponentsInChildren<ProtoFluxNode>();
    Check(nodes.Count > 0 && nodes.GroupBy(n => n.Slot).All(g => g.Count() == 1), "one Flux node per slot");
    Check(nodes.All(n => n.Slot.Parent.GetComponents<ProtoFluxNode>().Count == 0), "Flux nodes belong to named sections, not other nodes");
    Check(nodes.Select(n => n.Slot.GlobalPosition).Distinct().Count() == nodes.Count, "Flux node positions do not overlap across logic boards");
    var logic = expressions.FindChild("Core").FindChild("Logic");
    Check(logic.GetComponents<ProtoFluxNode>().Count == 0 && logic.Children.Any(s => s.Name.Contains("Output mixer")) &&
        logic.Children.Any(s => s.Name.Contains("Public request API")), "Core logic is organized by responsibility");
}
