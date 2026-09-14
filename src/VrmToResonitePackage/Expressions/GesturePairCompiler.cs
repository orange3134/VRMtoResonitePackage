namespace VrmToResonitePackage.Expressions;

/// <summary>Reduces Animator selection to data before exporting. No Animator runs in the avatar.</summary>
internal sealed class GesturePairCompiler
{
    public string[] Pairs { get; } = new string[64];
    public Dictionary<ExpressionMenuControl, string> Menu { get; } = new();
    public List<ExpressionClip> Generated { get; } = new();
    private readonly ExpressionModel _model;
    private readonly Dictionary<string, ExpressionClip> _clips;
    private readonly Func<ExpressionBinding, float> _baseline;
    private readonly Dictionary<string, string> _combinations = new();

    public GesturePairCompiler(ExpressionModel model, IEnumerable<ExpressionClip> available, Func<ExpressionBinding, float> baseline)
    {
        _model = model; _clips = available.ToDictionary(c => c.Id); _baseline = baseline;
        var layers = model.Layers.Where(l => Supported(l, new[] { "GestureLeft", "GestureRight" })).ToArray();
        int unresolved = 0;
        for (int left = 0; left < 8; left++)
            for (int right = 0; right < 8; right++)
            {
                float Value(string name) => name == "GestureLeft" ? left : right;
                var states = layers.Select(l => Resolve(l, Value)).ToArray();
                if (states.Any(s => s < 0)) { unresolved++; continue; }
                Pairs[left * 8 + right] = Compose(layers, states);
            }
        if (unresolved > 0) Warn($"{unresolved} gesture pairs have history-dependent or cyclic selection; their table entries are unassigned.");
        AddMenu(model.Menu);
        if (layers.Length > 0)
            Warn("Gesture pairs use a shared playback clock and Catalog fades. Animator transition timing, self-restarts and independent layer phases are not reproduced.");

        void AddMenu(IEnumerable<ExpressionMenuControl> controls)
        {
            foreach (var control in controls)
            {
                if (control.Type == 2) { AddMenu(control.Children); continue; }
                // A direct menu pose must depend on this control alone, without simulating parameters.
                var candidates = model.Layers.Where(l => Parameters(l).Contains(control.Parameter) &&
                    Supported(l, new[] { control.Parameter }, diagnose: false)).ToArray();
                var states = candidates.Select(l => Resolve(l, _ => control.Value)).ToArray();
                string id = candidates.Length > 0 && states.All(s => s >= 0) ? Compose(candidates, states) : null;
                if (id != null) Menu[control] = id;
                else Warn("Menu control has no independent direct pose: " + control.Name + "; supported clips remain in Catalog.");
            }
        }
    }

    private static IEnumerable<string> Parameters(ExpressionLayer layer) =>
        layer.Entry.Concat(layer.Transitions).SelectMany(t => t.Conditions).Select(c => c.Parameter);

    private bool Supported(ExpressionLayer layer, string[] parameters, bool diagnose = true)
    {
        string reason = null;
        if (Parameters(layer).Any(p => !parameters.Contains(p))) reason = "depends on other parameters";
        else if (layer.Entry.Concat(layer.Transitions).Any(t => t.HasExitTime || t.Offset != 0)) reason = "requires exit time or playback offset";
        else if (layer.States.Any(s => s.ClipId != null && !_clips.ContainsKey(s.ClipId))) reason = "has unavailable clips";
        else if (layer.States.Any(s => s.Speed <= 0 || !float.IsFinite(s.Speed))) reason = "has invalid playback speed";
        else if (layer.States.Select(s => s.WriteDefaults).Distinct().Count() > 1) reason = "mixes Write Defaults";
        else if (layer.States.Any(s => !s.WriteDefaults))
        {
            var sets = layer.States.Select(s => s.ClipId == null ? new HashSet<ExpressionBinding>() :
                _clips[s.ClipId].Curves.Select(c => c.Binding).ToHashSet()).ToArray();
            if (sets.Skip(1).Any(s => !s.SetEquals(sets[0]))) reason = "retains unanimated properties from previous states";
        }
        if (reason != null && diagnose) Warn(layer.Name + ": omitted from gesture table (" + reason + ").");
        return reason == null;
    }

    // Start from every possible prior state, not just Entry: otherwise a latched state looks stateless.
    internal static int Resolve(ExpressionLayer layer, Func<string, float> value)
    {
        bool Matches(ExpressionTransition t) => t.Conditions.All(c => c.Matches(value(c.Parameter)));
        int expected = -1;
        int initial = layer.Entry.FirstOrDefault(Matches)?.Destination ?? layer.DefaultState;
        foreach (int start in Enumerable.Range(0, layer.States.Count).Append(initial))
        {
            int state = start;
            var visited = new HashSet<int>();
            while (true)
            {
                if (state < 0 || state >= layer.States.Count || !visited.Add(state)) return -1;
                var next = layer.Transitions.FirstOrDefault(t =>
                    (t.Source < 0 || t.Source == state) &&
                    (t.Source >= 0 || t.CanTransitionToSelf || t.Destination != state) && Matches(t));
                if (next == null || next.Destination == state) break;
                state = next.Destination;
            }
            if (expected >= 0 && layer.States[expected] != layer.States[state]) return -1;
            expected = state;
        }
        return expected;
    }

