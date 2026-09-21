using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildApi()
    {
        var logic = _api.AddSlot("Logic");
        BuildGestureReceiver(new(logic.AddSlot("Left")), "Left", LeftTag, fromMenu: false);
        BuildGestureReceiver(new(logic.AddSlot("Right")), "Right", RightTag, fromMenu: false);
        BuildGestureReceiver(new(logic.AddSlot("MenuLeft")), "Left", MenuLeftTag, fromMenu: true);
        BuildGestureReceiver(new(logic.AddSlot("MenuRight")), "Right", MenuRightTag, fromMenu: true);
        BuildSelectReceiver(new(logic.AddSlot("Select")));
        BuildInputEnabledReceiver(new(logic.AddSlot("AllowExternalInput")));
    }

    // Initialization runs before checking the input gate so the first event after
    // cloning or reattachment can restore the default enabled state.
    private IWorldElement ApplyRequest(ExpressionFlux g, IWorldElement mutation, IWorldElement allowed = null) => g.Sequence(
        g.Trigger(g.Ref(_lifecycle), InitializeTag),
        g.If(allowed ?? g.Constant(true), g.Sequence(mutation,
            g.Trigger(g.Ref(_selection), SelectionTickTag))));

    private IWorldElement WriteHand(ExpressionFlux g, string hand, IWorldElement gesture) =>
        g.Write<int>(g.Ref(_core), CoreSpace, hand + "Gesture", gesture);

    private void BuildGestureReceiver(ExpressionFlux g, string hand, string tag, bool fromMenu)
    {
        var core = g.Ref(_core);
        var receiver = g.Receiver<int>(tag);
        var payload = Out(receiver, "Value");
        var mutation = fromMenu
            ? g.Sequence(g.Write<bool>(core, CoreSpace, "AllowExternalInput", g.Constant(false)), WriteHand(g, hand, payload))
            : WriteHand(g, hand, payload);
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.Binary<int>("ValueGreaterOrEqual", payload, g.Constant(0)),
            g.Binary<int>("ValueLessThan", payload, g.Constant(8))),
            ApplyRequest(g, mutation, fromMenu ? null : g.Read<bool>(core, CoreSpace, "AllowExternalInput"))));
    }

    // This is a numeric lookup over stable Pair.0..63 keys, not child-index traversal.
    // Deleted or reordered table Slots cannot change a pair's identity.
    private IWorldElement EachGesturePair(ExpressionFlux g, Func<IWorldElement, IWorldElement, IWorldElement> body)
    {
        var loop = g.Node("For", null, ("Count", g.Constant(64)));
        var index = Out(loop, "Iteration");
        var path = g.Node("ConcatenateString", null, ("A", g.Text(Path(TableSpace, "Pair."))),
            ("B", g.Node("ToString_Int", null, ("V", index))));
        Link(loop, "LoopIteration", body(index, g.Read<Slot>(g.Ref(_table), path)));
        return loop;
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(SelectTag);
        var id = Out(receiver, "Value");
        var pair = g.Local<int>();
        var find = EachGesturePair(g, (index, expression) => g.If(g.And(
            g.Equal<int>(pair, g.Constant(-1)), g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Equal<string>(id, g.Read<string>(expression, ClipSpace, "Id"))), g.Set<int>(pair, index)));
        var select = g.Sequence(g.Set<int>(pair, g.Constant(-1)), find,
            g.If(g.Binary<int>("ValueGreaterOrEqual", pair, g.Constant(0)), ApplyRequest(g, g.Sequence(
                g.Write<bool>(g.Ref(_core), CoreSpace, "AllowExternalInput", g.Constant(false)),
                WriteHand(g, "Left", g.Binary<int>("ValueDiv", pair, g.Constant(8))),
                WriteHand(g, "Right", g.Binary<int>("ValueMod", pair, g.Constant(8)))))));
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.NotEqual<string>(id, g.Text("")), g.Node("NotNull", typeof(string), ("Instance", id))), select));
    }

    private void BuildInputEnabledReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver<bool>(InputEnabledTag);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            ApplyRequest(g, g.Write<bool>(g.Ref(_core), CoreSpace, "AllowExternalInput", Out(receiver, "Value")))));
    }
}
