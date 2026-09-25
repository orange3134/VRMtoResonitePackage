using Elements.Core;
using VrmToResonitePackage.Expressions;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Resolves the face bindings, finalizes the first FaceEmo pattern, and extracts authored differences.</summary>
internal static class VrchatExpressionDetection
{
    private static readonly string[] Visemes = { "sil", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "aa", "e", "ih", "oh", "ou" };
    internal static bool IsViseme(string name) => Visemes.Any(v =>
        name.Contains("vrc.v_" + v, StringComparison.OrdinalIgnoreCase));
    public static void FilterFaceCurves(ExpressionModel model, IReadOnlyDictionary<ExpressionBinding, float> values)
    {
        if (model.DetectedExpressions == null) return;
        foreach (var clip in model.Clips) clip.Curves.RemoveAll(c => !values.ContainsKey(c.Binding));
        if (model.ImportedPatterns != null) VrchatFaceEmoExpressionImporter.SelectFirstSet(model, values);
        var candidates = model.DetectedExpressions.Select(c => c.Id).ToHashSet();
        model.DetectedExpressions.Clear();
        foreach (var clip in model.Clips)
        {
            if (!candidates.Contains(clip.Id)) continue;
            var difference = new ExpressionClip { Id = clip.Id, Name = clip.Name, Source = clip.Source,
                Duration = clip.Duration, Loop = clip.Loop };
            difference.Curves.AddRange(clip.Curves.Where(c => c.Keys.Any(k => !Approximately(k.Value, values[c.Binding]))));
            if (difference.Curves.Count > 0) model.DetectedExpressions.Add(difference);
        }
        string message = $"Face expression detection: {model.DetectedExpressions.Count} candidates with non-viseme face differences; " +
            "first FaceEmo pattern selected; authored baseline retained for pose resets.";
        model.Diagnostics.Add(message); UniLog.Log(message);
    }

    // Unity Mathf.Approximately, applied in the authored 0..100 weight range.
    internal static bool Approximately(float a, float b) =>
        MathF.Abs((a - b) * 100) < MathF.Max(0.000001f * MathF.Max(MathF.Abs(a * 100), MathF.Abs(b * 100)), float.Epsilon * 8);
}
