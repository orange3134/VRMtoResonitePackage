using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    internal const string RequestTag = "ResoPon/Expression/v1/Request";
    private readonly ExpressionModel _model;
    private readonly Slot _root, _catalog, _core, _sources, _parameters, _outputs, _layers, _api, _inputs;
    private ExpressionFlux _g;
    private readonly IWorldElement _now, _owner, _isOwner, _coreRef, _sourcesRef, _catalogRef, _parametersRef;
    private readonly Dictionary<string, Slot> _clips = new();
    private readonly Dictionary<string, Slot> _outputSlots = new();
    private readonly List<IWorldElement> _updates = new();
    private readonly List<IWorldElement> _gestureOverrides = new();

    private ExpressionSystemSetup(Slot avatar, ExpressionModel model)
    {
        _model = model;
        _root = Record(avatar, "Expressions");
        _catalog = _root.AddSlot("Catalog");
        _core = Record(_root, "Core");
        _sources = _core.AddSlot("SourceState");
        _parameters = Record(_core, "ParameterState");
        _outputs = _root.AddSlot("Outputs");
        _layers = _root.AddSlot("Rules").AddSlot("ImportedAnimator");
        _inputs = _root.AddSlot("Inputs");
        _api = _root.AddSlot("API").AddSlot("Receivers");
        _g = new(_core.AddSlot("Logic"));
        _coreRef = _g.Ref(_core); _sourcesRef = _g.Ref(_sources); _catalogRef = _g.Ref(_catalog); _parametersRef = _g.Ref(_parameters);
        _now = _g.Node("WorldTimeFloat");
        _owner = _g.Node("GetActiveUser", null, ("Instance", _g.Ref(_root)));
        _isOwner = _g.Node("IsLocalUser", null, ("User", _owner));
        Reference<User>(_core, "PreviousOwner", null);
        Data(_core, "Generation", 1); Data(_core, "Order", 0); Data(_core, "LastError", "");
        Data(_root, "Version", 1);
        Reference(_root, "Receiver", _api);
        Reference(_root, "Catalog", _catalog);
        foreach (var parameter in model.Parameters.Values)
        {
            Data(_parameters, "Value/" + parameter.Name, parameter.Default);
            Data(_parameters, "Default/" + parameter.Name, parameter.Default);
            Data(_parameters, "Type/" + parameter.Name, parameter.Type);
            Data(_parameters, "Saved/" + parameter.Name, parameter.Saved);
        }
        _root.AddSlot("Diagnostics");
    }

    public static async Task<Slot> BuildAsync(Slot avatar, ExpressionModel model, Func<ExpressionBinding, IField<float>> resolve,
        bool menu = true, Func<IField<float>, float?> initialWeight = null)
    {
        if (model.Clips.Count == 0) return null;
        var setup = new ExpressionSystemSetup(avatar, model);
        await setup.BuildCatalog(resolve, initialWeight);
        foreach (var layer in model.Layers.Where(l => l.States.Any(s => s.ClipId != null && !setup._clips.ContainsKey(s.ClipId))).ToArray())
        {
            string message = layer.Name + ": automatic layer omitted because a required clip has unresolved bindings";
            model.Diagnostics.Add(message); UniLog.Warning("Expressions: " + message); model.Layers.Remove(layer);
        }
        setup.BuildApi();
        setup.BuildInputs(menu);
        setup.BuildParameterEvaluation();
        setup._updates.AddRange(setup._gestureOverrides);
        setup.BuildLayers();
        setup.BuildMixer();
        setup.BuildLifecycle();
        foreach (string message in model.Diagnostics) Data(Record(setup._root.FindChild("Diagnostics"), "Import warning"), "Message", message);
        if (setup._clips.Count > 0)
        {
            var template = setup._clips.Values.First().Duplicate(setup._root.FindChild("API").AddSlot("Templates"));
            template.Name = "Expression (copy into Catalog)";
            template.GetComponents<DynamicValueVariable<string>>().Single(v => v.VariableName.Value == "Expr/Id").Value.Value = "";
            template.GetComponents<DynamicValueVariable<bool>>().Single(v => v.VariableName.Value == "Expr/Enabled").Value.Value = false;
        }
        Console.WriteLine($"Expression system: {setup._clips.Count} clips, {setup._outputSlots.Count} outputs, {setup._layers.Children.Count} imported layers");
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
                Data(output, "Snapshot", field.Value); Data(output, "FadeStart", 0f);
                Data(output, "FadeDuration", 0.1f);
                Reference<Slot>(output, "LastSource", null); Reference<Slot>(output, "LastExpression", null);
                Data(output, "LastOrder", 0);
                _outputSlots[id] = output; fields[field] = output;
            }
            // A partially resolved face must not be presented as a faithfully imported clip.
            if (clip.Curves.Any(c => !_outputSlots.ContainsKey(ExpressionAnimationConverter.BindingId(c.Binding))))
            { UniLog.Warning($"Expression '{clip.Name}' omitted: one or more output bindings were not resolved"); continue; }
            Slot entry = Record(_catalog, clip.Name);
            Data(entry, "Id", clip.Id); Data(entry, "DisplayName", clip.Name); Data(entry, "Enabled", true);
            Data(entry, "Loop", clip.Loop); Data(entry, "Duration", Math.Max(0.001f, clip.Duration));
            Data(entry, "FadeIn", 0.1f); Data(entry, "FadeOut", 0.1f); Data(entry, "FullFace", true);
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

    private Slot Source(string name, Slot owner, string channel, int kind = 0, float priority = 100, float lease = 0)
    {
        var source = Record(_sources, name);
        Reference(source, "Owner", owner); Reference<Slot>(source, "Selected", null);
        Data(source, "Enabled", true); Data(source, "Channel", channel); Data(source, "Kind", kind);
        Data(source, "Priority", priority); Data(source, "Lease", lease); Data(source, "Alive", 0f);
        Data(source, "Sequence", 0); Data(source, "SelectionToken", 0); Data(source, "Order", 0);
        Data(source, "Start", 0f); Data(source, "Weight", 1f); Data(source, "Mode", "Hold");
        Data(source, "Gesture", 0); Data(source, "GestureWeight", 0f);
        foreach (var parameter in _model.Parameters.Values)
        {
            Data(source, "Has/" + parameter.Name, false); Data(source, "Value/" + parameter.Name, parameter.Default);
            Data(source, "Until/" + parameter.Name, 0f); Data(source, "ParameterOrder/" + parameter.Name, 0);
        }
        return source;
    }
    private IWorldElement ValidSource(IWorldElement source) => _g.And(_g.Active(source), _g.Read<bool>(source, "Enabled"),
        _g.Active(_g.Read<Slot>(source, "Owner")), _g.Or(_g.Equal<float>(_g.Read<float>(source, "Lease"), _g.Constant(0f)),
            _g.Greater(_g.Add(_g.Read<float>(source, "Alive"), _g.Read<float>(source, "Lease")), _now)));
    private IWorldElement ClipAsset(IWorldElement expression) => _g.Node("GetAsset", typeof(Animation),
        ("Provider", _g.Read<IAssetProvider<Animation>>(expression, "Clip")));
    private IWorldElement ValidExpression(IWorldElement expression) => _g.And(_g.Active(expression), _g.Read<bool>(expression, "Enabled"),
        _g.Node("NotNull", typeof(Animation), ("Instance", ClipAsset(expression))));
    private IWorldElement SampleTime(IWorldElement expression, IWorldElement elapsed) => _g.Choose<float>(
        _g.Read<bool>(expression, "Loop"), _g.Binary<float>("ValueMod", elapsed, _g.Read<float>(expression, "Duration")), elapsed);
    private (IWorldElement Index, IWorldElement Value) Sample(IWorldElement expression, IWorldElement property, IWorldElement elapsed)
    {
        var asset = ClipAsset(expression);
        var index = _g.Node("FindAnimationTrackIndex", null, ("Animation", asset), ("Node", _g.Text("Expression")), ("Property", property));
        var value = _g.Node("SampleValueAnimationTrack", typeof(float), ("Animation", asset), ("TrackIndex", index), ("Time", SampleTime(expression, elapsed)));
        return (index, value);
    }
}
