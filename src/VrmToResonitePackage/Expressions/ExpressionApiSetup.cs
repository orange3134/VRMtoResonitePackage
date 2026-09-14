using FrooxEngine;
using FrooxEngine.ProtoFlux;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private IWorldElement Join(IWorldElement a, IWorldElement b) => _g.Node("ConcatenateString", null, ("A", a), ("B", b));
    private IWorldElement Key(string prefix, IWorldElement name)
    {
        if (prefix == "Own/") return Join(_g.Text("Expr/Own."), name);
        IWorldElement path = _g.Text("Expr/UnregisteredParameter");
        foreach (string parameter in _model.Parameters.Keys)
            path = _g.Choose<string>(_g.Equal<string>(name, _g.Text(parameter)), _g.Text(ExpressionFlux.Path(prefix + parameter)), path);
        return path;
    }
    private IWorldElement Increment(IWorldElement value) => _g.Binary<int>("ValueAdd", value, _g.Constant(1));
    private IWorldElement FiniteUnit(IWorldElement value) => _g.And(_g.Equal<float>(value, value),
        _g.Not(_g.Greater(_g.Constant(0f), value)), _g.Not(_g.Greater(value, _g.Constant(1f))));

    private void BuildApi()
    {
        _g.BeginSection("Public request API");
        var receiver = new ExpressionFlux(_api).Receiver(RequestTag);
        var command = Out(receiver, "Value");
        var source = _g.Read<Slot>(command, "SourceSlot");
        var operation = _g.Read<string>(command, "Operation");
        var sequence = _g.Read<int>(command, "Sequence");
        var accepted = _g.Local<bool>();
        var nextOrder = _g.Local<int>();
        var found = _g.Local<Slot>();
        var matches = _g.Local<int>();
        var sourceParent = _g.Node("GetParentSlot", null, ("Instance", source));
        var headerValid = _g.And(_isOwner, _g.Equal<int>(_g.Read<int>(command, "Version"), _g.Constant(1)),
            _g.Equal<Slot>(sourceParent, _sourcesRef), _g.Active(source), _g.Read<bool>(source, "Enabled"),
            _g.Active(_g.Read<Slot>(source, "Owner")),
            _g.Equal<int>(_g.Read<int>(command, "Generation"), _g.Read<int>(_coreRef, "Generation")),
            _g.Equal<string>(_g.Read<string>(command, "Channel"), _g.Read<string>(source, "Channel")),
            _g.Binary<int>("ValueGreaterThan", sequence, _g.Read<int>(source, "Sequence")));
        var find = _g.Each(_catalogRef, entry => _g.If(_g.And(_g.Active(entry), _g.Read<bool>(entry, "Enabled"),
            _g.Equal<string>(_g.Read<string>(entry, "Id"), _g.Read<string>(command, "ExpressionId"))),
            _g.Sequence(_g.Set<Slot>(found, entry), _g.Set<int>(matches, Increment(matches)))));
        var mode = _g.Read<string>(command, "Mode");
        var toggleOff = _g.And(_g.Equal<string>(mode, _g.Text("Toggle")), _g.Equal<Slot>(_g.Read<Slot>(source, "Selected"), found));
        var select = _g.Sequence(_g.Set<Slot>(found, _g.Ref<Slot>(null)), _g.Set<int>(matches, _g.Constant(0)), find,
            _g.If(_g.And(_g.Equal<int>(matches, _g.Constant(1)), FiniteUnit(_g.Read<float>(command, "Weight")),
                    _g.Equal<int>(_g.Read<int>(source, "Kind"), _g.Constant(0)),
                    _g.Or(_g.Equal<string>(mode, _g.Text("Hold")), _g.Equal<string>(mode, _g.Text("Toggle")), _g.Equal<string>(mode, _g.Text("OneShot")))),
                _g.Sequence(_g.Write<Slot>(source, "Selected", _g.Choose<Slot>(toggleOff, _g.Ref<Slot>(null), found)),
                    _g.Write<float>(source, "Weight", _g.Read<float>(command, "Weight")),
                    _g.Write<string>(source, "Mode", mode), _g.Write<float>(source, "Start", _now),
                    _g.Write<int>(source, "SelectionToken", sequence), _g.Write<int>(source, "Order", nextOrder),
                    _g.Set<bool>(accepted, _g.Constant(true)))));
        var tokenMatches = _g.And(_g.Equal<int>(_g.Read<int>(command, "ReleaseGeneration"), _g.Read<int>(_coreRef, "Generation")),
            _g.Equal<int>(_g.Read<int>(command, "ReleaseSequence"), _g.Read<int>(source, "SelectionToken")));
        var releaseActions = new List<IWorldElement> { _g.Write<Slot>(source, "Selected", _g.Ref<Slot>(null)),
            _g.Write<float>(source, "Alive", _g.Constant(-1e20f)), _g.Write<int>(source, "Gesture", _g.Constant(0)),
            _g.Write<float>(source, "GestureWeight", _g.Constant(0f)) };
        foreach (var parameter in _model.Parameters.Values) releaseActions.Add(_g.Write<bool>(source, "Has/" + parameter.Name, _g.Constant(false)));
        releaseActions.Add(_g.Set<bool>(accepted, _g.Constant(true)));
        var release = _g.If(tokenMatches, _g.Sequence(releaseActions.ToArray()));
        var renew = _g.If(_g.And(tokenMatches, FiniteUnit(_g.Read<float>(command, "Weight"))),
            _g.Sequence(_g.Write<float>(source, "Weight", _g.Read<float>(command, "Weight")), _g.Set<bool>(accepted, _g.Constant(true))));
        var gesture = _g.Read<int>(command, "GestureId");
        var kind = _g.Read<int>(source, "Kind");
        var gestures = _g.If(_g.And(_g.Or(_g.Equal<int>(kind, _g.Constant(1)), _g.Equal<int>(kind, _g.Constant(2))),
            _g.Equal<int>(_g.Read<int>(command, "Hand"), kind),
            _g.Binary<int>("ValueGreaterOrEqual", gesture, _g.Constant(0)),
            _g.Binary<int>("ValueLessOrEqual", gesture, _g.Constant(7)), FiniteUnit(_g.Read<float>(command, "GestureWeight"))),
            _g.Sequence(_g.Write<int>(source, "Gesture", _g.Choose<int>(_g.Read<bool>(command, "Available"), gesture, _g.Constant(0))),
                _g.Write<float>(source, "GestureWeight", _g.Choose<float>(_g.Read<bool>(command, "Available"), _g.Read<float>(command, "GestureWeight"), _g.Constant(0f))),
                _g.Write<int>(source, "SelectionToken", sequence), _g.Set<bool>(accepted, _g.Constant(true))));

        var parameterItems = _g.Read<Slot>(command, "Parameters");
        var validParameters = _g.Local<bool>();
        var duplicateCount = _g.Local<int>();
        IWorldElement ParameterValid(IWorldElement item)
        {
            var name = _g.Read<string>(item, "Name"); var value = _g.Read<float>(item, "Value");
            var type = _g.Read<int>(_parametersRef, Key("Type/", name));
            var bounded = _g.And(_g.Equal<float>(value, value), _g.Not(_g.Greater(_g.Constant(-1f), value)), _g.Not(_g.Greater(value, _g.Constant(255f))));
            return _g.And(bounded, _g.Or(_g.And(_g.Equal<int>(type, _g.Constant(1)), _g.Not(_g.Greater(value, _g.Constant(1f)))),
                _g.And(_g.Equal<int>(type, _g.Constant(3)), _g.Not(_g.Greater(_g.Constant(0f), value)), _g.Equal<float>(value, _g.Node("Floor_Float", null, ("N", value)))),
                _g.And(_g.Equal<int>(type, _g.Constant(4)), _g.Or(_g.Equal<float>(value, _g.Constant(0f)), _g.Equal<float>(value, _g.Constant(1f))))));
        }
        var resetAfter = _g.Read<float>(command, "ResetAfter");
        var paramsSet = _g.Sequence(_g.Set<bool>(validParameters, _g.And(
                _g.Or(_g.Equal<int>(kind, _g.Constant(0)), _g.Equal<int>(kind, _g.Constant(3))),
                _g.Node("NotNull", typeof(Slot), ("Instance", parameterItems)),
                _g.Binary<int>("ValueGreaterThan", _g.Node("ChildrenCount", null, ("Instance", parameterItems)), _g.Constant(0)),
                _g.Equal<float>(resetAfter, resetAfter), _g.Not(_g.Greater(_g.Constant(0f), resetAfter)), _g.Not(_g.Greater(resetAfter, _g.Constant(60f))))),
            _g.Each(parameterItems, item => _g.Sequence(_g.Set<int>(duplicateCount, _g.Constant(0)),
                _g.Each(parameterItems, other => _g.If(_g.Equal<string>(_g.Read<string>(item, "Name"), _g.Read<string>(other, "Name")),
                    _g.Set<int>(duplicateCount, Increment(duplicateCount)))),
                _g.Set<bool>(validParameters, _g.And(validParameters, ParameterValid(item), _g.Equal<int>(duplicateCount, _g.Constant(1)))))),
            _g.If(validParameters, _g.Sequence(_g.Each(parameterItems, item =>
            {
                var name = _g.Read<string>(item, "Name");
                return _g.Sequence(_g.Write<bool>(source, Key("Has/", name), _g.Constant(true)),
                    _g.Write<float>(source, Key("Value/", name), _g.Read<float>(item, "Value")),
                    _g.Write<int>(source, Key("ParameterOrder/", name), nextOrder),
                    _g.Write<float>(source, Key("Until/", name), _g.Choose<float>(_g.Greater(_g.Read<float>(command, "ResetAfter"), _g.Constant(0f)),
                        _g.Add(_now, _g.Read<float>(command, "ResetAfter")), _g.Constant(0f))));
            }), _g.Write<int>(source, "SelectionToken", sequence), _g.Set<bool>(accepted, _g.Constant(true)))));
        var reset = _g.If(_g.Equal<int>(kind, _g.Constant(0)), _g.Sequence(_g.Each(_sourcesRef, s =>
            _g.If(_g.And(_g.Equal<int>(_g.Read<int>(s, "Kind"), _g.Constant(0)), _g.Not(_g.Read<bool>(s, "Automatic"))),
                _g.Write<Slot>(s, "Selected", _g.Ref<Slot>(null)))),
            _g.Set<bool>(accepted, _g.Constant(true))));
        var dispatch = _g.Sequence(_g.Set<bool>(accepted, _g.Constant(false)), _g.Set<int>(nextOrder, Increment(_g.Read<int>(_coreRef, "Order"))),
            _g.If(_g.Equal<string>(operation, _g.Text("Select")), select),
            _g.If(_g.Equal<string>(operation, _g.Text("Release")), release),
            _g.If(_g.Equal<string>(operation, _g.Text("Renew")), renew),
            _g.If(_g.Equal<string>(operation, _g.Text("Gesture")), gestures),
            _g.If(_g.Equal<string>(operation, _g.Text("SetParameters")), paramsSet),
            _g.If(_g.Equal<string>(operation, _g.Text("Reset")), reset),
            _g.If(accepted, _g.Sequence(_g.Write<int>(source, "Sequence", sequence),
                _g.If(_g.Not(_g.Equal<string>(operation, _g.Text("Release"))), _g.Write<float>(source, "Alive", _now)),
                _g.Write<int>(_coreRef, "Order", nextOrder), _g.Write<string>(_coreRef, "LastError", _g.Text(""))),
                _g.Write<string>(_coreRef, "LastError", _g.Text("Rejected expression operation or payload"))));
        Link(receiver, "OnTriggered", _g.If(headerValid, dispatch,
            _g.If(_isOwner, _g.Write<string>(_coreRef, "LastError", _g.Text("Rejected expression header, source, generation or sequence")))));
    }

    private void BuildParameterEvaluation()
    {
        foreach (var parameter in _model.Parameters.Values)
        {
            _g.BeginSection("Parameter - " + parameter.Name);
            var value = _g.Local<float>(); var priority = _g.Local<float>(); var order = _g.Local<int>();
            string name = parameter.Name;
            var scan = _g.Each(_sourcesRef, source =>
            {
                var kind = _g.Read<int>(source, "Kind");
                bool left = name is "GestureLeft" or "GestureLeftWeight", right = name is "GestureRight" or "GestureRightWeight";
                var isGesture = _g.Equal<int>(kind, _g.Constant(left ? 1 : right ? 2 : -1));
                var has = _g.Or(isGesture, _g.Read<bool>(source, "Has/" + name));
                var sourceOrder = _g.Read<int>(source, "ParameterOrder/" + name);
                var better = _g.Or(_g.Greater(_g.Read<float>(source, "Priority"), priority),
                    _g.And(_g.Equal<float>(_g.Read<float>(source, "Priority"), priority), _g.Binary<int>("ValueGreaterThan", sourceOrder, order)));
                var until = _g.Read<float>(source, "Until/" + name);
                var expiry = _g.And(_g.Greater(until, _g.Constant(0f)), _g.Greater(_now, until));
                var gestureValue = name.EndsWith("Weight", StringComparison.Ordinal) ? _g.Read<float>(source, "GestureWeight") :
                    _g.Node("Cast_int_To_float", null, ("Input", _g.Read<int>(source, "Gesture")));
                return _g.Sequence(_g.If(expiry, _g.Sequence(_g.Write<float>(source, "Value/" + name, _g.Constant(0f)),
                        _g.Write<float>(source, "Until/" + name, _g.Constant(0f)))),
                    _g.If(_g.And(ValidSource(source), has, better), _g.Sequence(
                        _g.Set<float>(value, _g.Choose<float>(isGesture, gestureValue, _g.Read<float>(source, "Value/" + name))),
                        _g.Set<float>(priority, _g.Read<float>(source, "Priority")), _g.Set<int>(order, sourceOrder))));
            });
            _updates.Add(_g.Sequence(_g.Set<float>(value, _g.Constant(parameter.Default)), _g.Set<float>(priority, _g.Constant(-1e20f)),
                _g.Set<int>(order, _g.Constant(-1)), scan, _g.Write<float>(_parametersRef, "Value/" + name, value)));
        }
    }

    private void BuildLifecycle()
    {
        _g.BeginSection("Lifecycle and update order");
        // StoredValue belongs to the local execution scope, so it starts false after clone/load.
        var initialized = _g.Node("StoredValue", typeof(bool));
        var cleanup = _g.Each(_sourcesRef, source =>
        {
            var actions = new List<IWorldElement> { _g.Write<Slot>(source, "Selected", _g.Ref<Slot>(null)), _g.Write<int>(source, "Sequence", _g.Constant(0)),
                _g.Write<float>(source, "Alive", _g.Constant(-1e20f)), _g.Write<int>(source, "Gesture", _g.Constant(0)), _g.Write<float>(source, "GestureWeight", _g.Constant(0f)) };
            foreach (var p in _model.Parameters.Values) actions.Add(_g.Write<bool>(source, "Has/" + p.Name, _g.Constant(false)));
            return _g.Sequence(actions.ToArray());
        });
        var resetLayers = _g.Each(_g.Ref(_layers), layer => _g.Write<int>(layer, "State", _g.Constant(-1)));
        var resetOverrides = _g.Each(_g.Ref(_root.FindChild("Rules").FindChild("GestureOverrides")),
            rule => _g.Write<Slot>(rule, "Previous", _g.Ref<Slot>(null)));
        var resetKeyboard = _g.Each(_g.Ref(_inputs.FindChild("Keyboard")),
            shortcut => _g.Write<bool>(shortcut, "Held", _g.Constant(false)));
        var resetOutputs = _g.Each(_g.Ref(_outputs), output => _g.Sequence(
            _g.Write<float>(output, "Result", _g.Read<float>(output, "Base")),
            _g.Write<float>(output, "Snapshot", _g.Read<float>(output, "Base")),
            _g.Write<Slot>(output, "LastSource", _g.Ref<Slot>(null)), _g.Write<Slot>(output, "LastExpression", _g.Ref<Slot>(null))));
        var reset = _g.If(_g.Or(_g.Not(initialized), _g.Not(_g.Equal<User>(_owner, _g.Read<User>(_coreRef, "PreviousOwner")))),
            _g.Sequence(cleanup, resetLayers, resetOverrides, resetKeyboard, resetOutputs, _g.Write<int>(_coreRef, "Generation", Increment(_g.Read<int>(_coreRef, "Generation"))),
                _g.Write<User>(_coreRef, "PreviousOwner", _owner), _g.Set<bool>(initialized, _g.Constant(true))));
        var expire = _g.Each(_sourcesRef, source =>
        {
            var selected = _g.Read<Slot>(source, "Selected");
            return _g.If(_g.Or(_g.Not(ValidSource(source)), _g.And(_g.Equal<string>(_g.Read<string>(source, "Mode"), _g.Text("OneShot")),
                    _g.Greater(_g.Sub(_now, _g.Read<float>(source, "Start")), _g.Read<float>(selected, "Duration")))),
                _g.Write<Slot>(source, "Selected", _g.Ref<Slot>(null)));
        });
        var update = _g.Node("LocalUpdate");
        Link(update, "OnUpdate", _g.If(_g.Or(_isOwner, _g.Node("IsLocalUser", null, ("User", _g.Read<User>(_coreRef, "PreviousOwner")))),
            _g.Sequence(reset, expire, _g.Sequence(_updates.ToArray()))));
    }
}
