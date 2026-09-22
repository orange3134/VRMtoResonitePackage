using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

internal static class VrchatExpressionMask
{
    // SDK AV3 Demo Assets/Animation/Masks: these contain humanoid flags and no transform entries.
    private static readonly Dictionary<string, string> SdkMasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b2b8bad9583e56a46a3e21795e96ad92"] = "vrc_HandsOnly",
        ["903ce375d5f609d44b9f00b425d6eda9"] = "vrc_Hand Right",
        ["7ff0199655202a04eb175de45a6e078a"] = "vrc_Hand Left",
    };

    internal static bool IsBlendShapeCompatible(UnityPackage package, YamlNode mask, out string missingSdkMask)
    {
        missingSdkMask = null;
        if ((mask?.FileID ?? 0) == 0) return true;
        var asset = package.ByGuid(mask.Guid);
        if (asset == null)
            return mask.FileID == 31900000 && mask.Guid != null && SdkMasks.TryGetValue(mask.Guid, out missingSdkMask);
        // A packaged override always takes precedence over the SDK identity.
        return asset.HasContent && package.ReadScene(asset).Doc(mask.FileID.Value) is { ClassId: 319 } doc &&
            doc.Root["m_Elements"]?.Seq is { Count: 0 };
    }
}