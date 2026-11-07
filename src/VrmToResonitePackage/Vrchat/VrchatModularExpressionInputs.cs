using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Projects supported Merge Animator components from the selected, composed avatar only.</summary>
internal static class VrchatModularExpressionInputs
{
    internal const string MergeAnimatorGuid = "1bb122659f724ebf85fe095ac02dc339";
    internal static YamlNode Create(YamlNode descriptor, IEnumerable<YamlNode> components)
    {
        var result = UnityPropertyOverrides.Clone(descriptor);
        var layers = (result["baseAnimationLayers"]?.Seq ?? new()).ToList();
        var merged = new List<(int Priority, YamlNode Layer)>();
        foreach (var component in components)
        {
            if (component["m_Script"]?.Guid != MergeAnimatorGuid || component["m_Enabled"]?.AsBool() == false ||
                component["layerType"]?.AsInt() != 5 || (component["animator"]?.FileID ?? 0) == 0) continue;
            // Relative paths and Replace need a full Animator merge. Never guess their target paths.
            if (component["pathMode"]?.AsInt() != 1 || (component["mergeAnimatorMode"]?.AsInt() ?? 0) != 0)
            {
                UniLog.Warning("Expressions: MA Merge Animator requires Absolute/Append for expression import; unsupported merge omitted.");
                continue;
            }
            merged.Add((component["layerPriority"]?.AsInt() ?? 0, new YamlNode { Map = new()
            {
                ["type"] = new() { ScalarValue = "5" }, ["isDefault"] = new() { ScalarValue = "0" },
                ["animatorController"] = UnityPropertyOverrides.Clone(component["animator"])
            }
}
));
        }
        if (merged.Count == 0) return descriptor;
        // MA uses ascending priority, with appended zero-priority layers after the base controller.
        result.Map["baseAnimationLayers"] = new() { Seq = merged.Where(x => x.Priority < 0).OrderBy(x => x.Priority).Select(x => x.Layer)
            .Concat(layers).Concat(merged.Where(x => x.Priority >= 0).OrderBy(x => x.Priority).Select(x => x.Layer)).ToList() };
        UniLog.Log($"Expressions: imported {merged.Count} MA Merge Animator FX controller(s) from the selected avatar.");
        return result;
    }
}
