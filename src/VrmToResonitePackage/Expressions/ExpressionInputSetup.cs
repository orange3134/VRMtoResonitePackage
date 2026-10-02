using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using InputKey = Renderite.Shared.Key;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private const int KeyboardGestureCount = 10;
    private static string GestureTag(int hand) => hand == 0 ? LeftTag : RightTag;
    private IWorldElement SendHandInput(ExpressionFlux g, IWorldElement tag, IWorldElement gesture) =>
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
    private static readonly colorX SelectedMenuColor = new(0f, 1f, 0f, 1f, ColorProfile.Linear);

    private void SelectMenuColor(Slot item, Slot expression)
    {
        var driver = item.AttachComponent<ReferenceOptionDescriptionDriver<Slot>>();
        driver.Reference.Target = _root.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, "CurrentExpression")).Reference;
        driver.DefaultOption.Color.Value = colorX.White;
        // Null must never appear selected, including an imported item whose clip was deleted.
        driver.Options.Add().Color.Value = colorX.White;
        var selected = driver.Options.Add();
        selected.ReferenceTarget.Target = expression;
        selected.Color.Value = SelectedMenuColor;
        driver.Color.Target = item.GetComponent<ContextMenuItemSource>().Color;
    }

    private void GesturePermissionMenuColor(Slot item, string hand = null)
    {
        var driver = item.AttachComponent<ValueOptionDescriptionDriver<bool>>();
        IField<bool> Permission(string side) => _root.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<bool>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, HandGesturePermission(side))).Value;
        if (hand != null) driver.Value.Target = Permission(hand);
        else
        {
            var both = item.AttachComponent<ValueField<bool>>();
            var condition = item.AttachComponent<MultiBoolConditionDriver>();
            condition.Mode.Value = MultiBoolConditionDriver.ConditionMode.All;
            condition.Conditions.Add().Field.Target = Permission("Left");
            condition.Conditions.Add().Field.Target = Permission("Right");
            condition.Target.Target = both.Value;
            driver.Value.Target = both.Value;
        }
        driver.DefaultOption.Color.Value = new colorX(1f, 0f, 0f, 1f, ColorProfile.Linear);
        var selected = driver.Options.Add();
        selected.ReferenceValue.Value = true;
        selected.Color.Value = SelectedMenuColor;
        driver.Color.Target = item.GetComponent<ContextMenuItemSource>().Color;
    }

    private void SelectMenuTrigger(Slot item, Slot expression)
    {
        var trigger = item.AttachComponent<ButtonDynamicImpulseTriggerWithReference<Slot>>();
        trigger.Target.Target = _api;
        trigger.ExcludeDisabled.Value = true;
        trigger.PressedData.Tag.Value = SelectTag;
        trigger.PressedData.Reference.Target = expression;
        SelectMenuColor(item, expression);
    }
    private void BuildMenus()
    {
        var menu = _inputs.AddSlot("ContextMenu");
        menu.AttachComponent<RootContextMenuItem>().Item.Target = MenuItem(menu, "Expressions");
        var items = menu.AddSlot("Items");
        menu.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = items;
        foreach (var expression in _clips.Values)
        {
            MenuItem(expression, expression.Name);
            SelectMenuTrigger(expression, expression);
        }
        // Keep clips directly under Catalog for the Select API and copied templates.
        // Only menu entries are grouped, using the final compiled gesture assignments.
        var mapped = _compiled.Pairs.Where(id => id != null).ToHashSet();
        foreach (var (assigned, label) in new[] { (true, "Hand sign expressions"), (false, "Other expressions") })
        {
            var entries = _clips.Where(clip => mapped.Contains(clip.Key) == assigned).ToArray();
            if (entries.Length == 0) continue;
            var group = items.AddSlot(label); MenuItem(group, label);
            var children = group.AddSlot("Items");
            group.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = children;
            var back = children.AddSlot("Back"); MenuItem(back, "Back");
            back.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = items;
            foreach (var (_, expression) in entries)
            {
                var item = children.AddSlot(expression.Name);
                var source = MenuItem(item, expression.Name);
                var labelCopy = item.AttachComponent<ValueCopy<string>>();
                labelCopy.Source.Target = expression.GetComponent<ContextMenuItemSource>().Label;
                labelCopy.Target.Target = source.Label;
                SelectMenuTrigger(item, expression);
            }
        }
        var mode = items.AddSlot("Hand gestures"); MenuItem(mode, mode.Name);
        var toggleButton = mode.AttachComponent<ButtonDynamicImpulseTrigger>();
        toggleButton.Target.Target = _api;
        toggleButton.ExcludeDisabled.Value = true;
        toggleButton.PressedTag.Value = ToggleHandGesturesTag;
        GesturePermissionMenuColor(mode);
        foreach (var side in new[] { Chirality.Left, Chirality.Right })
        {
            var single = menu.AddSlot(side + " hand gestures");
            var rootItem = single.AttachComponent<RootContextMenuItem>();
            rootItem.Item.Target = MenuItem(single, single.Name);
            rootItem.OnlyForSide.Value = side;
            var toggle = single.AttachComponent<ButtonDynamicImpulseTrigger>();
            toggle.Target.Target = _api;
            toggle.ExcludeDisabled.Value = true;
            toggle.PressedTag.Value = ToggleHandGesturesHandTag(side.ToString());
            GesturePermissionMenuColor(single, side.ToString());
        }
        var reset = items.AddSlot("Reset settings"); MenuItem(reset, reset.Name);
        var resetButton = reset.AttachComponent<ButtonDynamicImpulseTrigger>();
        resetButton.Target.Target = _api;
        resetButton.ExcludeDisabled.Value = true;
        resetButton.PressedTag.Value = ResetTag;
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
                }
            }
        }
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard");
        _model.Diagnostics.Add("Keyboard: Shift + keypad uses Left; Ctrl + keypad uses Right; Ctrl + Shift + keypad uses both hands. Each hand's Modifier key is editable.");
        for (int hand = 0; hand < 2; hand++)
        {
            var settings = Record(root, hand == 0 ? "Left" : "Right", KeyboardSpace);
            Data(settings, "Tag", hand == 0 ? KeyboardLeftTag : KeyboardRightTag);
            Data(settings, "Modifier", hand == 0 ? InputKey.Shift : InputKey.Control);
            for (int gesture = 0; gesture < KeyboardGestureCount; gesture++)
                Data(settings, "Key." + gesture, (InputKey)((int)InputKey.Keypad0 + gesture));
            BuildKeyboardHand(settings.AddSlot("Logic"), settings);
        }
    }

    private void BuildKeyboardHand(Slot board, Slot settings)
    {
        var g = new ExpressionFlux(board);
        var source = g.Ref(settings);
        var match = (global::FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Utility.IndexOfFirstValueMatch<bool>)
            g.Node("IndexOfFirstValueMatch", typeof(bool), ("Match", g.Constant(true, shared: false)));
        for (int index = 0; index < KeyboardGestureCount; index++)
            match.Values.Add((INodeValueOutput<bool>)g.Node("KeyHeld", null,
                ("Key", g.Read<InputKey>(source, KeyboardSpace, "Key." + index))));

        var accepting = g.And(g.AvatarWornLocal,
            g.Node("KeyHeld", null, ("Key", g.Read<InputKey>(source, KeyboardSpace, "Modifier"))),
            Out(match, "FoundMatch"));
        // Mirror the authored hand graph: one rising condition sends the first
        // matching key index. Changing keys while the condition stays true does not resend.
        var send = g.If(accepting, SendHandInput(g, g.Read<string>(source, KeyboardSpace, "Tag"), Out(match, "Index")));
        g.OnChanged<bool>(accepting, send);
    }

    private void BuildGestures()
    {
        var root = _inputs.AddSlot("HandGestures"); var modules = root.AddSlot("Modules");
        using (var notice = new StreamReader(typeof(ExpressionSystemSetup).Assembly.GetManifestResourceStream("ResoPon.ControllerGestureNotice")))
            root.AttachComponent<Comment>().Text.Value = notice.ReadToEnd();
        foreach (string device in new[] { "TouchController", "IndexController", "ViveController", "WindowsMRController", "CosmosController" })
        {
            var module = Record(modules, device.Replace("Controller", ""), GestureSettingsSpace);
            Data(module, "StabilitySeconds", 0.05f);
            if (device == "IndexController")
            {
                Data(module, "FingerThreshold", 40f); Data(module, "ThumbThreshold", 25f);
                Data(module, "FingerNeutralRange", 20f); Data(module, "ThumbNeutralRange", 20f);
            }
            if (device is "ViveController" or "WindowsMRController")
                for (int direction = 0; direction < 8; direction++) Data(module, "Direction." + direction, direction);
            foreach (var (side, kind) in new[] { (Chirality.Left, 0), (Chirality.Right, 1) })
                BuildGestureHand(module, device, side, kind);
        }
    }

    private void BuildGestureHand(Slot module, string device, Chirality side, int kind)
    {
        var hand = module.AddSlot(side.ToString());
        var g = new ExpressionFlux(hand.AddSlot("Logic"));
        var modRef = g.Ref(module);
        var controller = g.Node(device, null, ("User", g.Owner(_root)), ("Node", g.Constant(side)));
        var gesture = BuildControllerGesture(g, controller, device, side, module);
        var accepting = g.And(g.AvatarWornLocal, Out(controller, "IsActive"),
            g.Node("UserVR_Active", null, ("User", g.Owner(_root))),
            g.Read<bool>(g.Ref(_internal), SystemSpace, HandGesturePermission(side.ToString())));
        var current = g.Choose<int>(accepting, gesture, g.Constant(-1));
        var send = SendHandInput(g, g.Text(GestureTag(kind)), current);
        // The authored graph drops changes during the timeout; it neither queues
        // them nor restarts the interval. Reset stays unconnected, including when
        // input stops. Only admitted impulses capture a delayed gesture value.
        var duration = g.Read<float>(modRef, GestureSettingsSpace, "StabilitySeconds");
        var delayed = g.Node("DelayWithValueSecondsFloat", typeof(int), ("Value", current), ("Duration", duration));
        Link(delayed, "Next", g.If(g.And(accepting, g.Equal<int>(current, Out(delayed, "DelayedValue"))),
            send));
        var timeout = g.Node("LocalImpulseTimeoutSeconds", null, ("Timeout", duration), ("Next", delayed));
        var start = g.Node("StartAsyncTask", null, ("TaskStart", Out(timeout, "Trigger")));
        // Leaving VR or losing the controller immediately sends the authored -1
        // sentinel, bypassing the delay. The API still enforces wear/permission.
        g.OnChanged<int>(current, g.If(accepting, start, send));
    }
}
