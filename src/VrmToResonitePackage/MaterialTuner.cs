using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Post-processes the XiexeToon materials created by the model importer with
/// MToon parameters from the VRM (blend mode, cutoff, outlines, double-sidedness).
/// </summary>
internal static class MaterialTuner
{
    public static async Task Apply(Slot root, Slot assetsSlot, VrmModel vrm, string vrmPath)
    {
        int tuned = 0;
        var outlineMaskCache = new Dictionary<int, StaticTexture2D>();
        foreach (XiexeToonMaterial material in assetsSlot.GetComponentsInChildren<XiexeToonMaterial>())
        {
            // XiexeToon attaches a default shadow ramp texture on creation; MToon's look
            // is closer to a rampless material with a soft shadow edge.
            material.ShadowRamp.Target = null;
            material.ShadowSharpness.Value = 0f;

            VrmMaterialInfo info = FindInfo(vrm, material.Slot.Name);
            if (info != null)
            {
                await ApplyInfo(material, info, vrm, vrmPath, assetsSlot, outlineMaskCache);
            }
            tuned++;
        }
        UniLog.Log($"マテリアルを {tuned} 件調整しました。");
    }

    private static VrmMaterialInfo FindInfo(VrmModel vrm, string slotName)
    {
        if (string.IsNullOrEmpty(slotName))
        {
            return null;
        }
        // Importer names material slots "Material: <name>".
        const string prefix = "Material: ";
        string name = slotName.StartsWith(prefix, StringComparison.Ordinal)
            ? slotName.Substring(prefix.Length)
            : slotName;
        if (vrm.Materials.TryGetValue(name, out VrmMaterialInfo info))
        {
            return info;
        }
        // Assimp may dedupe names with suffixes like ".001"; try the base name.
        int dot = name.LastIndexOf('.');
        if (dot > 0 && vrm.Materials.TryGetValue(name.Substring(0, dot), out info))
        {
            return info;
        }
        return null;
    }

    private static async Task ApplyInfo(XiexeToonMaterial material, VrmMaterialInfo info, VrmModel vrm,
        string vrmPath, Slot assetsSlot, Dictionary<int, StaticTexture2D> outlineMaskCache)
    {
        switch (info.AlphaMode)
        {
            case "blend":
                material.BlendMode.Value = BlendMode.Alpha;
                // Alpha blending defaults to the transparent queue (3000), which can
                // break sorting badly on converted MToon avatars; the opaque queue
                // (2000) avoids the worst artifacts.
                material.RenderQueue.Value = 2000;
                break;
            case "mask":
                material.BlendMode.Value = BlendMode.Cutout;
                material.AlphaClip.Value = info.AlphaCutoff;
                break;
            default:
                material.BlendMode.Value = BlendMode.Opaque;
                break;
        }

        if (info.DoubleSided)
        {
            material.Culling.Value = Culling.Off;
        }

        if (info.OutlineWidth > 0.00001f && info.OutlineWidthMode != "none")
        {
            material.Outline.Value = XiexeToonMaterial.OutlineStyle.Emissive;
            // XiexeToon (XSToon2.0) extrudes outlines in object space by
            // _OutlineWidth * 0.01, MToon's world mode extrudes by the width in
            // meters — so at model scale 1 the conversion is width_m * 100.
            // (Screen mode has no XSToon equivalent; the same mapping is used.)
            material.OutlineWidth.Value = MathX.Clamp(info.OutlineWidth * 100f, 0f, 5f);
            if (info.OutlineColor.HasValue)
            {
                System.Numerics.Vector4 c = info.OutlineColor.Value;
                material.OutlineColor.Value = new colorX(c.X, c.Y, c.Z, c.W);
            }
            if (info.OutlineWidthImageIndex.HasValue)
            {
                StaticTexture2D mask = await GetOrImportTexture(
                    assetsSlot, vrmPath, info.OutlineWidthImageIndex.Value, outlineMaskCache);
                if (mask != null)
                {
                    material.OutlineMask.Target = mask;
                }
            }
        }

        if (info.EmissionColor.HasValue && material.EmissionMap.Target != null)
        {
            System.Numerics.Vector4 e = info.EmissionColor.Value;
            if (e.X + e.Y + e.Z > 0.001f)
            {
                material.EmissionColor.Value = new colorX(e.X, e.Y, e.Z, 1f);
            }
        }

        if (info.ShadeColor.HasValue)
        {
            // Approximate MToon's shade tint with Xiexe's shadow rim color.
            System.Numerics.Vector4 s = info.ShadeColor.Value;
            material.ShadowRim.Value = new colorX(s.X, s.Y, s.Z, 1f);
        }
    }

    /// <summary>
    /// Imports an embedded GLB image as a texture asset. Used for MToon-only textures
    /// (outline width mask) that the regular model import never touches.
    /// </summary>
    private static async Task<StaticTexture2D> GetOrImportTexture(Slot assetsSlot, string vrmPath,
        int imageIndex, Dictionary<int, StaticTexture2D> cache)
    {
        if (cache.TryGetValue(imageIndex, out StaticTexture2D cached))
        {
            return cached;
        }
        cache[imageIndex] = null;
        Engine engine = assetsSlot.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            (byte[] data, string extension) = VrmParser.ExtractImage(vrmPath, imageIndex);
            string tempFile = engine.LocalDB.GetTempFilePath(extension);
            await File.WriteAllBytesAsync(tempFile, data);
            uri = await engine.LocalDB.ImportLocalAssetAsync(tempFile, LocalDB.ImportLocation.Move);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"アウトラインマスク画像の取り込みに失敗しました (image {imageIndex}): {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null)
        {
            return null;
        }
        Slot textureSlot = assetsSlot.AddSlot($"OutlineMask {imageIndex}");
        StaticTexture2D texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        cache[imageIndex] = texture;
        return texture;
    }
}
