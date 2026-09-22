using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionOutputWriteChecks
{
    public static async Task Run(Slot parent)
    {
        var avatar = parent.AddSlot("Output Write regression");
        try
        {
            var a = avatar.AttachComponent<ValueField<float>>().Value; a.Value = 0.2f;
            var b = avatar.AttachComponent<ValueField<float>>().Value; b.Value = 0.3f;
            var tracking = avatar.AddSlot("Tracking").AttachComponent<ValueField<float>>().Value; tracking.Value = 0.2f;
            var trackingCopy = avatar.AttachComponent<ValueCopy<float>>();
            trackingCopy.Source.Target = tracking; trackingCopy.Target.Target = a;
            var model = new ExpressionModel();
            ExpressionCurve Curve(string shape, float first, float last, float end, bool hold = false)
            {
                float slope = hold ? float.PositiveInfinity : (last - first) / end;
                var c = new ExpressionCurve { Binding = new("Face", shape) };
                c.Keys.Add(new(0, first, 0, slope)); c.Keys.Add(new(end, last, hold ? 0 : slope, 0));
                return c;
            }
            void Clip(string name, float duration, params ExpressionCurve[] curves)
            {
                var clip = new ExpressionClip { Id = name, Name = name, Duration = duration };
                clip.Curves.AddRange(curves); model.Clips.Add(clip);
            }
            Clip("Short", 0.001f, Curve("A", 1, 1, 0.001f), Curve("B", 0.8f, 0.8f, 0.001f));
            Clip("Reordered", 1, Curve("B", 0.6f, 0.6f, 1), Curve("A", 0.2f, 0.2f, 1));
            Clip("Sparse", 1, Curve("A", 0.7f, 0.7f, 1));
            Clip("Ramp", 100, Curve("A", 0, 1, 100), Curve("B", 1, 0, 100));
            Clip("Hold", 1, Curve("A", 0.2f, 0.8f, 1, hold: true));
            var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, binding => binding.Shape == "A" ? a : b, menu: false);
            await Frames(90);
            var core = expressions.FindChild("Core"); var catalog = expressions.FindChild("Catalog");
            var table = expressions.FindChild("GestureTable"); var outputs = expressions.FindChild("Outputs");
            var outputA = outputs.FindChild("A"); var outputB = outputs.FindChild("B");
            var api = expressions.FindChild("API").FindChild("Receivers");
            for (int i = 0; i < model.Clips.Count; i++)
            {
                var entry = catalog.FindChild(model.Clips[i].Name);
                Set(table, "Pair." + (i + 1), entry);
            }
            void Select(int index) => Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                ExpressionSystemSetup.RightTag, true, index) == 1, "int receiver selects test clip " + index);
            void Time(float elapsed) => Set(core, "PlaybackStart", (float)avatar.World.Time.WorldTime - elapsed);

            Select(1); await Frames(); Near(a.Value, 1, "short clip reaches its final pose");
            Select(2); await Frames(); Near(a.Value, 0.2f, "track order can change for A"); Near(b.Value, 0.6f, "track order can change for B");
            Select(3); await Frames(); Near(a.Value, 0.7f, "sparse clip drives its own shape"); Near(b.Value, 0.3f, "missing track restores Base");
            Set(outputA, "TrackingWeight", 0.5f); await Frames(); Near(a.Value, 0.45f, "partial tracking mix");
            tracking.Value = 0.4f; await Frames(); Near(a.Value, 0.55f, "tracking continues driving without a playback impulse");
            Set(outputA, "TrackingWeight", 2f); await Frames(); Near(a.Value, 0.4f, "tracking weight is clamped at one");
            Set(outputA, "TrackingWeight", -1f); await Frames(); Near(a.Value, 0.7f, "tracking weight is clamped at zero");
            Set(outputA, "TrackingWeight", 0f);

            Select(4); await Frames(); Time(25); await Frames();
            Near(a.Value, 0.25f, "non-looping animation advances"); Near(b.Value, 0.75f, "outputs share the same playback time");
            Time(150); await Frames(); Near(a.Value, 1, "non-looping animation holds its endpoint");
            Set(catalog.FindChild("Ramp"), "Loop", true); Time(125); await Frames(); Near(a.Value, 0.25f, "loop wraps by Duration");
            Set(catalog.FindChild("Ramp"), "Duration", 40f); Time(95); await Frames(); Near(a.Value, 0.15f, "edited Duration changes the loop period without stretching keys");
            Set(catalog.FindChild("Ramp"), "Loop", false); Time(25); await Frames(); Near(a.Value, 0.25f, "disabling Loop restores unwrapped time");
            Select(1); Near(a.Value, 1, "new pose is written synchronously"); Near(b.Value, 0.8f, "all outputs switch together");
            Select(5); Time(2); await Frames(); Near(a.Value, 0.8f, "Hold reaches its final key");
            Select(3); Near(a.Value, 0.7f, "sparse pose switches immediately"); Near(b.Value, 0.3f, "missing track immediately restores Base");
            Select(2); Near(a.Value, 0.2f, "rapid selection has no previous-pose interpolation");
            Select(0); Near(a.Value, 0.4f, "cleared selection immediately restores tracking");
            tracking.Value = 0.8f; await Frames(); Near(a.Value, 0.8f, "live Base continues updating without an expression");
            // An uninitialized unworn copy must not display a saved active expression.
            Select(1); await Frames();
            var unworn = avatar.Duplicate(avatar.World.RootSlot);
            await Frames();
            var unwornOutput = unworn.FindChild("Expressions").FindChild("Outputs").FindChild("A");
            Near(Get<float>(unwornOutput, "Result"), 0.8f, "unworn clone writes Base despite retained selection");
            Near(a.Value, 1, "unworn clone does not change the original result");
            unworn.Destroy();
            Check(expressions.GetComponentsInChildren<ProtoFluxNode>().Count(n => n.GetType().Name == "LocalUpdate") == 1,
                "one shared update for all output fields");
        }
        finally { avatar.Destroy(); }
        Console.WriteLine("OUTPUT WRITES: synchronous switching, sampling, tracking, loop time, missing/reordered tracks and unworn copies passed");
    }

    private static async Task Frames(int count = 8) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicVariableBase<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).DynamicValue;
    private static void Set<T>(Slot slot, string name, T value) => Check(slot.WriteDynamicVariable(VariablePath(slot, name), value) == DynamicVariableWriteResult.Success, "write " + name);
    private static void Near(float actual, float expected, string message, float tolerance = 0.01f) => Check(Math.Abs(actual - expected) < tolerance, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}