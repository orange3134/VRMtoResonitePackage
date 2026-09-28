using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildSelection()
    {
        // Fixed Core state binds to the named ancestor space; selected clips stay dynamic.
        var g = new ExpressionFlux(_selection);
        var core = g.Ref(_core);
        var actions = new List<IWorldElement>();

        var left = g.Read<int>(core, SystemSpace, "Core.LeftGesture");
        var right = g.Read<int>(core, SystemSpace, "Core.RightGesture");
        var key = FormatGesturePair(g, "L{0}R{1}", left, right);
        var candidate = ReadGesturePair(g, left, right);
        // Capture validation for this update without persisting intermediate references in Core.
        var selected = g.Local<Slot>();
        var current = g.Read<Slot>(core, SystemSpace, "Core.CurrentExpression");
        var noExpression = g.Ref<Slot>(null);
        var resolved = g.Choose<Slot>(ValidExpression(g, candidate), candidate, noExpression);
        actions.Add(g.Set<Slot>(selected, resolved));
        actions.Add(g.Write<string>(core, SystemSpace, "Core.PairKey", key));

        actions.Add(g.If(g.NotEqual<Slot>(selected, current),
            g.Write<Slot>(core, SystemSpace, "Core.CurrentExpression", selected)));
        actions.Add(g.Trigger(g.Ref(_playback), PlaybackTickTag));
        var select = g.Sequence(actions.ToArray());
        ReceiveUpdate(g, SelectionTickTag, select);
    }

    private void BuildPlayback()
    {
        var g = new ExpressionFlux(_playback);
        var core = g.Ref(_core);
        // Apply static outputs as one selection operation. Tracking outputs have a
        // separate live driver, without per-shape change detectors or impulses.
        var value = g.Local<float>();
        var wearer = g.AvatarWornLocal;
        var canWrite = g.Or(wearer, g.And(g.Not(g.AvatarWorn),
            g.Node("IsLocalUser", null, ("User", g.Node("HostUser")))));
        var current = g.Choose<Slot>(wearer, g.Read<Slot>(core, SystemSpace, "Core.CurrentExpression"), g.Ref<Slot>(null));
        var bindings = g.Read<Slot>(current, ClipSpace, "Bindings");
        var selectedBinding = g.Local<Slot>();
        var refresh = g.Each(g.Ref(_outputs), output =>
        {
            var result = MixOutput(g, output, wearer);
            return g.Sequence(
                g.Set<Slot>(selectedBinding, g.Ref<Slot>(null)),
                g.If(g.Active(output), g.Each(bindings, binding =>
                    g.If(g.And(g.Active(binding), g.Equal<Slot>(output, g.Read<Slot>(binding, BindingSpace, "Output"))),
                        g.Set<Slot>(selectedBinding, binding)))),
                g.Write<Slot>(output, OutputSpace, "Binding", selectedBinding),
                g.If(g.IsNull<ISyncRef>(g.Read<ISyncRef>(output, OutputSpace, "OriginalDriver")), g.Sequence(
                    g.Set<float>(value, result),
                    g.If(g.NotEqual<float>(value, g.Read<float>(output, OutputSpace, "Result")),
                        g.Write<float>(output, OutputSpace, "Result", value)))));
        });
        // Only the wearer writes a pose. The host follows Base on unworn copies.
        var receiver = g.Receiver(PlaybackTickTag, false);
        Link(receiver, "OnTriggered", g.If(wearer, refresh));
        g.OnChanged<bool>(canWrite, g.If(canWrite, refresh));
        g.OnChanged<Slot>(current, g.If(canWrite, refresh));
        g.OnChanged<Slot>(bindings, g.If(canWrite, refresh));
        g.OnStart(g.If(canWrite, refresh));
        foreach (var output in _outputSlots.Values)
        {
            if (!output.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<ISyncRef>>().Any(v =>
                v.VariableName.Value == Path(OutputSpace, "OriginalDriver"))) continue;
            BuildLiveTracking(output);
        }
    }

    private void BuildLiveTracking(Slot output)
    {
        // Only shapes already driven by blink/viseme/etc. need continuous mixing.
        // Selection resolves the binding; a missing binding follows the live Base.
        // Tracking never searches bindings or resamples a clip
        // and never sends synchronized per-frame Write impulses.
        var g = new ExpressionFlux(output.AddSlot("Tracking"));
        var target = output.FindChild("DV").GetComponentsInChildren<DynamicField<float>>()
            .Single(v => v.VariableName.Value == Path(OutputSpace, "Result")).TargetField.Target;
        var driver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float>)
            g.Node("ValueFieldDrive", typeof(float), ("Value", MixOutput(g, g.Ref(output),
                g.AvatarWorn)));
        driver.GetRootProxy(addIfMissing: true).Drive.Target = target;
    }

    private static IWorldElement MixOutput(ExpressionFlux g, IWorldElement output, IWorldElement worn)
    {
        var baseValue = g.Read<float>(output, OutputSpace, "Base");
        var binding = g.Read<Slot>(output, OutputSpace, "Binding");
        var desired = g.Choose<float>(g.And(worn, g.Active(binding)),
            g.Read<float>(binding, BindingSpace, "Value"), baseValue);
        desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(output, OutputSpace, "TrackingWeight")));
        var blinkMode = g.Read<int>(output, OutputSpace, "BlinkMode");
        return g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(1)), g.Binary<float>("ValueMax", desired, baseValue),
            g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(2)), g.Binary<float>("ValueMin", desired, baseValue), desired));
    }

    private static IWorldElement ValidExpression(ExpressionFlux g, IWorldElement expression) =>
        g.And(g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Active(g.Read<Slot>(expression, ClipSpace, "Bindings")));
}
