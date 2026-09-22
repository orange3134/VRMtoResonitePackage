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
                var clip = new ExpressionClip { Id = name, Name = name, Duration = duration, Loop = name == "Ramp" };
                clip.Curves.AddRange(curves); model.Clips.Add(clip);
            }
            Clip("Short", 0.001f, Curve("A", 1, 1, 0.001f), Curve("B", 0.8f, 0.8f, 0.001f));
            Clip("Reordered", 1, Curve("B", 0.6f, 0.6f, 1), Curve("A", 0.2f, 0.2f, 1));
            Clip("Sparse", 1, Curve("A", 0.7f, 0.7f, 1));
            Clip("Ramp", 100, Curve("A", 0, 1, 100), Curve("B", 1, 0, 1));
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

            Select(1); await Frames(); Near(a.Value, 1, "short clip reaches its final pose");
            Select(2); await Frames(); Near(a.Value, 0.2f, "track order can change for A"); Near(b.Value, 0.6f, "track order can change for B");
            Select(3); await Frames(); Near(a.Value, 0.7f, "sparse clip drives its own shape"); Near(b.Value, 0.3f, "missing track restores Base");
            Set(outputA, "TrackingWeight", 0.5f); await Frames(); Near(a.Value, 0.45f, "partial tracking mix");
            tracking.Value = 0.4f; await Frames(); Near(a.Value, 0.55f, "tracking continues driving without a playback impulse");
            Set(outputA, "TrackingWeight", 2f); await Frames(); Near(a.Value, 0.4f, "tracking weight is clamped at one");
            Set(outputA, "TrackingWeight", -1f); await Frames(); Near(a.Value, 0.7f, "tracking weight is clamped at zero");
            Set(outputA, "TrackingWeight", 0f);

            // A sentinel exposes accidental frame polling or whole-output sweeps.
            // Changing A must not reapply unrelated B while its inputs stay unchanged.
            Select(3); await Frames();
            b.Value = 0.123f;
            await Frames(30);
            Near(b.Value, 0.123f, "idle output is not rewritten without an input change");
            tracking.Value = 0.6f;
            Set(outputA, "TrackingWeight", 1f);
            await Frames();
            Near(a.Value, 0.6f, "changed tracking input updates its own output");
            Near(b.Value, 0.123f, "changing A does not sweep or rewrite B");
            Set(outputB, "Base", 0.45f);
            await Frames();
            Near(b.Value, 0.123f, "static Base edits do not write outside a selection event");
            Select(3);
            Near(b.Value, 0.45f, "reselection synchronously applies the edited static Base");
            Set(outputB, "Base", 0.3f);
            tracking.Value = 0.4f;
            Set(outputA, "TrackingWeight", 0f);
            await Frames();
            Select(4);
            Near(Get<float>(outputA, "Pose"), 1, "tracked pose is written synchronously");
            Near(b.Value, 0, "static ramp output is written synchronously");
            await Frames();
            Near(a.Value, 1, "tracking driver applies the final key without interpolation");
            await Frames(90);
            Near(a.Value, 1, "looping ramp stays at its final pose");
            Near(b.Value, 0, "time does not restart or advance the fixed pose");
            var ramp = catalog.FindChild("Ramp");
            var originalBindings = ramp.FindChild("Bindings");
            Set<Slot>(ramp, "Bindings", catalog.FindChild("Sparse").FindChild("Bindings"));
            await Frames();
            Near(a.Value, 0.7f, "changing pose records refreshes the same selected slot");
            Near(b.Value, 0.3f, "replacement pose clears cached tracks absent from the new pose");
            Set<Slot>(ramp, "Bindings", null);
            await Frames();
            Near(a.Value, 0.4f, "missing pose records clears the cached pose");
            Set<Slot>(ramp, "Bindings", originalBindings);
            await Frames();
            Near(a.Value, 1, "restored pose records applies its endpoint without a new gesture");
            Near(b.Value, 0, "tracks with different lengths each hold their own final key");
            Set(originalBindings.FindChild("A"), "Value", 0.9f);
            Select(4);
            Near(Get<float>(outputA, "Pose"), 0.9f, "reselection immediately writes edited stored values");
            await Frames();
            Near(a.Value, 0.9f, "tracking applies edited stored values");
            Set(originalBindings.FindChild("A"), "Value", 1f);
            Select(1); Near(b.Value, 0.8f, "static output switches synchronously");
            Near(Get<float>(outputA, "Pose"), 1, "new tracked pose is written synchronously");
            await Frames(); Near(a.Value, 1, "tracking applies the new fixed pose");
            Select(5); Near(Get<float>(outputA, "Pose"), 0.8f, "Hold immediately stores its final key");
            await Frames(); Near(a.Value, 0.8f, "Hold has no interpolation");
            Select(3); Near(b.Value, 0.3f, "missing static track immediately restores Base");
            await Frames(); Near(a.Value, 0.7f, "sparse tracked pose switches without a fade");
            Select(1); Select(2);
            Near(Get<float>(outputA, "Pose"), 0.2f, "rapid selection stores only the latest fixed pose");
            await Frames(); Near(a.Value, 0.2f, "rapid selection has no previous-pose interpolation");
            Select(0); Check(!Get<bool>(outputA, "HasPose"), "clearing immediately removes the tracked pose");
            await Frames(); Near(a.Value, 0.4f, "cleared selection restores tracking");
            tracking.Value = 0.8f; await Frames(); Near(a.Value, 0.8f, "live Base continues updating without an expression");
            // An uninitialized unworn copy must not display a saved active expression.
            Select(1); await Frames();
            var unworn = avatar.Duplicate(avatar.World.RootSlot);
            await Frames();
            var unwornOutput = unworn.FindChild("Expressions").FindChild("Outputs").FindChild("A");
            Near(Get<float>(unwornOutput, "Result"), 0.8f, "unworn clone writes Base despite retained selection");
            Near(a.Value, 1, "unworn clone does not change the original result");
            unworn.Destroy();
            Check(expressions.GetComponentsInChildren<ProtoFluxNode>().Count(n => n.GetType().Name == "LocalUpdate") == 0,
                "output updates are event driven without LocalUpdate");
        }
        finally { avatar.Destroy(); }
        Console.WriteLine("OUTPUT WRITES: immediate final poses, no loop playback, live tracking, missing/reordered tracks and unworn copies passed");
    }

    private static async Task Frames(int count = 8) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicVariableBase<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).DynamicValue;
    private static void Set<T>(Slot slot, string name, T value) => Check(slot.WriteDynamicVariable(VariablePath(slot, name), value) == DynamicVariableWriteResult.Success, "write " + name);
    private static void Near(float actual, float expected, string message, float tolerance = 0.01f) => Check(Math.Abs(actual - expected) < tolerance, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
