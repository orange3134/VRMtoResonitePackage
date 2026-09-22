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
        actions.Add(g.If(g.NotEqual<Slot>(selected, current),
            g.Write<Slot>(core, CoreSpace, "CurrentExpression", selected)));
        actions.Add(g.Trigger(g.Ref(_playback), PlaybackTickTag));
        var select = g.Sequence(actions.ToArray());
        ReceiveUpdate(g, SelectionTickTag, select);
        // API requests retain synchronous selection. Inspector/table edits also
        // update selection, but an unchanged pair/clip no longer runs this sequence.
        var changed = g.If(g.IsOwner(_root), select);
        g.OnChanged<int>(index, changed);
        g.OnChanged<Slot>(resolved, changed);
    }

    private void BuildPlayback()
    {
        var g = new ExpressionFlux(_playback);
        var core = g.Ref(_core);
        // Pose records contain only final values. Frame updates merge these cached
        // values with live tracking and blink inputs; no animation assets are used.
        var value = g.Local<float>();
        var wearer = g.IsOwner(_root);
        var canWrite = g.Or(wearer, g.And(g.IsNull<User>(g.Owner(_root)),
            g.Node("IsLocalUser", null, ("User", g.Node("HostUser")))));
        var current = g.Choose<Slot>(wearer, g.Read<Slot>(core, CoreSpace, "CurrentExpression"), g.Ref<Slot>(null));
        var bindings = g.Read<Slot>(current, ClipSpace, "Bindings");
        g.BeginSection("Apply fixed pose and live tracking");
        var update = g.Each(g.Ref(_outputs), output =>
        {
            var baseValue = g.Read<float>(output, OutputSpace, "Base");
            var desired = g.Choose<float>(g.And(wearer, g.Read<bool>(output, OutputSpace, "HasPose")),
                g.Read<float>(output, OutputSpace, "Pose"), baseValue);
            desired = g.Lerp(desired, baseValue, g.Clamp01(g.Read<float>(output, OutputSpace, "TrackingWeight")));
            var blinkMode = g.Read<int>(output, OutputSpace, "BlinkMode");
            var result = g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(1)), g.Binary<float>("ValueMax", desired, baseValue),
                g.Choose<float>(g.Equal<int>(blinkMode, g.Constant(2)), g.Binary<float>("ValueMin", desired, baseValue), desired));
            return g.Sequence(
                g.Set<float>(value, result),
                g.If(g.NotEqual<float>(value, g.Read<float>(output, OutputSpace, "Result")),
                    g.Write<float>(output, OutputSpace, "Result", value)));
        });
        g.BeginSection("Apply stored pose on selection");
        var bindingOutput = g.Local<Slot>();
        var refresh = g.Sequence(
            g.Each(g.Ref(_outputs), record => g.Write<bool>(record, OutputSpace, "HasPose", g.Constant(false))),
            g.Each(bindings, binding => g.Sequence(
                g.Set<Slot>(bindingOutput, g.Read<Slot>(binding, BindingSpace, "Output")),
                g.If(g.And(g.Active(binding), g.Active(bindingOutput)), g.Sequence(
                    g.Write<float>(bindingOutput, OutputSpace, "Pose", g.Read<float>(binding, BindingSpace, "Value")),
                    g.Write<bool>(bindingOutput, OutputSpace, "HasPose", g.Constant(true)))))),
            update);
        // Only the wearer writes a pose. The host follows Base on unworn copies.
        g.Node("LocalUpdate", null, ("OnUpdate", g.If(canWrite, update)));
        var receiver = g.Receiver(PlaybackTickTag, false);
        Link(receiver, "OnTriggered", g.If(wearer, refresh));
        g.OnChanged<Slot>(current, g.If(canWrite, refresh));
        g.OnChanged<Slot>(bindings, g.If(canWrite, refresh));
        g.OnStart(g.If(canWrite, refresh));
    }

    private static IWorldElement ValidExpression(ExpressionFlux g, IWorldElement expression) =>
        g.And(g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Active(g.Read<Slot>(expression, ClipSpace, "Bindings")));
}
