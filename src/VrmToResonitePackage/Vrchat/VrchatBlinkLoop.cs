using System.Text.RegularExpressions;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Recognizes eyelid pulses in an active startup loop, not arbitrary face poses.</summary>
internal sealed class VrchatBlinkLoop
{
    public List<VrchatBlink> Bindings { get; } = new();
    public Dictionary<ExpressionBinding, float> InitialValues { get; } = new();

    // Some avatars only hold a neutral face in their startup loop. A unique,
    // explicitly named binocular blink at zero can opt into native blinking.
    // Never treat arbitrary zero-valued expression helpers as eyelid bindings.
    public static VrchatBlinkLoop ReadNeutral(YamlNode clip)
    {
        if (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() != true) return null;
        var result = new VrchatBlinkLoop();
        foreach (var curve in clip["m_FloatCurves"]?.Seq ?? new())
        {
            string attribute = curve["attribute"]?.AsString(), path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || path == null ||
                attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true) continue;
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            if (keys == null || keys.Count == 0) return null;
            float value = keys[0]["value"]?.AsFloat() ?? float.NaN;
            if (!float.IsFinite(value) || keys.Any(k => (k["value"]?.AsFloat() ?? float.NaN) != value)) return null;
            for (int i = 0; i + 1 < keys.Count; i++)
                if ((keys[i]["outSlope"]?.AsFloat() ?? 0) != 0 || (keys[i + 1]["inSlope"]?.AsFloat() ?? 0) != 0)
                    return null;
            string name = attribute["blendShape.".Length..];
            if (value != 0 || !(name.Equals("blink", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("vrc.blink", StringComparison.OrdinalIgnoreCase) || name is "まばたき" or "瞬き" or "瞬目")) continue;
            result.Bindings.Add(new VrchatBlink { MeshGameObjectPath = path, MeshGameObjectName = path.Split('/').Last(),
                BlendShapeName = name, BlendShapeIndex = -1 });
            if (!result.InitialValues.TryAdd(new(path, name), 0)) return null;
        }
        return result.Bindings.Count == 1 && !HasVisibilityCurves(clip, result.InitialValues.Keys) ? result : null;
    }

    public static VrchatBlinkLoop Read(YamlNode clip)
    {
        if (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() != true) return null;
        var result = new VrchatBlinkLoop();
        var curves = clip["m_FloatCurves"]?.Seq ?? new();
        foreach (var curve in curves)
        {
            string attribute = curve["attribute"]?.AsString(), path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || path == null ||
                attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true) continue;
            string name = attribute["blendShape.".Length..];
            // A loop that also needs an unrelated face reset is not an eyelid-only motion.
            if (!IsBlinkName(name)) return null;
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            if (keys == null || keys.Count == 0) return null;
            var values = keys.Select(k => k["value"]?.AsFloat() ?? float.NaN).ToArray();
            if (values.Any(v => !float.IsFinite(v) || v < 0 || v > 100) ||
                values[0] != 0 || values[^1] != 0) return null;
            if (values.All(v => v == 0))
            {
                // Equal endpoints with nonzero slopes can still move between keys.
                if (keys.Any(k => (k["inSlope"]?.AsFloat() ?? 0) != 0 ||
                    (k["outSlope"]?.AsFloat() ?? 0) != 0)) return null;
            }
            else
            {
                if (values.Max() != 100) return null;
                result.Bindings.Add(new VrchatBlink
                {
                    MeshGameObjectPath = path, MeshGameObjectName = path.Split('/').Last(),
                    BlendShapeName = name, BlendShapeIndex = -1, Preset = SidePreset(name),
                });
            }
            if (!result.InitialValues.TryAdd(new(path, name), 0)) return null;
        }
        if (result.Bindings.Count == 0) return null;
        if (HasVisibilityCurves(clip, result.InitialValues.Keys)) return null;
        // Unlabelled, mixed, or one-sided sets still support ordinary binocular blinking.
        // Split only complete, unambiguous left/right sets (including eyelash helpers).
        if (result.Bindings.Any(b => b.Preset == "blink") ||
            !result.Bindings.Any(b => b.Preset == "blinkLeft") || !result.Bindings.Any(b => b.Preset == "blinkRight"))
            foreach (var binding in result.Bindings) binding.Preset = "blink";
        return result;
    }

    private static bool HasVisibilityCurves(YamlNode clip, IEnumerable<ExpressionBinding> bindings) =>
        (clip["m_FloatCurves"]?.Seq ?? new()).Any(curve =>
        {
            string path = curve["path"]?.AsString(), attribute = curve["attribute"]?.AsString();
            if (path == null) return false;
            int type = curve["classID"]?.AsInt() ?? 0;
            return bindings.Any(b =>
                type == 137 && attribute == "m_Enabled" && path == b.Path ||
                type == 1 && attribute == "m_IsActive" &&
                    (path.Length == 0 || path == b.Path || b.Path.StartsWith(path + "/", StringComparison.Ordinal)));
        });

    private static bool IsBlinkName(string name) =>
        name.Contains("blink", StringComparison.OrdinalIgnoreCase) || name.Contains("まばたき") ||
        name.Contains("瞬き") || name.Contains("瞬目");

    private static string SidePreset(string name)
    {
        bool left = name.Contains('左') || name.Contains("left", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name, @"(?:^|[_. -])L(?:$|[_. -])", RegexOptions.IgnoreCase);
        bool right = name.Contains('右') || name.Contains("right", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name, @"(?:^|[_. -])R(?:$|[_. -])", RegexOptions.IgnoreCase);
        return left == right ? "blink" : left ? "blinkLeft" : "blinkRight";
    }
}
