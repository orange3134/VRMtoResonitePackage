using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    // Gesture templates follow Avatar Expression Editor v1.12.1.
    // Index keeps an explicit neutral band between open and closed fingers.
    // Touch/Index pack sensor comparisons into a byte. See docs/controller-gestures.md.
    private IWorldElement BuildControllerGesture(ExpressionFlux g, Component controller, string device,
        Chirality side, Slot module)
    {
        if (device is "ViveController" or "WindowsMRController")
            return BuildPadGesture(g, controller, module);
        var bits = device == "IndexController" ? BuildIndexBits(g, side, module) : g.Node("ComposeBits_byte");
        if (device == "TouchController")
        {
            // Keep the used outputs in the stock TouchController's visual port order.
            string[] ports = { "ButtonYB_Touch", "ButtonXA_Touch", "ThumbRestTouch", "GripClick",
                "JoystickTouch", "TriggerTouch", "TriggerClick" };
            for (int bit = 0; bit < ports.Length; bit++) Link(bits, "Bit" + bit, Out(controller, ports[bit]));
        }
        else if (device == "CosmosController")
        {
            string[] ports = { "JoystickTouch", "GripClick", "TriggerTouch", "TriggerClick" };
            for (int bit = 0; bit < ports.Length; bit++) Link(bits, "Bit" + bit, Out(controller, ports[bit]));
        }

        // Thumb contact occupies bits 0/1/2/4; bit 3 is GripClick.
        // Bits 5/6 are TriggerTouch/TriggerClick, respectively.
        static byte[] WithThumbContact(params byte[] states) => states
            .SelectMany(state => Enumerable.Range(1, 23).Where(thumb => (thumb & 8) == 0)
                .Select(thumb => (byte)(state | thumb))).ToArray();

        (int Gesture, byte[] Codes)[] matches = device switch
        {
            "TouchController" => new (int, byte[])[] {
                (1, WithThumbContact(40, 72, 104)), (2, new byte[] { 0 }),
                (3, WithThumbContact(8)), (4, WithThumbContact(0)),
                (5, WithThumbContact(64, 96)), (6, new byte[] { 8 }), (7, new byte[] { 40, 72, 104 }) },
            "IndexController" => new (int, byte[])[] {
                (1, new byte[] { 63 }), (2, new byte[] { 96 }), (3, new byte[] { 62 }),
                (4, new byte[] { 60 }), (5, new byte[] { 38, 54, 102 }),
                (6, new byte[] { 110 }), (7, new byte[] { 111 }) },
            "CosmosController" => new (int, byte[])[] {
                (1, new byte[] { 12 }), (2, new byte[] { 0 }), (3, new byte[] { 3 }),
                (4, new byte[] { 1 }), (6, new byte[] { 2 }), (7, new byte[] { 4 }) },
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
        var match = (Nodes.Utility.IndexOfFirstValueMatch<bool>)g.Node("IndexOfFirstValueMatch", typeof(bool),
            ("Match", g.Constant(true, shared: false)));
        string[] names = { "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };
        var touchCode = device == "TouchController" ? g.Node("Cast_byte_To_int", null, ("Input", bits)) : null;

        // Export consecutive Touch codes as inclusive ranges rather than one
        // constant and input per code. The three sparse ThumbsUp codes stay a lookup.
        IWorldElement MatchTouchRanges(byte[] codes)
        {
            var sorted = codes.Order().ToArray();
            var ranges = new List<IWorldElement>();
            for (int i = 0; i < sorted.Length; i++)
            {
                int min = sorted[i];
                while (i + 1 < sorted.Length && sorted[i + 1] == sorted[i] + 1) i++;
                ranges.Add(g.Node("IsBetween_Int", null, ("Value", touchCode),
                    ("Min", g.Constant(min)), ("Max", g.Constant((int)sorted[i]))));
            }
            return g.Or(ranges.ToArray());
        }

        // One bool row per gesture keeps the code groups readable and the output
        // index stable. Cosmos has no RockNRoll, so that row is always false.
        var conditions = new List<INodeValueOutput<bool>>();
        for (int gesture = 1; gesture <= names.Length; gesture++)
        {
            byte[] codes = matches.FirstOrDefault(m => m.Gesture == gesture).Codes;
            IWorldElement found;
            Component group;
            if (codes == null)
            {
                group = (Component)g.Constant(false, shared: false);
                found = group;
            }
            else if (codes.Length == 1)
            {
                group = (Component)g.Equal<byte>(bits, g.Constant(codes[0]));
                found = group;
            }
            else if (device == "TouchController" && codes.Length > 3)
            {
                group = (Component)MatchTouchRanges(codes);
                found = group;
            }
            else
            {
                var codesMatch = (Nodes.Utility.IndexOfFirstValueMatch<byte>)g.Node("IndexOfFirstValueMatch", typeof(byte),
                    ("Match", bits));
                foreach (byte code in codes) codesMatch.Values.Add((INodeValueOutput<byte>)g.Constant(code));
                group = codesMatch;
                found = Out(codesMatch, "FoundMatch");
            }
            group.Slot.Name += $" : {names[gesture - 1]} ({gesture})";
            conditions.Add((INodeValueOutput<bool>)found);
        }
        if (device is "TouchController" or "IndexController")
        {
            // Explicit row 0 reuses the seven gesture conditions. Rows directly
            // match gesture numbers 0..7, including intermediate Index poses.
            var neutral = (Nodes.Operators.NOR_Multi_Bool)g.Node("NOR_Multi_Bool");
            neutral.Slot.Name += " : Neutral (0)";
            foreach (var condition in conditions) neutral.Operands.Add(condition);
            match.Values.Add(neutral);
        }
        foreach (var condition in conditions) match.Values.Add(condition);
        if (device is "TouchController" or "IndexController") return Out(match, "Index");

        // Other controllers keep rows 0..6 for gestures 1..7; unmatched becomes 0.
        return g.Node("ValueInc", typeof(int), ("N", Out(match, "Index")));
    }

    private Component BuildIndexBits(ExpressionFlux g, Chirality side, Slot module)
    {
        var source = g.Node("UserFingerPoseSource", null, ("User", g.Owner(_root)));
        var moduleRef = g.Ref(module);
        var threshold = g.Read<float>(moduleRef, GestureSettingsSpace, "FingerThreshold");
        var thumbThreshold = g.Read<float>(moduleRef, GestureSettingsSpace, "ThumbThreshold");
        if (side == Chirality.Right) thumbThreshold = g.Node("ValueNegate", typeof(float), ("N", thumbThreshold));
        // A negative edited width behaves like zero; strict open comparisons
        // keep open and closed disjoint even when the neutral band is disabled.
        IWorldElement Width(string name) => g.Binary<float>("ValueMax",
            g.Read<float>(moduleRef, GestureSettingsSpace, name), g.Constant(0f));
        var fingerOpen = g.Sub(threshold, Width("FingerNeutralRange"));
        var thumbOpen = g.Add(thumbThreshold, Width("ThumbNeutralRange"));
        string[] fingers = { "IndexFinger", "MiddleFinger", "RingFinger", "Pinky", "Thumb" };
        var open = new IWorldElement[5];
        var closed = new IWorldElement[5];
        for (int finger = 0; finger < fingers.Length; finger++)
        {
            var pose = g.Node("FingerPose", null, ("PoseSource", source),
                ("FingerNode", g.Constant(Enum.Parse<BodyNode>(side + fingers[finger] + "_Proximal"))));
            var euler = g.Node("EulerAngles_floatQ", null, ("Q", Out(pose, "Rotation")));
            var axes = g.Node("Unpack_Float3", null, ("V", euler));
            bool thumb = finger == 4;
            var angle = Out(axes, thumb ? "Y" : "X");
            closed[finger] = g.Binary<float>(thumb ? "ValueLessOrEqual" : "ValueGreaterOrEqual",
                angle, thumb ? thumbThreshold : threshold);
            open[finger] = g.Binary<float>(thumb ? "ValueGreaterThan" : "ValueLessThan",
                angle, thumb ? thumbOpen : fingerOpen);
            ((Component)closed[finger]).Slot.Name += " : " + fingers[finger] + " Closed";
            ((Component)open[finger]).Slot.Name += " : " + fingers[finger] + " Open";
        }
        // Closed bits alone cannot distinguish Open from intermediate. Bit5
        // requires all four non-thumb fingers to be outside the neutral band;
        // Bit4/Bit6 preserve the thumb's Closed/intermediate/Open states.
        var bits = g.Node("ComposeBits_byte");
        for (int finger = 0; finger < fingers.Length; finger++) Link(bits, "Bit" + finger, closed[finger]);
        var resolved = g.And(Enumerable.Range(0, 4).Select(finger => g.Or(open[finger], closed[finger])).ToArray());
        ((Component)resolved).Slot.Name += " : Four fingers outside neutral band";
        Link(bits, "Bit5", resolved);
        Link(bits, "Bit6", open[4]);
        return bits;
    }

    private static IWorldElement BuildPadGesture(ExpressionFlux g, Component controller, Slot module)
    {
        var axes = g.Node("Unpack_Float2", null, ("V", Out(controller, "Touchpad")));
        var angle = g.Node("Atan2_Float", null, ("Y", Out(axes, "X")), ("X", Out(axes, "Y")));
        var degrees = g.Mul(angle, g.Node("RadToDeg"));
        IWorldElement sector = g.Node("ValueAdd", typeof(int),
            ("A", g.Node("LegacyRoundToInt_Float", null, ("N", g.Div(degrees, g.Constant(45f))))), ("B", g.Constant(4)));
        // Both sides of the -180/+180 seam are the same downward sector.
        sector = g.Choose<int>(g.Equal<int>(sector, g.Constant(8)), g.Constant(0), sector);
        var selected = (Nodes.ValueMultiplex<int>)g.Node("ValueMultiplex", typeof(int), ("Index", sector));
        for (int i = 0; i < 8; i++)
            selected.Inputs.Add((INodeValueOutput<int>)g.Read<int>(g.Ref(module), GestureSettingsSpace, "Direction." + i));
        // Keep ResoPon's two hand inputs independent (the MR add-on merges them).
        return g.Choose<int>(Out(controller, "TouchpadTouch"), Out(selected, "Output"), g.Constant(0));
    }
}
