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

        g.BeginSection("Switch immediately when changed");
        actions.Add(g.If(g.NotEqual<Slot>(selected, current), g.Sequence(
            g.Write<float>(core, CoreSpace, "PlaybackStart", g.Now), g.Write<Slot>(core, CoreSpace, "CurrentExpression", selected))));
        actions.Add(g.Trigger(g.Ref(_playback), PlaybackTickTag));
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
        var core = g.Ref(_core);
        // One execution graph serves every output. Locals snapshot the selected asset and
        // clock once per update; Result is written only when its value actually changes.
        var current = g.Local<Slot>();
        var asset = g.Local<Animation>();
        var elapsed = g.Local<float>();
        var time = g.Local<float>();
        var index = g.Local<int>();
        var value = g.Local<float>();
        var wearer = g.IsOwner(_root);
        var canWrite = g.Or(wearer, g.And(g.IsNull<User>(g.Owner(_root)),
            g.Node("IsLocalUser", null, ("User", g.Node("HostUser")))));
        g.BeginSection("Capture shared playback state");
        var update = g.Sequence(
            g.Set<Slot>(current, g.Choose<Slot>(wearer, g.Read<Slot>(core, CoreSpace, "CurrentExpression"), g.Ref<Slot>(null))),
            g.Set<Animation>(asset, ClipAsset(g, current)),
            g.Set<float>(elapsed, g.Choose<float>(g.Active(current), g.Sub(g.Now, g.Read<float>(core, CoreSpace, "PlaybackStart")), g.Constant(0f))),
            g.Set<float>(time, SampleTime(g, current, elapsed)),
            g.Write<float>(core, CoreSpace, "PlaybackElapsed", elapsed),
            g.Write<float>(core, CoreSpace, "AnimationTime", time),
            g.Each(g.Ref(_outputs), output =>
            {
                g.BeginSection("Sample and write outputs");
                var baseValue = g.Read<float>(output, OutputSpace, "Base");
                var sample = g.Node("SampleValueAnimationTrack", typeof(float),
                    ("Animation", asset), ("TrackIndex", index), ("Time", time));
                var desired = g.Choose<float>(g.And(g.Active(current),
                    g.Binary<int>("ValueGreaterOrEqual", index, g.Constant(0))), sample, baseValue);
                desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(output, OutputSpace, "TrackingWeight")));
                var blinkMode = g.Read<int>(output, OutputSpace, "BlinkMode");
                var result = g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(1)), g.Binary<float>("ValueMax", desired, baseValue),
                    g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(2)), g.Binary<float>("ValueMin", desired, baseValue), desired));
                return g.Sequence(
                    g.Set<int>(index, g.Node("FindAnimationTrackIndex", null, ("Animation", asset),
                        ("Node", g.Text("Expression")), ("Property", g.Read<string>(output, OutputSpace, "Id")))),
                    g.Set<float>(value, result),
                    g.If(g.NotEqual<float>(value, g.Read<float>(output, OutputSpace, "Result")),
                        g.Write<float>(output, OutputSpace, "Result", value)));
            }));
        // The wearer writes animated values; observers consume the synchronized fields.
        // Only the host follows Base on unworn copies, avoiding competing writers.
        g.Node("LocalUpdate", null, ("OnUpdate", g.If(canWrite, update)));
        var receiver = g.Receiver(PlaybackTickTag, false);
        Link(receiver, "OnTriggered", g.If(wearer, update));
        g.OnStart(g.If(canWrite, update));
    }

    private static IWorldElement ClipAsset(ExpressionFlux g, IWorldElement expression) => g.Node("GetAsset", typeof(Animation),
        ("Provider", g.Read<IAssetProvider<Animation>>(expression, ClipSpace, "Clip")));
    private static IWorldElement ValidExpression(ExpressionFlux g, IWorldElement expression) =>
        g.And(g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Node("NotNull", typeof(Animation), ("Instance", ClipAsset(g, expression))));
    private static IWorldElement SampleTime(ExpressionFlux g, IWorldElement expression, IWorldElement elapsed) => g.Choose<float>(
        g.Read<bool>(expression, ClipSpace, "Loop"), g.Binary<float>("ValueMod", elapsed, g.Read<float>(expression, ClipSpace, "Duration")), elapsed);
}
