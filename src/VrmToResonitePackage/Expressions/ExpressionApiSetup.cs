using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildApi()
    {
        var logic = _api.AddSlot("Logic");
        BuildGestureReceiver(new(logic.AddSlot("Gesture")));
        BuildSelectReceiver(new(logic.AddSlot("Select")));
        BuildAutomaticReceiver(new(logic.AddSlot("Automatic")));
    }

    private void BuildGestureReceiver(ExpressionFlux g)
    {
        var core = g.Ref(_core);
        var receiver = g.Receiver(RequestTag);
        var command = Out(receiver, "Value");
        var hand = g.Read<int>(command, "Hand");
        var gesture = g.Read<int>(command, "Gesture");
        var available = g.Read<bool>(command, "Available");
        var valid = g.And(g.IsOwner(_root), g.Active(command),
            g.Binary<int>("ValueGreaterOrEqual", gesture, g.Constant(0)),
            g.Binary<int>("ValueLessOrEqual", gesture, g.Constant(7)));
        var writes = new List<IWorldElement>();
        for (int side = 0; side < 2; side++)
        {
            string name = side == 0 ? "Left" : "Right";
            // A disconnect only clears the hand still owned by this input.
            writes.Add(g.If(g.Equal<int>(hand, g.Constant(side)),
                g.If(available, g.Sequence(g.Write<int>(core, name + "Gesture", gesture),
                        g.Write<Slot>(core, name + "Input", command)),
                    g.If(g.Equal<Slot>(g.Read<Slot>(core, name + "Input"), command),
                        g.Sequence(g.Write<int>(core, name + "Gesture", g.Constant(0)),
                            g.Write<Slot>(core, name + "Input", g.Ref<Slot>(null)))))));
        }
        Link(receiver, "OnTriggered", g.If(valid, g.Sequence(writes.ToArray())));
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(SelectTag);
        var expression = Out(receiver, "Value");
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root), g.Active(expression), g.Read<bool>(expression, "Enabled"),
                g.Equal<Slot>(g.Node("GetParentSlot", null, ("Instance", expression)), g.Ref(_catalog))),
            g.Write<Slot>(g.Ref(_core), "Override", expression)));
    }

    private void BuildAutomaticReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(AutomaticTag, false);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            g.Write<Slot>(g.Ref(_core), "Override", g.Ref<Slot>(null))));
    }
}
