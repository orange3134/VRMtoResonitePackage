using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    // Observed in Avatar Expression Editor v1.12.1. See docs/controller-gestures.md.
    private IWorldElement BuildControllerGesture(ExpressionFlux g, Component controller, string device,
        Chirality side, Slot module)
    {
        if (device is "ViveController" or "WindowsMRController")
            return BuildPadGesture(g, controller, module);

        var bits = g.Node("ComposeBits_byte");
        if (device == "IndexController")
        {
            var source = g.Node("UserFingerPoseSource", null, ("User", g.LocalWearer));
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
        else
        {
            string[] ports = device == "TouchController"
                ? new[] { "ButtonYB_Touch", "ButtonXA_Touch", "GripClick", "JoystickTouch", "TriggerClick" }
                : new[] { "JoystickTouch", "GripClick", "TriggerTouch", "TriggerClick" };
            for (int bit = 0; bit < ports.Length; bit++) Link(bits, "Bit" + bit, Out(controller, ports[bit]));
        }

        (int Gesture, byte[] Codes)[] matches = device switch
        {
            "TouchController" => new (int, byte[])[] {
                (1, new byte[] { 28, 22, 21, 23 }), (2, new byte[] { 0 }),
                (3, new byte[] { 5, 6, 12, 7 }), (4, new byte[] { 2, 8, 1, 3 }),
                (5, new byte[] { 17, 18, 24 }), (6, new byte[] { 4 }), (7, new byte[] { 20 }) },
            "IndexController" => new (int, byte[])[] {
                (1, new byte[] { 31 }), (2, new byte[] { 0 }), (3, new byte[] { 30 }),
                (4, new byte[] { 28 }), (5, new byte[] { 6, 22 }), (6, new byte[] { 14 }), (7, new byte[] { 15 }) },
            "CosmosController" => new (int, byte[])[] {
                (1, new byte[] { 12 }), (2, new byte[] { 0 }), (3, new byte[] { 3 }),
                (4, new byte[] { 1 }), (6, new byte[] { 2 }), (7, new byte[] { 4 }) },
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
        var match = (Nodes.Utility.IndexOfFirstValueMatch<byte>)g.Node("IndexOfFirstValueMatch", typeof(byte),
            ("Match", bits));
        var selected = (Nodes.ValueMultiplex<int>)g.Node("ValueMultiplex", typeof(int), ("Index", Out(match, "Index")));
        // Search the packed code directly. Each code has a corresponding gesture
        // row, including repeated gestures for poses accepted by multiple codes.
        foreach (var (gesture, codes) in matches)
        foreach (byte code in codes)
        {
            match.Values.Add((INodeValueOutput<byte>)g.Constant(code));
            selected.Inputs.Add((INodeValueOutput<int>)g.Constant(gesture));
        }
        // No exact match clears all discrete poses in the source tool: Neutral here.
        return g.Choose<int>(Out(match, "FoundMatch"), Out(selected, "Output"), g.Constant(0));
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
