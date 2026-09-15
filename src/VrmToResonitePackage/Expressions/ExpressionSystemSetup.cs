using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    internal const string RequestTag = "ResoPon/Expression/v2/Gesture";
    internal const string SelectTag = "ResoPon/Expression/v2/Select";
    internal const string AutomaticTag = "ResoPon/Expression/v2/Automatic";
    private readonly ExpressionModel _model;
    private readonly Slot _root, _catalog, _core, _outputs, _table, _api, _inputs;
    private readonly Slot _lifecycle, _selection, _playback;
    private const string SelectionTickTag = "ResoPon/Expression/Internal/Selection";
    private const string PlaybackTickTag = "ResoPon/Expression/Internal/Playback";
    private GesturePairCompiler _compiled;
    private readonly Dictionary<string, Slot> _clips = new();
    private readonly Dictionary<string, Slot> _outputSlots = new();

    private ExpressionSystemSetup(Slot avatar, ExpressionModel model)
    {
        _model = model;
        _root = Record(avatar, "Expressions");
        _catalog = _root.AddSlot("Catalog");
        _core = Record(_root, "Core");
        _outputs = _root.AddSlot("Outputs");
        _table = Record(_root, "GestureTable");
        _table.GetComponent<DynamicVariableSpace>().OnlyDirectBinding.Value = false;
        _inputs = _root.AddSlot("Inputs");
        _api = _root.AddSlot("API").AddSlot("Receivers");
        var logic = _core.AddSlot("Logic");
        _lifecycle = logic.AddSlot("Lifecycle");
        _selection = logic.AddSlot("Selection");
        _playback = logic.AddSlot("Playback");
        Reference<User>(_core, "PreviousOwner", null);
        foreach (string hand in new[] { "Left", "Right" })
        {
            Data(_core, hand + "Gesture", 0); Reference<Slot>(_core, hand + "Input", null);
        }
        Reference<Slot>(_core, "Override", null); Reference<Slot>(_core, "CurrentExpression", null);
        Data(_core, "PlaybackStart", 0f); Data(_core, "FadeDuration", 0.1f);
        Data(_core, "PairIndex", 0);
        Reference<Slot>(_core, "MappedExpression", null); Reference<Slot>(_core, "CandidateExpression", null);
        Data(_core, "SelectionStatus", 0); // 0=unassigned, 1=gesture, 2=override, 3=invalid/unloaded
        Data(_core, "PlaybackElapsed", 0f); Data(_core, "FadeWeight", 1f);
        Data(_root, "Version", 2);
        Reference(_root, "Receiver", _api);
        Reference(_root, "Catalog", _catalog);
        _root.AddSlot("Diagnostics");
    }

    public static async Task<Slot> BuildAsync(Slot avatar, ExpressionModel model, Func<ExpressionBinding, IField<float>> resolve,
        bool menu = true, Func<IField<float>, float?> initialWeight = null)
    {
        if (model.Clips.Count == 0) return null;
        var setup = new ExpressionSystemSetup(avatar, model);
        await setup.BuildCatalog(resolve, initialWeight);
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
        foreach (string message in model.Diagnostics) Data(Record(setup._root.FindChild("Diagnostics"), "Import warning"), "Message", message);
        if (setup._clips.Count > 0)
        {
            var template = setup._clips.Values.First().Duplicate(setup._root.FindChild("API").AddSlot("Templates"));
            template.Name = "Expression (copy into Catalog)";
            template.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "Expr/Id").Value.Value = "";
            template.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == "Expr/Enabled").Value.Value = false;
        }
        int assigned = setup._compiled.Pairs.Count(id => id != null && setup._clips.ContainsKey(id));
        Console.WriteLine($"Expression system: {setup._clips.Count} clips, {setup._outputSlots.Count} outputs, {assigned}/64 gesture pairs assigned, " +
            $"{setup._root.GetComponentsInChildren<ProtoFluxNode>().Count} Flux nodes");
        return setup._root;
    }

    private async Task BuildCatalog(Func<ExpressionBinding, IField<float>> resolve, Func<IField<float>, float?> initialWeight)
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
                string id = ExpressionAnimationConverter.BindingId(curve.Binding);
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
                var output = Record(_outputs, curve.Binding.Shape);
                Data(output, "Id", id); Data(output, "Path", curve.Binding.Path); Data(output, "Shape", curve.Binding.Shape);
                Data(output, "Baseline", initialWeight?.Invoke(field) ?? field.Value);
                Data(output, "TrackingWeight", 0f);
                var baseValue = Data(output, "Base", field.Value);
                var result = Data(output, "Result", field.Value);
                Reference<IField<float>>(output, "Target", field);
                // Move the existing blink/viseme driver onto its own proxy; never ForceLink it away.
                if (field.ActiveLink is ISyncRef oldDriver)
                {
                    Reference<ISyncRef>(output, "OriginalDriver", oldDriver);
                    oldDriver.Target = baseValue.Value;
                }
                var writer = output.AttachComponent<ValueCopy<float>>();
                writer.Source.Target = result.Value;
                writer.Target.Target = field;
                Data(output, "Snapshot", field.Value);
                _outputSlots[id] = output; fields[field] = output;
            }
            // A partially resolved face must not be presented as a faithfully imported clip.
            if (clip.Curves.Any(c => !_outputSlots.ContainsKey(ExpressionAnimationConverter.BindingId(c.Binding))))
            { UniLog.Warning($"Expression '{clip.Name}' omitted: one or more output bindings were not resolved"); continue; }
            Slot entry = Record(_catalog, clip.Name);
            Data(entry, "Id", clip.Id); Data(entry, "DisplayName", clip.Name); Data(entry, "Enabled", true);
            Data(entry, "Loop", clip.Loop); Data(entry, "Duration", Math.Max(0.001f, clip.Duration));
            Data(entry, "FadeIn", 0.1f); Data(entry, "FadeOut", 0.1f);
            Data(entry, "Source", clip.Source ?? "");
            var animation = ExpressionAnimationConverter.ConvertClip(clip);
            string temporary = _root.Engine.LocalDB.GetTempFilePath("animx");
            animation.SaveToFile(temporary);
            Uri uri = await _root.Engine.LocalDB.ImportLocalAssetAsync(temporary, LocalDB.ImportLocation.Move);
            await default(ToWorld);
            var provider = entry.AttachComponent<StaticAnimationProvider>();
            provider.URL.Value = uri;
            var clipReference = Reference<IAssetProvider<Animation>>(entry, "Clip", provider);
            var loader = entry.AttachComponent<AssetLoader<Animation>>();
            var loaderReference = entry.AttachComponent<ReferenceCopy<IAssetProvider<Animation>>>();
            loaderReference.Source.Target = clipReference.Reference;
            loaderReference.Target.Target = loader.Asset;
            var bindings = entry.AddSlot("Bindings");
            foreach (var curve in clip.Curves)
            {
                string id = ExpressionAnimationConverter.BindingId(curve.Binding);
                Reference(Record(bindings, curve.Binding.Shape), "Output", _outputSlots[id]);
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
            var record = Record(modules, path);
            Data(record, "Path", path);
            Data(record, "NodeCount", board.Count());
        }
        Console.WriteLine($"Expression logic: {boards.Length} independent boards, largest {boards.Max(b => b.Count())} nodes");
    }
}
