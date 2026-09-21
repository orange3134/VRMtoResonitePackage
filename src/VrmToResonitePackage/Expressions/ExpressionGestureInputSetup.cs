using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private static IWorldElement BuildControllerGesture(ExpressionFlux g, Component controller, string device,
        IWorldElement grip, IWorldElement trigger)
    {
        // AvatarAddonSystem / Touch V1.4.3 separates input bit packing from pose matching.
        // Keep our existing thresholds and button priority; only adopt that graph structure.
        string module = device.Replace("Controller", "");
        bool wand = device is "ViveController" or "WindowsMRController";
        g.BeginSection(module + " input bits");
        IWorldElement thumb;
        if (wand) thumb = Out(controller, "TouchpadTouch");
        else
        {
            var contacts = (Nodes.Operators.OR_Multi_Bool)g.Node("OR_Multi_Bool");
            string[] ports = device == "TouchController"
                ? new[] { "JoystickTouch", "ButtonXA_Touch", "ButtonYB_Touch" }
                : new[] { "JoystickTouch", "ButtonA_Touch", "ButtonB_Touch" };
            foreach (string port in ports) contacts.Operands.Add((INodeValueOutput<bool>)Out(controller, port));
            thumb = contacts;
        }

        var bits = g.Node("ComposeBits_byte");
        var inputs = new (string Name, IWorldElement Value)[] {
            ("Grip held", grip), ("Trigger held", trigger), ("Thumb touching", thumb) };
        for (int bit = 0; bit < inputs.Length; bit++)
        {
            var input = g.Node("ValueRelay", typeof(bool), ("Input", inputs[bit].Value));
            input.Slot.Name += $" : Bit{bit} {inputs[bit].Name}";
            Link(bits, "Bit" + bit, input);
        }

        g.BeginSection(module + " gesture table");
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

        g.BeginSection(module + " button priority");
        // First matching row wins: RockNRoll, Victory, then the finger pose.
        // Vive/WindowsMR use the pad-click/grip chord; Touch/Index use B then A.
        string rockName, victoryName;
        IWorldElement rock, victory;
        if (wand)
        {
            rockName = "Pad click with grip"; victoryName = "Pad click without grip";
            var click = Out(controller, "TouchpadClick");
            rock = g.And(click, grip); victory = g.And(click, g.Not(grip));
        }
        else
        {
            bool touch = device == "TouchController";
            rockName = touch ? "B or Y pressed" : "B pressed";
            victoryName = touch ? "A or X pressed" : "A pressed";
            rock = Out(controller, touch ? "ButtonYB" : "ButtonB");
            victory = Out(controller, touch ? "ButtonXA" : "ButtonA");
        }
        var priority = (Nodes.Utility.IndexOfFirstValueMatch<bool>)g.Node("IndexOfFirstValueMatch", typeof(bool),
            ("Match", g.Constant(true, shared: false)));
        foreach (var (name, condition) in new[] {
            (rockName, rock), (victoryName, victory),
            ("Otherwise use finger pose", g.Constant(true, shared: false)) })
        {
            var input = g.Node("ValueRelay", typeof(bool), ("Input", condition));
            input.Slot.Name += " : " + name;
            priority.Values.Add((INodeValueOutput<bool>)input);
        }
        var selected = (Nodes.ValueMultiplex<int>)g.Node("ValueMultiplex", typeof(int), ("Index", Out(priority, "Index")));
        var results = new (string Name, IWorldElement Value)[] {
            (rockName + ": RockNRoll", g.Constant(5)), (victoryName + ": Victory", g.Constant(4)), ("Finger pose", Out(table, "Output")) };
        foreach (var (name, value) in results)
        {
            var input = g.Node("ValueRelay", typeof(int), ("Input", value));
            input.Slot.Name += " : " + name;
            selected.Inputs.Add((INodeValueOutput<int>)input);
        }
        return Out(selected, "Output");
    }
}
