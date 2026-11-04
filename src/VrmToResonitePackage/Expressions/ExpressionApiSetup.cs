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
        BuildHandReceiver(new(_api.AddSlot("TrySetGestureLeft")), "Left", TrySetGestureLeftTag, checkPermission: true);
        BuildHandReceiver(new(_api.AddSlot("TrySetGestureRight")), "Right", TrySetGestureRightTag, checkPermission: true);
        BuildHandReceiver(new(_api.AddSlot("SetGestureLeft")), "Left", SetGestureLeftTag, checkPermission: false);
        BuildHandReceiver(new(_api.AddSlot("SetGestureRight")), "Right", SetGestureRightTag, checkPermission: false);
        BuildSelectReceiver(new(_api.AddSlot("Select")));
        BuildHandGesturesToggleReceiver(new(_api.AddSlot("ToggleHandGestures")));
        var reset = new ExpressionFlux(_api.AddSlot("Reset"));
        ReceiveUpdate(reset, ResetTag, reset.Trigger(reset.Ref(_internal), ResetStateTag));
    }

    private IWorldElement WriteHand(ExpressionFlux g, string hand, IWorldElement gesture) =>
        g.Write<int>(g.Ref(_internal), SystemSpace, hand + "Gesture", gesture);

    private void BuildHandReceiver(ExpressionFlux g, string hand, string tag, bool checkPermission)
    {
        var receiver = g.Receiver<int>(tag);
        if (checkPermission)
        {
            // SetGesture owns wearer checks and mutation for every input source.
            // Early gestures with saved false permissions are intentionally dropped.
            Link(receiver, "OnTriggered", g.If(
                g.Read<bool>(g.Ref(_internal), SystemSpace, HandGesturePermission(hand)),
                SendHandInput(g, g.Text(hand == "Left" ? SetGestureLeftTag : SetGestureRightTag), Out(receiver, "Value"))));
            return;
        }
        var mutation = g.Sequence(WriteHand(g, hand, Out(receiver, "Value")),
            g.Trigger(g.Ref(_internal), SelectionTickTag));
        Link(receiver, "OnTriggered", g.If(g.AvatarWornLocal, mutation));
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
        var select = g.Sequence(
            WriteHandGesturePermissions(g, g.Constant(false)),
            g.Trigger<Slot>(g.Ref(_internal), g.Text(PlaybackTickTag), selected));
        Link(receiver, "OnTriggered", g.If(g.And(g.AvatarWornLocal, valid), select));
    }

    private IWorldElement WriteHandGesturePermissions(ExpressionFlux g, IWorldElement value) =>
        g.Sequence(new[] { "Left", "Right" }
            .Select(side => (IWorldElement)g.Write<bool>(g.Ref(_internal), SystemSpace, HandGesturePermission(side), value)).ToArray());

    private void BuildHandGesturesToggleReceiver(ExpressionFlux g)
    {
        var core = g.Ref(_internal);
        // Match the authored Salt graph: capture NAND once per impulse before either write.
        var enabled = g.Local<bool>();
        var capture = g.Set<bool>(enabled, g.Node("NAND_Bool", null,
            ("A", g.Read<bool>(core, SystemSpace, HandGesturePermission("Left"))),
            ("B", g.Read<bool>(core, SystemSpace, HandGesturePermission("Right")))));
        Link(capture, "OnWritten", WriteHandGesturePermissions(g, enabled));
        ReceiveUpdate(g, ToggleHandGesturesTag, capture);
    }
}
