using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    internal const string TrySetGestureLeftTag = "ExpressionSystem/Method.TrySetGesture.Left";
    internal const string TrySetGestureRightTag = "ExpressionSystem/Method.TrySetGesture.Right";
    internal const string SetGestureLeftTag = "ExpressionSystem/Method.SetGesture.Left";
    internal const string SetGestureRightTag = "ExpressionSystem/Method.SetGesture.Right";
    internal const string MenuSelectTag = "ExpressionSystem/Method.SelectMenuExpression";
    internal const string ContextMenuSelected = "State.ContextMenuSelected";
    internal const string ResetTag = "ExpressionSystem/Method.Reset";
    internal const string ToggleHandGesturesTag = "ExpressionSystem/Method.ToggleHandGestures";
    internal static string HandGesturePermission(string hand) => "Config.AllowHandGesture." + hand;
    private readonly ExpressionModel _model;
    private bool _hasTrackedNeutral;
    private readonly Slot _root, _clipRoot, _outputs, _table, _api, _method, _inputs;
    private readonly Slot _events, _resetState, _selection, _playback;
    private const string PlaybackTickTag = "ExpressionSystem/Internal.ApplyExpression";
    private const string SelectionTickTag = "ExpressionSystem/Internal.UpdateSelection";
    private const string ResetStateTag = "ExpressionSystem/Internal.ResetState";
    private GesturePairCompiler _compiled;
    private readonly Dictionary<string, Slot> _clips = new();
    private readonly Dictionary<string, Slot> _outputSlots = new();

    private ExpressionSystemSetup(Slot avatar, ExpressionModel model)
    {
        _model = model;
        _root = Record(avatar, "Expressions", SystemSpace);
        _clipRoot = _root.AddSlot("Clips");
        _api = _root.AddSlot("API");
        // Method accepts calls from outside API; Internal is shared only within API.
        // Event owns automatic startup and condition-change processing.
        var internalSlot = _api.AddSlot("Internal");
        _method = _api.AddSlot("Method");
        _events = _api.AddSlot("Event");
        _outputs = _root.AddSlot("Outputs");
        _table = _root.AddSlot("DV").AddSlot("GestureTable");
        // State fields and table rows bind to the single system space.
        _root.GetComponent<DynamicVariableSpace>().OnlyDirectBinding.Value = false;
        _inputs = _root.AddSlot("Inputs");
        _resetState = internalSlot.AddSlot("ResetState");
        _selection = internalSlot.AddSlot("UpdateSelection");
        _playback = internalSlot.AddSlot("ApplyExpression");
        foreach (string hand in new[] { "Left", "Right" })
        {
            Data(_root, "State.Gesture." + hand, 0);
            Data(_root, HandGesturePermission(hand), true);
        }
        Reference<Slot>(_root, "State.CurrentExpression", null);
        Data(_root, ContextMenuSelected, false);
        Data(_root, "Config.SmoothingSpeed", DefaultSmoothingSpeed);
        Reference(_root, "Reference.API", _api);
        Reference(_root, "Reference.Clips", _clipRoot);
    }

    public static Task<Slot> BuildAsync(Slot avatar, ExpressionModel model, Func<ExpressionBinding, IField<float>> resolve,
        bool menu = true, Func<IField<float>, float?> initialWeight = null)
    {
        if (model.Clips.Count == 0 || model.DetectedExpressions != null && model.Clips.All(c => c.Curves.Count == 0)) return Task.FromResult<Slot>(null);
        AvatarSetup.EnsureAvatarRootIdentification(avatar);
        var setup = new ExpressionSystemSetup(avatar, model);
        setup.BuildClips(resolve, initialWeight);
        for (int left = 0; left < setup._compiled.GestureCount; left++)
        for (int right = 0; right < setup._compiled.GestureCount; right++)
        {
            string id = setup._compiled.Pairs[left * setup._compiled.GestureCount + right];
            string key = $"L{left}R{right}";
            var row = setup._table.AddSlot(key).AttachComponent<DynamicReferenceVariable<Slot>>();
            row.VariableName.Value = Path(SystemSpace, "GestureTable." + key);
            row.Reference.Target = id != null ? setup._clips.GetValueOrDefault(id) : null;
        }
        setup.BuildApi();
        setup.BuildInputs(menu);
        setup.BuildSelection();
        setup.BuildPlayback();
        setup.BuildResetState();
        setup.BuildLifecycle();
        ExpressionFlux.BindSlotReferences(setup._root);
        ExpressionFlux.Arrange(setup._root);
        setup.ArrangeSlots();
        setup.LogDiagnostics();
        int assigned = setup._compiled.Pairs.Count(id => id != null && setup._clips.ContainsKey(id));
        Console.WriteLine($"Expression system: {setup._clips.Count} clips, {setup._outputSlots.Count} outputs, {assigned}/{setup._compiled.Pairs.Length} gesture pairs assigned, " +
            $"{setup._root.GetComponentsInChildren<ProtoFluxNode>().Count} Flux nodes");
        return Task.FromResult(setup._root);
    }

    private void ArrangeSlots()
    {
        // Finalize ordering after generated Reference.* slots and optional Drivers exist.
        int RootOrder(Slot slot) => slot.Name switch
        {
            "API" => 0, "Clips" => 1, "Outputs" => 2,
            "DV" => 3, "Inputs" => 4, "Drivers" => 5, _ => 6
        };
        int VariableOrder(Slot slot) => slot.Name.Split('.')[0] switch
        {
            "Config" => 0, "State" => 1, "Reference" => 2, _ => 3
        };
        _root.SortChildren((a, b) => RootOrder(a).CompareTo(RootOrder(b)));
        _root.FindChild("DV").SortChildren((a, b) =>
        {
            int group = VariableOrder(a).CompareTo(VariableOrder(b));
            return group != 0 ? group : string.CompareOrdinal(a.Name, b.Name);
        });
    }

    private void BuildClips(Func<ExpressionBinding, IField<float>> resolve, Func<IField<float>, float?> initialWeight)
    {
        var fields = new Dictionary<IField<float>, Slot>();
        var definitions = _model.Clips.Where(clip =>
        {
            var unavailable = clip.Curves.Where(curve =>
            {
                var field = resolve(curve.Binding);
                return field == null || field.InheritedLink != null || (field.ActiveLink != null && field.ActiveLink is not ISyncRef);
            }).Select(c => c.Binding.Path + "/" + c.Binding.Shape).ToArray();
            if (unavailable.Length == 0) return true;
            string message = $"Expression '{clip.Name}' omitted: unresolved or unsupported driven bindings: " + string.Join(", ", unavailable);
            _model.Diagnostics.Add(message); UniLog.Warning(message);
            return false;
        }).ToList();
        var neutral = new ExpressionClip { Id = "resopon:neutral", Name = "Neutral (authored baseline)", Duration = 1, Source = "Authored baseline" };
        foreach (var binding in definitions.SelectMany(c => c.Curves).Select(c => c.Binding).Distinct())
        {
            var field = resolve(binding);
            if (field == null) continue;
            var curve = new ExpressionCurve { Binding = binding };
            curve.Keys.Add(new(0, initialWeight?.Invoke(field) ?? field.Value, 0, 0)); neutral.Curves.Add(curve);
        }
        if (neutral.Curves.Count > 0) definitions.Add(neutral);
        int previousDiagnostics = _model.Diagnostics.Count;
        _compiled = new GesturePairCompiler(_model, definitions, binding =>
        {
            var field = resolve(binding); return field == null ? 0 : initialWeight?.Invoke(field) ?? field.Value;
        });
        foreach (string message in _model.Diagnostics.Skip(previousDiagnostics)) UniLog.Warning("Expressions: " + message);
        definitions.AddRange(_compiled.Generated);
        // L0R0 is exported as release, but its authored pose must first become
        // the release baseline. Otherwise removing its Clip silently loses it.
        var neutralPose = definitions.FirstOrDefault(c => c.Id == _compiled.Pairs[0]);
        var defaults = neutralPose?.Curves.ToDictionary(c => c.Binding, c => c.Keys[^1].Value)
            ?? new Dictionary<ExpressionBinding, float>();
        var sourceDefaults = neutral.Curves.ToDictionary(c => c.Binding, c => c.Keys[0].Value);
        float Default(ExpressionBinding binding)
        {
            var field = resolve(binding);
            return defaults.TryGetValue(binding, out float value) ? value : initialWeight?.Invoke(field) ?? field.Value;
        }
        // Detected menu poses were made sparse against the source baseline. Rebase
        // their COMPLETE curves so an explicit zero can cancel the new neutral pose.
        ExpressionClip Rebase(ExpressionClip candidate)
        {
            var source = definitions.First(c => c.Id == candidate.Id);
            var rebased = new ExpressionClip { Id = source.Id, Name = source.Name, Source = source.Source,
                Duration = source.Duration, Loop = source.Loop, MenuIconUrl = source.MenuIconUrl };
            rebased.Curves.AddRange(source.Curves.Where(c => c.Keys.Any(k => k.Value != Default(c.Binding))));
            // Missing tracks previously released to the source baseline too. Preserve
            // that complete pose instead of accumulating the neutral mouth/cheek on it.
            foreach (var (binding, value) in defaults)
                if (value != sourceDefaults[binding] && !source.Curves.Any(c => c.Binding == binding))
                {
                    var reset = new ExpressionCurve { Binding = binding };
                    reset.Keys.Add(new(0, sourceDefaults[binding], 0, 0));
                    rebased.Curves.Add(reset);
                }
            return rebased;
        }
        // Retain complete poses required by validated mappings (including reset curves).
        // Unmapped candidates use differences from the new neutral default.
        var mapped = _compiled.Pairs.Where(id => id != null).ToHashSet();
        var catalog = _model.DetectedExpressions == null ? definitions : definitions
            .Where(c => mapped.Contains(c.Id) || c.Id == neutral.Id)
            .Concat(_model.DetectedExpressions.Where(c => definitions.Any(d => d.Id == c.Id)).Select(Rebase))
            .Where(c => c.Curves.Count > 0).DistinctBy(c => c.Id).ToList();
        // Neutral is release, not a selectable clip. Keep the compile-time baseline
        // and its output bindings, but export neither it nor the L0R0 pose.
        var omitted = new HashSet<string> { neutral.Id };
        if (_compiled.Pairs[0] is { } neutralId) omitted.Add(neutralId);
        for (int pair = 0; pair < _compiled.Pairs.Length; pair++)
            if (_compiled.Pairs[pair] is { } id && omitted.Contains(id)) _compiled.Pairs[pair] = null;
        // Allocate readable keys across all clips before creating any drives. Sorting
        // source bindings makes collision suffixes independent of clip/curve ordering.
        var outputNames = new Dictionary<string, string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in catalog.SelectMany(c => c.Curves).Select(c => c.Binding).Distinct()
            .OrderBy(b => b.Path, StringComparer.Ordinal).ThenBy(b => b.Shape, StringComparer.Ordinal))
        {
            var field = resolve(binding);
            if (field == null) continue;
            var (renderer, shape) = ResolveShape(field);
            string mesh = renderer?.Slot.Name ?? binding.Path?.Split('/').LastOrDefault();
            outputNames.Add(binding.Id, ExpressionBindingNames.Create(mesh, shape ?? binding.Shape, usedNames));
        }
        foreach (var clip in catalog)
        {
            foreach (var curve in clip.Curves)
            {
                string id = curve.Binding.Id;
                if (_outputSlots.ContainsKey(id)) continue;
                var field = resolve(curve.Binding);
                if (field == null)
                {
                    string message = $"Expression binding missing: {curve.Binding.Path}/{curve.Binding.Shape}";
                    if (!_model.Diagnostics.Contains(message)) { _model.Diagnostics.Add(message); UniLog.Warning(message); }
                    continue;
                }
                if (fields.TryGetValue(field, out var alias))
                    throw new InvalidOperationException($"Expression bindings resolve to the same output: {curve.Binding}");
                if (field.InheritedLink != null || (field.ActiveLink != null && field.ActiveLink is not ISyncRef))
                { UniLog.Warning($"Expression binding has an unsupported inherited drive: {curve.Binding}"); continue; }
                var output = Record(_outputs, UniqueChildName(_outputs, curve.Binding.Shape), OutputSpace);
                Data(output, "Id", outputNames[id]);
                Data(output, "TrackingWeight", 0f);
                // Only eyelid openness uses a closing-side union. Other tracking drivers
                // (visemes, gaze, etc.) retain their existing TrackingWeight behavior.
                var eye = field.ActiveLink?.Parent as EyeLinearDriver.Eye;
                int blinkMode = eye != null && eye.OpenCloseTarget == field.ActiveLink
                    ? (eye.ClosedState.Value < eye.OpenState.Value ? 2 : 1) : 0;
                Data(output, "BlinkMode", blinkMode);
                bool tracked = field.ActiveLink is ISyncRef;
                float initialValue = defaults.TryGetValue(curve.Binding, out float defaultValue) ? defaultValue : field.Value;
                var baseValue = Data(output, "Base", tracked ? field.Value : initialValue);
                if (tracked && defaults.ContainsKey(curve.Binding) &&
                    defaultValue != (initialWeight?.Invoke(field) ?? field.Value))
                {
                    // Live tracking keeps owning Base; the neutral pose is mixed by
                    // the same rules as selected expressions, including blinking.
                    Data(output, "Default", defaultValue);
                    _hasTrackedNeutral = true;
                    initialValue = blinkMode switch
                    {
                        1 => Math.Max(initialValue, field.Value),
                        2 => Math.Min(initialValue, field.Value),
                        _ => initialValue
                    };
                }
                else if (tracked) initialValue = field.Value;
                if (!tracked) field.Value = initialValue;
                // Move the existing blink/viseme driver onto its own proxy; never ForceLink it away.
                if (field.ActiveLink is ISyncRef oldDriver)
                {
                    Reference<ISyncRef>(output, "OriginalDriver", oldDriver);
                    oldDriver.Target = baseValue.Value;
                }
                var result = VariableSlot(output, "Result").AttachComponent<DynamicField<float>>();
                result.VariableName.Value = Path(OutputSpace, "Result");
                result.TargetField.Target = BuildOutputTarget(field, initialValue, blinkMode != 0);
                _outputSlots[id] = output; fields[field] = output;
            }
            // A partially resolved face must not be presented as a faithfully imported clip.
            if (clip.Curves.Any(c => !_outputSlots.ContainsKey(c.Binding.Id)))
            { UniLog.Warning($"Expression '{clip.Name}' omitted: one or more output bindings were not resolved"); continue; }
            if (omitted.Contains(clip.Id)) continue;
            Slot entry = Record(_clipRoot, clip.Name, ClipSpace);
            var values = entry.AddSlot("DV");
            foreach (var curve in clip.Curves)
            {
                string id = curve.Binding.Id;
                string name = outputNames[id];
                var variable = values.AddSlot(name).AttachComponent<DynamicValueVariable<float>>();
                variable.VariableName.Value = Path(ClipSpace, name);
                variable.Value.Value = curve.Keys[^1].Value;
            }
            _clips[clip.Id] = entry;
        }
    }

    private void LogDiagnostics()
    {
        foreach (string message in _model.Diagnostics.Distinct(StringComparer.Ordinal))
            Console.WriteLine("Expression diagnostics: " + message);
        var boards = _root.GetComponentsInChildren<ProtoFluxNode>().GroupBy(n => n.Slot.Parent).ToArray();
        foreach (var board in boards)
        {
            var names = new Stack<string>();
            for (var slot = board.Key; slot != _root; slot = slot.Parent) names.Push(slot.Name);
            string path = string.Join("/", names);
            Console.WriteLine($"Expression graph: {path}: {board.Count()} nodes");
        }
        Console.WriteLine($"Expression logic: {boards.Length} independent boards, largest {boards.Max(b => b.Count())} nodes");
    }
}
