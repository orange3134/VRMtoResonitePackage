using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using InputKey = Renderite.Shared.Key;
using static ExpressionTestFields;

internal static class KeyboardShortcutChecks
{
    public static async Task Run(Slot expressions)
    {
        var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
        var hands = new[] { keyboard.FindChild("Left"), keyboard.FindChild("Right") };
        var modifiers = hands.Select(h => h.FindChild("DV").FindChild("Modifier").GetComponent<DynamicValueVariable<InputKey>>()).ToArray();
        Check(modifiers[0].Value.Value == InputKey.Shift && modifiers[1].Value.Value == InputKey.Control,
            "keyboard defaults always use Shift for Left and Ctrl for Right");
        var core = expressions.FindChild("Internal");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var mocks = expressions.Parent.AddSlot("Keyboard test sensors");
        var ports = new List<(ISyncRef Port, IWorldElement Target)>();
        var sensors = new List<(ProtoFluxNode KeyInput, Nodes.ValueInput<bool> Sensor)>();
        var heldKeys = new HashSet<InputKey>();
        var assigner = expressions.Parent.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>();
        var wearers = assigner.References.Select(r => (Reference: r, Previous: r.Target)).ToArray();
        void RefreshSensors()
        {
            foreach (var (input, sensor) in sensors)
            {
                var proxy = input.Slot.GetComponent<global::ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables.DynamicVariableInputProxy<InputKey>>();
                Check(proxy != null && proxy.HasValue, "exported KeyHeld key remains dynamically bound");
                sensor.Value.Value = proxy.DynamicValue != InputKey.None && heldKeys.Contains(proxy.DynamicValue);
            }
        }
        async Task Frames() { for (int i = 0; i < 10; i++) { RefreshSensors(); await default(NextUpdate); } }
        void Key(InputKey key, bool held) { if (held) heldKeys.Add(key); else heldKeys.Remove(key); }
        void Gesture(int hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, hand == 0 ? ExpressionSystemSetup.KeyboardLeftTag : ExpressionSystemSetup.KeyboardRightTag, true, value);
        int GestureValue(int hand) => Get<int>(core, hand == 0 ? "LeftGesture" : "RightGesture");
        bool allowedBefore = Get<bool>(core, "AllowHandGestures");
        Slot extendedRow = null;
        try
        {
            foreach (var w in wearers) w.Reference.Target = expressions.World.LocalUser;
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.HandGesturesEnabledTag, true, false);
            foreach (var hand in hands)
            {
                var board = hand.FindChild("Logic");
                foreach (var node in board.GetComponentsInChildren<ProtoFluxNode>().Where(n => n.GetType().Name == "KeyHeld").ToArray())
                {
                    var input = ((ISyncRef)ExpressionFlux.Member(node, "Key")).Target;
                    while (input is not ProtoFluxNode) input = input.Parent;
                    var sensor = mocks.AddSlot("Key").AttachComponent<Nodes.ValueInput<bool>>();
                    sensors.Add(((ProtoFluxNode)input, sensor));
                    foreach (var port in board.GetComponentsInChildren<ProtoFluxNode>().SelectMany(n => n.AllInputs).Where(p => p.Target == node).ToArray())
                    { ports.Add((port, port.Target)); port.Target = sensor; }
                }
            }
            await Frames();
            for (int combination = 0; combination < 4; combination++)
            {
                bool left = (combination & 1) != 0, right = (combination & 2) != 0;
                Key(InputKey.Shift, left); Key(InputKey.Control, right);
                for (int gesture = 0; gesture < 10; gesture++)
                {
                    Gesture(0, (gesture + 1) % 10); Gesture(1, (gesture + 1) % 10);
                    Key((InputKey)((int)InputKey.Keypad0 + gesture), true); await Frames();
                    Check(GestureValue(0) == (left ? gesture : (gesture + 1) % 10) &&
                        GestureValue(1) == (right ? gesture : (gesture + 1) % 10),
                        $"Shift={left}, Ctrl={right}, keypad {gesture} updates the expected hands");
                    Key((InputKey)((int)InputKey.Keypad0 + gesture), false); await Frames();
                }
            }
            Gesture(0, 0); Gesture(1, 0);
            Key(InputKey.Shift, true); Key(InputKey.Control, false); Key(InputKey.Keypad8, true); await Frames();
            Key(InputKey.Keypad8, false); await Frames();
            Key(InputKey.Shift, false); Key(InputKey.Control, true); Key(InputKey.Keypad9, true); await Frames();
            Key(InputKey.Keypad9, false); await Frames();
            Check(GestureValue(0) == 8 && GestureValue(1) == 9 && Get<string>(core, "PairKey") == "L8R9" &&
                Reference<Slot>(core, "CurrentExpression") == null, "keypad 8/9 produce an unmapped L8R9 without truncation");
            var expression = expressions.FindChild("Catalog").Children.First();
            extendedRow = expressions.FindChild("DV").FindChild("GestureTable").AddSlot("L8R9");
            var mapping = extendedRow.AttachComponent<DynamicReferenceVariable<Slot>>();
            mapping.VariableName.Value = "ExpressionSystem/GestureTable.L8R9";
            mapping.Reference.Target = expression;
            await Frames();
            Check(Reference<Slot>(core, "CurrentExpression") == null, "adding an extended row waits for keyboard input");
            Key(InputKey.Keypad9, true); await Frames();
            Check(Reference<Slot>(core, "CurrentExpression") == expression && !Get<bool>(core, "AllowHandGestures"),
                "keypad 9 selects an externally added L8R9 expression while hand gestures are disabled");
            Key(InputKey.Keypad9, false); await Frames();
            Key(InputKey.Shift, false); Key(InputKey.Control, false);
            Gesture(0, 0); Gesture(1, 0);
            Key(InputKey.Shift, true); Key(InputKey.Control, true); await Frames();
            Check(GestureValue(0) == 0 && GestureValue(1) == 0, "modifiers alone do not send a gesture");
            Key(InputKey.Keypad1, true); await Frames();
            Check(GestureValue(0) == 1 && GestureValue(1) == 1 && Get<string>(core, "PairKey") == "L1R1" &&
                Reference<Slot>(core, "CurrentExpression") == Reference<Slot>(core, "GestureTable.L1R1"),
                "both keyboard events settle on the simultaneous hand pair");
            Gesture(0, 4); Gesture(1, 5); await Frames();
            Key(InputKey.Control, false); await Frames();
            Check(GestureValue(0) == 4 && GestureValue(1) == 5, "releasing Ctrl preserves newer input on both hands");
            Key(InputKey.Control, true); await Frames();
            Check(GestureValue(0) == 4 && GestureValue(1) == 1, "adding Ctrl to a held Shift chord sends only Right");
            Key(InputKey.Keypad1, false); Key(InputKey.Shift, false); Key(InputKey.Control, false); await Frames();

            modifiers[0].Value.Value = InputKey.Alt;
            await Frames();
            ExpressionDynamicInputChecks.CheckBindings(expressions);
            Gesture(0, 6); Gesture(1, 7);
            Key(InputKey.Shift, true); Key(InputKey.Keypad2, true); await Frames();
            Check(GestureValue(0) == 6 && GestureValue(1) == 7, "editing Modifier removes the old Shift shortcut");
            Key(InputKey.Alt, true); await Frames();
            Check(GestureValue(0) == 2 && GestureValue(1) == 7, "editable Alt modifier affects only Left, even with Shift held");
            modifiers[0].Value.Value = InputKey.Control; await Frames();
            Check(GestureValue(0) == 2 && GestureValue(1) == 7, "unheld edited modifier preserves the gesture");
            Gesture(0, 6);
            Key(InputKey.Control, true); await Frames();
            Check(GestureValue(0) == 2 && GestureValue(1) == 2, "the same modifier can trigger both hands");
            modifiers[0].Value.Value = InputKey.None; await Frames();
            Gesture(0, 6); Key(InputKey.Keypad2, false); await Frames();
            Key(InputKey.Keypad2, true); await Frames();
            Check(GestureValue(0) == 6, "None leaves the modifier unassigned");
        }
        finally
        {
            modifiers[0].Value.Value = InputKey.Shift;
            modifiers[1].Value.Value = InputKey.Control;
            foreach (var (port, target) in ports) port.Target = target;
            foreach (var w in wearers) w.Reference.Target = w.Previous;
            mocks.Destroy();
            extendedRow?.Destroy();
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.HandGesturesEnabledTag, true, allowedBefore);
        }
        for (int i = 0; i < 10; i++) await default(NextUpdate);
        Console.WriteLine("PASS: keyboard Shift=Left, Ctrl=Right, Ctrl+Shift=both; all keypad 0-9 combinations and editable Modifier keys");
    }
    private static T Get<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, name)).Value.Value;
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
