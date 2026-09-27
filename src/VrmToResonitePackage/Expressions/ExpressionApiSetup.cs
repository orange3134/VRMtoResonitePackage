using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildApi()
    {
        var logic = _api.AddSlot("Logic");
        BuildHandReceiver(new(logic.AddSlot("Left")), "Left", LeftTag, gestureInput: true);
        BuildHandReceiver(new(logic.AddSlot("Right")), "Right", RightTag, gestureInput: true);
        BuildHandReceiver(new(logic.AddSlot("KeyboardLeft")), "Left", KeyboardLeftTag, gestureInput: false);
        BuildHandReceiver(new(logic.AddSlot("KeyboardRight")), "Right", KeyboardRightTag, gestureInput: false);
        BuildSelectReceiver(new(logic.AddSlot("Select")));
        BuildHandGesturesEnabledReceiver(new(logic.AddSlot("AllowHandGestures")));
    }

    // Initialization runs before checking the input gate so the first event after
    // cloning or reattachment can restore the default enabled state.
    private IWorldElement ApplyRequest(ExpressionFlux g, IWorldElement mutation, IWorldElement allowed = null) => g.Sequence(
        g.Trigger(g.Ref(_lifecycle), InitializeTag),
        g.If(allowed ?? g.Constant(true), mutation));

    private IWorldElement WriteHand(ExpressionFlux g, string hand, IWorldElement gesture) =>
        g.Write<int>(g.Ref(_core), SystemSpace, "Core." + hand + "Gesture", gesture);

    private void BuildHandReceiver(ExpressionFlux g, string hand, string tag, bool gestureInput)
    {
        var receiver = g.Receiver<int>(tag);
        var mutation = g.Sequence(WriteHand(g, hand, Out(receiver, "Value")),
            g.Trigger(g.Ref(_selection), SelectionTickTag));
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            ApplyRequest(g, mutation, gestureInput
                ? g.Read<bool>(g.Ref(_core), SystemSpace, "Core.AllowHandGestures") : null)));
    }

    private static IWorldElement FormatGesturePair(ExpressionFlux g, string format, IWorldElement left, IWorldElement right)
    {
        var node = (Nodes.Strings.FormatString)
            g.Node("FormatString", null, ("Format", g.Text(format)));
        node.Parameters.Add((INodeObjectOutput<object>)g.Node("Box", typeof(int), ("Input", left)));
        node.Parameters.Add((INodeObjectOutput<object>)g.Node("Box", typeof(int), ("Input", right)));
        return node;
    }

    private IWorldElement ReadGesturePair(ExpressionFlux g, IWorldElement left, IWorldElement right)
    {
        var path = FormatGesturePair(g, Path(SystemSpace, "GestureTable.Pair.L{0}R{1}"), left, right);
        return g.Read<Slot>(g.Ref(_table), path);
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(SelectTag);
        var id = Out(receiver, "Value");
        var selected = g.Local<Slot>();
        var find = g.Each(g.Ref(_catalog), expression => g.If(g.And(
            g.IsNull<Slot>(selected), ValidExpression(g, expression),
            g.Equal<string>(id, g.Read<string>(expression, ClipSpace, "Id"))),
            g.Set<Slot>(selected, expression)));
        var select = g.Sequence(g.Set<Slot>(selected, g.Ref<Slot>(null)), find,
            g.If(g.Not(g.IsNull<Slot>(selected)), ApplyRequest(g, g.Sequence(
                g.Write<bool>(g.Ref(_core), SystemSpace, "Core.AllowHandGestures", g.Constant(false)),
                g.Write<Slot>(g.Ref(_core), SystemSpace, "Core.CurrentExpression", selected),
                g.Trigger(g.Ref(_playback), PlaybackTickTag)))));
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.NotEqual<string>(id, g.Text("")), g.Node("NotNull", typeof(string), ("Instance", id))), select));
    }

    private void BuildHandGesturesEnabledReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver<bool>(HandGesturesEnabledTag);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            ApplyRequest(g, g.Write<bool>(g.Ref(_core), SystemSpace, "Core.AllowHandGestures", Out(receiver, "Value")))));
    }
}
