using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    internal const string LeftTag = "ResoPon/Expression/Gesture/Left";
    internal const string RightTag = "ResoPon/Expression/Gesture/Right";
    internal const string MenuLeftTag = "ResoPon/Expression/Menu/Left";
    internal const string MenuRightTag = "ResoPon/Expression/Menu/Right";
    internal const string SelectTag = "ResoPon/Expression/Menu/Select";
    internal const string InputEnabledTag = "ResoPon/Expression/AllowExternalInput";
    private readonly ExpressionModel _model;
    private readonly Slot _root, _catalog, _core, _outputs, _table, _api, _inputs;
    private readonly Slot _lifecycle, _selection, _playback;
    private const string OutputUpdateTag = "ResoPon/Expression/Internal/Output";
    private const string PlaybackTickTag = "ResoPon/Expression/Internal/Playback";
    private const string SelectionTickTag = "ResoPon/Expression/Internal/Selection";
    private const string InitializeTag = "ResoPon/Expression/Internal/Initialize";
    private const string MenuRefreshTag = "ResoPon/Expression/Internal/MenuRefresh";
    private Slot _menuAvailability;
    private GesturePairCompiler _compiled;
    private readonly Dictionary<string, Slot> _clips = new();
    private readonly Dictionary<string, Slot> _outputSlots = new();

    private ExpressionSystemSetup(Slot avatar, ExpressionModel model)
    {
        _model = model;
        _root = Record(avatar, "Expressions", SystemSpace);
        _catalog = _root.AddSlot("Catalog");
        _core = Record(_root, "Core", CoreSpace);
        _outputs = _root.AddSlot("Outputs");
        _table = Record(_root, "GestureTable", TableSpace);
        _table.GetComponent<DynamicVariableSpace>().OnlyDirectBinding.Value = false;
        _inputs = _root.AddSlot("Inputs");
        _api = _root.AddSlot("API").AddSlot("Receivers");
        var logic = _core.AddSlot("Logic");
        _lifecycle = logic.AddSlot("Lifecycle");
        _selection = logic.AddSlot("Selection");
        _playback = logic.AddSlot("Playback");
        foreach (string hand in new[] { "Left", "Right" })
        {
            Data(_core, hand + "Gesture", 0);
        }
        Data(_core, "AllowExternalInput", true);
        Reference<Slot>(_core, "CurrentExpression", null);
        Data(_core, "PairIndex", 0);
        Data(_root, "Version", 14);
        Reference(_root, "Receiver", _api);
        Reference(_root, "Catalog", _catalog);
        _root.AddSlot("Diagnostics");
    }

    public static Task<Slot> BuildAsync(Slot avatar, ExpressionModel model, Func<ExpressionBinding, IField<float>> resolve,
        bool menu = true, Func<IField<float>, float?> initialWeight = null)
    {
        if (model.Clips.Count == 0) return Task.FromResult<Slot>(null);
        AvatarSetup.ImportAvatarRootIdentification(avatar);
        var setup = new ExpressionSystemSetup(avatar, model);
        setup.BuildCatalog(resolve, initialWeight);
        for (int index = 0; index < 64; index++)
        {
            var cell = setup._table.AddSlot($"{index:D2} Left {index / 8} - Right {index % 8}");
            string id = setup._compiled.Pairs[index];
            Reference(cell, "Pair." + index, id != null ? setup._clips.GetValueOrDefault(id) : null);
        }
        setup.BuildApi();
        setup.BuildInputs(menu);
        setup.BuildSelection();
        setup.BuildPlayback();
        setup.BuildLifecycle();
        ExpressionFlux.Arrange(setup._root);
        setup.DescribeGraphs();
        foreach (string message in model.Diagnostics) Data(Record(setup._root.FindChild("Diagnostics"), "Import warning", WarningSpace), "Message", message);
        if (setup._clips.Count > 0)
        {
            var template = setup._clips.Values.First().Duplicate(setup._root.FindChild("API").AddSlot("Templates"));
            template.Name = "Expression (copy into Catalog)";
            template.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == Path(ClipSpace, "Id")).Value.Value = "";
            template.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == Path(ClipSpace, "Enabled")).Value.Value = false;
        }
        int assigned = setup._compiled.Pairs.Count(id => id != null && setup._clips.ContainsKey(id));
        Console.WriteLine($"Expression system: {setup._clips.Count} clips, {setup._outputSlots.Count} outputs, {assigned}/64 gesture pairs assigned, " +
            $"{setup._root.GetComponentsInChildren<ProtoFluxNode>().Count} Flux nodes");
        return Task.FromResult(setup._root);
    }

    private void BuildCatalog(Func<ExpressionBinding, IField<float>> resolve, Func<IField<float>, float?> initialWeight)
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
        foreach (var clip in definitions)
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
                Data(output, "Id", id); Data(output, "Path", curve.Binding.Path); Data(output, "Shape", curve.Binding.Shape);
                Data(output, "Baseline", initialWeight?.Invoke(field) ?? field.Value);
                Data(output, "TrackingWeight", 0f);
                Data(output, "HasPose", false);
                Data(output, "Pose", 0f);
                // Only eyelid openness uses a closing-side union. Other tracking drivers
                // (visemes, gaze, etc.) retain their existing TrackingWeight behavior.
                var eye = field.ActiveLink?.Parent as EyeLinearDriver.Eye;
                int blinkMode = eye != null && eye.OpenCloseTarget == field.ActiveLink
                    ? (eye.ClosedState.Value < eye.OpenState.Value ? 2 : 1) : 0;
                Data(output, "BlinkMode", blinkMode);
                var baseValue = Data(output, "Base", field.Value);
                float initialValue = field.Value;
                Reference<IField<float>>(output, "Target", field);
                // Move the existing blink/viseme driver onto its own proxy; never ForceLink it away.
                if (field.ActiveLink is ISyncRef oldDriver)
                {
                    Reference<ISyncRef>(output, "OriginalDriver", oldDriver);
                    oldDriver.Target = baseValue.Value;
                }
                var result = output.AttachComponent<DynamicField<float>>();
                result.VariableName.Value = Path(OutputSpace, "Result");
                result.TargetField.Target = BuildOutputTarget(field, initialValue);
                _outputSlots[id] = output; fields[field] = output;
            }
            // A partially resolved face must not be presented as a faithfully imported clip.
            if (clip.Curves.Any(c => !_outputSlots.ContainsKey(c.Binding.Id)))
            { UniLog.Warning($"Expression '{clip.Name}' omitted: one or more output bindings were not resolved"); continue; }
            Slot entry = Record(_catalog, clip.Name, ClipSpace);
            Data(entry, "Id", clip.Id); Data(entry, "DisplayName", clip.Name); Data(entry, "Enabled", true);
            Data(entry, "Source", clip.Source ?? "");
            var bindings = entry.AddSlot("Bindings");
            Reference(entry, "Bindings", bindings);
            foreach (var curve in clip.Curves)
            {
                string id = curve.Binding.Id;
                var record = Record(bindings, curve.Binding.Shape, BindingSpace);
                Reference(record, "Output", _outputSlots[id]);
                Data(record, "Value", curve.Keys[^1].Value);
            }
            _clips[clip.Id] = entry;
        }
    }

    private void DescribeGraphs()
    {
        var modules = _root.FindChild("Diagnostics").AddSlot("Graph modules");
        var boards = _root.GetComponentsInChildren<ProtoFluxNode>().GroupBy(n => n.Slot.Parent.Parent).ToArray();
        foreach (var board in boards)
        {
            var names = new Stack<string>();
            for (var slot = board.Key; slot != _root; slot = slot.Parent) names.Push(slot.Name);
            string path = string.Join("/", names);
            var record = Record(modules, path, GraphModuleSpace);
            Data(record, "Path", path);
            Data(record, "NodeCount", board.Count());
        }
        Console.WriteLine($"Expression logic: {boards.Length} independent boards, largest {boards.Max(b => b.Count())} nodes");
    }
}
