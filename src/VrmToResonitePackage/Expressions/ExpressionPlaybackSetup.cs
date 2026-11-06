using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildSelection()
    {
        // Fixed system state binds to the named ancestor space; selected clips stay dynamic.
        var g = new ExpressionFlux(_selection);
        var core = g.Ref(_api);
        var left = g.Read<int>(core, SystemSpace, "State.LeftGesture");
        var right = g.Read<int>(core, SystemSpace, "State.RightGesture");
        var candidate = ReadGesturePair(g, left, right);
        var select = g.Trigger<Slot>(core, g.Text(PlaybackTickTag), candidate);
        ReceiveUpdate(g, SelectionTickTag, select);
    }

    private void BuildPlayback()
    {
        var g = new ExpressionFlux(_playback);
        var core = g.Ref(_api);
        // Apply static outputs as one selection operation. Tracking outputs have a
        // separate live driver, without per-shape change detectors or impulses.
        var value = g.Local<float>();
        var wearer = g.AvatarWornLocal;
        var current = g.Read<Slot>(core, SystemSpace, "State.CurrentExpression");
        var refresh = g.Each(g.Ref(_outputs), output =>
        {
            var result = MixOutput(g, output, wearer, current);
            return g.If(g.IsNull<ISyncRef>(g.Read<ISyncRef>(output, OutputSpace, "OriginalDriver")), g.Sequence(
                g.Set<float>(value, result),
                g.If(g.NotEqual<float>(value, g.Read<float>(output, OutputSpace, "Result")),
                    g.Write<float>(output, OutputSpace, "Result", value))));
        });
        // Only the local wearer writes static outputs, including Base restoration.
        var receiver = g.Receiver<Slot>(PlaybackTickTag);
        var selected = Out(receiver, "Value");
        // Explicit reset may clear state even without local wear authority.
        // Non-null selections require the local wearer; unworn instances accept only clearing.
        var accept = g.Or(wearer, g.And(g.Not(g.AvatarWorn), g.IsNull<Slot>(selected)));
        // Menu hold gates only application; inputs and gesture lookup keep updating.
        var menuReleased = g.Not(g.Read<bool>(core, SystemSpace, ContextMenuSelected));
        // Compare before writing CurrentExpression; repeated Slots (including null) do not sweep Outputs.
        Link(receiver, "OnTriggered", g.If(g.And(accept, menuReleased, g.NotEqual<Slot>(selected, current)), g.Sequence(
            g.Write<Slot>(core, SystemSpace, "State.CurrentExpression", selected),
            g.If(wearer, refresh))));
        // OnAvatarWorn initializes through ResetState's impulse; no separate startup or wear refresh.
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
        // Read the selected expression by name; missing variables follow live Base.
        // No per-shape state, binding scans or synchronized per-frame impulses.
        var g = new ExpressionFlux(output.AddSlot("Tracking"));
        var current = g.Read<Slot>(g.Ref(_api), SystemSpace, "State.CurrentExpression");
        var target = output.FindChild("DV").GetComponentsInChildren<DynamicField<float>>()
            .Single(v => v.VariableName.Value == Path(OutputSpace, "Result")).TargetField.Target;
        var driver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float>)
            g.Node("ValueFieldDrive", typeof(float), ("Value", MixOutput(g, g.Ref(output),
                g.AvatarWorn, current)));
        driver.GetRootProxy(addIfMissing: true).Drive.Target = target;
    }

    private static IWorldElement MixOutput(ExpressionFlux g, IWorldElement output, IWorldElement worn, IWorldElement expression)
    {
        var baseValue = g.Read<float>(output, OutputSpace, "Base");
        var path = g.Node("ConcatenateString", null, ("A", g.Text(Path(ClipSpace, ""))),
            ("B", g.Read<string>(output, OutputSpace, "Id")));
        var pose = g.Node("ReadDynamicValueVariable", typeof(float), ("Source", expression), ("Path", path));
        var desired = g.Choose<float>(g.And(worn, Out(pose, "FoundValue")),
            Out(pose, "Value"), baseValue);
        desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(output, OutputSpace, "TrackingWeight")));
        var blinkMode = g.Read<int>(output, OutputSpace, "BlinkMode");
        return g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(1)), g.Binary<float>("ValueMax", desired, baseValue),
            g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(2)), g.Binary<float>("ValueMin", desired, baseValue), desired));
    }
}
