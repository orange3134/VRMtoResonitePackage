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
        foreach (var expression in vrm.Expressions)
        {
            if (expression.Preset is "aa" or "ih" or "ou" or "ee" or "oh" or "blink" or "blinkLeft" or "blinkRight"
                or "lookUp" or "lookDown" or "lookLeft" or "lookRight") continue;
            var clip = new ExpressionClip { Id = "vrm:" + model.Clips.Count + ":" + expression.Preset,
                Name = expression.Name ?? expression.Preset ?? "Expression", Duration = 1, Source = "VRM" };
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
            if (clip.Curves.Count > 0) model.Clips.Add(clip);
        }
        return ExpressionSystemSetup.BuildAsync(avatar, model, binding => fields.GetValueOrDefault(binding), menu, resolver.InitialWeight);
    }
}
