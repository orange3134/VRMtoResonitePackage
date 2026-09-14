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
    private IWorldElement Send(IWorldElement command) => _g.Trigger(_g.Ref(_api), RequestTag, command);
    private void OwnerUpdate(ExpressionFlux graph, IWorldElement action)
    {
        var update = graph.Node("LocalUpdate");
        Link(update, "OnUpdate", graph.If(_isOwner, action));
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
        var previous = _g; _g = new(root.AddSlot("Logic"));
        try
        {
            IWorldElement Held(InputKey key) => _g.Node("KeyHeld", null, ("Key", _g.Constant(key)));
            var control = _g.Or(Held(InputKey.LeftControl), Held(InputKey.RightControl));
            var alt = _g.Or(Held(InputKey.LeftAlt), Held(InputKey.RightAlt));
            var shift = _g.Or(Held(InputKey.LeftShift), Held(InputKey.RightShift));
            var loop = _g.Each(_g.Ref(bindings), shortcut =>
            {
                var key = _g.Read<InputKey>(shortcut, "Key");
                var held = _g.And(_g.Active(shortcut), _g.Read<bool>(shortcut, "Enabled"), control, alt,
                    _g.Equal<bool>(shift, _g.Read<bool>(shortcut, "Shift")),
                    _g.Not(_g.Equal<InputKey>(key, _g.Constant(InputKey.None))), _g.Node("KeyHeld", null, ("Key", key)));
                return _g.Sequence(_g.If(_g.And(held, _g.Not(_g.Read<bool>(shortcut, "Held"))), Send(shortcut)),
                    _g.Write<bool>(shortcut, "Held", held));
            });
            OwnerUpdate(_g, loop);
        }
        finally { _g = previous; }
    }

    private void BuildGestures()
    {
        var root = _inputs.AddSlot("HandGestures"); var modules = root.AddSlot("Modules");
        foreach (string device in new[] { "TouchController", "IndexController", "ViveController", "WindowsMRController" })
        {
            var module = Record(modules, device.Replace("Controller", ""));
            Data(module, "GripThreshold", 0.55f); Data(module, "TriggerThreshold", 0.55f); Data(module, "StabilitySeconds", 0.05f);
            Data(module, "GripReleaseThreshold", 0.45f); Data(module, "TriggerReleaseThreshold", 0.45f);
            var previous = _g; _g = new(module.AddSlot("Logic"));
            try
            {
                foreach (var (side, kind) in new[] { (Chirality.Left, 0), (Chirality.Right, 1) })
                {
                    _g.BeginSection(side + " controller input");
                    var hand = Record(module, side.ToString()); var handRef = _g.Ref(hand); var modRef = _g.Ref(module);
                    var commandSlot = Command(hand, kind, 0); var command = _g.Ref(commandSlot);
                    Data(hand, "Candidate", -1); Data(hand, "Since", 0f); Data(hand, "Stable", -1);
                    Data(hand, "GripHeld", false); Data(hand, "TriggerHeld", false);
                    Reference<User>(hand, "PreviousOwner", null);
                    var initialized = _g.Node("StoredValue", typeof(bool));
                    var reset = _g.If(_g.Or(_g.Not(initialized),
                        _g.Not(_g.Equal<User>(_g.Read<User>(handRef, "PreviousOwner"), _owner))), _g.Sequence(
                        _g.Write<int>(handRef, "Candidate", _g.Constant(-1)), _g.Write<int>(handRef, "Stable", _g.Constant(-1)),
                        _g.Write<bool>(handRef, "GripHeld", _g.Constant(false)), _g.Write<bool>(handRef, "TriggerHeld", _g.Constant(false)),
                        _g.Write<User>(handRef, "PreviousOwner", _owner), _g.Set<bool>(initialized, _g.Constant(true))));
                    var controller = _g.Node(device, null, ("User", _owner), ("Node", _g.Constant(side)));
                    var active = Out(controller, "IsActive"); var trigger = Out(controller, "Trigger");
                    IWorldElement grip = Out(controller, "Grip");
                    bool wand = device is "ViveController" or "WindowsMRController";
                    if (!wand) grip = _g.Greater(grip, _g.Choose<float>(_g.Read<bool>(handRef, "GripHeld"),
                        _g.Read<float>(modRef, "GripReleaseThreshold"), _g.Read<float>(modRef, "GripThreshold")));
                    var indexCurled = _g.Greater(trigger, _g.Choose<float>(_g.Read<bool>(handRef, "TriggerHeld"),
                        _g.Read<float>(modRef, "TriggerReleaseThreshold"), _g.Read<float>(modRef, "TriggerThreshold")));
                    IWorldElement thumb, victory, rock;
                    if (wand)
                    {
                        thumb = Out(controller, "TouchpadTouch");
                        victory = _g.And(Out(controller, "TouchpadClick"), _g.Not(grip));
                        rock = _g.And(Out(controller, "TouchpadClick"), grip);
                    }
                    else
                    {
                        bool touch = device == "TouchController";
                        thumb = _g.Or(Out(controller, "JoystickTouch"), Out(controller, touch ? "ButtonXA_Touch" : "ButtonA_Touch"),
                            Out(controller, touch ? "ButtonYB_Touch" : "ButtonB_Touch"));
                        victory = Out(controller, touch ? "ButtonXA" : "ButtonA");
                        rock = Out(controller, touch ? "ButtonYB" : "ButtonB");
                    }
                    // Devices without individual finger curl use explicit button chords for Victory/Rock.
                    var gesture = _g.Choose<int>(rock, _g.Constant(5), _g.Choose<int>(victory, _g.Constant(4),
                        _g.Choose<int>(grip, _g.Choose<int>(indexCurled, _g.Choose<int>(thumb, _g.Constant(1), _g.Constant(7)),
                            _g.Choose<int>(thumb, _g.Constant(3), _g.Constant(6))), _g.Constant(2))));
                    var changed = _g.Not(_g.Equal<int>(gesture, _g.Read<int>(handRef, "Candidate")));
                    var stable = _g.Not(_g.Greater(_g.Add(_g.Read<float>(handRef, "Since"), _g.Read<float>(modRef, "StabilitySeconds")), _now));
                    var send = _g.Sequence(_g.Write<int>(command, "Gesture", gesture),
                        _g.Write<bool>(command, "Available", _g.Constant(true)), Send(command),
                        _g.Write<int>(handRef, "Stable", gesture));
                    OwnerUpdate(_g, _g.Sequence(reset, _g.If(active, _g.Sequence(
                        _g.Write<bool>(handRef, "GripHeld", grip), _g.Write<bool>(handRef, "TriggerHeld", indexCurled),
                        _g.If(changed, _g.Sequence(_g.Write<int>(handRef, "Candidate", gesture), _g.Write<float>(handRef, "Since", _now))),
                        _g.If(_g.And(stable, _g.Not(_g.Equal<int>(_g.Read<int>(handRef, "Stable"), gesture))), send)),
                        _g.Sequence(_g.If(_g.Not(_g.Equal<int>(_g.Read<int>(handRef, "Stable"), _g.Constant(-1))),
                            _g.Sequence(_g.Write<bool>(command, "Available", _g.Constant(false)), Send(command))),
                            _g.Write<int>(handRef, "Candidate", _g.Constant(-1)),
                            _g.Write<int>(handRef, "Stable", _g.Constant(-1)),
                            _g.Write<bool>(handRef, "GripHeld", _g.Constant(false)),
                            _g.Write<bool>(handRef, "TriggerHeld", _g.Constant(false))))));
                }
            }
            finally { _g = previous; }
        }
    }
}
