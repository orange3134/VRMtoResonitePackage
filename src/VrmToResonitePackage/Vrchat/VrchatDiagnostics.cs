using Elements.Core;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

internal static class VrchatDiagnostics
{
    public static void LogPackageSummary(UnityPackage package, string packagePath)
    {
        if (package == null)
        {
            return;
        }

        long packageSize = File.Exists(packagePath) ? new FileInfo(packagePath).Length : 0;
        int contentCount = package.Assets.Values.Count(a => a.HasContent);
        int metaCount = package.Assets.Values.Count(a => !string.IsNullOrEmpty(a.MetaPath) && File.Exists(a.MetaPath));
        UniLog.Log("VRChat diagnostic: package summary");
        UniLog.Log($"  file={Path.GetFileName(packagePath)}, bytes={packageSize}, assets={package.Assets.Count}, " +
                   $"content={contentCount}, meta={metaCount}");

        string extensionSummary = string.Join(", ",
            package.Assets.Values
                .GroupBy(a => string.IsNullOrEmpty(a.Extension) ? "(none)" : a.Extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .Select(g => $"{g.Key}:{g.Count()}"));
        UniLog.Log($"  extensions={extensionSummary}");

        LogAssetSample(package, ".prefab", 20);
        LogAssetSample(package, ".unity", 10);
        LogAssetSample(package, ".fbx", 20);
        LogAssetSample(package, ".mat", 20);
        LogAssetSample(package, ".controller", 10);
    }

    public static void LogAvatarSummary(UnityPackage package, VrchatAvatar avatar, string stage)
    {
        if (avatar == null)
        {
            return;
        }

        UnityAsset primaryFbx = package?.ByGuid(avatar.FbxGuid);
        UniLog.Log($"VRChat diagnostic: avatar summary ({stage})");
        UniLog.Log($"  name={avatar.Name ?? "(null)"}, prefab={avatar.PrefabPath ?? "(null)"}");
        UniLog.Log($"  primaryFbx={AssetLabel(primaryFbx, avatar.FbxGuid)}, instance={avatar.FbxInstanceName ?? "(null)"}, " +
                   $"scale={avatar.FbxImportScale:G6}, up={avatar.FbxUpAxis}");
        UniLog.Log($"  counts: humanBones={avatar.HumanBones.Count}, visemes={avatar.Visemes.Count}, " +
                   $"physBones={avatar.PhysBones.Count}, rendererMaterials={avatar.RendererMaterials.Count}, " +
                   $"prefabGameObjects={avatar.PrefabGameObjectNames.Count}, inactiveGameObjects={avatar.InactiveGameObjects.Count}, " +
                   $"additionalFbxs={avatar.AdditionalFbxs.Count}, fbxMaterialMappings={avatar.FbxMaterialGuids.Count}");

        if (avatar.AdditionalFbxs.Count > 0)
        {
            foreach (VrchatFbxAsset additional in avatar.AdditionalFbxs.Take(20))
            {
                UnityAsset asset = package?.ByGuid(additional.Guid);
                UniLog.Log($"  additionalFbx: {AssetLabel(asset, additional.Guid)}, instance={additional.InstanceName ?? "(null)"}, " +
                           $"parent={ShortGuid(additional.ParentFbxGuid) ?? "(root)"}/{additional.ParentNodeName ?? "-"}, " +
                           $"scale={additional.ImportScale:G6}, materials={additional.MaterialGuids.Count}");
            }
            if (avatar.AdditionalFbxs.Count > 20)
            {
                UniLog.Log($"  additionalFbx: ... {avatar.AdditionalFbxs.Count - 20} more");
            }
        }

        LogRendererMaterialSample(package, avatar);
        LogBlendShapeSample(avatar);
        LogPhysBoneSample(avatar);
    }

    public static void LogFailureDiagnostics(UnityPackage package, VrchatAvatar avatar, Exception exception)
    {
        UniLog.Warning($"VRChat diagnostic: conversion failed at {exception.GetType().Name}: {exception.Message}");
        try
        {
            if (package != null)
            {
                DiagnoseCandidateFiles(package);
            }
            LogAvatarSummary(package, avatar, "failure");
        }
        catch (Exception diagnosticException)
        {
            UniLog.Warning($"VRChat diagnostic logging failed: {diagnosticException.GetType().Name}: {diagnosticException.Message}");
        }
    }

    public static void DiagnoseCandidateFiles(UnityPackage package)
    {
        UniLog.Warning("VRChat diagnostic: candidate files");
        VrchatAvatarParser.DiagnoseCandidates(package);
    }

    private static void LogAssetSample(UnityPackage package, string extension, int limit)
    {
        List<UnityAsset> assets = package.ByExtension(extension)
            .OrderBy(a => a.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToList();
        if (assets.Count == 0)
        {
            return;
        }

        UniLog.Log($"  {extension} assets:");
        foreach (UnityAsset asset in assets.Take(limit))
        {
            UniLog.Log($"    {asset.LogicalPath} guid={ShortGuid(asset.Guid)}, content={asset.HasContent}, meta={HasMeta(asset)}");
        }
        if (assets.Count > limit)
        {
            int total = package.ByExtension(extension).Count();
            UniLog.Log($"    ... {total - limit} more");
        }
    }

    private static void LogRendererMaterialSample(UnityPackage package, VrchatAvatar avatar)
    {
        if (avatar.RendererMaterials.Count == 0)
        {
            return;
        }

        UniLog.Log("  renderer materials:");
        foreach (VrchatRendererMaterials renderer in avatar.RendererMaterials.Take(30))
        {
            string materials = string.Join(", ", renderer.MaterialGuids.Select(g => AssetLabel(package?.ByGuid(g), g)));
            UniLog.Log($"    {renderer.RendererGameObjectName}: [{materials}], initialBlendShapes={renderer.InitialBlendShapes.Count}");
        }
        if (avatar.RendererMaterials.Count > 30)
        {
            UniLog.Log($"    ... {avatar.RendererMaterials.Count - 30} more");
        }
    }

    private static void LogBlendShapeSample(VrchatAvatar avatar)
    {
        if (avatar.FbxBlendShapeNames.Count == 0)
        {
            return;
        }

        UniLog.Log("  FBX blendshape sources:");
        foreach ((string renderer, IReadOnlyList<string> names) in avatar.FbxBlendShapeNames.Take(20))
        {
            UniLog.Log($"    {renderer}: count={names.Count}, first=[{string.Join(", ", names.Take(12))}]");
        }
        if (avatar.FbxBlendShapeNames.Count > 20)
        {
            UniLog.Log($"    ... {avatar.FbxBlendShapeNames.Count - 20} more");
        }
    }

    private static void LogPhysBoneSample(VrchatAvatar avatar)
    {
        if (avatar.PhysBones.Count == 0)
        {
            return;
        }

        UniLog.Log("  PhysBones:");
        foreach (VrchatPhysBone bone in avatar.PhysBones.Take(20))
        {
            UniLog.Log($"    root={bone.RootBoneName ?? "(null)"}, ignored={bone.IgnoreBoneNames.Count}, " +
                       $"colliders={bone.Colliders.Count}, radius={bone.Radius:G6}");
        }
        if (avatar.PhysBones.Count > 20)
        {
            UniLog.Log($"    ... {avatar.PhysBones.Count - 20} more");
        }
    }

    private static string AssetLabel(UnityAsset asset, string guid)
    {
        if (asset != null)
        {
            return $"{asset.LogicalPath}({ShortGuid(asset.Guid)})";
        }
        return string.IsNullOrEmpty(guid) ? "(null)" : $"{ShortGuid(guid)}(missing)";
    }

    private static string ShortGuid(string guid)
        => string.IsNullOrEmpty(guid) ? null : guid.Length <= 8 ? guid : guid[..8];

    private static bool HasMeta(UnityAsset asset)
        => !string.IsNullOrEmpty(asset.MetaPath) && File.Exists(asset.MetaPath);
}
