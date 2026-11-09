using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage.Expressions;

internal static class VrmExpressionAdapter
{
    public static Task<Slot> BuildAsync(Slot avatar, VrmModel vrm, BlendshapeResolver resolver, bool menu)
    {
        var model = new ExpressionModel();
        var fields = new Dictionary<ExpressionBinding, IField<float>>();
        var identities = new Dictionary<IField<float>, ExpressionBinding>();
        var presets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expression in vrm.Expressions)
        {
            if (expression.Preset is "aa" or "ih" or "ou" or "ee" or "oh" or "blink" or "blinkLeft" or "blinkRight"
                or "lookUp" or "lookDown" or "lookLeft" or "lookRight") continue;
            // VRM0 has no surprised preset; exporters store it as a named custom expression.
            // Recognize this compatibility name for the menu icon only, not gesture mapping.
            string iconPreset = !expression.IsCustom ? expression.Preset :
                vrm.SpecVersionMajor == 0 && string.Equals(expression.Name, "Surprised", StringComparison.OrdinalIgnoreCase)
                    ? "surprised" : null;
            var clip = new ExpressionClip { Id = "vrm:" + model.Clips.Count + ":" + expression.Preset,
                Name = expression.Name ?? expression.Preset ?? "Expression", Duration = 1, Source = "VRM",
                MenuIconUrl = ExpressionMenuSetup.PresetIconUrl(iconPreset) };
            var weights = new Dictionary<IField<float>, float>();
            foreach (var bind in expression.Binds)
            {
                var field = resolver.Resolve(bind);
                if (field == null) continue;
                if (!identities.TryGetValue(field, out var binding))
                {
                    binding = new("VRM/" + bind.MeshIndex, bind.MorphIndex.ToString());
                    identities[field] = binding; fields[binding] = field;
                }
                weights[field] = Math.Max(weights.GetValueOrDefault(field), bind.Weight);
            }
            foreach (var pair in weights)
            {
                var curve = new ExpressionCurve { Binding = identities[pair.Key] };
                curve.Keys.Add(new(0, pair.Value, 0, 0)); clip.Curves.Add(curve);
            }
            if (clip.Curves.Count > 0)
            {
                model.Clips.Add(clip);
                if (!expression.IsCustom && expression.Preset != null) presets.TryAdd(expression.Preset, clip.Id);
            }
        }
        // XWear Packager 0.3.4 VrcAssetTemplate / VRC_FaceLayer_Template:
        // Point=sad, Peace=happy, RockNRoll=angry, Gun=surprised, Thumbs up=relaxed.
        // Right Hand overrides Left Hand; empty motions pass the lower layer through.
        // 8 is our editable extension, with the same empty-motion behavior as 0..2.
        string[] gestures = { null, null, null, "sad", "happy", "angry", "surprised", "relaxed", null };
        model.GestureTable = new string[gestures.Length, gestures.Length];
        string Clip(int gesture) => gestures[gesture] is { } preset ? presets.GetValueOrDefault(preset) : null;
        for (int left = 0; left < gestures.Length; left++)
        for (int right = 0; right < gestures.Length; right++)
            model.GestureTable[left, right] = Clip(right) ?? Clip(left);
        // Mark the neutral preset for the common compiler's baseline-only omission.
        model.GestureTable[0, 0] = presets.GetValueOrDefault("neutral");
        return ExpressionSystemSetup.BuildAsync(avatar, model, binding => fields.GetValueOrDefault(binding), menu, resolver.InitialWeight);
    }
}
