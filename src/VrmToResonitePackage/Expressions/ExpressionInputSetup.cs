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
    private static string TrySetGestureTag(int hand) => hand == 0 ? TrySetGestureLeftTag : TrySetGestureRightTag;
    private IWorldElement SendHandInput(ExpressionFlux g, IWorldElement tag, IWorldElement gesture) =>
        g.Trigger<int>(g.Ref(_api), tag, gesture);

    private void BuildInputs(bool menu)
    {
        if (menu) BuildMenus();
        BuildKeyboard(); BuildGestures();
    }
    private static ContextMenuItemSource MenuItem(Slot slot, string label)
    {
        var item = slot.AttachComponent<ContextMenuItemSource>();
        item.Label.Value = label; item.CloseMenuOnPress.Value = false;
        return item;
    }
    private static readonly colorX SelectedMenuColor = new(0f, 1f, 0f, 1f, ColorProfile.Linear);

    private SyncRef<Slot> CurrentExpressionReference =>
        _root.FindChild("DV").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, "State.CurrentExpression")).Reference;

    private void SelectMenuColor(Slot item, Slot expression)
    {
        var driver = item.AttachComponent<ReferenceOptionDescriptionDriver<Slot>>();
        driver.Reference.Target = CurrentExpressionReference;
        driver.DefaultOption.Color.Value = colorX.White;
        // Null must never appear selected, including an imported item whose clip was deleted.
        driver.Options.Add().Color.Value = colorX.White;
        var selected = driver.Options.Add();
        selected.ReferenceTarget.Target = expression;
        selected.Color.Value = SelectedMenuColor;
        driver.Color.Target = item.GetComponent<ContextMenuItemSource>().Color;
    }

    private IField<bool> HandGesturePermissionField(string hand) =>
        _root.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<bool>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, HandGesturePermission(hand))).Value;

    private void GesturePermissionMenuColor(Slot item, bool left = true, bool right = true, bool option = false)
    {
        var driver = item.AttachComponent<ValueOptionDescriptionDriver<bool>>();
        var selectedState = item.AttachComponent<ValueField<bool>>();
        var condition = item.AttachComponent<MultiBoolConditionDriver>();
        condition.Mode.Value = MultiBoolConditionDriver.ConditionMode.All;
        foreach (var (hand, enabled) in new[] { ("Left", left), ("Right", right) })
        {
            var match = condition.Conditions.Add();
            match.Field.Target = HandGesturePermissionField(hand);
            match.Invert.Value = !enabled;
        }
        condition.Target.Target = selectedState.Value;
        driver.Value.Target = selectedState.Value;
        driver.DefaultOption.Color.Value = option ? colorX.White : new colorX(1f, 0f, 0f, 1f, ColorProfile.Linear);
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
        trigger.PressedData.Tag.Value = MenuSelectTag;
        trigger.PressedData.Reference.Target = expression;
        // Keep the authored expression separate from the payload, which becomes null
        // while selected. These local references also remap when a Clips entry is copied.
        var payload = item.AttachComponent<BooleanReferenceDriver<Slot>>();
        payload.FalseTarget.Target = expression;
        payload.TargetReference.Target = trigger.PressedData.Reference;
        var selected = item.AttachComponent<ReferenceEqualityDriver<Slot>>();
        selected.TargetReference.Target = CurrentExpressionReference;
        selected.Reference.Target = expression;
        selected.Target.Target = payload.State;
        SelectMenuColor(item, expression);
    }
    private void BuildMenus()
    {
        var menu = _inputs.AddSlot("ContextMenu");
        foreach (var expression in _clips.Values)
        {
            MenuItem(expression, expression.Name);
            SelectMenuTrigger(expression, expression);
        }
        foreach (var clip in _model.Clips)
            if (clip.MenuIconUrl != null && _clips.TryGetValue(clip.Id, out var expression))
                ExpressionMenuIcons.Apply(expression, expression.GetComponent<ContextMenuItemSource>(), clip.MenuIconUrl);
        var rootItem = menu.AttachComponent<RootContextMenuItem>();
        rootItem.Item.Target = MenuItem(menu, "Expressions");
        ExpressionMenuIcons.Apply(menu, rootItem.Item.Target, ExpressionMenuIcons.NeutralIconUrl);
        rootItem.OnlyForSide.Value = null;
        ConfigureSubmenu(menu, menu);
        // Keep editable clip templates in Clips; expose each once through this entrance.
        var selection = AddSubmenu(menu, "Select expression");
        var selectionColor = selection.AttachComponent<ValueOptionDescriptionDriver<bool>>();
        selectionColor.Value.Target = _root.FindChild("DV").GetComponentsInChildren<DynamicValueVariable<bool>>()
            .Single(v => v.VariableName.Value == Path(SystemSpace, ContextMenuSelected)).Value;
        selectionColor.DefaultOption.Color.Value = colorX.White;
        var heldSelection = selectionColor.Options.Add();
        heldSelection.ReferenceValue.Value = true;
        heldSelection.Color.Value = SelectedMenuColor;
        selectionColor.Color.Target = selection.GetComponent<ContextMenuItemSource>().Color;
        var groups = ExpressionMenuGroups.Build(_clips.Keys, _compiled.Pairs, _compiled.GestureCount);
        for (int index = 0; index < groups.Count; index++)
        {
            var children = _clips.Count <= 8 ? selection :
                AddSubmenu(selection, "Group " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string id in groups[index])
            {
                var expression = _clips[id];
                var item = children.AddSlot(expression.Name);
                var source = MenuItem(item, expression.Name);
                source.Sprite.Target = expression.GetComponent<ContextMenuItemSource>().Sprite.Target;
                var labelCopy = item.AttachComponent<ValueCopy<string>>();
                labelCopy.Source.Target = expression.GetComponent<ContextMenuItemSource>().Label;
                labelCopy.Target.Target = source.Label;
                SelectMenuTrigger(item, expression);
            }
        }
        BuildGesturePermissionMenu(menu);
        var reset = menu.AddSlot("Reset State");
        var resetSource = MenuItem(reset, "Reset");
        ExpressionMenuIcons.Apply(reset, resetSource, ExpressionMenuIcons.ResetIconUrl);
        var resetButton = reset.AttachComponent<ButtonDynamicImpulseTrigger>();
        resetButton.Target.Target = _api;
        resetButton.ExcludeDisabled.Value = true;
        resetButton.PressedTag.Value = ResetTag;
    }

    private static void ConfigureSubmenu(Slot slot, Slot itemsRoot)
    {
        var submenu = slot.AttachComponent<ContextMenuSubmenu>();
        submenu.ItemsRoot.Target = itemsRoot;
        submenu.SearchWholeHierarchy.Value = false;
    }

    private static Slot AddSubmenu(Slot parent, string label)
    {
        var group = parent.AddSlot(label);
        MenuItem(group, label);
        ConfigureSubmenu(group, group);
        var back = group.AddSlot("Back");
        MenuItem(back, "Back");
        ConfigureSubmenu(back, parent);
        return group;
    }

    private void BuildGesturePermissionMenu(Slot menu)
    {
        var group = AddSubmenu(menu, "Hand gesture options");
        var groupSource = group.GetComponent<ContextMenuItemSource>();
        groupSource.Label.Value = "Hand gestures";
        foreach (var (label, left, right) in new[]
        {
            ("Both hands enabled", true, true),
            ("Left hand only", true, false),
            ("Right hand only", false, true),
            ("Both hands disabled", false, false)
        })
        {
            var item = group.AddSlot(label); MenuItem(item, label);
            foreach (var (hand, enabled) in new[] { ("Left", left), ("Right", right) })
            {
                var set = item.AttachComponent<ButtonValueSet<bool>>();
                set.TargetValue.Target = HandGesturePermissionField(hand);
                set.SetValue.Value = enabled;
            }
            GesturePermissionMenuColor(item, left, right, option: true);
        }

        var mode = menu.AddSlot("Hand gestures");
        var toggleSource = MenuItem(mode, mode.Name);
        var toggleButton = mode.AttachComponent<ButtonDynamicImpulseTrigger>();
        toggleButton.Target.Target = _api;
        toggleButton.ExcludeDisabled.Value = true;
        toggleButton.PressedTag.Value = ToggleHandGesturesTag;
        GesturePermissionMenuColor(mode);
        BuildGesturePermissionLabels(menu, groupSource, toggleSource);

        // Keep the board outside the filtered entries so it runs in both modes.
        // Disabled item sources are omitted when ContextMenuSubmenu collects entries.
        var g = new ExpressionFlux(menu.AddSlot("Gesture menu visibility"));
        var vr = g.Node("UserVR_Active", null, ("User", g.Owner(_root)));
        foreach (var (source, visible) in new[] { (groupSource, (IWorldElement)vr), (toggleSource, g.Not(vr)) })
        {
            var driver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<bool>)
                g.Node("ValueFieldDrive", typeof(bool), ("Value", visible));
            driver.GetRootProxy(addIfMissing: true).Drive.Target = source.EnabledField;
        }
    }

    private void BuildGesturePermissionLabels(Slot menu, params ContextMenuItemSource[] sources)
    {
        // Keep the permission state live even when one of the menu entries is hidden.
        var board = menu.AddSlot("Gesture menu label");
        var state = board.AttachComponent<ValueField<bool2>>();
        var g = new ExpressionFlux(board);
        var left = g.Read<bool>(g.Ref(_api), SystemSpace, HandGesturePermission("Left"));
        var right = g.Read<bool>(g.Ref(_api), SystemSpace, HandGesturePermission("Right"));
        var value = g.Node("Pack_Bool2", null, ("X", left), ("Y", right));
        var stateDriver = (global::FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueFieldDrive<bool2>)
            g.Node("ValueFieldDrive", typeof(bool2), ("Value", value));
        stateDriver.GetRootProxy(addIfMissing: true).Drive.Target = state.Value;
        foreach (var source in sources)
        {
            var driver = source.Slot.AttachComponent<ValueOptionDescriptionDriver<bool2>>();
            driver.Value.Target = state.Value;
            driver.DefaultOption.Label.Value = "Hand gestures: None";
            foreach (var (permissions, label) in new[]
            {
                (new bool2(false, false), "None"), (new bool2(true, false), "LeftOnly"),
                (new bool2(false, true), "RightOnly"), (new bool2(true, true), "Both")
            })
            {
                var option = driver.Options.Add();
                option.ReferenceValue.Value = permissions;
                option.Label.Value = "Hand gestures: " + label;
            }
            driver.Label.Target = source.Label;
        }
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard");
        _model.Diagnostics.Add("Keyboard: Shift + keypad uses Left; Ctrl + keypad uses Right; Ctrl + Shift + keypad uses both hands. Each hand's Modifier key is editable.");
        for (int hand = 0; hand < 2; hand++)
        {
            var settings = Record(root, hand == 0 ? "Left" : "Right", KeyboardSpace);
            Data(settings, "Tag", hand == 0 ? SetGestureLeftTag : SetGestureRightTag);
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

    // Gesture classifiers derive from Avatar Expression Editor; see THIRD-PARTY-NOTICES.md.
    private void BuildGestures()
    {
        var root = _inputs.AddSlot("HandGestures");
        foreach (string device in new[] { "TouchController", "IndexController", "ViveController", "WindowsMRController", "CosmosController" })
        {
            var module = Record(root, device.Replace("Controller", ""), GestureSettingsSpace);
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
            g.Read<bool>(g.Ref(_api), SystemSpace, HandGesturePermission(side.ToString())));
        var current = g.Choose<int>(accepting, gesture, g.Constant(-1));
        var send = SendHandInput(g, g.Text(TrySetGestureTag(kind)), current);
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
