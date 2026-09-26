using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using InputKey = Renderite.Shared.Key;
using static ExpressionTestFields;

internal static class KeyboardPriorityChecks
{
    public static async Task Run(Slot expressions, int? expectedHand = null)
    {
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
        var hands = new[] { keyboard.FindChild("Left"), keyboard.FindChild("Right") };
        bool Control(int hand) => Get<bool>(hands[hand].FindChild("DV").FindChild("Control"), "Control");
        Check(Control(0) != Control(1), "exactly one hand needs Ctrl");
        int primary = Control(0) ? 1 : 0;
        Check(expectedHand == null || primary == expectedHand, "exported keyboard uses expected hand priority");
        Check(hands.All(h => Get<bool>(h.FindChild("DV").FindChild("Shift"), "Shift")), "both hands require Shift");
        var core = expressions.FindChild("Core");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var mocks = expressions.Parent.AddSlot("Keyboard test sensors");
        var ports = new List<(ISyncRef Port, IWorldElement Target)>();
        var sensors = new Dictionary<InputKey, List<Nodes.ValueInput<bool>>>();
        var assigner = expressions.Parent.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>();
        var wearers = assigner.References.Select(r => (Reference: r, Previous: r.Target)).ToArray();
        async Task Frames() { for (int i = 0; i < 10; i++) await default(NextUpdate); }
        void Key(InputKey key, bool held) { foreach (var s in sensors[key]) s.Value.Value = held; }
        void Gesture(int hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, hand == 0 ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, value);
        int GestureValue(int hand) => Get<int>(core, hand == 0 ? "LeftGesture" : "RightGesture");
        try
        {
            foreach (var w in wearers) w.Reference.Target = expressions.World.LocalUser;
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.InputEnabledTag, true, true);
            foreach (var hand in hands)
            {
                var board = hand.FindChild("Logic");
                foreach (var node in board.GetComponentsInChildren<ProtoFluxNode>().Where(n => n.GetType().Name == "KeyHeld").ToArray())
                {
                    var input = ((ISyncRef)ExpressionFlux.Member(node, "Key")).Target;
                    InputKey key;
                    if (input is Nodes.ValueInput<InputKey> literal) key = literal.Value.Value;
                    else
                    {
                        while (input is not ProtoFluxNode) input = input.Parent;
                        string name = ((ProtoFluxNode)input).Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value.Split('/').Last();
                        key = Get<InputKey>(hand.FindChild("DV").FindChild(name), name);
                    }
                    var sensor = mocks.AddSlot("Key").AttachComponent<Nodes.ValueInput<bool>>();
                    if (!sensors.TryGetValue(key, out var list)) sensors[key] = list = new();
                    list.Add(sensor);
                    foreach (var port in board.GetComponentsInChildren<ProtoFluxNode>().SelectMany(n => n.AllInputs).Where(p => p.Target == node).ToArray())
                    { ports.Add((port, port.Target)); port.Target = sensor; }
                }
            }
            await Frames();
            for (int ctrl = 0; ctrl < 2; ctrl++)
            {
                int target = ctrl == 0 ? primary : 1 - primary;
                Key(InputKey.Control, ctrl == 1); Key(InputKey.Shift, true);
                for (int gesture = 0; gesture < 8; gesture++)
                {
                    Gesture(0, (gesture + 1) % 8); Gesture(1, (gesture + 1) % 8);
                    Key((InputKey)((int)InputKey.Keypad0 + gesture), true); await Frames();
                    Check(GestureValue(target) == gesture && GestureValue(1 - target) == (gesture + 1) % 8,
                        $"Ctrl={ctrl} keypad {gesture} updates only hand {target}");
                    Key((InputKey)((int)InputKey.Keypad0 + gesture), false); await Frames();
                }
            }
            Key(InputKey.Shift, false); Key(InputKey.Control, false);
            Gesture(0, 0); Gesture(1, 0); Key(InputKey.Keypad1, true); await Frames();
            Check(GestureValue(0) == 0 && GestureValue(1) == 0, "no modifier does not trigger either hand");
            Key(InputKey.Control, true); await Frames();
            Check(GestureValue(0) == 0 && GestureValue(1) == 0, "Ctrl alone does not trigger either hand");
        }
        finally
        {
            foreach (var (port, target) in ports) port.Target = target;
            foreach (var w in wearers) w.Reference.Target = w.Previous;
            mocks.Destroy();
        }
        await Frames();
        Console.WriteLine($"PASS: keyboard Shift hand={primary}, Shift+Ctrl hand={1 - primary}, all 16 shortcuts and modifier exclusion");
    }
    private static T Get<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, name)).Value.Value;
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
