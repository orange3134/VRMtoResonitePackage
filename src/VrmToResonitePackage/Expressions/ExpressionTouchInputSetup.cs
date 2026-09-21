using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private static IWorldElement BuildTouchGesture(ExpressionFlux g, Component controller,
        IWorldElement grip, IWorldElement trigger)
    {
        // AvatarAddonSystem / Touch V1.4.3 separates input bit packing from pose matching.
        // Keep our existing thresholds and button priority; only adopt that graph structure.
        g.BeginSection("Touch input bits");
        var thumb = (Nodes.Operators.OR_Multi_Bool)g.Node("OR_Multi_Bool");
        foreach (string port in new[] { "JoystickTouch", "ButtonXA_Touch", "ButtonYB_Touch" })
            thumb.Operands.Add((INodeValueOutput<bool>)Out(controller, port));

        var bits = g.Node("ComposeBits_byte");
        var inputs = new (string Name, IWorldElement Value)[] {
            ("Grip held", grip), ("Trigger held", trigger), ("Thumb touching", thumb) };
        for (int bit = 0; bit < inputs.Length; bit++)
        {
            var input = g.Node("ValueRelay", typeof(bool), ("Input", inputs[bit].Value));
            input.Slot.Name += $" : Bit{bit} {inputs[bit].Name}";
            Link(bits, "Bit" + bit, input);
        }

        g.BeginSection("Touch gesture table");
        // Index bits (low to high): Grip, Trigger, Thumb. An open grip always means HandOpen.
        int[] gestures = { 2, 6, 2, 7, 2, 3, 2, 1 }; // Open, Gun, Open, ThumbsUp, Open, Point, Open, Fist
        var index = g.Node("Cast_byte_To_int", null, ("Input", bits));
        var table = (Nodes.ValueMultiplex<int>)g.Node("ValueMultiplex", typeof(int), ("Index", index));
        for (int mask = 0; mask < gestures.Length; mask++)
        {
            // Separate literals keep all eight rows adjacent to the table, in input order.
            var value = (Component)g.Constant(gestures[mask], shared: false);
            value.Slot.Name += $" : {Convert.ToString(mask, 2).PadLeft(3, '0')} -> {GestureNames[gestures[mask]]}";
            table.Inputs.Add((INodeValueOutput<int>)value);
        }

        g.BeginSection("Touch button priority");
        // First matching row wins: B/Y, A/X, then the finger pose. No conditional chain.
        var priority = (Nodes.Utility.IndexOfFirstValueMatch<bool>)g.Node("IndexOfFirstValueMatch", typeof(bool),
            ("Match", g.Constant(true, shared: false)));
        foreach (var (name, condition) in new[] {
            ("B or Y pressed", Out(controller, "ButtonYB")),
            ("A or X pressed", Out(controller, "ButtonXA")),
            ("Otherwise use finger pose", g.Constant(true, shared: false)) })
        {
            var input = g.Node("ValueRelay", typeof(bool), ("Input", condition));
            input.Slot.Name += " : " + name;
            priority.Values.Add((INodeValueOutput<bool>)input);
        }
        var selected = (Nodes.ValueMultiplex<int>)g.Node("ValueMultiplex", typeof(int), ("Index", Out(priority, "Index")));
        var results = new (string Name, IWorldElement Value)[] {
            ("B or Y: RockNRoll", g.Constant(5)), ("A or X: Victory", g.Constant(4)), ("Finger pose", Out(table, "Output")) };
        foreach (var (name, value) in results)
        {
            var input = g.Node("ValueRelay", typeof(int), ("Input", value));
            input.Slot.Name += " : " + name;
            selected.Inputs.Add((INodeValueOutput<int>)input);
        }
        return Out(selected, "Output");
    }
}
