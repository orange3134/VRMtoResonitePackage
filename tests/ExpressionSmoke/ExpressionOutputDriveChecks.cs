using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionOutputDriveChecks
{
    public static async Task Run(Slot parent)
    {
        var avatar = parent.AddSlot("Output Drive regression");
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
                Set(table, "Pair." + (i + 1), entry); Set(entry, "FadeIn", 0f);
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
            Near(a.Value, 0.25f, "non-looping animation continues after FadeDuration"); Near(b.Value, 0.75f, "outputs share the same playback time");
            // Replace the producer temporarily: outputs must follow shared fields even
            // when their values differ from WorldTime and the clip's timing settings.
            var playback = core.FindChild("Logic").FindChild("Playback");
            global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float> Driver(string name) =>
                playback.GetComponentsInChildren<global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float>>()
                    .Single(d => d.GetRootProxy(addIfMissing: false).Drive.Target == core.GetComponents<DynamicValueVariable<float>>()
                        .Single(v => v.VariableName.Value == "ExpressionCore/" + name).Value);
            var timeDriver = Driver("AnimationTime"); var fadeDriver = Driver("FadeWeight");
            var oldTime = timeDriver.Value.Target; var oldFade = fadeDriver.Value.Target;
            var fixedTime = playback.AddSlot("Test time").AttachComponent<global::FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueInput<float>>();
            var fixedFade = playback.AddSlot("Test fade").AttachComponent<global::FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueInput<float>>();
            try
            {
                fixedTime.Value.Value = 75; timeDriver.Value.Target = fixedTime;
                await Frames(); Near(Get<float>(core, "AnimationTime"), 75, "Playback drives the shared AnimationTime field");
                Near(a.Value, 0.75f, "A reads shared AnimationTime through ValueSource");
                Near(b.Value, 0.25f, "B reads the same shared AnimationTime");
                fixedTime.Value.Value = 25; await Frames();
                Near(a.Value, 0.25f, "shared clock changes invalidate the output sampler");
                fixedFade.Value.Value = 0.5f; fadeDriver.Value.Target = fixedFade; await Frames();
                Near(a.Value, (Get<float>(outputA, "Snapshot") + 0.25f) / 2, "A reads shared FadeWeight");
                Near(b.Value, (Get<float>(outputB, "Snapshot") + 0.75f) / 2, "B reads shared FadeWeight");
            }
            finally
            {
                timeDriver.Value.Target = oldTime; fadeDriver.Value.Target = oldFade;
                fixedTime.Slot.Destroy(); fixedFade.Slot.Destroy();
            }
            Time(150); await Frames(); Near(a.Value, 1, "non-looping animation holds its endpoint");
            Set(catalog.FindChild("Ramp"), "Loop", true); Time(125); await Frames(); Near(a.Value, 0.25f, "loop wraps by Duration");
            Set(catalog.FindChild("Ramp"), "Duration", 40f); Time(95); await Frames(); Near(a.Value, 0.15f, "edited Duration changes the loop period without stretching keys");
            Set(catalog.FindChild("Ramp"), "Loop", false); Time(25); await Frames(); Near(a.Value, 0.25f, "disabling Loop restores unwrapped time");
            Set(catalog.FindChild("Short"), "FadeIn", 100f);
            float beforeSwitch = Get<float>(outputA, "Result"); Select(1);
            for (int i = 0; i < 4; i++)
            {
                await default(NextUpdate);
                Near(Get<float>(outputA, "Result"), beforeSwitch, "switch resets the shared fade before mixing the new clip");
            }
            Select(5); await Frames(); Time(2); await Frames(); Near(a.Value, 0.8f, "Hold reaches the last value beyond the last key");

            Select(0); Time(1); await Frames(); Near(a.Value, 0.4f, "unassigned selection returns to tracking");
            Set(catalog.FindChild("Short"), "FadeIn", 0.1f);
            Select(1);
            bool sawFade = false;
            float fadeStart = Get<float>(core, "PlaybackStart");
            while ((float)avatar.World.Time.WorldTime - fadeStart < 0.2f)
            {
                await default(NextUpdate);
                sawFade |= a.Value > 0.405f && a.Value < 0.995f;
            }
            await Frames();
            Check(sawFade, "0.001-second clip has intermediate values during its 0.1-second fade");
            Near(a.Value, 1, "short clip completes its fade without resetting its clock");

            Set(catalog.FindChild("Sparse"), "FadeIn", 100f);
            Select(3); Time(25f); await Frames();
            Near(a.Value, 0.925f, "fade mixes from the previous Result");
            float interrupted = Get<float>(outputA, "Result");
            Set(catalog.FindChild("Reordered"), "FadeIn", 100f);
            Select(2);
            Near(Get<float>(outputA, "Snapshot"), interrupted, "interrupted fade snapshots the mixed output", 0.001f);
            Time(25f); await Frames(); Near(a.Value, interrupted * 0.75f + 0.2f * 0.25f, "new fade starts from the interrupted value");
            Time(101); await Frames(); Near(a.Value, 0.2f, "interrupted fade reaches the next pose");
            Set(catalog.FindChild("Reordered"), "FadeOut", 100f);
            Select(0);
            Check(Reference<Slot>(core, "CurrentExpression") == null && Get<float>(core, "FadeDuration") == 100,
                "FadeOut retains duration after clearing CurrentExpression");
            Time(25f); await Frames(); Near(a.Value, 0.25f, "null expression fades to Base");
            tracking.Value = 0.8f; await Frames(); Near(a.Value, 0.35f, "FadeOut target follows live Base");
            Time(101); await Frames(); Near(a.Value, 0.8f, "FadeOut completes at the live Base");

            // An uninitialized unworn copy must not display a saved active expression.
            Set(catalog.FindChild("Short"), "FadeIn", 0f); Select(1); await Frames();
            var unworn = avatar.Duplicate(avatar.World.RootSlot);
            await Frames();
            var unwornOutput = unworn.FindChild("Expressions").FindChild("Outputs").FindChild("A");
            Near(Get<float>(unwornOutput, "Result"), 0.8f, "unworn clone drives Base despite retained selection");
            Near(a.Value, 1, "unworn clone does not change the original result");
            unworn.Destroy();
            Check(expressions.GetComponentsInChildren<ProtoFluxNode>().All(n => n.GetType().Name != "LocalUpdate"),
                "output regression fixture uses no LocalUpdate");
        }
        finally { avatar.Destroy(); }
        Console.WriteLine("OUTPUT DRIVES: sampling, fades, tracking, loop time, missing/reordered tracks and unworn copies passed");
    }

    private static async Task Frames(int count = 8) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).Value.Value;
    private static void Set<T>(Slot slot, string name, T value) => Check(slot.WriteDynamicVariable(VariablePath(slot, name), value) == DynamicVariableWriteResult.Success, "write " + name);
    private static void Near(float actual, float expected, string message, float tolerance = 0.01f) => Check(Math.Abs(actual - expected) < tolerance, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}