using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void BuildMixer()
    {
        _g.BeginSection("Gesture pair lookup");
        foreach (string hand in new[] { "Left", "Right" })
            _updates.Add(_g.If(_g.Not(_g.Active(_g.Read<Slot>(_coreRef, hand + "Input"))),
                _g.Write<int>(_coreRef, hand + "Gesture", _g.Constant(0))));
        var index = _g.Binary<int>("ValueAdd", _g.Binary<int>("ValueMul", _g.Read<int>(_coreRef, "LeftGesture"), _g.Constant(8)),
            _g.Read<int>(_coreRef, "RightGesture"));
        var path = _g.Node("ConcatenateString", null, ("A", _g.Text("Expr/Pair.")),
            ("B", _g.Node("ToString_Int", null, ("V", index))));
        var mapped = _g.Read<Slot>(_g.Ref(_table), path);
        var manual = _g.Read<Slot>(_coreRef, "Override");
        var candidate = _g.Choose<Slot>(_g.And(_g.Active(manual), _g.Read<bool>(manual, "Enabled")), manual, mapped);
        var selected = _g.Local<Slot>();
        var current = _g.Read<Slot>(_coreRef, "CurrentExpression");
        _updates.Add(_g.Sequence(_g.Set<Slot>(selected, _g.Choose<Slot>(ValidExpression(candidate), candidate, _g.Ref<Slot>(null))),
            _g.If(_g.Not(_g.Equal<Slot>(selected, current)), _g.Sequence(
                _g.Each(_g.Ref(_outputs), output => _g.Write<float>(output, "Snapshot", _g.Read<float>(output, "Result"))),
                _g.Write<float>(_coreRef, "FadeDuration", _g.Choose<float>(_g.Active(selected),
                    _g.Read<float>(selected, "FadeIn"), _g.Read<float>(current, "FadeOut"))),
                _g.Write<float>(_coreRef, "PlaybackStart", _now), _g.Write<Slot>(_coreRef, "CurrentExpression", selected)))));

        _g.BeginSection("Output mixer");
        var elapsed = _g.Sub(_now, _g.Read<float>(_coreRef, "PlaybackStart"));
        var duration = _g.Read<float>(_coreRef, "FadeDuration");
        var blend = _g.Choose<float>(_g.Greater(duration, _g.Constant(0f)), _g.Clamp01(_g.Div(elapsed, duration)), _g.Constant(1f));
        _updates.Add(_g.Each(_g.Ref(_outputs), output =>
        {
            var sample = Sample(current, _g.Read<string>(output, "Id"), elapsed);
            var baseValue = _g.Read<float>(output, "Base");
            var desired = _g.Choose<float>(_g.And(_g.Active(current),
                _g.Binary<int>("ValueGreaterOrEqual", sample.Index, _g.Constant(0))), sample.Value, baseValue);
            desired = _g.Lerp(desired, baseValue, _g.Clamp01(_g.Read<float>(output, "TrackingWeight")));
            return _g.Write<float>(output, "Result", _g.Lerp(_g.Read<float>(output, "Snapshot"), desired, blend));
        }));
    }
}
