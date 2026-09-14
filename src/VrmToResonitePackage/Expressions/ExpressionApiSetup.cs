using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildApi()
    {
        _g.BeginSection("Public request API");
        var receivers = new ExpressionFlux(_api.AddSlot("Logic"));
        var receiver = receivers.Receiver(RequestTag);
        var command = Out(receiver, "Value");
        var hand = _g.Read<int>(command, "Hand"); var gesture = _g.Read<int>(command, "Gesture");
        var available = _g.Read<bool>(command, "Available");
        var valid = _g.And(_isOwner, _g.Active(command),
            _g.Binary<int>("ValueGreaterOrEqual", gesture, _g.Constant(0)),
            _g.Binary<int>("ValueLessOrEqual", gesture, _g.Constant(7)));
        var writes = new List<IWorldElement>();
        for (int side = 0; side < 2; side++)
        {
            string name = side == 0 ? "Left" : "Right";
            // A disconnect only clears the hand still owned by this input.
            writes.Add(_g.If(_g.Equal<int>(hand, _g.Constant(side)),
                _g.If(available, _g.Sequence(_g.Write<int>(_coreRef, name + "Gesture", gesture),
                        _g.Write<Slot>(_coreRef, name + "Input", command)),
                    _g.If(_g.Equal<Slot>(_g.Read<Slot>(_coreRef, name + "Input"), command),
                        _g.Sequence(_g.Write<int>(_coreRef, name + "Gesture", _g.Constant(0)),
                            _g.Write<Slot>(_coreRef, name + "Input", _g.Ref<Slot>(null)))))));
        }
        Link(receiver, "OnTriggered", _g.If(valid, _g.Sequence(writes.ToArray())));
        var select = receivers.Receiver(SelectTag);
        var expression = Out(select, "Value");
        Link(select, "OnTriggered", _g.If(_g.And(_isOwner, _g.Active(expression), _g.Read<bool>(expression, "Enabled"),
                _g.Equal<Slot>(_g.Node("GetParentSlot", null, ("Instance", expression)), _g.Ref(_catalog))),
            _g.Write<Slot>(_coreRef, "Override", expression)));
        var automatic = receivers.Receiver(AutomaticTag, false);
        Link(automatic, "OnTriggered", _g.If(_isOwner, _g.Write<Slot>(_coreRef, "Override", _g.Ref<Slot>(null))));
    }

    private void BuildLifecycle()
    {
        _g.BeginSection("Lifecycle and update order");
        // Local scope state is not serialized, so clone/load starts with fresh hand inputs.
        var initialized = _g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(_g.Write<int>(_coreRef, hand + "Gesture", _g.Constant(0)));
            cleanup.Add(_g.Write<Slot>(_coreRef, hand + "Input", _g.Ref<Slot>(null)));
        }
        cleanup.Add(_g.Write<Slot>(_coreRef, "Override", _g.Ref<Slot>(null)));
        cleanup.Add(_g.Write<Slot>(_coreRef, "CurrentExpression", _g.Ref<Slot>(null)));
        cleanup.Add(_g.Each(_g.Ref(_outputs), output => _g.Sequence(
            _g.Write<float>(output, "Result", _g.Read<float>(output, "Base")),
            _g.Write<float>(output, "Snapshot", _g.Read<float>(output, "Base")))));
        cleanup.Add(_g.Each(_g.Ref(_inputs.FindChild("Keyboard").FindChild("Bindings")),
            shortcut => _g.Write<bool>(shortcut, "Held", _g.Constant(false))));
        cleanup.Add(_g.Write<User>(_coreRef, "PreviousOwner", _owner));
        cleanup.Add(_g.Set<bool>(initialized, _g.Constant(true)));
        var reset = _g.If(_g.Or(_g.Not(initialized), _g.Not(_g.Equal<User>(_owner, _g.Read<User>(_coreRef, "PreviousOwner")))),
            _g.Sequence(cleanup.ToArray()));
        var update = _g.Node("LocalUpdate");
        Link(update, "OnUpdate", _g.If(_g.Or(_isOwner, _g.Node("IsLocalUser", null, ("User", _g.Read<User>(_coreRef, "PreviousOwner")))),
            _g.Sequence(reset, _g.Sequence(_updates.ToArray()))));
    }
}