    private sealed record Term(ExpressionCurve Curve, ExpressionClip Clip, float Speed, float Weight);
    private static bool Constant(ExpressionCurve curve) => curve.Keys.All(k => k.Value == curve.Keys[0].Value) &&
        (curve.Keys.Count == 1 || curve.Keys.Zip(curve.Keys.Skip(1)).All(p =>
            float.IsInfinity(p.First.OutSlope) || float.IsInfinity(p.Second.InSlope) || p.First.OutSlope == 0 && p.Second.InSlope == 0));

    private string Compose(ExpressionLayer[] layers, int[] states)
    {
        if (layers.Length == 0) return null;
        string key = string.Join("|", layers.Select((l, i) => l.Id + ":" + states[i]));
        if (_combinations.TryGetValue(key, out var cached)) return cached;
        var terms = new Dictionary<ExpressionBinding, List<Term>>();
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i]; var state = layer.States[states[i]];
            var clip = state.ClipId == null ? null : _clips[state.ClipId];
            var owned = layer.States.Where(s => s.ClipId != null).SelectMany(s => _clips[s.ClipId].Curves).Select(c => c.Binding).Distinct();
            foreach (var binding in owned)
            {
                ExpressionCurve Baseline()
                {
                    var c = new ExpressionCurve { Binding = binding }; c.Keys.Add(new(0, _baseline(binding), 0, 0)); return c;
                }
                if (!terms.TryGetValue(binding, out var list)) terms[binding] = list = new() { new(Baseline(), null, 1, 1) };
                for (int j = 0; j < list.Count; j++) list[j] = list[j] with { Weight = list[j].Weight * (1 - layer.Weight) };
                list.RemoveAll(t => t.Weight == 0);
                if (layer.Weight > 0) list.Add(new(clip?.Curves.FirstOrDefault(c => c.Binding == binding) ?? Baseline(), clip, state.Speed, layer.Weight));
            }
        }
        var animated = terms.Values.SelectMany(t => t).Where(t => !Constant(t.Curve)).ToArray();
        var timing = animated.Select(t => (Duration: t.Clip.Duration / t.Speed, t.Clip.Loop)).Distinct().ToArray();
        if (timing.Length > 1) return Reject("incompatible animation durations or loops");
        var result = new ExpressionClip { Id = "resopon:pair:" + Generated.Count,
            Name = string.Join(" + ", layers.Select((l, i) => l.States[states[i]].Name)),
            Source = "Compiled gesture/menu pose", Duration = timing.Length == 0 ? 1 : timing[0].Duration,
            Loop = timing.Length > 0 && timing[0].Loop };
        foreach (var (binding, list) in terms)
        {
            var moving = list.Where(t => !Constant(t.Curve)).ToArray();
            var curve = new ExpressionCurve { Binding = binding };
            if (moving.Length == 0) curve.Keys.Add(new(0, list.Sum(t => t.Weight * t.Curve.Keys[0].Value), 0, 0));
            else
            {
                // Affine transforms preserve Hermite and hold interpolation exactly. Multiple moving
                // curves are combined only when their key times and step segments coincide.
                var reference = moving[0];
                if (moving.Skip(1).Any(t => !t.Curve.Keys.Select(k => k.Time / t.Speed)
                    .SequenceEqual(reference.Curve.Keys.Select(k => k.Time / reference.Speed)))) return Reject("unaligned animated curve keys");
                bool Step(Term t, int j) => float.IsInfinity(t.Curve.Keys[j].OutSlope) || float.IsInfinity(t.Curve.Keys[j + 1].InSlope);
                for (int j = 0; j < reference.Curve.Keys.Count - 1; j++)
                    if (moving.Any(t => Step(t, j) != Step(reference, j))) return Reject("incompatible stepped curves");
                float constant = list.Where(t => Constant(t.Curve)).Sum(t => t.Weight * t.Curve.Keys[0].Value);
                for (int j = 0; j < reference.Curve.Keys.Count; j++)
                {
                    float slope(bool incoming)
                    {
                        var slopes = moving.Select(t => t.Weight * t.Speed *
                            (incoming ? t.Curve.Keys[j].InSlope : t.Curve.Keys[j].OutSlope)).ToArray();
                        return slopes.Any(float.IsInfinity) ? float.PositiveInfinity : slopes.Sum();
                    }
                    curve.Keys.Add(new(reference.Curve.Keys[j].Time / reference.Speed,
                        constant + moving.Sum(t => t.Weight * t.Curve.Keys[j].Value), slope(true), slope(false)));
                }
            }
            result.Curves.Add(curve);
        }
        // Share equal poses across cells and reuse source clips where possible.
        var existing = _clips.Values.Concat(Generated).FirstOrDefault(c => c.Loop == result.Loop &&
            (animated.Length == 0 || c.Duration == result.Duration) && c.Curves.Count == result.Curves.Count &&
            result.Curves.All(r => c.Curves.Any(o => o.Binding == r.Binding &&
                (Constant(o) && Constant(r) ? o.Keys[0].Value == r.Keys[0].Value : o.Keys.SequenceEqual(r.Keys)))));
        if (existing != null) return _combinations[key] = existing.Id;
        Generated.Add(result);
        return _combinations[key] = result.Id;

        string Reject(string reason)
        {
            Warn("Pose not assigned: " + key + " (" + reason + "); select/map supported source clips manually.");
            _combinations[key] = null; return null;
        }
    }

    private void Warn(string message) { if (!_model.Diagnostics.Contains(message)) _model.Diagnostics.Add(message); }
}
