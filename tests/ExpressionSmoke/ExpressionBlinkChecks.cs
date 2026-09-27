using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionBlinkChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var avatar = parent.AddSlot("Blink cooperation regression");
        Slot clone = null, restored = null;
        try
        {
            var fields = new Dictionary<string, IField<float>>();
            foreach (string name in new[] { "Close", "Reverse", "Mouth" })
                fields[name] = avatar.AddSlot(name).AttachComponent<ValueField<float>>().Value;
            fields["Reverse"].Value = 1;
            var manager = avatar.AttachComponent<EyeManager>();
            // Use deterministic overrides through the real EyeManager/EyeLinearDriver updates.
            manager.MinBlinkInterval.Value = manager.MaxBlinkInterval.Value = 1000000;
            var driver = avatar.AttachComponent<EyeLinearDriver>(); driver.EyeManager.Target = manager;
            var left = driver.Eyes.Add(); left.Side.Value = EyeSide.Left; left.OpenCloseTarget.Target = fields["Close"];
            var right = driver.Eyes.Add(); right.Side.Value = EyeSide.Right;
            right.OpenState.Value = 1; right.ClosedState.Value = 0; right.OpenCloseTarget.Target = fields["Reverse"];
            var mouth = avatar.AddSlot("Mouth input").AttachComponent<ValueField<float>>().Value;
            var copy = avatar.AttachComponent<ValueCopy<float>>(); copy.Source.Target = mouth; copy.Target.Target = fields["Mouth"];
            var model = new ExpressionModel();
            foreach (var (name, close) in new[] { ("Open", 0f), ("Half", 0.6f), ("Closed", 1f), ("Sparse", -1f) })
            {
                var clip = new ExpressionClip { Id = name, Name = name, Duration = 1 };
                foreach (var (shape, value) in new[] { ("Close", close), ("Reverse", 1 - close), ("Mouth", 0.25f) })
                {
                    if (close < 0 && shape != "Mouth") continue;
                    var curve = new ExpressionCurve { Binding = new("Face", shape) };
                    curve.Keys.Add(new(0, value, 0, 0)); clip.Curves.Add(curve);
                }
                model.Clips.Add(clip);
            }
            await ExpressionSystemSetup.BuildAsync(avatar, model, b => fields[b.Shape],
                initialWeight: f => f == fields["Reverse"] ? 1f : 0f);
            await Frames(90);
            await Verify(avatar);
            clone = avatar.Duplicate(parent); await Frames(90); await Verify(clone);
            var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
            string package = Path.Combine(artifacts, "Blink.resonitepackage");
            var engine = avatar.Engine;
            await default(ToBackground);
            using (var stream = File.Create(package)) await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            restored = parent.AddSlot("Restored blink cooperation");
            await PackageImporter.ImportPackage(package, restored); await default(ToWorld); await Frames(120);
            await Verify(restored);
        }
        finally { restored?.Destroy(); clone?.Destroy(); avatar.Destroy(); }
        Console.WriteLine("BLINK: real EyeLinearDriver, closing-side mix, reversed range, immediate switching, manual routing, clone and reload passed");
    }

    private static async Task Verify(Slot avatar)
    {
        var expressions = avatar.FindChild("Expressions");
        var core = expressions.FindChild("Core"); var table = expressions.FindChild("GestureTable");
        var catalog = expressions.FindChild("Catalog"); var outputs = expressions.FindChild("Outputs");
        var close = outputs.FindChild("Close"); var reverse = outputs.FindChild("Reverse"); var mouth = outputs.FindChild("Mouth");
        var driver = avatar.GetComponent<EyeLinearDriver>(); var manager = avatar.GetComponent<EyeManager>();
        Check(Get<int>(close, "BlinkMode") == 1 && Get<int>(reverse, "BlinkMode") == 2 && Get<int>(mouth, "BlinkMode") == 0,
            "only OpenCloseTarget automatically enables closing-side mix");
        Check(driver.Eyes[0].OpenCloseTarget.IsLinkValid && driver.Eyes[0].OpenCloseTarget.Target == Field(close, "Base") &&
            driver.Eyes[1].OpenCloseTarget.IsLinkValid && driver.Eyes[1].OpenCloseTarget.Target == Field(reverse, "Base"),
            "real eye targets are rerouted to their own Base fields");
        Check(Reference<ISyncRef>(close, "OriginalDriver") == driver.Eyes[0].OpenCloseTarget, "original blink link remaps");
        for (int i = 0; i < 4; i++) Set(table, "Pair." + (i + 1), catalog.FindChild(new[] { "Open", "Half", "Closed", "Sparse" }[i]));
        void Select(int index) => Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            expressions.FindChild("API").FindChild("Receivers"), ExpressionSystemSetup.RightTag, true, index) == 1, "select blink test expression");
        void Blink(float l, float r) { manager.LeftEyeCloseOverride.Value = l; manager.RightEyeCloseOverride.Value = r; }
        float Result(Slot output) => Reference<IField<float>>(output, "Target").Value;
        Blink(0, 0); Select(1); await Frames();
        Near(Result(close), 0, "open expression opens left eye"); Near(Result(reverse), 1, "open expression opens reversed eye");
        Blink(0.8f, 0.3f); await Frames();
        Near(Result(close), 0.8f, "blink passes through a zero animation track"); Near(Result(reverse), 0.7f, "right eye stays independent");
        Select(2); await Frames();
        Near(Result(close), 0.8f, "stronger blink wins"); Near(Result(reverse), 0.4f, "stronger reversed expression wins");
        Blink(0, 0); await Frames();
        Near(Result(close), 0.6f, "blink reopening preserves authored half-close"); Near(Result(reverse), 0.4f, "reversed half-close is preserved");
        Select(3); await Frames();
        Near(Result(close), 1, "closed expression stays closed between blinks"); Near(Result(reverse), 0, "reversed closed expression stays closed");
        Select(4); Blink(0.3f, 0.8f); await Frames();
        Near(Result(close), 0.3f, "missing track follows blink Base"); Near(Result(reverse), 0.2f, "missing reversed track follows Base");
        avatar.FindChild("Mouth input").GetComponent<ValueField<float>>().Value.Value = 0.9f; await Frames();
        Near(Result(mouth), 0.25f, "viseme driver is not given blink max behavior");
        Set(mouth, "TrackingWeight", 0.5f); await Frames(); Near(Result(mouth), 0.575f, "existing weighted tracking is retained");
        Set(mouth, "TrackingWeight", 0f);
        // A manually connected blink after conversion can opt in without rebuilding the graph.
        Set(mouth, "BlinkMode", 1); await Frames(); Near(Result(mouth), 0.9f, "manual max mode uses routed Base");
        Set(mouth, "BlinkMode", 0); await Frames(); Near(Result(mouth), 0.25f, "manual mode can be disabled");
        Blink(0, 0); Select(1); await Frames();
        Select(2); Near(Get<float>(close, "Pose"), 0.6f, "expression pose changes synchronously");
        await Frames(); Near(Get<float>(close, "Result"), 0.6f, "live mixing applies the new pose without a fade");
        Blink(1, 1); await Frames(); Near(Result(close), 1, "immediate expression retains full blink"); Near(Result(reverse), 0, "reverse blink remains independent");
        Blink(0, 0); await Frames(); Near(Result(close), 0.6f, "reopening reveals the selected pose");
        Select(0); Check(!Get<bool>(close, "HasPose"), "clearing immediately removes the pose");
        await Frames(); Near(Get<float>(close, "Result"), 0, "clearing restores live Base without a fade");
        Blink(1, 1); await Frames(); Near(Result(close), 1, "blink works while CurrentExpression is null");
        Blink(0, 0); await Frames(); Near(Result(close), 0, "blink returns to open base");
        ExpressionGraphChecks.CheckLayout(expressions);
    }

    private static IField<float> Field(Slot s, string n) => s.ExpressionVariables<DynamicValueVariable<float>>().Single(v => v.VariableName.Value == VariablePath(s, n)).Value;
    private static T Get<T>(Slot s, string n) => s.ExpressionVariables<DynamicVariableBase<T>>().Single(v => v.VariableName.Value == VariablePath(s, n)).DynamicValue;
    private static void Set<T>(Slot s, string n, T value) => Check(s.WriteDynamicVariable(VariablePath(s, n), value) == DynamicVariableWriteResult.Success, "write " + n);
    private static async Task Frames(int count = 10) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < 0.01f, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
