using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using InputKey = Renderite.Shared.Key;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private static readonly string[] GestureNames = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };

    private static string GestureTag(int hand) => hand == 0 ? LeftTag : RightTag;
    private IWorldElement SendGesture(ExpressionFlux g, IWorldElement tag, IWorldElement gesture) =>
        g.Trigger<int>(g.Ref(_api), tag, gesture);
    private void OwnerUpdate(ExpressionFlux graph, IWorldElement action, IWorldElement otherwise = null)
    {
        var update = graph.Node("LocalUpdate");
        Link(update, "OnUpdate", graph.If(graph.IsOwner(_root), action, otherwise));
    }
    private void BuildInputs(bool menu)
    {
        if (menu) BuildMenus();
        BuildKeyboard(); BuildGestures();
        var examples = _root.FindChild("API").AddSlot("Examples");
        foreach (var (hand, tag) in new[] { ("Left", LeftTag), ("Right", RightTag) })
            MenuTrigger(examples.AddSlot(hand + " Fist (int 1)"), _api, tag, 1);
    }
    private static ContextMenuItemSource MenuItem(Slot slot, string label)
    {
        var item = slot.AttachComponent<ContextMenuItemSource>();
        item.Label.Value = label; item.CloseMenuOnPress.Value = false;
        return item;
    }
    private static void MenuTrigger<T>(Slot item, Slot target, string tag, T payload)
    {
        var trigger = item.AttachComponent<ButtonDynamicImpulseTriggerWithValue<T>>();
        trigger.Target.Target = target; trigger.ExcludeDisabled.Value = true;
        trigger.PressedData.Tag.Value = tag; trigger.PressedData.Value.Value = payload;
    }
    private void SelectMenuTrigger(Slot item, Slot expression)
    {
        var id = expression.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "Expr/Id").Value;
        MenuTrigger(item, _api, SelectTag, id.Value);
        item.GetComponent<ButtonDynamicImpulseTriggerWithValue<string>>().PressedData.Value.DriveFrom(id);
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
                MenuTrigger(item, _api, hand == 0 ? MenuLeftTag : MenuRightTag, gesture);
            }
        }
        var direct = items.AddSlot("Direct selection"); MenuItem(direct, "Select expression");
        direct.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = _catalog;
        foreach (var expression in _clips.Values)
        {
            var item = MenuItem(expression, expression.Name);
            item.Label.DriveFrom(expression.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "Expr/DisplayName").Value);
            item.EnabledField.DriveFrom(Data(expression, "MenuAvailable", false).Value);
            SelectMenuTrigger(expression, expression);
        }
        foreach (var (name, enabled) in new[] { ("Allow gestures and keyboard", true), ("Menu only", false) })
        {
            var mode = items.AddSlot(name); MenuItem(mode, name);
            MenuTrigger(mode, _api, InputEnabledTag, enabled);
        }
        BuildMenuAvailability(menu);
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
                {
                    SelectMenuTrigger(slot, expression);
                    slot.GetComponent<ContextMenuItemSource>().EnabledField.DriveFrom(
                        expression.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == "Expr/MenuAvailable").Value);
                }
            }
        }
    }

    private void BuildMenuAvailability(Slot menu)
    {
        var g = new ExpressionFlux(menu.AddSlot("Logic"));
        // Keep menu visibility editable: adding/removing a table mapping updates the next frame.
        var available = g.Local<bool>();
        OwnerUpdate(g, g.Each(g.Ref(_catalog), expression => g.Sequence(
            g.Set<bool>(available, g.Constant(false)),
            EachGesturePair(g, (_, mapped) => g.If(g.Equal<Slot>(expression, mapped),
                g.Set<bool>(available, g.And(g.Active(expression), g.Read<bool>(expression, "Enabled"))))),
            g.Write<bool>(expression, "MenuAvailable", available))));
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard"); var bindings = root.AddSlot("Bindings");
        for (int hand = 0; hand < 2; hand++)
            for (int gesture = 0; gesture < 8; gesture++)
            {
                var shortcut = Record(bindings, (hand == 0 ? "Left " : "Right ") + gesture + " " + GestureNames[gesture]);
                Data(shortcut, "Tag", GestureTag(hand)); Data(shortcut, "Gesture", gesture);
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
            return g.Sequence(g.If(g.And(held, g.Not(g.Read<bool>(shortcut, "Held"))), SendGesture(g, g.Read<string>(shortcut, "Tag"), g.Read<int>(shortcut, "Gesture"))),
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
        Data(hand, "LastRevision", -1);
        Data(hand, "Candidate", -1); Data(hand, "Since", 0f); Data(hand, "Stable", -1);
        Data(hand, "GripHeld", false); Data(hand, "TriggerHeld", false);
        var initialized = g.Node("StoredValue", typeof(bool));
        var reset = g.If(g.Not(initialized), g.Sequence(
            g.Write<int>(handRef, "Candidate", g.Constant(-1)), g.Write<int>(handRef, "Stable", g.Constant(-1)),
            g.Write<int>(handRef, "LastRevision", g.Constant(-1)),
            g.Write<bool>(handRef, "GripHeld", g.Constant(false)), g.Write<bool>(handRef, "TriggerHeld", g.Constant(false)),
            g.Set<bool>(initialized, g.Constant(true))));
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
        string revision = side + "Revision";
        var send = g.Sequence(SendGesture(g, g.Text(GestureTag(kind)), gesture),
            g.Write<int>(handRef, "LastRevision", g.Read<int>(g.Ref(_core), revision)),
            g.Write<int>(handRef, "Stable", gesture));
        OwnerUpdate(g, g.Sequence(reset, g.If(g.And(active, g.Read<bool>(g.Ref(_core), "AllowExternalInput")), g.Sequence(
            g.Write<bool>(handRef, "GripHeld", grip), g.Write<bool>(handRef, "TriggerHeld", indexCurled),
            g.If(changed, g.Sequence(g.Write<int>(handRef, "Candidate", gesture), g.Write<float>(handRef, "Since", g.Now))),
            g.If(g.And(stable, g.Not(g.Equal<int>(g.Read<int>(handRef, "Stable"), gesture))), send)),
            g.Sequence(g.If(g.And(g.Not(g.Equal<int>(g.Read<int>(handRef, "Stable"), g.Constant(-1))),
                    g.Equal<int>(g.Read<int>(handRef, "LastRevision"), g.Read<int>(g.Ref(_core), revision))),
                SendGesture(g, g.Text(GestureTag(kind)), g.Constant(0))),
                g.Write<int>(handRef, "Candidate", g.Constant(-1)),
                g.Write<int>(handRef, "Stable", g.Constant(-1)),
                g.Write<bool>(handRef, "GripHeld", g.Constant(false)),
                g.Write<bool>(handRef, "TriggerHeld", g.Constant(false))))),
            g.Set<bool>(initialized, g.Constant(false)));
    }
}
