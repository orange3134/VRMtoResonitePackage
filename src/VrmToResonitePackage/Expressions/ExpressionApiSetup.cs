using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildApi()
    {
        var logic = _api.AddSlot("Logic");
        BuildGestureReceiver(new(logic.AddSlot("Left")), "Left", LeftTag);
        BuildGestureReceiver(new(logic.AddSlot("Right")), "Right", RightTag);
        BuildSelectReceiver(new(logic.AddSlot("Select")));
        BuildAutomaticReceiver(new(logic.AddSlot("Automatic")));
    }

    // Keep initialization and both stages synchronous: each event resolves the hand pair
    // that exists when it arrives, even when two events arrive before the next frame.
    private IWorldElement ApplyRequest(ExpressionFlux g, IWorldElement mutation) => g.Sequence(
        g.Trigger(g.Ref(_lifecycle), InitializeTag), mutation,
        g.Trigger(g.Ref(_selection), SelectionTickTag), g.Trigger(g.Ref(_playback), PlaybackTickTag));

    private void BuildGestureReceiver(ExpressionFlux g, string hand, string tag)
    {
        var core = g.Ref(_core);
        var receiver = g.Receiver<int>(tag);
        var payload = Out(receiver, "Value");
        var mutation = g.Sequence(
            g.Write<int>(core, hand + "Gesture", payload),
            g.Write<int>(core, hand + "Revision",
                g.Binary<int>("ValueAdd", g.Read<int>(core, hand + "Revision"), g.Constant(1))));
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.Binary<int>("ValueGreaterOrEqual", payload, g.Constant(0)),
            g.Binary<int>("ValueLessThan", payload, g.Constant(8))), ApplyRequest(g, mutation)));
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(SelectTag);
        var id = Out(receiver, "Value");
        var select = g.Each(g.Ref(_catalog), expression => g.If(
            g.And(g.Active(expression), g.Read<bool>(expression, "Enabled"),
                g.Equal<string>(id, g.Read<string>(expression, "Id"))),
            ApplyRequest(g, g.Write<Slot>(g.Ref(_core), "Override", expression))));
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.Not(g.Equal<string>(id, g.Text(""))), g.Node("NotNull", typeof(string), ("Instance", id))), select));
    }

    private void BuildAutomaticReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(AutomaticTag);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            ApplyRequest(g, g.Write<Slot>(g.Ref(_core), "Override", g.Ref<Slot>(null)))));
    }
}