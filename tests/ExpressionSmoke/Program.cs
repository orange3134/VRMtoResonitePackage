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
        var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, _ => field);
        Console.WriteLine("Built graph");
        for (int i = 0; i < 90; i++) await default(NextUpdate);
        Check(expressions.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "all generated ProtoFlux groups are valid");
        var core = expressions.FindChild("Core"); var api = expressions.FindChild("API").FindChild("Receivers");
        var command = expressions.FindChild("API").FindChild("Examples").Children.Single();
        var source = command.GetComponents<DynamicReferenceVariable<Slot>>().Single(v => v.VariableName.Value == "Expr/SourceSlot").Reference.Target;
        Console.WriteLine($"Wearer={avatar.ActiveUser}, generation={Get<int>(core, "Generation")}, baseline={field.Value}");
        int sequence = 0;
        void Send(string operation, string id, int token = 0)
        {
            Set(command, "Operation", operation); Set(command, "ExpressionId", id); Set(command, "Generation", Get<int>(core, "Generation"));
            Set(command, "Sequence", ++sequence); Set(command, "ReleaseGeneration", Get<int>(core, "Generation")); Set(command, "ReleaseSequence", token);
            int count = ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.RequestTag, true, command);
            Console.WriteLine($"{operation} {id}: receivers={count}, error={Get<string>(core, "LastError")}, seq={Get<int>(source, "Sequence")}");
            Check(count == 1, "exactly one public receiver");
        }
        Send("Select", "Smile");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 1) < 0.01, "impulse selects Smile");
        Send("Select", "Angry");
        Send("Release", "", 1);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "stale release preserves newer expression");
        Send("Release", "", 2);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "release restores authored nonzero baseline");
        var parameters = command.AddSlot("Test parameters");
        var leftItem = ExpressionFlux.Record(parameters, "Left");
        ExpressionFlux.Data(leftItem, "Name", "GestureLeft"); ExpressionFlux.Data(leftItem, "Value", 1f);
        var rightItem = ExpressionFlux.Record(parameters, "Right");
        ExpressionFlux.Data(rightItem, "Name", "GestureRight"); ExpressionFlux.Data(rightItem, "Value", 0f);
        for (int i = 0; i < 3; i++) await default(NextUpdate);
        Set(command, "Parameters", parameters);
        Send("SetParameters", "");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 1) < 0.01, "left gesture enters its animated state");
        Set(rightItem, "Value", 1f); Send("SetParameters", "");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "both-hand AND condition transitions with a fade");
        Send("Select", "Smile");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 1) < 0.01, "direct selection overrides current gesture state");
        Send("Release", "", sequence);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "release clears only this source's parameter overrides");
        var touch = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules").FindChild("Touch");
        var gestureCommand = touch.FindChild("Left").FindChild("Command");
        Set(gestureCommand, "Operation", "Gesture"); Set(gestureCommand, "Generation", Get<int>(core, "Generation"));
        Set(gestureCommand, "Sequence", 1); Set(gestureCommand, "Hand", 1); Set(gestureCommand, "GestureId", 1);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.RequestTag, true, gestureCommand);
        for (int i = 0; i < 12; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 1f) < 0.01, "controller adapter command reaches gesture state");
        touch.Destroy();
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting a controller module invalidates its requests");
        var mapping = expressions.FindChild("Rules").FindChild("GestureOverrides").FindChild("Left").FindChild("Mappings").FindChild("1");
        Set(mapping, "Expression", expressions.FindChild("Catalog").FindChild("Angry")); Set(mapping, "Enabled", true);
        Set(rightItem, "Value", 0f); Send("SetParameters", "");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "editable gesture mapping overrides imported rule");
        Set(mapping, "Enabled", false);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 1f) < 0.01, "disabling custom mapping restores imported rule");
        Send("Release", "", sequence);
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Send("Select", "Smile");
        for (int i = 0; i < 30; i++) await default(NextUpdate);
        var clone = avatar.Duplicate(avatar.Parent);
        for (int i = 0; i < 90; i++) await default(NextUpdate);
        Check(Math.Abs(clone.GetComponent<ValueField<float>>().Value.Value - 0.2f) < 0.01, "cloning resets transient selection under the same wearer");
        Check(Math.Abs(field.Value - 1f) < 0.01, "cloning does not reset the original avatar");
        clone.Destroy();
        expressions.FindChild("Catalog").FindChild("Smile").Destroy();
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.2f) < 0.01, "deleting active expression clears its contribution");
        Set(expressions.FindChild("Rules").FindChild("ImportedAnimator").Children.Single(), "Enabled", false);
        tracking.Value.Value = 0.4f;
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.4f) < 0.01, "existing tracking driver continues through its proxy");
        Send("Select", "Angry");
        for (int i = 0; i < 60; i++) await default(NextUpdate);
        Check(Math.Abs(field.Value - 0.7f) < 0.01, "animation owns the shared tracking output while selected");
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
        var restoredCommand = restoredExpressions.FindChild("API").FindChild("Examples").Children.Single();
        Set(restoredCommand, "Operation", "Select"); Set(restoredCommand, "ExpressionId", "Angry");
        Set(restoredCommand, "Sequence", 1); Set(restoredCommand, "Generation", Get<int>(restoredExpressions.FindChild("Core"), "Generation"));
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
