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

        g.BeginSection("Resolve gesture pair");
        var index = g.Binary<int>("ValueAdd", g.Binary<int>("ValueMul", g.Read<int>(core, CoreSpace, "LeftGesture"), g.Constant(8)),
            g.Read<int>(core, CoreSpace, "RightGesture"));
        var path = g.Node("ConcatenateString", null, ("A", g.Text(Path(TableSpace, "Pair."))),
            ("B", g.Node("ToString_Int", null, ("V", index))));
        var candidate = g.Read<Slot>(g.Ref(_table), path);
        // Capture validation for this update without persisting intermediate references in Core.
        var selected = g.Local<Slot>();
        var current = g.Read<Slot>(core, CoreSpace, "CurrentExpression");
        var noExpression = g.Ref<Slot>(null);
        var resolved = g.Choose<Slot>(ValidExpression(g, candidate), candidate, noExpression);
        actions.Add(g.Set<Slot>(selected, resolved));
        actions.Add(g.Write<int>(core, CoreSpace, "PairIndex", index));

        g.BeginSection("Snapshot and switch only when changed");
        actions.Add(g.If(g.NotEqual<Slot>(selected, current), g.Sequence(
            g.Each(g.Ref(_outputs), output => g.Write<float>(output, OutputSpace, "Snapshot", g.Read<float>(output, OutputSpace, "Result"))),
            g.Write<float>(core, CoreSpace, "FadeDuration", g.Choose<float>(g.Active(selected),
                g.Read<float>(selected, ClipSpace, "FadeIn"), g.Read<float>(current, ClipSpace, "FadeOut"))),
            g.Write<float>(core, CoreSpace, "PlaybackStart", g.Now), g.Write<Slot>(core, CoreSpace, "CurrentExpression", selected))));
        var select = g.Sequence(actions.ToArray());
        ReceiveUpdate(g, SelectionTickTag, select);
        // API requests retain synchronous selection. Inspector/table/asset edits also
        // update selection, but an unchanged pair/clip no longer runs this sequence.
        var changed = g.If(g.IsOwner(_root), select);
        g.OnChanged<int>(index, changed);
        g.OnChanged<Slot>(resolved, changed);
    }

    private void BuildPlayback()
    {
        var g = new ExpressionFlux(_playback);
        var timing = PlaybackTiming(g, g.Ref(_core));
        // Diagnostics and outputs are evaluated locally, without synchronized per-frame writes.
        DriveValue(g, _core, CoreSpace, "PlaybackElapsed", timing.Elapsed);
        DriveValue(g, _core, CoreSpace, "FadeWeight", timing.Blend);
        foreach (var output in _outputSlots.Values) BuildOutputPlayback(output);
    }

    private void BuildOutputPlayback(Slot output)
    {
        var g = new ExpressionFlux(output.AddSlot("Logic"));
        var core = g.Ref(_core);
        var record = g.Ref(output);
        var current = g.Read<Slot>(core, CoreSpace, "CurrentExpression");
        // Evaluate the clock in this board. Reading driven diagnostic fields could combine
        // a new CurrentExpression with the previous update's elapsed/fade values.
        var timing = PlaybackTiming(g, core);

        g.BeginSection("Sample current expression");
        var sample = Sample(g, current, g.Read<string>(record, OutputSpace, "Id"), timing.Elapsed);
        var baseValue = g.Read<float>(record, OutputSpace, "Base");
        var desired = g.Choose<float>(g.And(g.Active(current),
            g.Binary<int>("ValueGreaterOrEqual", sample.Index, g.Constant(0))), sample.Value, baseValue);
        desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(record, OutputSpace, "TrackingWeight")));

        g.BeginSection("Fade and drive result");
        var result = g.Lerp(g.Read<float>(record, OutputSpace, "Snapshot"), desired, timing.Blend);
        // All clients evaluate the same synchronized selection. An unworn instance follows
        // Base even if it has retained selection state from saving or cloning.
        DriveValue(g, output, OutputSpace, "Result", g.Choose<float>(g.IsNull<User>(g.Owner(_root)), baseValue, result));
    }

    private static (IWorldElement Elapsed, IWorldElement Blend) PlaybackTiming(ExpressionFlux g, IWorldElement core)
    {
        var elapsed = g.Sub(g.Now, g.Read<float>(core, CoreSpace, "PlaybackStart"));
        var duration = g.Read<float>(core, CoreSpace, "FadeDuration");
        var blend = g.Choose<float>(g.Greater(duration, g.Constant(0f)),
            g.Clamp01(g.Div(elapsed, duration)), g.Constant(1f));
        return (elapsed, blend);
    }

    private static void DriveValue(ExpressionFlux g, Slot record, string space, string name, IWorldElement value)
    {
        var driver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float>)
            g.Node("ValueFieldDrive", typeof(float), ("Value", value));
        var field = record.GetComponents<DynamicValueVariable<float>>()
            .Single(v => v.VariableName.Value == Path(space, name)).Value;
        driver.GetRootProxy(addIfMissing: true).Drive.Target = field;
    }

    private static IWorldElement ClipAsset(ExpressionFlux g, IWorldElement expression) => g.Node("GetAsset", typeof(Animation),
        ("Provider", g.Read<IAssetProvider<Animation>>(expression, ClipSpace, "Clip")));
    private static IWorldElement ValidExpression(ExpressionFlux g, IWorldElement expression) =>
        g.And(g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Node("NotNull", typeof(Animation), ("Instance", ClipAsset(g, expression))));
    private static IWorldElement SampleTime(ExpressionFlux g, IWorldElement expression, IWorldElement elapsed) => g.Choose<float>(
        g.Read<bool>(expression, ClipSpace, "Loop"), g.Binary<float>("ValueMod", elapsed, g.Read<float>(expression, ClipSpace, "Duration")), elapsed);
    private static (IWorldElement Index, IWorldElement Value) Sample(ExpressionFlux g, IWorldElement expression,
        IWorldElement property, IWorldElement elapsed)
    {
        var asset = ClipAsset(g, expression);
        var index = g.Node("FindAnimationTrackIndex", null, ("Animation", asset), ("Node", g.Text("Expression")), ("Property", property));
        var value = g.Node("SampleValueAnimationTrack", typeof(float),
            ("Animation", asset), ("TrackIndex", index), ("Time", SampleTime(g, expression, elapsed)));
        return (index, value);
    }
}
