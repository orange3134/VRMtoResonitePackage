using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildSelection()
    {
        var g = new ExpressionFlux(_selection);
        var core = g.Ref(_core);
        var actions = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
            actions.Add(g.If(g.Not(g.Active(g.Read<Slot>(core, hand + "Input"))),
                g.Write<int>(core, hand + "Gesture", g.Constant(0))));

        g.BeginSection("Resolve gesture and override");
        var index = g.Binary<int>("ValueAdd", g.Binary<int>("ValueMul", g.Read<int>(core, "LeftGesture"), g.Constant(8)),
            g.Read<int>(core, "RightGesture"));
        var path = g.Node("ConcatenateString", null, ("A", g.Text("Expr/Pair.")),
            ("B", g.Node("ToString_Int", null, ("V", index))));
        var mapped = g.Read<Slot>(g.Ref(_table), path);
        var manual = g.Read<Slot>(core, "Override");
        var useManual = g.And(g.Active(manual), g.Read<bool>(manual, "Enabled"));
        var candidate = g.Choose<Slot>(useManual, manual, mapped);
        var selected = g.Local<Slot>();
        var current = g.Read<Slot>(core, "CurrentExpression");
        var noExpression = g.Ref<Slot>(null);
        actions.Add(g.Set<Slot>(selected, g.Choose<Slot>(ValidExpression(g, candidate), candidate, noExpression)));
        actions.Add(g.Write<int>(core, "PairIndex", index));
        actions.Add(g.Write<Slot>(core, "MappedExpression", mapped));
        actions.Add(g.Write<Slot>(core, "CandidateExpression", candidate));
        actions.Add(g.Write<int>(core, "SelectionStatus", g.Choose<int>(g.Equal<Slot>(selected, noExpression),
            g.Choose<int>(g.Equal<Slot>(candidate, noExpression), g.Constant(0), g.Constant(3)),
            g.Choose<int>(useManual, g.Constant(2), g.Constant(1)))));

        g.BeginSection("Snapshot and switch only when changed");
        actions.Add(g.If(g.Not(g.Equal<Slot>(selected, current)), g.Sequence(
            g.Each(g.Ref(_outputs), output => g.Write<float>(output, "Snapshot", g.Read<float>(output, "Result"))),
            g.Write<float>(core, "FadeDuration", g.Choose<float>(g.Active(selected),
                g.Read<float>(selected, "FadeIn"), g.Read<float>(current, "FadeOut"))),
            g.Write<float>(core, "PlaybackStart", g.Now), g.Write<Slot>(core, "CurrentExpression", selected))));
        ReceiveUpdate(g, SelectionTickTag, g.Sequence(actions.ToArray()));
    }

    private void BuildPlayback()
    {
        var g = new ExpressionFlux(_playback);
        var core = g.Ref(_core);
        var current = g.Read<Slot>(core, "CurrentExpression");
        var elapsed = g.Sub(g.Now, g.Read<float>(core, "PlaybackStart"));
        var duration = g.Read<float>(core, "FadeDuration");
        var blend = g.Choose<float>(g.Greater(duration, g.Constant(0f)),
            g.Clamp01(g.Div(elapsed, duration)), g.Constant(1f));

        g.BeginSection("Sample, mix tracking, and fade");
        var mix = g.Each(g.Ref(_outputs), output =>
        {
            var sample = Sample(g, current, g.Read<string>(output, "Id"), elapsed);
            var baseValue = g.Read<float>(output, "Base");
            var desired = g.Choose<float>(g.And(g.Active(current),
                g.Binary<int>("ValueGreaterOrEqual", sample.Index, g.Constant(0))), sample.Value, baseValue);
            desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(output, "TrackingWeight")));
            return g.Write<float>(output, "Result", g.Lerp(g.Read<float>(output, "Snapshot"), desired, blend));
        });
        // Diagnostics are derived locally on each client. Writing elapsed every update would
        // otherwise add continuous network traffic even while a static expression is unchanged.
        DriveDiagnostic("PlaybackElapsed", elapsed);
        DriveDiagnostic("FadeWeight", blend);
        ReceiveUpdate(g, PlaybackTickTag, mix);

        void DriveDiagnostic(string name, IWorldElement value)
        {
            var driver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<float>)
                g.Node("ValueFieldDrive", typeof(float), ("Value", value));
            var field = _core.GetComponents<DynamicValueVariable<float>>()
                .Single(v => v.VariableName.Value == Path(name)).Value;
            driver.GetRootProxy(addIfMissing: true).Drive.Target = field;
        }
    }

    private static IWorldElement ClipAsset(ExpressionFlux g, IWorldElement expression) => g.Node("GetAsset", typeof(Animation),
        ("Provider", g.Read<IAssetProvider<Animation>>(expression, "Clip")));
    private static IWorldElement ValidExpression(ExpressionFlux g, IWorldElement expression) =>
        g.And(g.Active(expression), g.Read<bool>(expression, "Enabled"),
            g.Node("NotNull", typeof(Animation), ("Instance", ClipAsset(g, expression))));
    private static IWorldElement SampleTime(ExpressionFlux g, IWorldElement expression, IWorldElement elapsed) => g.Choose<float>(
        g.Read<bool>(expression, "Loop"), g.Binary<float>("ValueMod", elapsed, g.Read<float>(expression, "Duration")), elapsed);
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
