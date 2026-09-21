using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using InputKey = Renderite.Shared.Key;
using static ExpressionTestFields;

// Substitute only sensor outputs, leaving the exported change detectors and actions intact.
internal static class ExpressionInputEventChecks
{
    public static async Task Run(Slot expressions)
    {
        var core = expressions.FindChild("Core");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var catalog = expressions.FindChild("Catalog");
        var touch = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules").FindChild("Touch");
        var mocks = expressions.Parent.AddSlot("Temporary sensor inputs");
        var restore = new List<(ISyncRef Port, IWorldElement Target)>();
        // The synthetic fixture is parented to UserRoot rather than equipped through
        // AvatarRoot. Supply the reference normally assigned by the equip operation.
        var assigner = expressions.Parent.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>();
        var wearerReferences = assigner.References.Select(r => (Reference: r, User: r.Target)).ToArray();
        float originalStability = Get<float>(touch, "StabilitySeconds");
        void Gesture(string side, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, side == "Left" ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, value);
        void Allow(bool value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, ExpressionSystemSetup.InputEnabledTag, true, value);
        Nodes.ValueInput<T> Replace<T>(Slot board, IWorldElement original) where T : unmanaged
        {
            var input = mocks.AddSlot("Sensor " + restore.Count).AttachComponent<Nodes.ValueInput<T>>();
            foreach (var port in board.GetComponentsInChildren<ProtoFluxNode>().SelectMany(n => n.AllInputs).ToArray())
                if (port.Target == original)
                {
                    restore.Add((port, port.Target));
                    port.Target = input;
                }
            return input;
        }
        try
        {
            foreach (var entry in wearerReferences) entry.Reference.Target = expressions.World.LocalUser;
            Allow(true); Gesture("Left", 1); Gesture("Right", 0);
            await Frames(30);
            int pair = Get<int>(core, "PairIndex");
            Set(core, "PairIndex", -42);
            await Frames(10);
            Check(Get<int>(core, "PairIndex") == -42, "idle frames do not execute Selection");
            Set(core, "PairIndex", pair);
            var smile = catalog.FindChild("Smile");
            Set(smile, "MenuAvailable", false);
            await Frames(10);
            Check(!Get<bool>(smile, "MenuAvailable"), "idle frames do not rescan menu availability");
            Set(smile, "Enabled", false);
            await Frames(30);
            Check(Reference<Slot>(core, "CurrentExpression") == null, "disabling a selected clip updates selection without an API event");
            Set(smile, "Enabled", true);
            await Frames(30);
            Check(Reference<Slot>(core, "CurrentExpression") == smile && Get<bool>(smile, "MenuAvailable"),
                "enabling a mapped clip refreshes selection and menu availability");

            foreach (string side in new[] { "Left", "Right" })
            {
                var hand = touch.FindChild(side);
                var controller = hand.GetComponentsInChildren<ProtoFluxNode>().Single(n => n.GetType().Name == "TouchController");
                var active = Replace<bool>(hand, ExpressionFlux.Out(controller, "IsActive"));
                var grip = Replace<float>(hand, ExpressionFlux.Out(controller, "Grip"));
                var trigger = Replace<float>(hand, ExpressionFlux.Out(controller, "Trigger"));
                var buttons = new[] { "JoystickTouch", "ButtonXA_Touch", "ButtonYB_Touch", "ButtonXA", "ButtonYB" }
                    .Select(port => Replace<bool>(hand, ExpressionFlux.Out(controller, port))).ToArray();
                await Frames(10);
                Set(touch, "StabilitySeconds", 10f);
                Gesture(side, 1);
                active.Value.Value = true;
                await Frames(3);
                Check(Get<int>(hand, "Candidate") == 2 && Get<int>(core, side + "Gesture") == 1,
                    side + ": new sensor candidate waits for stability");
                Set(touch, "StabilitySeconds", 0.05f);
                await Frames(30);
                Check(Get<int>(core, side + "Gesture") == 2 && Get<int>(hand, "Stable") == 2,
                    side + ": time threshold sends the gesture without a further sensor change");
                Gesture(side, 1);
                grip.Value.Value = 0.1f;
                await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 1, side + ": unchanged interpreted gesture preserves newer manual input");
                grip.Value.Value = 0.8f;
                await Frames(30);
                Check(Get<int>(core, side + "Gesture") == 6 && Get<bool>(hand, "GripHeld"),
                    side + ": grip transition sends HandGun");
                grip.Value.Value = 0.5f;
                await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 6 && Get<bool>(hand, "GripHeld"),
                    side + ": grip hysteresis retains held state");
                grip.Value.Value = 0.4f;
                await Frames(30);
                Check(Get<int>(core, side + "Gesture") == 2 && !Get<bool>(hand, "GripHeld"),
                    side + ": crossing release threshold sends HandOpen");
                trigger.Value.Value = 0.8f;
                await Frames(10);
                Check(Get<bool>(hand, "TriggerHeld"), side + ": trigger state updates even when the gesture is unchanged");
                trigger.Value.Value = 0.4f;
                await Frames(10);
                Check(!Get<bool>(hand, "TriggerHeld"), side + ": trigger release updates independently");
                Allow(false);
                await Frames(10);
                Check(Get<int>(hand, "Candidate") == -1 && Get<int>(hand, "Stable") == -1 &&
                    Get<int>(core, side + "Gesture") == 2, side + ": input gate resets sensing while retaining the last value");
                Allow(true);
                await Frames(30);
                Check(Get<int>(hand, "Stable") == 2, side + ": re-enabling input redetects a stable gesture");
                active.Value.Value = false;
                await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 2 && Get<int>(hand, "Candidate") == -1 &&
                    Get<int>(hand, "Stable") == -1 && !Get<bool>(hand, "GripHeld") && !Get<bool>(hand, "TriggerHeld"),
                    side + ": disconnect retains input and resets sensing once");
                Gesture(side, 4);
                active.Value.Value = true;
                await Frames(30);
                Check(Get<int>(core, side + "Gesture") == 2, side + ": reconnect submits the stable physical gesture");
                Set(touch, "StabilitySeconds", 0f);
                // Exhaust the raw sensors, independently of the packed table implementation.
                // Bits 0/1 are analog grip/trigger extremes; 2..6 are the five bool sensors.
                for (int mask = 0; mask < 128; mask++)
                {
                    bool Held(int bit) => (mask & (1 << bit)) != 0;
                    grip.Value.Value = Held(0) ? 1f : 0f;
                    trigger.Value.Value = Held(1) ? 1f : 0f;
                    for (int i = 0; i < buttons.Length; i++) buttons[i].Value.Value = Held(i + 2);
                    await Frames(6);
                    bool thumbTouch = Held(2) || Held(3) || Held(4);
                    int expected;
                    if (Held(6)) expected = 5;
                    else if (Held(5)) expected = 4;
                    else if (!Held(0)) expected = 2;
                    else if (Held(1)) expected = thumbTouch ? 1 : 7;
                    else expected = thumbTouch ? 3 : 6;
                    Check(Get<int>(hand, "Candidate") == expected && Get<int>(hand, "Stable") == expected &&
                        Get<int>(core, side + "Gesture") == expected,
                        $"{side}: sensor combination {mask} preserves gesture {expected}");
                }
                active.Value.Value = false;
                grip.Value.Value = trigger.Value.Value = 0f;
                foreach (var button in buttons) button.Value.Value = false;
                Set(touch, "StabilitySeconds", originalStability);
                await Frames(10);
            }

            var keys = new Dictionary<InputKey, List<Nodes.ValueInput<bool>>>();
            var keyboard = expressions.FindChild("Inputs").FindChild("Keyboard");
            foreach (var board in keyboard.Children.Select(hand => hand.FindChild("Logic")))
            {
                foreach (var keyNode in board.GetComponentsInChildren<ProtoFluxNode>().Where(n => n.GetType().Name == "KeyHeld").ToArray())
                {
                    IWorldElement input = ((ISyncRef)ExpressionFlux.Member(keyNode, "Key")).Target;
                    InputKey key;
                    if (input is Nodes.ValueInput<InputKey> literal) key = literal.Value.Value;
                    else
                    {
                        while (input is not ProtoFluxNode) input = input.Parent;
                        string path = ((ProtoFluxNode)input).Slot.GetComponentsInChildren<GlobalValue<string>>().Single().Value.Value;
                        string name = path.Split('/').Last();
                        key = Get<InputKey>(board.Parent.FindChild("DV").FindChild(name), name);
                    }
                    if (!keys.TryGetValue(key, out var sensors)) keys[key] = sensors = new();
                    sensors.Add(Replace<bool>(board, keyNode));
                }
            }
            void Key(InputKey key, bool held)
            {
                foreach (var sensor in keys[key]) sensor.Value.Value = held;
            }
            await Frames(10);
            Gesture("Left", 0); Gesture("Right", 0);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 0 && Get<int>(core, "RightGesture") == 0,
                "keypad alone does not trigger either hand");
            Key(InputKey.Shift, true);
            await Frames(10);
            Check(Get<int>(core, "LeftGesture") == 1 && Get<int>(core, "RightGesture") == 0,
                "Shift plus keypad sends only the left-hand input");
            Gesture("Left", 4);
            await Frames(10);
            Check(Get<int>(core, "LeftGesture") == 4, "held keyboard chord does not repeatedly overwrite later input");
            Key(InputKey.Keypad1, false);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 4, "keyboard release preserves the last input");
            Allow(false);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Allow(true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 4, "a key pressed while input is disabled must be released before retrying");
            Key(InputKey.Keypad1, false);
            await Frames(5);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "keyboard chord fires again after release and repress");
            Key(InputKey.Keypad2, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "pressing another keypad key while holding the first does not retrigger");
            Key(InputKey.Keypad2, false);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "releasing another key does not retrigger the held chord");
            Key(InputKey.Keypad1, false);
            await Frames(5);
            Key(InputKey.Keypad1, true); Key(InputKey.Keypad3, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "simultaneous keys select the lowest gesture index");
            Key(InputKey.Keypad1, false); Key(InputKey.Keypad3, false);
            await Frames(5);
            var leftSettings = keyboard.FindChild("Left").FindChild("DV");
            var rightSettings = keyboard.FindChild("Right").FindChild("DV");
            Set(leftSettings.FindChild("Tag"), "Tag", ExpressionSystemSetup.RightTag);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "RightGesture") == 1, "keyboard sender reads the edited hand tag");
            Key(InputKey.Keypad1, false);
            Set(leftSettings.FindChild("Tag"), "Tag", ExpressionSystemSetup.LeftTag);
            await Frames(5);
            Set(leftSettings.FindChild("Control"), "Control", true);
            Gesture("Left", 6);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 6, "shared Control setting gates the whole hand");
            Set(leftSettings.FindChild("Control"), "Control", false);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "editing shared modifiers detects a newly valid held chord");
            Key(InputKey.Keypad1, false);
            await Frames(5);
            Set(leftSettings.FindChild("Shift"), "Shift", false);
            Key(InputKey.Shift, false);
            Key(InputKey.Keypad2, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 2, "shared Shift can be disabled for the left hand");
            Key(InputKey.Keypad2, false);
            Set(leftSettings.FindChild("Shift"), "Shift", true);
            Key(InputKey.Shift, true);
            await Frames(5);
            Check(Get<bool>(rightSettings.FindChild("Control"), "Control") && Get<bool>(rightSettings.FindChild("Shift"), "Shift"),
                "editing left-hand modifiers preserves right-hand settings");
            Key(InputKey.Keypad0, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 0, "keypad zero sends Neutral through the shared sender");
            Key(InputKey.Keypad0, false);
            Key(InputKey.Shift, false);
            Key(InputKey.Control, true);
            Gesture("Left", 4); Gesture("Right", 4);
            await Frames(5);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 4 && Get<int>(core, "RightGesture") == 4,
                "Ctrl plus keypad without Shift does not trigger either hand");
            Key(InputKey.Shift, true);
            await Frames(10);
            Check(Get<int>(core, "LeftGesture") == 4 && Get<int>(core, "RightGesture") == 1,
                "Ctrl plus Shift plus keypad sends only the right-hand input");
            Key(InputKey.Keypad1, false);
            await Frames(5);
            Key(InputKey.Control, false);
            await Frames(5);
            foreach (var entry in wearerReferences) entry.Reference.Target = null;
            await Frames(5);
            Gesture("Left", 6);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 6, "AvatarWornLocal false blocks keyboard even under the active user");
            foreach (var entry in wearerReferences) entry.Reference.Target = expressions.World.LocalUser;
            await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "AvatarWornLocal true accepts a held chord when wearing begins");
            Key(InputKey.Keypad1, false);
            Key(InputKey.Shift, false);
            await Frames(5);
        }
        finally
        {
            foreach (var entry in wearerReferences) entry.Reference.Target = entry.User;
            foreach (var (port, target) in restore) port.Target = target;
            mocks.Destroy();
            Set(touch, "StabilitySeconds", originalStability);
        }
        await Frames(10);
        Allow(true); Gesture("Left", 0); Gesture("Right", 0);
        await Frames(30);
    }

    private static async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await default(NextUpdate);
    }
    private static T Get<T>(Slot slot, string name) =>
        slot.GetComponents<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).Value.Value;
    private static void Set<T>(Slot slot, string name, T value)
    {
        if (slot.WriteDynamicVariable(VariablePath(slot, name), value) != DynamicVariableWriteResult.Success)
            throw new InvalidOperationException("Cannot write " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("EVENTS: " + message);
    }
}
