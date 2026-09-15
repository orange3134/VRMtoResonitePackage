using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using InputKey = Renderite.Shared.Key;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private static readonly string[] GestureNames = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };

    private static Slot Command(Slot parent, int hand, int gesture)
    {
        var command = Record(parent, "Command");
        Data(command, "Hand", hand); Data(command, "Gesture", gesture); Data(command, "Available", true);
        return command;
    }
    private IWorldElement Send(ExpressionFlux g, IWorldElement command) => g.Trigger(g.Ref(_api), RequestTag, command);
    private void OwnerUpdate(ExpressionFlux graph, IWorldElement action)
    {
        var update = graph.Node("LocalUpdate");
        Link(update, "OnUpdate", graph.If(graph.IsOwner(_root), action));
    }
    private void BuildInputs(bool menu)
    {
        if (menu) BuildMenus();
        BuildKeyboard(); BuildGestures();
        var example = Command(_root.FindChild("API").AddSlot("Examples"), 0, 0);
        example.Name = "Gesture request (copy for another input)";
    }
    private static ContextMenuItemSource MenuItem(Slot slot, string label)
    {
        var item = slot.AttachComponent<ContextMenuItemSource>();
        item.Label.Value = label; item.CloseMenuOnPress.Value = false;
        return item;
    }
    private static void MenuTrigger(Slot item, Slot target, string tag, Slot payload)
    {
        var trigger = item.AttachComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        trigger.Target.Target = target; trigger.ExcludeDisabled.Value = true;
        trigger.PressedData.Tag.Value = tag; trigger.PressedData.Reference.Target = payload;
    }
    private void BuildMenus()
    {
        var menu = _inputs.AddSlot("ContextMenu");
        menu.AttachComponent<RootContextMenuItem>().Item.Target = MenuItem(menu, "Expressions");
        var items = menu.AddSlot("Items");
        menu.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = items;
        for (int hand = 0; hand < 2; hand++)
        {
            var side = items.AddSlot(hand == 0 ? "Left hand" : "Right hand"); MenuItem(side, side.Name);
            var gestures = side.AddSlot("Items"); side.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = gestures;
            for (int gesture = 0; gesture < 8; gesture++)
            {
                var item = gestures.AddSlot(gesture + " " + GestureNames[gesture]); MenuItem(item, item.Name);
                MenuTrigger(item, _api, RequestTag, Command(item, hand, gesture));
            }
        }
        var direct = items.AddSlot("Direct selection"); MenuItem(direct, "Select expression");
        direct.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = _catalog;
        foreach (var expression in _clips.Values)
        {
            var item = MenuItem(expression, expression.Name);
            item.Label.DriveFrom(expression.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "Expr/DisplayName").Value);
            item.EnabledField.DriveFrom(expression.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == "Expr/Enabled").Value);
            MenuTrigger(expression, _api, SelectTag, expression);
        }
        var automatic = items.AddSlot("Automatic"); MenuItem(automatic, "Return to gestures");
        var button = automatic.AttachComponent<ButtonDynamicImpulseTrigger>();
        button.Target.Target = _api; button.ExcludeDisabled.Value = true; button.PressedTag.Value = AutomaticTag;
        if (_compiled.Menu.Count > 0)
        {
            var imported = items.AddSlot("Imported menu"); MenuItem(imported, imported.Name);
            var children = imported.AddSlot("Items"); imported.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = children;
            AddControls(children, _model.Menu);
        }
        void AddControls(Slot parent, IEnumerable<ExpressionMenuControl> controls)
        {
            foreach (var control in controls)
            {
                if (control.Type != 2 && !_compiled.Menu.ContainsKey(control)) continue;
                var slot = parent.AddSlot(control.Name); MenuItem(slot, control.Name);
                if (control.Type == 2)
                {
                    var children = slot.AddSlot("Items"); slot.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = children;
                    AddControls(children, control.Children);
                    if (children.Children.Count == 0) slot.Destroy();
                }
                else if (_clips.TryGetValue(_compiled.Menu[control], out var expression))
                    MenuTrigger(slot, _api, SelectTag, expression);
            }
        }
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard"); var bindings = root.AddSlot("Bindings");
        for (int hand = 0; hand < 2; hand++)
            for (int gesture = 0; gesture < 8; gesture++)
            {
                var shortcut = Command(bindings, hand, gesture);
                shortcut.Name = (hand == 0 ? "Left " : "Right ") + gesture + " " + GestureNames[gesture];
                Data(shortcut, "Enabled", true); Data(shortcut, "Key", (InputKey)((int)InputKey.Alpha1 + gesture));
                Data(shortcut, "Shift", hand == 1); Data(shortcut, "Held", false);
            }
        var g = new ExpressionFlux(root.AddSlot("Logic"));
        IWorldElement Held(InputKey key) => g.Node("KeyHeld", null, ("Key", g.Constant(key)));
        var control = g.Or(Held(InputKey.LeftControl), Held(InputKey.RightControl));
        var alt = g.Or(Held(InputKey.LeftAlt), Held(InputKey.RightAlt));
        var shift = g.Or(Held(InputKey.LeftShift), Held(InputKey.RightShift));
        var loop = g.Each(g.Ref(bindings), shortcut =>
        {
            var key = g.Read<InputKey>(shortcut, "Key");
            var held = g.And(g.Active(shortcut), g.Read<bool>(shortcut, "Enabled"), control, alt,
                g.Equal<bool>(shift, g.Read<bool>(shortcut, "Shift")),
                g.Not(g.Equal<InputKey>(key, g.Constant(InputKey.None))), g.Node("KeyHeld", null, ("Key", key)));
            return g.Sequence(g.If(g.And(held, g.Not(g.Read<bool>(shortcut, "Held"))), Send(g, shortcut)),
                g.Write<bool>(shortcut, "Held", held));
        });
        OwnerUpdate(g, loop);
    }

    private void BuildGestures()
    {
        var root = _inputs.AddSlot("HandGestures"); var modules = root.AddSlot("Modules");
        foreach (string device in new[] { "TouchController", "IndexController", "ViveController", "WindowsMRController" })
        {
            var module = Record(modules, device.Replace("Controller", ""));
            Data(module, "GripThreshold", 0.55f); Data(module, "TriggerThreshold", 0.55f); Data(module, "StabilitySeconds", 0.05f);
            Data(module, "GripReleaseThreshold", 0.45f); Data(module, "TriggerReleaseThreshold", 0.45f);
            foreach (var (side, kind) in new[] { (Chirality.Left, 0), (Chirality.Right, 1) })
                BuildGestureHand(module, device, side, kind);
        }
    }

    private void BuildGestureHand(Slot module, string device, Chirality side, int kind)
    {
        var hand = Record(module, side.ToString());
        var g = new ExpressionFlux(hand.AddSlot("Logic"));
        var handRef = g.Ref(hand); var modRef = g.Ref(module);
        var commandSlot = Command(hand, kind, 0); var command = g.Ref(commandSlot);
        Data(hand, "Candidate", -1); Data(hand, "Since", 0f); Data(hand, "Stable", -1);
        Data(hand, "GripHeld", false); Data(hand, "TriggerHeld", false);
        Reference<User>(hand, "PreviousOwner", null);
        var initialized = g.Node("StoredValue", typeof(bool));
        var reset = g.If(g.Or(g.Not(initialized),
            g.Not(g.Equal<User>(g.Read<User>(handRef, "PreviousOwner"), g.Owner(_root)))), g.Sequence(
            g.Write<int>(handRef, "Candidate", g.Constant(-1)), g.Write<int>(handRef, "Stable", g.Constant(-1)),
            g.Write<bool>(handRef, "GripHeld", g.Constant(false)), g.Write<bool>(handRef, "TriggerHeld", g.Constant(false)),
            g.Write<User>(handRef, "PreviousOwner", g.Owner(_root)), g.Set<bool>(initialized, g.Constant(true))));
        var controller = g.Node(device, null, ("User", g.Owner(_root)), ("Node", g.Constant(side)));
        var active = Out(controller, "IsActive"); var trigger = Out(controller, "Trigger");
        IWorldElement grip = Out(controller, "Grip");
        bool wand = device is "ViveController" or "WindowsMRController";
        if (!wand) grip = g.Greater(grip, g.Choose<float>(g.Read<bool>(handRef, "GripHeld"),
            g.Read<float>(modRef, "GripReleaseThreshold"), g.Read<float>(modRef, "GripThreshold")));
        var indexCurled = g.Greater(trigger, g.Choose<float>(g.Read<bool>(handRef, "TriggerHeld"),
            g.Read<float>(modRef, "TriggerReleaseThreshold"), g.Read<float>(modRef, "TriggerThreshold")));
        IWorldElement thumb, victory, rock;
        if (wand)
        {
            thumb = Out(controller, "TouchpadTouch");
            victory = g.And(Out(controller, "TouchpadClick"), g.Not(grip));
            rock = g.And(Out(controller, "TouchpadClick"), grip);
        }
        else
        {
            bool touch = device == "TouchController";
            thumb = g.Or(Out(controller, "JoystickTouch"), Out(controller, touch ? "ButtonXA_Touch" : "ButtonA_Touch"),
                Out(controller, touch ? "ButtonYB_Touch" : "ButtonB_Touch"));
            victory = Out(controller, touch ? "ButtonXA" : "ButtonA");
            rock = Out(controller, touch ? "ButtonYB" : "ButtonB");
        }
        // Devices without individual finger curl use explicit button chords for Victory/Rock.
        var gesture = g.Choose<int>(rock, g.Constant(5), g.Choose<int>(victory, g.Constant(4),
            g.Choose<int>(grip, g.Choose<int>(indexCurled, g.Choose<int>(thumb, g.Constant(1), g.Constant(7)),
                g.Choose<int>(thumb, g.Constant(3), g.Constant(6))), g.Constant(2))));
        var changed = g.Not(g.Equal<int>(gesture, g.Read<int>(handRef, "Candidate")));
        var stable = g.Not(g.Greater(g.Add(g.Read<float>(handRef, "Since"), g.Read<float>(modRef, "StabilitySeconds")), g.Now));
        var send = g.Sequence(g.Write<int>(command, "Gesture", gesture),
            g.Write<bool>(command, "Available", g.Constant(true)), Send(g, command),
            g.Write<int>(handRef, "Stable", gesture));
        OwnerUpdate(g, g.Sequence(reset, g.If(active, g.Sequence(
            g.Write<bool>(handRef, "GripHeld", grip), g.Write<bool>(handRef, "TriggerHeld", indexCurled),
            g.If(changed, g.Sequence(g.Write<int>(handRef, "Candidate", gesture), g.Write<float>(handRef, "Since", g.Now))),
            g.If(g.And(stable, g.Not(g.Equal<int>(g.Read<int>(handRef, "Stable"), gesture))), send)),
            g.Sequence(g.If(g.Not(g.Equal<int>(g.Read<int>(handRef, "Stable"), g.Constant(-1))),
                g.Sequence(g.Write<bool>(command, "Available", g.Constant(false)), Send(g, command))),
                g.Write<int>(handRef, "Candidate", g.Constant(-1)),
                g.Write<int>(handRef, "Stable", g.Constant(-1)),
                g.Write<bool>(handRef, "GripHeld", g.Constant(false)),
                g.Write<bool>(handRef, "TriggerHeld", g.Constant(false))))));
    }
}
