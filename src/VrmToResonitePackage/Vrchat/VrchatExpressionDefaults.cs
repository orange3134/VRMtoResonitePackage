using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Constant FX base-layer values underlying the imported face expressions.</summary>
internal static class VrchatExpressionDefaults
{
    public static Dictionary<ExpressionBinding, float> Read(YamlNode state, YamlNode clip)
    {
        var result = new Dictionary<ExpressionBinding, float>();
        if (state == null || clip == null || state["m_TimeParameterActive"]?.AsBool() == true) return result;
        var rejected = new HashSet<ExpressionBinding>();
        foreach (var curve in clip["m_FloatCurves"]?.Seq ?? new())
        {
            string attribute = curve["attribute"]?.AsString(), path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || path == null ||
                attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true) continue;
            var binding = new ExpressionBinding(path, attribute["blendShape.".Length..]);
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            float value = keys?.FirstOrDefault()?["value"]?.AsFloat() ?? float.NaN;
            bool constant = float.IsFinite(value) && keys.All(k => k["value"]?.AsFloat() == value);
            if (constant)
                for (int i = 0; i + 1 < keys.Count; i++)
                    constant &= (keys[i]["outSlope"]?.AsFloat() ?? 0) == 0 &&
                        (keys[i + 1]["inSlope"]?.AsFloat() ?? 0) == 0;
            if (!constant || !result.TryAdd(binding, value / 100f)) rejected.Add(binding);
        }
        foreach (var binding in rejected) result.Remove(binding);
        return result;
    }
}
