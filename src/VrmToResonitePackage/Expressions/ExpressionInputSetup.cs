using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;
using InputKey = Renderite.Shared.Key;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private Slot Command(Slot parent, Slot source, string channel)
    {
        var command = Record(parent, "Command");
        Data(command, "Version", 1); Data(command, "Channel", channel); Reference(command, "SourceSlot", source);
        Data(command, "Generation", 0); Data(command, "Sequence", 0); Data(command, "ReleaseGeneration", 0); Data(command, "ReleaseSequence", 0);
        Data(command, "Operation", "Select"); Data(command, "ExpressionId", ""); Data(command, "Weight", 1f); Data(command, "Mode", "Hold");
        Data(command, "Hand", 0); Data(command, "GestureId", 0); Data(command, "GestureWeight", 0f); Data(command, "Available", true);
        Data(command, "ResetAfter", 0f);
        Reference<Slot>(command, "Parameters", null);
        return command;
    }
    private IWorldElement Send(IWorldElement command)
    {
        var source = _g.Read<Slot>(command, "SourceSlot");
        return _g.Sequence(_g.Write<int>(command, "Generation", _g.Read<int>(_coreRef, "Generation")),
            _g.Write<int>(command, "Sequence", Increment(_g.Read<int>(source, "Sequence"))),
            _g.Trigger(_g.Ref(_api), RequestTag, command));
    }
    private IWorldElement Release(IWorldElement command)
    {
        var source = _g.Read<Slot>(command, "SourceSlot");
        return _g.Sequence(_g.Write<string>(command, "Operation", _g.Text("Release")),
            _g.Write<int>(command, "ReleaseGeneration", _g.Read<int>(_coreRef, "Generation")),
            _g.Write<int>(command, "ReleaseSequence", _g.Read<int>(source, "SelectionToken")), Send(command));
    }
    private void OwnerUpdate(ExpressionFlux graph, Slot root, IWorldElement action)
    {
        var update = graph.Node("LocalUpdate");
        Link(update, "OnUpdate", graph.If(_isOwner, action));
    }
    private void BuildInputs(bool menu)
    {
        if (menu) BuildMenus();
        BuildKeyboard(); BuildGestures();
        BuildGestureOverrides();
        var external = _inputs.AddSlot("External");
        var source = Source("External", external, "face");
        var example = Command(_root.FindChild("API").AddSlot("Examples"), source, "face");
        example.Name = "External request (copy SourceState for another sender)";
        Data(external, "Instructions", "Use the avatar wearer client. Copy Core/SourceState/External for each sender; set Command.SourceSlot, Generation from Core, and increasing Sequence. Trigger API/Receivers with tag ResoPon/Expression/v1/Request and Slot payload.");
    }
    private void BuildGestureOverrides()
    {
        var root = _root.FindChild("Rules").AddSlot("GestureOverrides");
        foreach (var (hand, priority) in new[] { ("Left", 20f), ("Right", 21f) })
        {
            _g.BeginSection("Gesture override - " + hand);
            var rule = Record(root, hand); var entries = rule.AddSlot("Mappings");
            Reference<Slot>(rule, "Previous", null);
            for (int gesture = 0; gesture < 8; gesture++)
            {
                var mapping = Record(entries, gesture.ToString()); Data(mapping, "Gesture", gesture);
                Data(mapping, "Enabled", false); Reference<Slot>(mapping, "Expression", null);
            }
            var source = Source("Gesture override/" + hand, rule, "face", priority: priority);
            Data(source, "Automatic", true);
            var command = _g.Ref(Command(rule, source, "face"));
            var selected = _g.Local<Slot>();
            var ruleRef = _g.Ref(rule);
            var scan = _g.Each(_g.Ref(entries), entry => _g.If(_g.And(_g.Active(entry), _g.Read<bool>(entry, "Enabled"),
                _g.Equal<float>(_g.Node("Cast_int_To_float", null, ("Input", _g.Read<int>(entry, "Gesture"))),
                    _g.Read<float>(_parametersRef, "Value/Gesture" + hand)), ValidExpression(_g.Read<Slot>(entry, "Expression"))),
                _g.Set<Slot>(selected, _g.Read<Slot>(entry, "Expression"))));
            // Run after parameter arbitration. Every change enters the same public request receiver.
            _gestureOverrides.Add(_g.Sequence(_g.Set<Slot>(selected, _g.Ref<Slot>(null)),
                _g.If(_g.Active(ruleRef), scan),
                _g.If(_g.Not(_g.Equal<Slot>(selected, _g.Read<Slot>(ruleRef, "Previous"))), _g.Sequence(
                    _g.If(_g.Node("NotNull", typeof(Slot), ("Instance", selected)),
                        _g.Sequence(_g.Write<string>(command, "ExpressionId", _g.Read<string>(selected, "Id")),
                            _g.Write<string>(command, "Operation", _g.Text("Select")), Send(command)), Release(command)),
                    _g.Write<Slot>(ruleRef, "Previous", selected)))));
        }
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
        _g.BeginSection("Context menu commands");
        var menu = _inputs.AddSlot("ContextMenu");
        var rootItem = MenuItem(menu, "Expressions");
        menu.AttachComponent<RootContextMenuItem>().Item.Target = rootItem;
        var items = menu.AddSlot("Items");
        menu.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = items;
        var direct = items.AddSlot("Direct selection"); MenuItem(direct, "Select expression");
        direct.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = _catalog;
        var source = Source("ContextMenu", menu, "face");
        var command = Command(menu, source, "face");
        var receiverRoot = menu.AddSlot("Adapter");
        var graph = new ExpressionFlux(receiverRoot);
        var receiver = graph.Receiver("ResoPon/Expression/v1/MenuSelect");
        var entry = Out(receiver, "Value"); var cmd = _g.Ref(command);
        Link(receiver, "OnTriggered", _g.If(_isOwner, _g.Sequence(
            _g.Write<string>(cmd, "ExpressionId", _g.Read<string>(entry, "Id")), _g.Write<string>(cmd, "Operation", _g.Text("Select")),
            _g.Write<string>(cmd, "Mode", _g.Text("Toggle")), Send(cmd))));
        foreach (var expression in _clips.Values)
        {
            var item = MenuItem(expression, expression.Name);
            var display = expression.GetComponents<DynamicValueVariable<string>>().First(v => v.VariableName.Value == "Expr/DisplayName");
            item.Label.DriveFrom(display.Value);
            item.EnabledField.DriveFrom(expression.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == "Expr/Enabled").Value);
            MenuTrigger(expression, receiverRoot, "ResoPon/Expression/v1/MenuSelect", expression);
        }
        var resetSlot = items.AddSlot("Automatic"); MenuItem(resetSlot, "Return to automatic");
        var resetReceiver = graph.Receiver("ResoPon/Expression/v1/MenuAutomatic", false);
        var resetButton = resetSlot.AttachComponent<ButtonDynamicImpulseTrigger>();
        resetButton.Target.Target = receiverRoot; resetButton.ExcludeDisabled.Value = true;
        resetButton.PressedTag.Value = "ResoPon/Expression/v1/MenuAutomatic";
        Link(resetReceiver, "OnTriggered", _g.If(_isOwner, _g.Sequence(_g.Write<string>(cmd, "Operation", _g.Text("Reset")), Send(cmd))));
        if (_model.Menu.Count == 0) return;
        var imported = items.AddSlot("Imported menu"); MenuItem(imported, "Imported menu");
        var importedItems = imported.AddSlot("Items"); imported.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = importedItems;
        var paramSource = Source("ImportedMenu", menu, "parameters", 3);
        var paramCommand = Command(menu, paramSource, "parameters");
        var parameterItems = paramCommand.AddSlot("Parameters");
        var parameterItem = Record(parameterItems, "Parameter"); Data(parameterItem, "Name", ""); Data(parameterItem, "Value", 0f);
        paramCommand.GetComponents<DynamicReferenceVariable<Slot>>().First(v => v.VariableName.Value == "Expr/Parameters").Reference.Target = parameterItems;
        var paramReceiver = graph.Receiver("ResoPon/Expression/v1/MenuParameter");
        var control = Out(paramReceiver, "Value"); var pCmd = _g.Ref(paramCommand); var pItem = _g.Ref(parameterItem);
        var parameterName = _g.Read<string>(control, "Parameter"); var target = _g.Read<float>(control, "Value");
        var isToggle = _g.Equal<int>(_g.Read<int>(control, "Type"), _g.Constant(1));
        var selected = _g.Equal<float>(_g.Read<float>(_parametersRef, Key("Value/", parameterName)), target);
        Link(paramReceiver, "OnTriggered", _g.If(_isOwner, _g.Sequence(_g.Write<string>(pCmd, "Operation", _g.Text("SetParameters")),
            _g.Write<string>(pItem, "Name", parameterName), _g.Write<float>(pItem, "Value", _g.Choose<float>(_g.And(isToggle, selected), _g.Constant(0f), target)),
            _g.Write<float>(pCmd, "ResetAfter", _g.Choose<float>(_g.Equal<int>(_g.Read<int>(control, "Type"), _g.Constant(0)), _g.Constant(1f), _g.Constant(0f))), Send(pCmd))));
        AddControls(importedItems, _model.Menu);
        void AddControls(Slot parent, IEnumerable<ExpressionMenuControl> controls)
        {
            foreach (var definition in controls)
            {
                if (definition.Type != 2 && !_model.Layers.SelectMany(l => l.Entry.Concat(l.Transitions))
                    .SelectMany(t => t.Conditions).Any(c => c.Parameter == definition.Parameter))
                {
                    _model.Diagnostics.Add("Menu control omitted: no supported automatic layer uses " + definition.Parameter + " (" + definition.Name + ")");
                    continue;
                }
                var slot = Record(parent, definition.Name); MenuItem(slot, definition.Name);
                Data(slot, "Parameter", definition.Parameter ?? ""); Data(slot, "Value", definition.Value); Data(slot, "Type", definition.Type);
                if (definition.Type == 2)
                {
                    var children = slot.AddSlot("Items"); slot.AttachComponent<ContextMenuSubmenu>().ItemsRoot.Target = children;
                    AddControls(children, definition.Children);
                    if (!children.Children.Any()) { slot.Destroy(); continue; }
                    if (!string.IsNullOrEmpty(definition.Parameter))
                        _model.Diagnostics.Add("Submenu parameter open/close is not supported: " + definition.Name);
                }
                else MenuTrigger(slot, receiverRoot, "ResoPon/Expression/v1/MenuParameter", slot);
            }
        }
    }

    private void BuildKeyboard()
    {
        var root = _inputs.AddSlot("Keyboard");
        int index = 0;
        foreach (var expression in _clips.Values)
        {
            var shortcut = Record(root, expression.Name);
            Data(shortcut, "Enabled", true); Data(shortcut, "Key", index < 9 ? (InputKey)((int)InputKey.Alpha1 + index) : InputKey.None);
            Data(shortcut, "Control", true); Data(shortcut, "Alt", true); Data(shortcut, "Mode", "Toggle"); Data(shortcut, "Held", false);
            Reference(shortcut, "Expression", expression);
            var source = Source("Keyboard/" + expression.Name, shortcut, "face");
            Reference(shortcut, "Command", Command(shortcut, source, "face")); index++;
        }
        var previous = _g; _g = new(root.AddSlot("Logic"));
        try
        {
            IWorldElement Held(InputKey key) => _g.Node("KeyHeld", null, ("Key", _g.Constant(key)));
            var control = _g.Or(Held(InputKey.LeftControl), Held(InputKey.RightControl)); var alt = _g.Or(Held(InputKey.LeftAlt), Held(InputKey.RightAlt));
            var loop = _g.Each(_g.Ref(root), shortcut =>
            {
                var key = _g.Read<InputKey>(shortcut, "Key"); var command = _g.Read<Slot>(shortcut, "Command");
                var held = _g.And(_g.Read<bool>(shortcut, "Enabled"), _g.Not(_g.Equal<InputKey>(key, _g.Constant(InputKey.None))),
                    _g.Node("KeyHeld", null, ("Key", key)), _g.Or(_g.Not(_g.Read<bool>(shortcut, "Control")), control),
                    _g.Or(_g.Not(_g.Read<bool>(shortcut, "Alt")), alt));
                var wasHeld = _g.Read<bool>(shortcut, "Held"); var mode = _g.Read<string>(shortcut, "Mode");
                return _g.Sequence(_g.If(_g.And(held, _g.Not(wasHeld)), _g.Sequence(
                        _g.Write<string>(command, "ExpressionId", _g.Read<string>(_g.Read<Slot>(shortcut, "Expression"), "Id")),
                        _g.Write<string>(command, "Operation", _g.Text("Select")), _g.Write<string>(command, "Mode", mode), Send(command))),
                    _g.If(_g.And(wasHeld, _g.Not(held), _g.Equal<string>(mode, _g.Text("Hold"))), Release(command)),
                    _g.Write<bool>(shortcut, "Held", held));
            });
            OwnerUpdate(_g, root, loop);
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
            Data(module, "Priority", 10f);
            var previous = _g; _g = new(module.AddSlot("Logic"));
            try
            {
                foreach (var (side, kind) in new[] { (Chirality.Left, 1), (Chirality.Right, 2) })
                {
                    _g.BeginSection(side + " controller input");
                    var hand = Record(module, side.ToString()); var handRef = _g.Ref(hand); var modRef = _g.Ref(module);
                    var source = Source(device + "/" + side, module, side.ToString(), kind, 10, 0.5f);
                    var priority = source.GetComponents<DynamicValueVariable<float>>().Single(v => v.VariableName.Value == "Expr/Priority");
                    priority.Value.DriveFrom(module.GetComponents<DynamicValueVariable<float>>().Single(v => v.VariableName.Value == "Expr/Priority").Value);
                    var sourceRef = _g.Ref(source); var command = _g.Ref(Command(hand, source, side.ToString()));
                    Data(hand, "Candidate", -1); Data(hand, "Since", 0f); Data(hand, "LastSent", -1e20f); Data(hand, "Stable", 0);
                    Data(hand, "GripHeld", false); Data(hand, "TriggerHeld", false);
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
                    var send = _g.Sequence(_g.Write<string>(command, "Operation", _g.Text("Gesture")),
                        _g.Write<int>(command, "Hand", _g.Constant(kind)), _g.Write<int>(command, "GestureId", _g.Read<int>(handRef, "Stable")),
                        _g.Write<float>(command, "GestureWeight", trigger), _g.Write<bool>(command, "Available", active),
                        Send(command), _g.Write<float>(handRef, "LastSent", _now));
                    OwnerUpdate(_g, hand, _g.If(active, _g.Sequence(
                        _g.Write<bool>(handRef, "GripHeld", grip), _g.Write<bool>(handRef, "TriggerHeld", indexCurled),
                        _g.If(changed, _g.Sequence(_g.Write<int>(handRef, "Candidate", gesture), _g.Write<float>(handRef, "Since", _now))),
                        _g.If(stable, _g.Sequence(
                            _g.If(_g.Not(_g.Equal<int>(_g.Read<int>(handRef, "Stable"), gesture)),
                                _g.Sequence(_g.Write<int>(handRef, "Stable", gesture), send)),
                            _g.If(_g.Greater(_g.Sub(_now, _g.Read<float>(handRef, "LastSent")), _g.Constant(0.1f)), send)))),
                        _g.If(_g.Greater(_g.Read<float>(handRef, "LastSent"), _g.Constant(0f)),
                            _g.Sequence(Release(command), _g.Write<float>(handRef, "LastSent", _g.Constant(-1e20f))))));
                    Reference(hand, "Source", source);
                }
            }
            finally { _g = previous; }
        }
    }
}
