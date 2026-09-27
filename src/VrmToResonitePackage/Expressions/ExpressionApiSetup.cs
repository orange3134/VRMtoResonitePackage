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
        g.Write<int>(g.Ref(_core), SystemSpace, "Core." + hand + "Gesture", gesture);

    private void BuildGestureReceiver(ExpressionFlux g, string hand, string tag, bool fromMenu)
    {
        var core = g.Ref(_core);
        var receiver = g.Receiver<int>(tag);
        var payload = Out(receiver, "Value");
        var mutation = fromMenu
            ? g.Sequence(g.Write<bool>(core, SystemSpace, "Core.AllowExternalInput", g.Constant(false)), WriteHand(g, hand, payload))
            : WriteHand(g, hand, payload);
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.Binary<int>("ValueGreaterOrEqual", payload, g.Constant(0)),
            g.Binary<int>("ValueLessThan", payload, g.Constant(8))),
            ApplyRequest(g, mutation, fromMenu ? null : g.Read<bool>(core, SystemSpace, "Core.AllowExternalInput"))));
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

    // Visit explicit L/R keys in left-then-right order, independent of Slot order.
    private IWorldElement EachGesturePair(ExpressionFlux g, Func<IWorldElement, IWorldElement, IWorldElement, IWorldElement> body)
    {
        var leftLoop = g.Node("For", null, ("Count", g.Constant(8)));
        var rightLoop = g.Node("For", null, ("Count", g.Constant(8)));
        var left = Out(leftLoop, "Iteration");
        var right = Out(rightLoop, "Iteration");
        Link(leftLoop, "LoopIteration", rightLoop);
        Link(rightLoop, "LoopIteration", body(left, right, ReadGesturePair(g, left, right)));
        return leftLoop;
    }

    private void BuildSelectReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver(SelectTag);
        var id = Out(receiver, "Value");
        var selectedLeft = g.Local<int>();
        var selectedRight = g.Local<int>();
        var find = EachGesturePair(g, (left, right, expression) => g.If(g.And(
            g.Equal<int>(selectedLeft, g.Constant(-1)), g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"),
            g.Equal<string>(id, g.Read<string>(expression, ClipSpace, "Id"))),
            g.Sequence(g.Set<int>(selectedLeft, left), g.Set<int>(selectedRight, right))));
        var select = g.Sequence(g.Set<int>(selectedLeft, g.Constant(-1)), g.Set<int>(selectedRight, g.Constant(-1)), find,
            g.If(g.Binary<int>("ValueGreaterOrEqual", selectedLeft, g.Constant(0)), ApplyRequest(g, g.Sequence(
                g.Write<bool>(g.Ref(_core), SystemSpace, "Core.AllowExternalInput", g.Constant(false)),
                WriteHand(g, "Left", selectedLeft), WriteHand(g, "Right", selectedRight)))));
        Link(receiver, "OnTriggered", g.If(g.And(g.IsOwner(_root),
            g.NotEqual<string>(id, g.Text("")), g.Node("NotNull", typeof(string), ("Instance", id))), select));
    }

    private void BuildInputEnabledReceiver(ExpressionFlux g)
    {
        var receiver = g.Receiver<bool>(InputEnabledTag);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root),
            ApplyRequest(g, g.Write<bool>(g.Ref(_core), SystemSpace, "Core.AllowExternalInput", Out(receiver, "Value")))));
    }
}
