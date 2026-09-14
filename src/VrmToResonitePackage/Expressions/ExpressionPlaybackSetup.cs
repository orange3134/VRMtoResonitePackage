using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private IWorldElement Conditions(IEnumerable<ExpressionCondition> conditions) => _g.And(conditions.Select(condition =>
    {
        var value = _g.Read<float>(_parametersRef, "Value/" + condition.Parameter);
        var threshold = _g.Constant(condition.Threshold);
        return condition.Mode switch
        {
            1 => _g.Not(_g.Equal<float>(value, _g.Constant(0f))), 2 => _g.Equal<float>(value, _g.Constant(0f)),
            3 => _g.Greater(value, threshold), 4 => _g.Greater(threshold, value), 6 => _g.Equal<float>(value, threshold),
            7 => _g.Not(_g.Equal<float>(value, threshold)), _ => _g.Constant(false),
        };
    }).ToArray());

    private void BuildLayers()
    {
        foreach (var model in _model.Layers)
        {
            if (model.States.Any(s => s.ClipId != null && !_clips.ContainsKey(s.ClipId))) continue;
            var layer = Record(_layers, model.Name);
            Data(layer, "Id", model.Id); Data(layer, "Enabled", true); Data(layer, "Weight", model.Weight);
            Data(layer, "State", -1); Data(layer, "Start", 0f); Data(layer, "Speed", 1f); Data(layer, "Empty", true);
            Data(layer, "PreviousStart", 0f); Data(layer, "PreviousSpeed", 1f); Data(layer, "PreviousEmpty", true);
            Data(layer, "FadeStart", 0f); Data(layer, "FadeDuration", 0f); Data(layer, "Normalized", -0.00001f);
            Reference<Slot>(layer, "Clip", null); Reference<Slot>(layer, "PreviousClip", null);
            var statesRoot = layer.AddSlot("States");
            foreach (var state in model.States)
            {
                var slot = Record(statesRoot, state.Name);
                Reference(slot, "Clip", state.ClipId == null ? null : _clips[state.ClipId]);
                Data(slot, "Speed", state.Speed); Data(slot, "Empty", state.ClipId == null);
            }
            var bindings = model.States.Where(s => s.ClipId != null).SelectMany(s => _model.Clips.First(c => c.Id == s.ClipId).Curves)
                .Select(c => ExpressionAnimationConverter.BindingId(c.Binding)).Distinct();
            foreach (string id in bindings) Data(layer, "Own/" + id, true);
            var layerRef = _g.Ref(layer); var states = _g.Ref(statesRoot);
            IWorldElement Current(string key) => _g.Read<float>(layerRef, key);
            var clip = _g.Read<Slot>(layerRef, "Clip");
            var duration = _g.Choose<float>(_g.Greater(_g.Read<float>(clip, "Duration"), _g.Constant(0f)), _g.Read<float>(clip, "Duration"), _g.Constant(1f));
            var normalized = _g.Div(_g.Mul(_g.Sub(_now, Current("Start")), Current("Speed")), duration);
            var changed = _g.Local<bool>();
            IWorldElement Change(IWorldElement destination, IWorldElement fade, float offset = 0)
            {
                var state = _g.Node("GetChild", null, ("Instance", states), ("ChildIndex", destination));
                var nextClip = _g.Read<Slot>(state, "Clip");
                var nextSpeed = _g.Read<float>(state, "Speed");
                // Capture all outgoing values before replacing the current state.
                return _g.Sequence(_g.Write<Slot>(layerRef, "PreviousClip", clip),
                    _g.Write<float>(layerRef, "PreviousStart", Current("Start")), _g.Write<float>(layerRef, "PreviousSpeed", Current("Speed")),
                    _g.Write<bool>(layerRef, "PreviousEmpty", _g.Read<bool>(layerRef, "Empty")),
                    _g.Write<float>(layerRef, "FadeDuration", fade), _g.Write<float>(layerRef, "FadeStart", _now),
                    _g.Write<float>(layerRef, "Start", _g.Sub(_now, _g.Div(_g.Mul(_g.Constant(offset), _g.Read<float>(nextClip, "Duration")), nextSpeed))),
                    _g.Write<float>(layerRef, "Speed", nextSpeed), _g.Write<bool>(layerRef, "Empty", _g.Read<bool>(state, "Empty")),
                    _g.Write<Slot>(layerRef, "Clip", nextClip), _g.Write<int>(layerRef, "State", destination),
                    _g.Write<float>(layerRef, "Normalized", _g.Constant(offset - 0.00001f)), _g.Set<bool>(changed, _g.Constant(true)));
            }
            IWorldElement initial = _g.Constant(model.DefaultState);
            foreach (var entry in model.Entry.AsEnumerable().Reverse()) initial = _g.Choose<int>(Conditions(entry.Conditions), _g.Constant(entry.Destination), initial);
            var actions = new List<IWorldElement> { _g.Set<bool>(changed, _g.Constant(false)),
                _g.If(_g.Equal<int>(_g.Read<int>(layerRef, "State"), _g.Constant(-1)), Change(initial, _g.Constant(0f))) };
            foreach (var transition in model.Transitions)
            {
                var sourceMatches = transition.Source < 0 ? _g.Constant(true) : _g.Equal<int>(_g.Read<int>(layerRef, "State"), _g.Constant(transition.Source));
                var canSelf = transition.Source >= 0 || transition.CanTransitionToSelf ? _g.Constant(true) :
                    _g.Not(_g.Equal<int>(_g.Read<int>(layerRef, "State"), _g.Constant(transition.Destination)));
                IWorldElement exit = _g.Constant(true);
                if (transition.HasExitTime)
                {
                    var previous = Current("Normalized");
                    if (transition.ExitTime < 1)
                    {
                        var currentCycle = _g.Node("Floor_Float", null, ("N", _g.Sub(normalized, _g.Constant(transition.ExitTime))));
                        var previousCycle = _g.Node("Floor_Float", null, ("N", _g.Sub(previous, _g.Constant(transition.ExitTime))));
                        exit = _g.Greater(currentCycle, previousCycle);
                    }
                    else exit = _g.And(_g.Greater(_g.Constant(transition.ExitTime), previous),
                        _g.Not(_g.Greater(_g.Constant(transition.ExitTime), normalized)));
                }
                var fade = transition.FixedDuration ? _g.Constant(transition.Duration) :
                    _g.Div(_g.Mul(_g.Constant(transition.Duration), duration), Current("Speed"));
                actions.Add(_g.If(_g.And(_g.Not(changed), sourceMatches, canSelf, exit, Conditions(transition.Conditions),
                    _g.Not(_g.Greater(_g.Add(Current("FadeStart"), Current("FadeDuration")), _now))),
                    Change(_g.Constant(transition.Destination), fade, transition.Offset)));
            }
            actions.Add(_g.If(_g.Not(changed), _g.Write<float>(layerRef, "Normalized", normalized)));
            _updates.Add(_g.If(_g.And(_g.Active(layerRef), _g.Read<bool>(layerRef, "Enabled")), _g.Sequence(actions.ToArray())));
        }
    }

    private void BuildMixer()
    {
        // These loops enumerate records at runtime: adding/deleting/reordering definitions needs no new graph wiring.
        _updates.Add(_g.Each(_g.Ref(_outputs), output =>
        {
            var property = _g.Read<string>(output, "Id");
            var baseValue = _g.Read<float>(output, "Base");
            var baseline = _g.Read<float>(output, "Baseline");
            var result = _g.Local<float>();
            var winner = _g.Local<Slot>(); var winnerExpression = _g.Local<Slot>();
            var priority = _g.Local<float>(); var order = _g.Local<int>();
            var layerPass = _g.Each(_g.Ref(_layers), layer =>
            {
                var expression = _g.Read<Slot>(layer, "Clip");
                var previous = _g.Read<Slot>(layer, "PreviousClip");
                var sample = Sample(expression, property, _g.Mul(_g.Sub(_now, _g.Read<float>(layer, "Start")), _g.Read<float>(layer, "Speed")));
                var oldSample = Sample(previous, property, _g.Mul(_g.Sub(_now, _g.Read<float>(layer, "PreviousStart")), _g.Read<float>(layer, "PreviousSpeed")));
                var value = _g.Choose<float>(_g.Binary<int>("ValueGreaterOrEqual", sample.Index, _g.Constant(0)), sample.Value, baseline);
                var oldValue = _g.Choose<float>(_g.Binary<int>("ValueGreaterOrEqual", oldSample.Index, _g.Constant(0)), oldSample.Value, baseline);
                var fadeDuration = _g.Read<float>(layer, "FadeDuration");
                var t = _g.Choose<float>(_g.Greater(fadeDuration, _g.Constant(0f)),
                    _g.Clamp01(_g.Div(_g.Sub(_now, _g.Read<float>(layer, "FadeStart")), fadeDuration)), _g.Constant(1f));
                var currentReady = _g.Or(_g.Read<bool>(layer, "Empty"), ValidExpression(expression));
                var previousReady = _g.Or(_g.Read<bool>(layer, "PreviousEmpty"), ValidExpression(previous));
                // A removed or unavailable clip contributes the lower layer, not a spurious zero pose.
                value = _g.Choose<float>(currentReady, value, result);
                oldValue = _g.Choose<float>(previousReady, oldValue, result);
                return _g.If(_g.And(_g.Active(layer), _g.Read<bool>(layer, "Enabled"), _g.Read<bool>(layer, Key("Own/", property))),
                    _g.Set<float>(result, _g.Lerp(result, _g.Lerp(oldValue, value, t), _g.Clamp01(_g.Read<float>(layer, "Weight")))));
            });
            var sourcePass = _g.Each(_sourcesRef, source =>
            {
                var expression = _g.Read<Slot>(source, "Selected");
                var sample = Sample(expression, property, _g.Sub(_now, _g.Read<float>(source, "Start")));
                var affected = _g.Or(_g.Read<bool>(expression, "FullFace"), _g.Binary<int>("ValueGreaterOrEqual", sample.Index, _g.Constant(0)));
                var sourcePriority = _g.Read<float>(source, "Priority"); var sourceOrder = _g.Read<int>(source, "Order");
                var better = _g.Or(_g.Greater(sourcePriority, priority), _g.And(_g.Equal<float>(sourcePriority, priority),
                    _g.Binary<int>("ValueGreaterThan", sourceOrder, order)));
                return _g.If(_g.And(ValidSource(source), ValidExpression(expression), affected, better),
                    _g.Sequence(_g.Set<Slot>(winner, source), _g.Set<Slot>(winnerExpression, expression),
                        _g.Set<float>(priority, sourcePriority), _g.Set<int>(order, sourceOrder)));
            });
            var winnerSample = Sample(winnerExpression, property, _g.Sub(_now, _g.Read<float>(winner, "Start")));
            var desired = _g.Choose<float>(_g.Binary<int>("ValueGreaterOrEqual", winnerSample.Index, _g.Constant(0)), winnerSample.Value, baseValue);
            desired = _g.Lerp(desired, baseValue, _g.Clamp01(_g.Read<float>(output, "TrackingWeight")));
            var withDirect = _g.Choose<float>(_g.Node("NotNull", typeof(Slot), ("Instance", winner)),
                _g.Lerp(result, desired, _g.Clamp01(_g.Read<float>(winner, "Weight"))), result);
            var changed = _g.Or(_g.Not(_g.Equal<Slot>(winner, _g.Read<Slot>(output, "LastSource"))),
                _g.Not(_g.Equal<Slot>(winnerExpression, _g.Read<Slot>(output, "LastExpression"))),
                _g.Not(_g.Equal<int>(order, _g.Read<int>(output, "LastOrder"))));
            var duration = _g.Choose<float>(_g.Node("NotNull", typeof(Slot), ("Instance", winner)),
                _g.Read<float>(winnerExpression, "FadeIn"), _g.Read<float>(_g.Read<Slot>(output, "LastExpression"), "FadeOut"));
            var fadeDuration = _g.Read<float>(output, "FadeDuration");
            var blend = _g.Choose<float>(_g.Greater(fadeDuration, _g.Constant(0f)),
                _g.Clamp01(_g.Div(_g.Sub(_now, _g.Read<float>(output, "FadeStart")), fadeDuration)), _g.Constant(1f));
            return _g.Sequence(_g.Set<float>(result, baseValue), layerPass,
                _g.Set<Slot>(winner, _g.Ref<Slot>(null)), _g.Set<Slot>(winnerExpression, _g.Ref<Slot>(null)),
                _g.Set<float>(priority, _g.Constant(-1e20f)), _g.Set<int>(order, _g.Constant(-1)), sourcePass,
                _g.If(changed, _g.Sequence(_g.Write<float>(output, "Snapshot", _g.Read<float>(output, "Result")),
                    _g.Write<float>(output, "FadeDuration", duration),
                    _g.Write<float>(output, "FadeStart", _now), _g.Write<Slot>(output, "LastSource", winner),
                    _g.Write<Slot>(output, "LastExpression", winnerExpression), _g.Write<int>(output, "LastOrder", order))),
                _g.Write<float>(output, "Result", _g.Lerp(_g.Read<float>(output, "Snapshot"), withDirect, blend)));
        }));
    }
}
