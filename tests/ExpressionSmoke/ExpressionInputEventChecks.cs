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
        var core = expressions.FindChild("Internal");
        var api = expressions.FindChild("API").FindChild("Receivers");
        var catalog = expressions.FindChild("Catalog");
        var modules = expressions.FindChild("Inputs").FindChild("HandGestures").FindChild("Modules");
        var mocks = expressions.Parent.AddSlot("Temporary sensor inputs");
        var restore = new List<(ISyncRef Port, IWorldElement Target)>();
        // Save the equip-assigned reference so the worn flags can also exercise dequip.
        var assigner = expressions.Parent.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>();
        var wearerReferences = assigner.References.Select(r => (Reference: r, User: r.Target)).ToArray();
        var originalStability = modules.Children.ToDictionary(module => module, module => Get<float>(module, "StabilitySeconds"));
        void Gesture(string side, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, side == "Left" ? ExpressionSystemSetup.LeftTag : ExpressionSystemSetup.RightTag, true, value);
        void Allow(bool value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            api, ExpressionSystemSetup.HandGesturesEnabledTag, true, value);
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
            string pair = Get<string>(core, "PairKey");
            Set(core, "PairKey", "__unchanged");
            await Frames(10);
            Check(Get<string>(core, "PairKey") == "__unchanged", "idle frames do not execute Selection");
            Set(core, "PairKey", pair);
            var smile = catalog.FindChild("Smile");
            var menuItem = smile.GetComponent<ContextMenuItemSource>();
            smile.ActiveSelf = false;
            await Frames(30);
            Check(Reference<Slot>(core, "CurrentExpression") == smile, "clip state edits do not reevaluate the gesture pair");
            Gesture("Left", 1);
            Check(Reference<Slot>(core, "CurrentExpression") == smile, "next gesture accepts the inactive clip");
            Check(menuItem.EnabledField.Value, "inactive expression does not change the menu Enabled field");
            smile.ActiveSelf = true;
            await Frames(30);
            Gesture("Left", 1);
            Check(Reference<Slot>(core, "CurrentExpression") == smile && menuItem.EnabledField.Value,
                "gesture event selects the restored clip");

            foreach (var module in modules.Children)
            foreach (string side in new[] { "Left", "Right" })
            {
                var hand = module.FindChild(side);
                bool pad = module.Name is "Vive" or "WindowsMR";
                var controller = hand.GetComponentsInChildren<ProtoFluxNode>().Single(n => n.GetType().Name == module.Name + "Controller");
                var active = Replace<bool>(hand, ExpressionFlux.Out(controller, "IsActive"));
                var vrNode = hand.GetComponentsInChildren<Nodes.FrooxEngine.Users.UserVR_Active>().Single();
                var vrActive = Replace<bool>(hand, vrNode);
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
                async Task WaitSeconds(double seconds)
                {
                    double end = expressions.World.Time.WorldTime + seconds;
                    while (expressions.World.Time.WorldTime < end) await default(NextUpdate);
                }
                // Each task captures the value and duration at its start. Begin
                // with Neutral to catch accidental default-int suppression.
                setCode(Array.FindIndex(expected, value => value == 0));
                await Frames(10);
                Set(module, "StabilitySeconds", 0.3f);
                Gesture(side, 1);
                active.Value.Value = true;
                await WaitSeconds(0.4);
                Check(Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": desktop ignores active controller sensors");
                vrActive.Value.Value = true;
                await WaitSeconds(0.1);
                Check(Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": first Neutral waits for the captured delay");
                Set(module, "StabilitySeconds", 0.05f);
                await WaitSeconds(0.1);
                Check(Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": editing delay does not shorten a pending task");
                await WaitSeconds(0.2);
                Check(Get<int>(core, side + "Gesture") == 0,
                    module.Name + "/" + side + ": first Neutral sends after the captured delay");
                setCode(0); await WaitSeconds(0.12);
                Check(Get<int>(core, side + "Gesture") == expected[0],
                    module.Name + "/" + side + ": next change uses the edited delay");
                Gesture(side, 1); await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 1, module.Name + "/" + side + ": idle sensors preserve newer input");
                Allow(false); await Frames(10);
                Check(Get<int>(core, side + "Gesture") == 1,
                    module.Name + "/" + side + ": gate retains the accepted value");
                Allow(true); await Frames(30);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": re-enable detects current pose");
                active.Value.Value = false; await Frames(10);
                Check(Get<int>(core, side + "Gesture") == -1,
                    module.Name + "/" + side + ": disconnect immediately sends the unavailable sentinel");
                Gesture(side, 4); active.Value.Value = true; await Frames(30);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": reconnect submits physical pose");
                // VR exit clears just this hand without waiting for StabilitySeconds.
                int opposite = Get<int>(core, (side == "Left" ? "Right" : "Left") + "Gesture");
                Set(module, "StabilitySeconds", 0.3f);
                vrActive.Value.Value = false; await Frames(3);
                Check(Get<int>(core, side + "Gesture") == -1 &&
                    Get<int>(core, (side == "Left" ? "Right" : "Left") + "Gesture") == opposite,
                    module.Name + "/" + side + ": VR exit immediately clears only its own hand");
                Gesture(side, 7); await WaitSeconds(0.4);
                Check(Get<int>(core, side + "Gesture") == 7,
                    module.Name + "/" + side + ": idle desktop does not repeatedly send the sentinel");
                vrActive.Value.Value = true; await WaitSeconds(0.1);
                Check(Get<int>(core, side + "Gesture") == 7,
                    module.Name + "/" + side + ": VR re-entry waits for a stable pose");
                await WaitSeconds(0.3);
                Check(Get<int>(core, side + "Gesture") == expected[0],
                    module.Name + "/" + side + ": VR re-entry submits the unchanged physical pose");
                int nextCode = Array.FindIndex(expected, value => value != expected[0]);
                int thirdCode = Array.FindIndex(expected, value => value != expected[0] && value != expected[nextCode]);
                Set(module, "StabilitySeconds", 0.3f);
                Gesture(side, 7);
                setCode(nextCode); await WaitSeconds(0.15);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": changed pose waits for its task");
                setCode(0); await WaitSeconds(0.4);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": returning pose during timeout is dropped without a queued resend");
                Gesture(side, 7);
                setCode(nextCode); await WaitSeconds(0.15);
                setCode(thirdCode); await WaitSeconds(0.2);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": expired snapshot for a different pose is discarded");
                await WaitSeconds(0.2);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": replacement during timeout stays unsent even after holding it");
                Gesture(side, 7);
                setCode(nextCode); await WaitSeconds(0.1);
                Allow(false); await WaitSeconds(0.3);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": pending task is rejected while input is disabled");
                // The authored false branch sends -1, but the public API rejects
                // it while AllowHandGestures is false.
                vrActive.Value.Value = false; await Frames(3);
                Check(Get<int>(core, side + "Gesture") == 7,
                    module.Name + "/" + side + ": permission gate rejects the VR-exit sentinel");
                vrActive.Value.Value = true; await Frames(3);
                // Settle the mocked sensor while disabled before testing re-enable.
                setCode(0); await Frames(3); Allow(true); await WaitSeconds(0.4);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": re-enable starts a new task");
                // Only the first snapshot survives an A -> B -> A round trip.
                // Dropped impulses do not extend the cooldown or capture a value.
                setCode(nextCode); await WaitSeconds(0.08);
                setCode(0); await WaitSeconds(0.08);
                setCode(nextCode); await WaitSeconds(0.19);
                Check(Get<int>(core, side + "Gesture") == expected[nextCode], module.Name + "/" + side + ": admitted snapshot still sends when its pose returns");
                Gesture(side, 7);
                setCode(thirdCode); await WaitSeconds(0.22);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": suppressed matching change did not create another delayed task");
                await WaitSeconds(0.18);
                Check(Get<int>(core, side + "Gesture") == expected[thirdCode], module.Name + "/" + side + ": suppressed impulses do not extend the timeout and a later change sends");
                // A short disable/re-enable neither resets the timeout nor queues
                // another copy. The original snapshot may still match on completion.
                setCode(0); await WaitSeconds(0.08);
                Allow(false); await WaitSeconds(0.04);
                Allow(true); await WaitSeconds(0.23);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": original admitted snapshot may send after quick re-enable");
                Gesture(side, 7); await WaitSeconds(0.2);
                Check(Get<int>(core, side + "Gesture") == 7, module.Name + "/" + side + ": quick re-enable does not reset timeout or schedule a duplicate");
                // Exit during an admitted delay: -1 bypasses Timeout, and the
                // pending snapshot must not overwrite it when the delay expires.
                setCode(nextCode); await WaitSeconds(0.08);
                vrActive.Value.Value = false; await Frames(3);
                Check(Get<int>(core, side + "Gesture") == -1,
                    module.Name + "/" + side + ": VR exit bypasses an active timeout");
                await WaitSeconds(0.35);
                Check(Get<int>(core, side + "Gesture") == -1,
                    module.Name + "/" + side + ": pending snapshot cannot overwrite the VR-exit sentinel");
                setCode(0); await Frames(3);
                vrActive.Value.Value = true; await WaitSeconds(0.4);
                foreach (var entry in wearerReferences) entry.Reference.Target = null;
                await Frames(10);
                foreach (var entry in wearerReferences) entry.Reference.Target = expressions.World.LocalUser;
                await WaitSeconds(0.45);
                Check(Get<int>(core, side + "Gesture") == expected[0], module.Name + "/" + side + ": rewear submits the unchanged physical pose");
                Set(module, "StabilitySeconds", 0f);
                int other = Get<int>(core, (side == "Left" ? "Right" : "Left") + "Gesture");
                for (int code = 0; code < expected.Length; code++)
                {
                    setCode(code); await Frames(6);
                    Check(Get<int>(core, side + "Gesture") == expected[code],
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
            Check(Get<int>(core, "LeftGesture") == 1 && !Get<bool>(core, "AllowHandGestures") &&
                Reference<Slot>(core, "CurrentExpression") == smile, "keyboard selects a pose while hand gestures are disabled");
            Gesture("Left", 4); await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 1, "disabled gesture API cannot overwrite keyboard selection");
            Allow(true);
            Gesture("Left", 4); await Frames(5);
            Check(Get<int>(core, "LeftGesture") == 4, "enabling gestures does not resend a held keyboard chord");
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
            Set(leftSettings.FindChild("Tag"), "Tag", ExpressionSystemSetup.KeyboardRightTag);
            Key(InputKey.Keypad1, true);
            await Frames(5);
            Check(Get<int>(core, "RightGesture") == 1, "keyboard sender reads the edited hand tag");
            Key(InputKey.Keypad1, false);
            Set(leftSettings.FindChild("Tag"), "Tag", ExpressionSystemSetup.KeyboardLeftTag);
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
            Check(Get<int>(core, "LeftGesture") == 0, "AvatarWornLocal false blocks both gesture API and keyboard even under UserRoot");
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
