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
        var modules = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules");
        var mocks = expressions.Parent.AddSlot("Temporary sensor inputs");
        var restore = new List<(ISyncRef Port, IWorldElement Target)>();
        // The synthetic fixture is parented to UserRoot rather than equipped through
        // AvatarRoot. Supply the reference normally assigned by the equip operation.
        var assigner = expressions.Parent.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>();
        var wearerReferences = assigner.References.Select(r => (Reference: r, User: r.Target)).ToArray();
        var originalStability = modules.Children.ToDictionary(module => module, module => Get<float>(module, "StabilitySeconds"));
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
            var menuItem = smile.GetComponent<ContextMenuItemSource>();
            Set(smile, "Enabled", false);
            await Frames(30);
            Check(Reference<Slot>(core, "CurrentExpression") == null, "disabling a selected clip updates selection without an API event");
            Check(menuItem.EnabledField.Value, "disabled expression keeps its menu item enabled");
            smile.ActiveSelf = false; await Frames(10);
            Check(menuItem.EnabledField.Value, "inactive expression does not change the menu Enabled field");
            smile.ActiveSelf = true;
            Set(smile, "Enabled", true);
            await Frames(30);
            Check(Reference<Slot>(core, "CurrentExpression") == smile && menuItem.EnabledField.Value,
                "enabling a mapped clip refreshes selection while the menu remains enabled");

            foreach (var module in modules.Children)
            foreach (string side in new[] { "Left", "Right" })
            {
                var hand = module.FindChild(side);
                bool pad = module.Name is "Vive" or "WindowsMR";
                var controller = hand.GetComponentsInChildren<ProtoFluxNode>().Single(n => n.GetType().Name == module.Name + "Controller");
                var active = Replace<bool>(hand, ExpressionFlux.Out(controller, "IsActive"));
                Action<int> setCode;
                Nodes.ValueInput<Elements.Core.float2> axis = null;
                Nodes.ValueInput<bool> touch = null;
                var rotations = new List<Nodes.ValueInput<Elements.Core.floatQ>>();
                if (pad)
                {
                    axis = Replace<Elements.Core.float2>(hand, ExpressionFlux.Out(controller, "Touchpad"));
                    touch = Replace<bool>(hand, ExpressionFlux.Out(controller, "TouchpadTouch"));
                    setCode = code =>
                    {
                        double angle = (code - 4) * Math.PI / 4;
                        axis.Value.Value = new((float)Math.Sin(angle), (float)Math.Cos(angle));
                        touch.Value.Value = true;
                    };
                }
                else if (module.Name == "Index")
                {
                    string[] fingers = { "IndexFinger", "MiddleFinger", "RingFinger", "Pinky", "Thumb" };
                    foreach (string finger in fingers)
                    {
                        var pose = hand.GetComponentsInChildren<ProtoFluxNode>().Single(n => n.GetType().Name == "FingerPose" &&
                            ((Nodes.ValueInput<Renderite.Shared.BodyNode>)((ISyncRef)ExpressionFlux.Member(n, "FingerNode")).Target)
                                .Value.Value.ToString() == side + finger + "_Proximal");
                        rotations.Add(Replace<Elements.Core.floatQ>(hand, ExpressionFlux.Out(pose, "Rotation")));
                    }
                    setCode = code =>
                    {
                        for (int i = 0; i < 4; i++) rotations[i].Value.Value = Elements.Core.floatQ.Euler((code & (1 << i)) != 0 ? 60f : 20f, 0, 0);
                        float threshold = side == "Left" ? 25f : -25f;
                        rotations[4].Value.Value = Elements.Core.floatQ.Euler(0, threshold + ((code & 16) != 0 ? -10f : 10f), 0);
                    };
                }
                else
                {
                    string[] ports = module.Name == "Touch"
                        ? new[] { "ButtonYB_Touch", "ButtonXA_Touch", "GripClick", "JoystickTouch", "TriggerClick" }
                        : new[] { "JoystickTouch", "GripClick", "TriggerTouch", "TriggerClick" };
                    var sensors = ports.Select(port => Replace<bool>(hand, ExpressionFlux.Out(controller, port))).ToArray();
                    setCode = code => { for (int bit = 0; bit < sensors.Length; bit++) sensors[bit].Value.Value = (code & (1 << bit)) != 0; };
                }
                // Independent oracle transcribed from the live editor's ValueEqualityDrivers.
                int[] expected = module.Name switch
                {
                    "Touch" => new[] { 2,4,4,4,6,3,3,3,4,0,0,0,3,0,0,0,0,5,5,0,7,1,1,1,5,0,0,0,1,0,0,0 },
                    "Index" => new[] { 2,0,0,0,0,0,5,0,0,0,0,0,0,0,6,7,0,0,0,0,0,0,5,0,0,0,0,0,4,0,3,1 },
                    "Cosmos" => new[] { 2,4,6,3,7,0,0,0,0,0,0,0,1,0,0,0 },
                    _ => new[] { 0,1,2,3,4,5,6,7 }
                };
                setCode(0);
                await Frames(10);
                Set(module, "StabilitySeconds", 10f);
                Gesture(side, 1);
                active.Value.Value = true;
                await Frames(3);
                Check(Get<int>(hand, "Candidate") == expected[0] && Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": new sensor candidate waits for stability");
                Set(module, "StabilitySeconds", 0.05f);
                await Frames(30);
                Check(Get<int>(core, side + "Gesture") == expected[0] && Get<int>(hand, "Stable") == expected[0],
                    module.Name + "/" + side + ": resting sensors send when the stability delay expires");
                Gesture(side, 1); await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 1, module.Name + "/" + side + ": idle sensors preserve newer input");
                Allow(false); await Frames(10);
                Check(Get<int>(hand, "Candidate") == -1 && Get<int>(hand, "Stable") == -1 && Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": gate resets sensing and retains the accepted value");
                Allow(true); await Frames(30);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": re-enable detects current pose");
                active.Value.Value = false; await Frames(10);
                Check(Get<int>(core, side + "Gesture") == expected[0] && Get<int>(hand, "Candidate") == -1 && Get<int>(hand, "Stable") == -1,
                    module.Name + "/" + side + ": disconnect retains input and resets sensing");
                Gesture(side, 4); active.Value.Value = true; await Frames(30);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": reconnect submits physical pose");
                Set(module, "StabilitySeconds", 0f);
                int other = Get<int>(core, (side == "Left" ? "Right" : "Left") + "Gesture");
                for (int code = 0; code < expected.Length; code++)
                {
                    setCode(code); await Frames(6);
                    Check(Get<int>(hand, "Candidate") == expected[code] && Get<int>(hand, "Stable") == expected[code] &&
                        Get<int>(core, side + "Gesture") == expected[code],
                        $"{module.Name}/{side}: editor input code {code} selects {expected[code]}");
                    Check(Get<int>(core, (side == "Left" ? "Right" : "Left") + "Gesture") == other, "device input does not change the opposite hand");
                }
                if (pad)
                {
                    foreach (float x in new[] { -0.001f, 0f, 0.001f })
                    {
                        axis.Value.Value = new(x, -1); await Frames(6);
                        Check(Get<int>(core, side + "Gesture") == 0, "both sides of the downward seam select Direction.0");
                    }
                    foreach (float degrees in new[] { 22.4f, 22.6f, -22.4f, -22.6f })
                    {
                        double radians = degrees * Math.PI / 180;
                        axis.Value.Value = new((float)Math.Sin(radians), (float)Math.Cos(radians)); await Frames(6);
                        int sector = degrees > 22.5f ? 5 : degrees < -22.5f ? 3 : 4;
                        Check(Get<int>(core, side + "Gesture") == sector, "pad sector boundary " + degrees);
                    }
                    setCode(4); Set(module, "Direction.4", 7); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 7, "editing a direction assignment refreshes the gesture");
                    Set(module, "Direction.4", 4); touch.Value.Value = false; await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 0, "pad release returns Neutral");
                }
                if (module.Name == "Index")
                {
                    setCode(30); await Frames(6);
                    rotations[0].Value.Value = Elements.Core.floatQ.Euler(39.9f, 0, 0); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 3, "index angle below threshold remains FingerPoint");
                    rotations[0].Value.Value = Elements.Core.floatQ.Euler(40.1f, 0, 0); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 1, "index angle above threshold makes Fist");
                    Set(module, "FingerThreshold", 70f); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 0, "editing finger threshold refreshes detection");
                    Set(module, "FingerThreshold", 40f);
                    setCode(15); await Frames(6);
                    float boundary = side == "Left" ? 25f : -25f;
                    rotations[4].Value.Value = Elements.Core.floatQ.Euler(0, boundary + 0.1f, 0); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 7, "thumb angle above side threshold makes ThumbsUp");
                    rotations[4].Value.Value = Elements.Core.floatQ.Euler(0, boundary - 0.1f, 0); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == 1, "thumb angle below side threshold makes Fist");
                }
                active.Value.Value = false;
                Set(module, "StabilitySeconds", originalStability[module]);
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
            foreach (var (module, stability) in originalStability) Set(module, "StabilitySeconds", stability);
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
        slot.ExpressionVariables<DynamicValueVariable<T>>().Single(v => v.VariableName.Value == VariablePath(slot, name)).Value.Value;
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
