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

    private void GesturePermissionMenuColor(Slot item)
    {
        var driver = item.AttachComponent<ValueOptionDescriptionDriver<bool>>();
        driver.Value.Target = _root.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<bool>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, "AllowHandGestures")).Value;
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
        var direct = items.AddSlot("Direct selection"); MenuItem(direct, "Select expression");
        direct.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = _catalog;
        foreach (var expression in _clips.Values)
        {
            var item = MenuItem(expression, expression.Name);
            item.Label.DriveFrom(expression.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == Path(ClipSpace, "DisplayName")).Value);
            SelectMenuTrigger(expression, expression);
        }
        var mode = items.AddSlot("Hand gestures"); MenuItem(mode, mode.Name);
        var toggleButton = mode.AttachComponent<ButtonDynamicImpulseTrigger>();
        toggleButton.Target.Target = _api;
        toggleButton.ExcludeDisabled.Value = true;
        toggleButton.PressedTag.Value = ToggleHandGesturesTag;
        GesturePermissionMenuColor(mode);
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
        _model.Diagnostics.Add($"Keyboard: Shift + keypad uses {(_keyboardPrimaryHand == 0 ? "Left" : "Right")}; Shift + Ctrl + keypad uses the other hand. Priority is inferred from exported gesture poses; ties prefer Left.");
        for (int hand = 0; hand < 2; hand++)
        {
            var settings = Record(root, hand == 0 ? "Left" : "Right", KeyboardSpace);
            Data(settings, "Tag", hand == 0 ? KeyboardLeftTag : KeyboardRightTag);
            Data(settings, "Shift", true);
            Data(settings, "Control", hand != _keyboardPrimaryHand);
            for (int gesture = 0; gesture < KeyboardGestureCount; gesture++)
                Data(settings, "Key." + gesture, (InputKey)((int)InputKey.Keypad0 + gesture));
            BuildKeyboardHand(settings.AddSlot("Logic"), settings);
        }
    }

    private void BuildKeyboardHand(Slot board, Slot settings)
    {
        var g = new ExpressionFlux(board);
        var source = g.Ref(settings);
        IWorldElement Held(InputKey key) => g.Node("KeyHeld", null, ("Key", g.Constant(key)));
        var match = (global::FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Utility.IndexOfFirstValueMatch<bool>)
            g.Node("IndexOfFirstValueMatch", typeof(bool), ("Match", g.Constant(true, shared: false)));
        for (int index = 0; index < KeyboardGestureCount; index++)
            match.Values.Add((INodeValueOutput<bool>)g.Node("KeyHeld", null,
                ("Key", g.Read<InputKey>(source, KeyboardSpace, "Key." + index))));

        var accepting = g.And(g.AvatarWornLocal,
            g.Equal<bool>(Held(InputKey.Control), g.Read<bool>(source, KeyboardSpace, "Control")),
            g.Equal<bool>(Held(InputKey.Shift), g.Read<bool>(source, KeyboardSpace, "Shift")),
            Out(match, "FoundMatch"));
        // Mirror the authored hand graph: one rising condition sends the first
        // matching key index. Changing keys while the condition stays true does not resend.
        var send = g.If(accepting, SendHandInput(g, g.Read<string>(source, KeyboardSpace, "Tag"), Out(match, "Index")));
        g.OnChanged<bool>(accepting, send);
        g.OnStart(send);
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
            }
            if (device is "ViveController" or "WindowsMRController")
                for (int direction = 0; direction < 8; direction++) Data(module, "Direction." + direction, direction);
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
        var initialized = g.Node("StoredValue", typeof(bool));
        var reset = g.If(g.Not(initialized), g.Sequence(
            g.Write<int>(handRef, GestureHandSpace, "Candidate", g.Constant(-1)), g.Write<int>(handRef, GestureHandSpace, "Stable", g.Constant(-1)),
            g.Set<bool>(initialized, g.Constant(true))));
        var controller = g.Node(device, null, ("User", g.Owner(_root)), ("Node", g.Constant(side)));
        var active = Out(controller, "IsActive");
        var gesture = BuildControllerGesture(g, controller, device, side, module);
        var changed = g.NotEqual<int>(gesture, g.Read<int>(handRef, GestureHandSpace, "Candidate"));
        var stable = g.Not(g.Greater(g.Add(g.Read<float>(handRef, GestureHandSpace, "Since"), g.Read<float>(modRef, GestureSettingsSpace, "StabilitySeconds")), g.Now));
        var send = g.Sequence(SendHandInput(g, g.Text(GestureTag(kind)), gesture),
            g.Write<int>(handRef, GestureHandSpace, "Stable", gesture));
        var enabled = g.And(active, g.Read<bool>(g.Ref(_internal), SystemSpace, "AllowHandGestures"));
        var update = g.If(g.AvatarWornLocal, g.Sequence(reset, g.If(enabled, g.Sequence(
            g.If(changed, g.Sequence(g.Write<int>(handRef, GestureHandSpace, "Candidate", gesture), g.Write<float>(handRef, GestureHandSpace, "Since", g.Now))),
            g.If(g.And(stable, g.NotEqual<int>(g.Read<int>(handRef, GestureHandSpace, "Stable"), gesture)), send)),
            g.Sequence(
                g.Write<int>(handRef, GestureHandSpace, "Candidate", g.Constant(-1)),
                g.Write<int>(handRef, GestureHandSpace, "Stable", g.Constant(-1))))),
            g.Set<bool>(initialized, g.Constant(false)));
        // The stable predicate changes when the waiting time expires, even at rest.
        var accepting = g.And(g.AvatarWornLocal, enabled);
        g.OnChanged<int>(g.Choose<int>(accepting, gesture, g.Constant(-1)), update);
        g.OnChanged<bool>(g.And(accepting, stable), update);
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnStart(update);
    }
}
