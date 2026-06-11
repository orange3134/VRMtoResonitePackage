using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Vrm;
using ColorProfile = Renderite.Shared.ColorProfile;
using TextureFormat = Renderite.Shared.TextureFormat;
using TextureWrapMode = Renderite.Shared.TextureWrapMode;

namespace VrmToResonitePackage;

/// <summary>
/// Post-processes the XiexeToon materials created by the model importer with
/// MToon parameters from the VRM. The mapping follows Resonite's official
/// MToon10XiexeConverter (Resonite.UnitySDK) wherever the data allows.
/// </summary>
internal static class MaterialTuner
{
    public static async Task Apply(Slot root, Slot assetsSlot, VrmModel vrm, string vrmPath)
    {
        int tuned = 0;
        var textureCache = new Dictionary<int, StaticTexture2D>();
        var rampCache = new Dictionary<string, StaticTexture2D>();
        foreach (XiexeToonMaterial material in assetsSlot.GetComponentsInChildren<XiexeToonMaterial>())
        {
            VrmMaterialInfo info = FindInfo(vrm, material.Slot.Name);
            if (info != null)
            {
                await ApplyInfo(material, info, vrmPath, assetsSlot, textureCache, rampCache);
            }
            else
            {
                // No MToon data: a rampless soft shadow is the closest neutral look.
                material.ShadowRamp.Target = null;
                material.ShadowSharpness.Value = 0f;
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

    private static async Task ApplyInfo(XiexeToonMaterial material, VrmMaterialInfo info,
        string vrmPath, Slot assetsSlot,
        Dictionary<int, StaticTexture2D> textureCache, Dictionary<string, StaticTexture2D> rampCache)
    {
        // --- Alpha handling (MToon spec: explicit ZWrite + render queue) ---
        switch (info.AlphaMode)
        {
            case "blend":
                material.BlendMode.Value = BlendMode.Alpha;
                material.ZWrite.Value = info.TransparentWithZWrite ? ZWrite.On : ZWrite.Off;
                break;
            case "mask":
                material.BlendMode.Value = BlendMode.Cutout;
                material.AlphaClip.Value = info.AlphaCutoff;
                material.ZWrite.Value = ZWrite.On;
                break;
            default:
                material.BlendMode.Value = BlendMode.Opaque;
                material.ZWrite.Value = ZWrite.On;
                break;
        }
        material.RenderQueue.Value = ComputeRenderQueue(info);

        if (info.DoubleSided)
        {
            material.Culling.Value = Culling.Off;
        }

        // --- Lit / base color ---
        // The standard glTF import only reads pbrMetallicRoughness.baseColorFactor,
        // which VRM0 MToon leaves white while storing the real lit color in the
        // material's _Color vector property (parsed into info.BaseColor). Assign it
        // explicitly so the XiexeToon main Color matches MToon's LitColor.
        if (info.BaseColor.HasValue)
        {
            System.Numerics.Vector4 b = info.BaseColor.Value;
            material.Color.Value = new colorX(b.X, b.Y, b.Z, b.W, ColorProfile.Linear);
        }

        // --- Neutral PBR-ish features MToon doesn't have ---
        material.Saturation.Value = 1f;
        material.Metallic.Value = 0f;
        material.Glossiness.Value = 0f;
        material.Reflectivity.Value = 0f;
        material.SpecularIntensity.Value = 0f;
        material.SpecularArea.Value = 0.5f;
        material.UseVertexColors.Value = false;

        // --- Rim lighting ---
        // MToon: pow(saturate(1 - N·V + lift), power) — a smooth power falloff.
        // XiexeToon: smoothstep(range - sharpness, range + sharpness, 1 - N·V),
        // with RimThreshold being an N·L exponent (lit-side gating) MToon doesn't have.
        // Center the smoothstep band on MToon's half-intensity point and match the
        // falloff slope there so the rim width and softness line up.
        System.Numerics.Vector4 rim = info.RimColor;
        float fresnelPower = MathX.Max(info.RimFresnelPower, 0.001f);
        float halfPoint = MathF.Pow(0.5f, 1f / fresnelPower);
        float slope = fresnelPower * MathF.Pow(0.5f, (fresnelPower - 1f) / fresnelPower);
        material.RimColor.Value = new colorX(rim.X, rim.Y, rim.Z, 1f);
        material.RimAlbedoTint.Value = 0f;
        material.RimAttenuationEffect.Value = info.RimLightingMix;
        material.RimIntensity.Value = MathX.Max(rim.X, rim.Y, rim.Z);
        material.RimRange.Value = MathX.Clamp01(halfPoint - info.RimLift);
        material.RimThreshold.Value = 0f;
        material.RimSharpness.Value = MathX.Clamp(0.75f / slope, 0.01f, 1f);

        // --- Emission ---
        if (info.EmissionColor.HasValue)
        {
            System.Numerics.Vector4 e = info.EmissionColor.Value;
            if (e.X + e.Y + e.Z > 0.001f)
            {
                material.EmissionColor.Value = new colorX(e.X, e.Y, e.Z, 1f, ColorProfile.Linear);
            }
        }

        // --- Outline ---
        if (info.OutlineWidth > 0.00001f && info.OutlineWidthMode != "none")
        {
            bool lit = info.OutlineLightingMix >= 0.5f;
            material.Outline.Value = lit
                ? XiexeToonMaterial.OutlineStyle.Lit
                : XiexeToonMaterial.OutlineStyle.Emissive;
            material.OutlineAlbedoTint.Value = lit;
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
                StaticTexture2D mask = await GetOrImportTexture(assetsSlot, vrmPath,
                    info.OutlineWidthImageIndex.Value, textureCache, "OutlineMask");
                if (mask != null)
                {
                    material.OutlineMask.Target = mask;
                }
            }
        }
        else
        {
            material.Outline.Value = XiexeToonMaterial.OutlineStyle.None;
        }

        // --- Shadow ramp generated from the MToon shading parameters ---
        if (info.IsMToon)
        {
            StaticTexture2D ramp = await GetOrGenerateShadowRamp(assetsSlot, info, rampCache);
            material.ShadowRamp.Target = ramp;
            if (info.ShadingShiftImageIndex.HasValue)
            {
                StaticTexture2D shiftMask = await GetOrImportTexture(assetsSlot, vrmPath,
                    info.ShadingShiftImageIndex.Value, textureCache, "ShadingShiftMask");
                if (shiftMask != null)
                {
                    material.ShadowRampMask.Target = shiftMask;
                }
            }
            material.ShadowRim.Value = colorX.White;
            material.ShadowSharpness.Value = 0f;
            material.ShadowRimRange.Value = 0.7f;
            material.ShadowRimThreshold.Value = 0.1f;
            material.ShadowRimSharpness.Value = 0.3f;
            material.ShadowRimAlbedoTint.Value = 0f;
        }
        else
        {
            material.ShadowRamp.Target = null;
            material.ShadowSharpness.Value = 0f;
        }

        // --- Matcap ---
        if (info.MatcapImageIndex.HasValue)
        {
            StaticTexture2D matcap = await GetOrImportTexture(assetsSlot, vrmPath,
                info.MatcapImageIndex.Value, textureCache, "Matcap");
            if (matcap != null)
            {
                material.Matcap.Target = matcap;
                System.Numerics.Vector4 tint = info.MatcapColor;
                material.MatcapTint.Value = new colorX(tint.X, tint.Y, tint.Z, 1f);
            }
        }
    }

    /// <summary>Unity render queue, following the MToon 1.0 queue layout.</summary>
    private static int ComputeRenderQueue(VrmMaterialInfo info)
    {
        if (info.RenderQueue.HasValue)
        {
            return info.RenderQueue.Value; // VRM0 stores the queue explicitly
        }
        return info.AlphaMode switch
        {
            "blend" when info.TransparentWithZWrite => 2501 + Math.Clamp(info.RenderQueueOffset, 0, 9),
            "blend" => 3000 + Math.Clamp(info.RenderQueueOffset, -9, 0),
            "mask" => 2450,
            _ => 2000,
        };
    }

    // ---------------------------------------------------------------- shadow ramp

    /// <summary>
    /// Bakes the MToon shading band (shadeColor, shadingShift, shadingToony) into a
    /// 256x256 ramp texture, the same way Resonite's official MToon converter does.
    /// X = remapped N·L, Y = shading shift mask value.
    /// </summary>
    private static async Task<StaticTexture2D> GetOrGenerateShadowRamp(Slot assetsSlot,
        VrmMaterialInfo info, Dictionary<string, StaticTexture2D> cache)
    {
        System.Numerics.Vector4 baseColor = info.BaseColor ?? new(1f, 1f, 1f, 1f);
        System.Numerics.Vector4 shadeColor = info.ShadeColor ?? new(1f, 1f, 1f, 1f);
        float texScale = info.ShadingShiftImageIndex.HasValue ? info.ShadingShiftTextureScale : 0f;

        string key = FormattableString.Invariant(
            $"{shadeColor.X:F4},{shadeColor.Y:F4},{shadeColor.Z:F4}/{baseColor.X:F4},{baseColor.Y:F4},{baseColor.Z:F4}/{info.ShadingToony:F4}/{info.ShadingShift:F4}/{texScale:F4}");
        if (cache.TryGetValue(key, out StaticTexture2D cached))
        {
            return cached;
        }

        Engine engine = assetsSlot.Engine;
        await default(ToBackground);
        const int rampSize = 256;
        var bitmap = new Bitmap2D(rampSize, rampSize, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);
        var shadowMultiplier = new color(
            SafeColorRatio(shadeColor.X, baseColor.X),
            SafeColorRatio(shadeColor.Y, baseColor.Y),
            SafeColorRatio(shadeColor.Z, baseColor.Z),
            1f);
        float shadeMin = -1f + info.ShadingToony;
        float shadeMax = 1f - info.ShadingToony;
        float shadeRange = MathX.Max(shadeMax - shadeMin, 0.0001f);
        for (int y = 0; y < rampSize; y++)
        {
            float mask = y / (rampSize - 1f);
            float effectiveShift = info.ShadingShift + mask * texScale;
            for (int x = 0; x < rampSize; x++)
            {
                float dotNL = MathX.Lerp(-1f, 1f, x / (rampSize - 1f));
                float lit = MathX.Clamp01((dotNL + effectiveShift - shadeMin) / shadeRange);
                var pixel = new color(
                    MathX.Lerp(shadowMultiplier.r, 1f, lit),
                    MathX.Lerp(shadowMultiplier.g, 1f, lit),
                    MathX.Lerp(shadowMultiplier.b, 1f, lit),
                    1f);
                bitmap.SetPixel(x, y, in pixel);
            }
        }
        Uri uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        await default(ToWorld);

        Slot rampSlot = assetsSlot.AddSlot($"ShadowRamp {info.Name}");
        StaticTexture2D texture = rampSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        texture.Uncompressed.Value = true;
        texture.WrapMode = TextureWrapMode.Clamp;
        cache[key] = texture;
        return texture;
    }

    private static float SafeColorRatio(float numerator, float denominator)
    {
        if (denominator <= 0.0001f)
        {
            return MathX.Clamp01(numerator);
        }
        return MathX.Clamp01(numerator / denominator);
    }

    // ---------------------------------------------------------------- texture import

    /// <summary>
    /// Imports an embedded GLB image as a texture asset. Used for MToon-only textures
    /// (outline width mask, matcap, shading shift mask) that the regular model import
    /// never touches.
    /// </summary>
    private static async Task<StaticTexture2D> GetOrImportTexture(Slot assetsSlot, string vrmPath,
        int imageIndex, Dictionary<int, StaticTexture2D> cache, string label)
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
            UniLog.Warning($"テクスチャの取り込みに失敗しました ({label}, image {imageIndex}): {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null)
        {
            return null;
        }
        Slot textureSlot = assetsSlot.AddSlot($"{label} {imageIndex}");
        StaticTexture2D texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        cache[imageIndex] = texture;
        return texture;
    }
}
