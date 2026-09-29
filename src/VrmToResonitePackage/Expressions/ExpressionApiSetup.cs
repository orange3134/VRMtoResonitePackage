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
        BuildHandGesturesToggleReceiver(new(logic.AddSlot("ToggleHandGestures")));
        var reset = new ExpressionFlux(logic.AddSlot("Reset"));
        ReceiveUpdate(reset, ResetTag, reset.Trigger(reset.Ref(_internal), ResetStateTag));
    }

    // Initialization runs before checking the input gate so the first event after
    // cloning or reattachment can restore the default enabled state.
    private IWorldElement ApplyRequest(ExpressionFlux g, IWorldElement mutation, IWorldElement allowed = null) => g.Sequence(
        g.Trigger(g.Ref(_internal), InitializeTag),
        g.If(allowed ?? g.Constant(true), mutation));

    private IWorldElement WriteHand(ExpressionFlux g, string hand, IWorldElement gesture) =>
        g.Write<int>(g.Ref(_internal), SystemSpace, hand + "Gesture", gesture);

    private void BuildHandReceiver(ExpressionFlux g, string hand, string tag, bool gestureInput)
    {
        var receiver = g.Receiver<int>(tag);
        var mutation = g.Sequence(WriteHand(g, hand, Out(receiver, "Value")),
            g.Trigger(g.Ref(_internal), SelectionTickTag));
        Link(receiver, "OnTriggered", g.If(g.AvatarWornLocal,
            ApplyRequest(g, mutation, gestureInput
                ? g.Read<bool>(g.Ref(_internal), SystemSpace, "AllowHandGestures") : null)));
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
        var path = FormatGesturePair(g, Path(SystemSpace, "GestureTable.L{0}R{1}"), left, right);
        return g.Read<Slot>(g.Ref(_table), path);
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver<Slot>(SelectTag);
        var selected = Out(receiver, "Value");
        var valid = g.And(g.Not(g.IsNull<Slot>(selected)),
            g.Equal<Slot>(g.Node("GetParentSlot", null, ("Instance", selected)), g.Ref(_catalog)));
        var select = ApplyRequest(g, g.Sequence(
            g.Write<bool>(g.Ref(_internal), SystemSpace, "AllowHandGestures", g.Constant(false)),
            g.Trigger<Slot>(g.Ref(_internal), g.Text(PlaybackTickTag), selected)));
        Link(receiver, "OnTriggered", g.If(g.And(g.AvatarWornLocal, valid), select));
    }

    private void BuildHandGesturesToggleReceiver(ExpressionFlux g)
    {
        var core = g.Ref(_internal);
        ReceiveUpdate(g, ToggleHandGesturesTag, ApplyRequest(g,
            g.Write<bool>(core, SystemSpace, "AllowHandGestures",
                g.Not(g.Read<bool>(core, SystemSpace, "AllowHandGestures")))));
    }

    private void BuildHandGesturesEnabledReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver<bool>(HandGesturesEnabledTag);
        Link(receiver, "OnTriggered", g.If(g.AvatarWornLocal,
            ApplyRequest(g, g.Write<bool>(g.Ref(_internal), SystemSpace, "AllowHandGestures", Out(receiver, "Value")))));
    }
}
