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
            Clip("StaticOnly", 1, Curve("B", 0, 0, 1));
            var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, binding => binding.Shape == "A" ? a : b, menu: false);
            await Frames(90);
            var core = expressions.FindChild("Core"); var catalog = expressions.FindChild("Catalog");
            var table = expressions.FindChild("DV").FindChild("GestureTable"); var outputs = expressions.FindChild("Outputs");
            var outputA = outputs.FindChild("A"); var outputB = outputs.FindChild("B");
            var api = expressions.FindChild("API").FindChild("Receivers");
            void Playback() => Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(
                core.FindChild("Logic").FindChild("Playback"), "ResoPon/Expression/Internal/Playback", true) == 1,
                "explicit playback impulse reaches the receiver");
            for (int i = 0; i < model.Clips.Count; i++)
            {
                var entry = catalog.FindChild(model.Clips[i].Name);
                Set(table, $"L0R{i + 1}", entry);
            }
            void Select(int index) => Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                ExpressionSystemSetup.RightTag, true, index) == 1, "int receiver selects test clip " + index);

            Select(1); await Frames();
            Check(Get<int>(core, "RightGesture") == 0 && Reference<Slot>(core, "CurrentExpression") == null,
                "parenting below UserRoot without equip does not accept gesture requests");
            Near(a.Value, 0.2f, "unassigned avatar retains tracking Base");
            EquipAvatar(avatar); await Frames(30);
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
            Near(SelectedValue(outputA), 1, "tracked binding is resolved synchronously");
            Near(b.Value, 0, "static ramp output is written synchronously");
            await Frames();
            Near(a.Value, 1, "tracking driver applies the final key without interpolation");
            await Frames(90);
            Near(a.Value, 1, "looping ramp stays at its final pose");
            Near(b.Value, 0, "time does not restart or advance the fixed pose");
            var ramp = catalog.FindChild("Ramp");
            Set<Slot>(core, "CurrentExpression", catalog.FindChild("Sparse"));
            await Frames();
            Near(a.Value, 0.7f, "changing CurrentExpression reads values from the new clip");
            Near(b.Value, 0, "direct reference edits do not apply static outputs without an impulse");
            Playback();
            Near(b.Value, 0.3f, "playback impulse synchronously restores Base for absent binding names");
            Set<Slot>(core, "CurrentExpression", null);
            Playback();
            await Frames();
            Near(a.Value, 0.4f, "null CurrentExpression restores Base");
            Set<Slot>(core, "CurrentExpression", ramp);
            Playback();
            Near(b.Value, 0, "explicit playback applies a reference change synchronously");
            await Frames();
            Near(a.Value, 1, "restored CurrentExpression applies the clip value without a new gesture");
            Near(b.Value, 0, "tracks with different lengths each hold their own final key");
            var shortClip = catalog.FindChild("Short");
            Set<Slot>(core, "CurrentExpression", shortClip);
            var shortB = BindingVariable(shortClip, outputB);
            outputB.ActiveSelf = false;
            shortClip.ActiveSelf = false;
            shortB.Slot.Parent.ActiveSelf = false;
            shortB.Slot.ActiveSelf = false;
            await Frames();
            Playback();
            Near(b.Value, 0.8f, "inactive Output, Clip and Binding slots do not gate playback values");
            outputB.ActiveSelf = true;
            shortClip.ActiveSelf = true;
            shortB.Slot.Parent.ActiveSelf = true;
            shortB.Slot.ActiveSelf = true;
            await Frames();
            Set(ramp, "Binding." + Get<string>(outputA, "Id"), 0.9f);
            Select(4);
            Near(SelectedValue(outputA), 0.9f, "reselection immediately resolves the edited binding");
            await Frames();
            Near(a.Value, 0.9f, "tracking applies edited stored values");
            Set(ramp, "Binding." + Get<string>(outputA, "Id"), 1f);
            Select(1); Near(b.Value, 0.8f, "static output switches synchronously");
            Near(SelectedValue(outputA), 1, "new tracked binding is resolved synchronously");
            await Frames(); Near(a.Value, 1, "tracking applies the new fixed pose");
            Select(5); Near(SelectedValue(outputA), 0.8f, "Hold immediately resolves its final key");
            await Frames(); Near(a.Value, 0.8f, "Hold has no interpolation");
            Select(3); Near(b.Value, 0.3f, "missing static track immediately restores Base");
            await Frames(); Near(a.Value, 0.7f, "sparse tracked pose switches without a fade");
            Select(1); Select(2);
            Near(SelectedValue(outputA), 0.2f, "rapid selection resolves only the latest binding");
            await Frames(); Near(a.Value, 0.2f, "rapid selection has no previous-pose interpolation");
            Select(0); Check(!TryReadSelectedValue(outputA, out _), "clearing immediately removes the tracked pose");
            await Frames(); Near(a.Value, 0.4f, "cleared selection restores tracking");
            tracking.Value = 0.8f; await Frames(); Near(a.Value, 0.8f, "live Base continues updating without an expression");
            Select(6);
            Near(b.Value, 0, "an explicit zero binding is not treated as missing");
            Check(!TryReadSelectedValue(outputA, out _), "sparse selection has no binding for tracked A");
            await Frames(); Near(a.Value, 0.8f, "missing tracked binding starts from current Base");
            tracking.Value = 0.35f; await Frames();
            Near(a.Value, 0.35f, "missing tracked binding follows later Base changes");
            Near(b.Value, 0, "tracking does not rewrite the static output");
            var shortA = BindingVariable(catalog.FindChild("Short"), outputA);
            string originalName = shortA.VariableName.Value;
            shortA.VariableName.Value = originalName + ".Renamed";
            await Frames(); Select(1); await Frames();
            Near(a.Value, 0.35f, "renamed variable is missing and falls back to Base");
            Check(!TryReadSelectedValue(outputA, out _), "lookup requires the output's exact variable name");
            shortA.VariableName.Value = originalName;
            await Frames(); Near(a.Value, 1, "restoring a variable name restores live tracking without selection");
            shortA.Value.Value = 0.55f;
            await Frames(); Near(a.Value, 0.55f, "named tracking values are read live");
            shortA.Slot.Destroy();
            await Frames(); Near(a.Value, 0.35f, "deleted variable follows Base");
            var replacement = VrmToResonitePackage.Expressions.ExpressionFlux.Data(
                catalog.FindChild("Short"), "Binding." + Get<string>(outputA, "Id"), 1f);
            await Frames(); Select(1); await Frames();
            Near(a.Value, 1, "new variable with the same name is resolved without Output references");
            tracking.Value = 0.8f; await Frames();
            // An uninitialized unworn copy must not display a saved active expression.
            Select(1); await Frames();
            var unworn = avatar.Duplicate(avatar.World.RootSlot);
            await Frames();
            var unwornOutput = unworn.FindChild("Expressions").FindChild("Outputs").FindChild("A");
            Near(Get<float>(unwornOutput, "Result"), 0.8f, "unworn clone writes Base despite retained selection");
            Near(a.Value, 1, "unworn clone does not change the original result");
            unworn.Destroy();
            // Dequip must also work while the avatar remains beneath the same UserRoot.
            avatar.FindChild("Avatar Root Identification")
                .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>().OnDequip(null);
            await Frames(30);
            Check(Get<int>(core, "RightGesture") == 0 && Reference<Slot>(core, "CurrentExpression") == null,
                "identification dequip clears selection without a hierarchy change");
            Near(a.Value, 0.8f, "dequip restores tracked output Base");
            Near(b.Value, 0.3f, "dequip restores static output Base");
            Select(1);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.KeyboardRightTag, true, 1);
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true,
                Get<string>(catalog.FindChild("Short"), "Id"));
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.HandGesturesEnabledTag, true, false);
            await Frames();
            Check(Get<int>(core, "RightGesture") == 0 && Get<bool>(core, "AllowHandGestures") &&
                Reference<Slot>(core, "CurrentExpression") == null,
                "identification blocks gesture, keyboard, menu and permission APIs while unworn under UserRoot");
            tracking.Value = 0.65f; await Frames();
            Near(a.Value, 0.65f, "unworn tracking continues to follow Base");
            EquipAvatar(avatar); await Frames(30);
            Select(1); await Frames();
            Near(a.Value, 1, "re-equip re-enables selection without a hierarchy change");
            avatar.Parent = avatar.World.RootSlot; await Frames(30);
            Check(Get<int>(core, "RightGesture") == 0 && Reference<Slot>(core, "CurrentExpression") == null,
                "hierarchy departure clears state despite different worn-flag update order");
            Near(a.Value, 0.65f, "hierarchy departure restores tracking Base");
            avatar.Parent = parent; await Frames(30);
            Select(1); await Frames(); Near(a.Value, 1, "reattachment accepts input after worn flags update");
            Check(expressions.GetComponentsInChildren<ProtoFluxNode>().Count(n => n.GetType().Name == "LocalUpdate") == 0,
                "output updates are event driven without LocalUpdate");
        }
        finally { avatar.Destroy(); }
        Console.WriteLine("OUTPUT WRITES: immediate final poses, no loop playback, live tracking, missing/reordered tracks and unworn copies passed");
    }

    private static async Task Frames(int count = 8) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicVariableBase<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).DynamicValue;
    private static void Set<T>(Slot slot, string name, T value) => Check(slot.WriteDynamicVariable(VariablePath(slot, name), value) == DynamicVariableWriteResult.Success, "write " + name);
    private static void Near(float actual, float expected, string message, float tolerance = 0.01f) => Check(Math.Abs(actual - expected) < tolerance, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
