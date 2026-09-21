using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using InputKey = Renderite.Shared.Key;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private static readonly string[] GestureNames = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };

    private static string GestureTag(int hand) => hand == 0 ? LeftTag : RightTag;
    private IWorldElement SendGesture(ExpressionFlux g, IWorldElement tag, IWorldElement gesture) =>
        g.Trigger<int>(g.Ref(_api), tag, gesture);

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
        var id = expression.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == Path(ClipSpace, "Id")).Value;
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
            item.Label.DriveFrom(expression.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == Path(ClipSpace, "DisplayName")).Value);
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
                        expression.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == Path(ClipSpace, "MenuAvailable")).Value);
                }
            }
        }
    }

    private void BuildMenuAvailability(Slot menu)
    {
        _menuAvailability = menu.AddSlot("Logic");
        var g = new ExpressionFlux(_menuAvailability);
        var available = g.Local<bool>();
        var refresh = g.Each(g.Ref(_catalog), expression => g.Sequence(
            g.Set<bool>(available, g.Constant(false)),
            EachGesturePair(g, (_, mapped) => g.If(g.Equal<Slot>(expression, mapped),
                g.Set<bool>(available, g.And(g.Active(expression), g.Read<bool>(expression, ClipSpace, "Enabled"))))),
            g.Write<bool>(expression, ClipSpace, "MenuAvailable", available)));
        ReceiveUpdate(g, MenuRefreshTag, refresh);
        g.OnChanged<int>(g.Node("ChildrenCount", null, ("Instance", g.Ref(_catalog))),
            g.If(g.IsOwner(_root), refresh));

        // Watch fixed table keys from inside their namespace. Keep watchers outside
        // the editable rows so deleting/recreating a row cannot remove its watcher.
        var logic = _table.AddSlot("Logic");
        for (int first = 0; first < 64; first += 8)
        {
            var watch = new ExpressionFlux(logic.AddSlot($"Menu mappings {first:D2}-{first + 7:D2}"));
            var changed = watch.If(watch.IsOwner(_root), watch.Trigger(watch.Ref(_menuAvailability), MenuRefreshTag));
            for (int pair = first; pair < first + 8; pair++)
            {
                var mapped = watch.Read<Slot>(watch.Ref(_table), TableSpace, "Pair." + pair);
                var visible = watch.Choose<Slot>(watch.And(watch.Active(mapped),
                    watch.Read<bool>(mapped, ClipSpace, "Enabled")), mapped, watch.Ref<Slot>(null));
                watch.OnChanged<Slot>(visible, changed);
            }
        }
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard");
        for (int hand = 0; hand < 2; hand++)
        {
            var settings = Record(root, hand == 0 ? "Left" : "Right", KeyboardSpace);
            var data = settings.AddSlot("DV");
            Data(data.AddSlot("Tag"), "Tag", GestureTag(hand));
            Data(data.AddSlot("Shift"), "Shift", true);
            Data(data.AddSlot("Control"), "Control", hand == 1);
            for (int gesture = 0; gesture < 8; gesture++)
                Data(data.AddSlot("Key." + gesture), "Key." + gesture, (InputKey)((int)InputKey.Keypad0 + gesture));
            BuildKeyboardHand(settings.AddSlot("Logic"), settings);
        }
    }

    private void BuildKeyboardHand(Slot board, Slot settings)
    {
        var g = new ExpressionFlux(board);
        var source = g.Ref(settings);
        IWorldElement Held(InputKey key) => g.Node("KeyHeld", null, ("Key", g.Constant(key)));
        var match = (global::FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Utility.IndexOfFirstValueMatch<bool>)
            g.Node("IndexOfFirstValueMatch", typeof(bool), ("Match", g.Constant(true)));
        for (int index = 0; index < 8; index++)
            match.Values.Add((INodeValueOutput<bool>)g.Node("KeyHeld", null,
                ("Key", g.Read<InputKey>(source, KeyboardSpace, "Key." + index))));

        var accepting = g.And(g.DynamicInput<bool>("modular_avatar", "AvatarWornLocal"),
            g.Equal<bool>(Held(InputKey.Control), g.Read<bool>(source, KeyboardSpace, "Control")),
            g.Equal<bool>(Held(InputKey.Shift), g.Read<bool>(source, KeyboardSpace, "Shift")),
            Out(match, "FoundMatch"));
        // Mirror the authored hand graph: one rising condition sends the first
        // matching key index. Changing keys while the condition stays true does not resend.
        var send = g.If(accepting, SendGesture(g, g.Read<string>(source, KeyboardSpace, "Tag"), Out(match, "Index")));
        g.OnChanged<bool>(accepting, send);
        g.OnStart(send);
    }

    private void BuildGestures()
    {
        var root = _inputs.AddSlot("HandGestures"); var modules = root.AddSlot("Modules");
        foreach (string device in new[] { "TouchController", "IndexController", "ViveController", "WindowsMRController" })
        {
            var module = Record(modules, device.Replace("Controller", ""), GestureSettingsSpace);
            Data(module, "GripThreshold", 0.55f); Data(module, "TriggerThreshold", 0.55f); Data(module, "StabilitySeconds", 0.05f);
            Data(module, "GripReleaseThreshold", 0.45f); Data(module, "TriggerReleaseThreshold", 0.45f);
            foreach (var (side, kind) in new[] { (Chirality.Left, 0), (Chirality.Right, 1) })
                BuildGestureHand(module, device, side, kind);
        }
    }

    private void BuildGestureHand(Slot module, string device, Chirality side, int kind)
    {
        var hand = Record(module, side.ToString(), GestureHandSpace);
        var g = new ExpressionFlux(hand.AddSlot("Logic"));
        var handRef = g.Ref(hand); var modRef = g.Ref(module);
        Data(hand, "Candidate", -1); Data(hand, "Since", 0f); Data(hand, "Stable", -1);
        Data(hand, "GripHeld", false); Data(hand, "TriggerHeld", false);
        var initialized = g.Node("StoredValue", typeof(bool));
        var reset = g.If(g.Not(initialized), g.Sequence(
            g.Write<int>(handRef, GestureHandSpace, "Candidate", g.Constant(-1)), g.Write<int>(handRef, GestureHandSpace, "Stable", g.Constant(-1)),
            g.Write<bool>(handRef, GestureHandSpace, "GripHeld", g.Constant(false)), g.Write<bool>(handRef, GestureHandSpace, "TriggerHeld", g.Constant(false)),
            g.Set<bool>(initialized, g.Constant(true))));
        var controller = g.Node(device, null, ("User", g.Owner(_root)), ("Node", g.Constant(side)));
        var active = Out(controller, "IsActive"); var trigger = Out(controller, "Trigger");
        IWorldElement grip = Out(controller, "Grip");
        bool wand = device is "ViveController" or "WindowsMRController";
        if (!wand) grip = g.Greater(grip, g.Choose<float>(g.Read<bool>(handRef, GestureHandSpace, "GripHeld"),
            g.Read<float>(modRef, GestureSettingsSpace, "GripReleaseThreshold"), g.Read<float>(modRef, GestureSettingsSpace, "GripThreshold")));
        var indexCurled = g.Greater(trigger, g.Choose<float>(g.Read<bool>(handRef, GestureHandSpace, "TriggerHeld"),
            g.Read<float>(modRef, GestureSettingsSpace, "TriggerReleaseThreshold"), g.Read<float>(modRef, GestureSettingsSpace, "TriggerThreshold")));
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
        var changed = g.NotEqual<int>(gesture, g.Read<int>(handRef, GestureHandSpace, "Candidate"));
        var stable = g.Not(g.Greater(g.Add(g.Read<float>(handRef, GestureHandSpace, "Since"), g.Read<float>(modRef, GestureSettingsSpace, "StabilitySeconds")), g.Now));
        var send = g.Sequence(SendGesture(g, g.Text(GestureTag(kind)), gesture),
            g.Write<int>(handRef, GestureHandSpace, "Stable", gesture));
        var enabled = g.And(active, g.Read<bool>(g.Ref(_core), CoreSpace, "AllowExternalInput"));
        var update = g.If(g.IsOwner(_root), g.Sequence(reset, g.If(enabled, g.Sequence(
            g.Write<bool>(handRef, GestureHandSpace, "GripHeld", grip), g.Write<bool>(handRef, GestureHandSpace, "TriggerHeld", indexCurled),
            g.If(changed, g.Sequence(g.Write<int>(handRef, GestureHandSpace, "Candidate", gesture), g.Write<float>(handRef, GestureHandSpace, "Since", g.Now))),
            g.If(g.And(stable, g.NotEqual<int>(g.Read<int>(handRef, GestureHandSpace, "Stable"), gesture)), send)),
            g.Sequence(
                g.Write<int>(handRef, GestureHandSpace, "Candidate", g.Constant(-1)),
                g.Write<int>(handRef, GestureHandSpace, "Stable", g.Constant(-1)),
                g.Write<bool>(handRef, GestureHandSpace, "GripHeld", g.Constant(false)),
                g.Write<bool>(handRef, GestureHandSpace, "TriggerHeld", g.Constant(false))))),
            g.Set<bool>(initialized, g.Constant(false)));
        // Watch interpreted values, not continuously changing analog amounts.
        // The stable predicate produces one change when the waiting time expires.
        var accepting = g.And(g.IsOwner(_root), enabled);
        g.OnChanged<int>(g.Choose<int>(accepting, gesture, g.Constant(-1)), update);
        g.OnChanged<bool>(g.And(accepting, grip), update);
        g.OnChanged<bool>(g.And(accepting, indexCurled), update);
        g.OnChanged<bool>(g.And(accepting, stable), update);
        g.OnChanged<bool>(g.IsOwner(_root), update);
        g.OnStart(update);
    }
}
