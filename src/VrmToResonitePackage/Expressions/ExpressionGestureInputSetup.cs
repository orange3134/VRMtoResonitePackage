using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    // Index/Cosmos/pad tables follow Avatar Expression Editor v1.12.1.
    // Touch packs each sensor separately. See docs/controller-gestures.md.
    private IWorldElement BuildControllerGesture(ExpressionFlux g, Component controller, string device,
        Chirality side, Slot module)
    {
        if (device is "ViveController" or "WindowsMRController")
            return BuildPadGesture(g, controller, module);

        var bits = g.Node("ComposeBits_byte");
        if (device == "IndexController")
        {
            var source = g.Node("UserFingerPoseSource", null, ("User", g.Owner(_root)));
            var threshold = g.Read<float>(g.Ref(module), GestureSettingsSpace, "FingerThreshold");
            var thumbThreshold = g.Read<float>(g.Ref(module), GestureSettingsSpace, "ThumbThreshold");
            if (side == Chirality.Right) thumbThreshold = g.Node("ValueNegate", typeof(float), ("N", thumbThreshold));
            string[] fingers = { "IndexFinger", "MiddleFinger", "RingFinger", "Pinky", "Thumb" };
            for (int bit = 0; bit < fingers.Length; bit++)
            {
                var pose = g.Node("FingerPose", null, ("PoseSource", source),
                    ("FingerNode", g.Constant(Enum.Parse<BodyNode>(side + fingers[bit] + "_Proximal"))));
                var euler = g.Node("EulerAngles_floatQ", null, ("Q", Out(pose, "Rotation")));
                var axes = g.Node("Unpack_Float3", null, ("V", euler));
                var curled = bit == 4
                    ? g.Node("ValueGreaterOrEqual", typeof(float), ("A", thumbThreshold), ("B", Out(axes, "Y")))
                    : g.Node("ValueLessOrEqual", typeof(float), ("A", threshold), ("B", Out(axes, "X")));
                Link(bits, "Bit" + bit, curled);
            }
        }
        else if (device == "TouchController")
        {
            // Keep the used outputs in the stock TouchController's visual port order.
            string[] ports = { "ButtonYB_Touch", "ButtonXA_Touch", "ThumbRestTouch", "GripClick",
                "JoystickTouch", "TriggerTouch", "TriggerClick" };
            for (int bit = 0; bit < ports.Length; bit++) Link(bits, "Bit" + bit, Out(controller, ports[bit]));
        }
        else
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
                (1, new byte[] { 31 }), (2, new byte[] { 0 }), (3, new byte[] { 30 }),
                (4, new byte[] { 28 }), (5, new byte[] { 6, 22 }), (6, new byte[] { 14 }), (7, new byte[] { 15 }) },
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
            match.Values.Add((INodeValueOutput<bool>)found);
        }
        // Rows 0..6 become gestures 1..7; the unmatched Index (-1) becomes Neutral (0).
        var selected = g.Node("ValueInc", typeof(int), ("N", Out(match, "Index")));
        if (device != "TouchController") return selected;

        // Resting the index finger without pulling either trigger is explicitly
        // Neutral, including when the thumb is lifted. A pressed trigger takes
        // precedence over a missing touch signal; it must not become Open/Point.
        var neutral = (Component)g.And(Out(controller, "TriggerTouch"),
            g.Not(Out(controller, "TriggerClick")), g.Not(Out(controller, "GripClick")));
        neutral.Slot.Name += " : Neutral (0)";
        return g.Choose<int>(neutral, g.Constant(0), selected);
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
