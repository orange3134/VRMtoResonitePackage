using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Unity;
using ColorProfile = Renderite.Shared.ColorProfile;
using TextureFormat = Renderite.Shared.TextureFormat;
using TextureWrapMode = Renderite.Shared.TextureWrapMode;
using Vec4 = System.Numerics.Vector4;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Builds XiexeToon materials from the avatar's liltoon .mat files (textures + tone parameters)
/// and assigns them to the imported renderers per the prefab's material slot order. The FBX import
/// only produces bare materials (Unity FBX avatars keep materials/textures in separate files), so
/// these are created here rather than tuned in place. The conversion targets the same XiexeToon
/// look the VRM path produces.
/// </summary>
internal static class VrchatMaterialBuilder
{
    public static async Task Apply(Slot root, Slot assetsSlot, VrchatAvatar avatar, UnityPackage package)
    {
        var textureCache = new Dictionary<string, StaticTexture2D>(StringComparer.OrdinalIgnoreCase);
        var materialCache = new Dictionary<string, XiexeToonMaterial>(StringComparer.OrdinalIgnoreCase);

        // Build one XiexeToon material per unique liltoon .mat referenced by the avatar.
        IEnumerable<string> uniqueGuids = avatar.RendererMaterials
            .SelectMany(r => r.MaterialGuids)
            .Concat(avatar.FbxMaterialGuids.Values)
            .Concat(avatar.AdditionalFbxs.SelectMany(f => f.MaterialGuids.Values))
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string guid in uniqueGuids)
        {
            XiexeToonMaterial material = await BuildMaterial(assetsSlot, guid, package, textureCache);
            if (material != null)
            {
                materialCache[guid] = material;
            }
            else
            {
                UnityAsset asset = package.ByGuid(guid);
                UniLog.Warning($"Could not convert referenced Unity material: " +
                               $"{asset?.LogicalPath ?? guid} (guid {guid}).");
            }
        }

        // FBX prefab variants keep their renderer hierarchy as stripped objects, so the prefab
        // doesn't contain normal SkinnedMeshRenderer documents to read. ModelImporter mappings
        // associate embedded FBX material names with Unity .mat assets; replace the imported
        // placeholder materials by that mapping before applying per-renderer prefab overrides.
        int assigned = 0;
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            Dictionary<string, string> fbxMaterialGuids = FbxMaterialGuidsForRenderer(root, renderer, avatar);
            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                IAssetProvider<FrooxEngine.Material> placeholder = renderer.Materials[i];
                string name = MaterialName(placeholder?.Slot?.Name);
                if (name != null &&
                    fbxMaterialGuids.TryGetValue(name, out string guid) &&
                    materialCache.TryGetValue(guid, out XiexeToonMaterial material))
                {
                    renderer.Materials[i] = material;
                    assigned++;
                }
            }
        }

        // Composed FBXs can contain identically named renderers. Keep variant overrides on the
        // model they came from by matching both the slot name and its owning FBX.
        List<MeshRenderer> renderers = root.GetComponentsInChildren<MeshRenderer>().ToList();

        foreach (VrchatRendererMaterials rm in avatar.RendererMaterials)
        {
            MeshRenderer renderer = renderers.FirstOrDefault(candidate =>
                string.Equals(candidate.Slot.Name, rm.RendererGameObjectName, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(rm.FbxGuid) || string.Equals(
                    VrchatSceneSetup.FbxGuidForSlot(root, candidate.Slot, avatar), rm.FbxGuid,
                    StringComparison.OrdinalIgnoreCase)));
            if (renderer == null)
            {
                continue;
            }
            for (int i = 0; i < rm.MaterialGuids.Count; i++)
            {
                string guid = rm.MaterialGuids[i];
                if (guid == null || !materialCache.TryGetValue(guid, out XiexeToonMaterial material))
                {
                    continue;
                }
                while (renderer.Materials.Count <= i)
                {
                    renderer.Materials.Add();
                }
                renderer.Materials[i] = material;
                assigned++;
            }
        }
        UniLog.Log($"liltoonマテリアルを {materialCache.Count} 件生成し、{assigned} スロットに割り当てました。");

        // The FBX import created bare placeholder materials; drop the ones no renderer references now.
        var referenced = new HashSet<RefID>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            foreach (IAssetProvider<FrooxEngine.Material> material in renderer.Materials)
            {
                if (material != null)
                {
                    referenced.Add(material.ReferenceID);
                }
            }
        }
        int removed = 0;
        foreach (XiexeToonMaterial material in root.GetComponentsInChildren<XiexeToonMaterial>())
        {
            if (!referenced.Contains(material.ReferenceID) && !materialCache.ContainsValue(material))
            {
                material.Slot.Destroy();
                removed++;
            }
        }
        if (removed > 0)
        {
            UniLog.Log($"未使用の素マテリアルを {removed} 件削除しました。");
        }
    }

    private static string MaterialName(string slotName)
    {
        const string prefix = "Material: ";
        return slotName?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? slotName[prefix.Length..]
            : slotName;
    }

    private static Dictionary<string, string> FbxMaterialGuidsForRenderer(Slot root, MeshRenderer renderer,
        VrchatAvatar avatar)
    {
        VrchatFbxAsset additional = AdditionalFbxForSlot(root, renderer.Slot, avatar);
        return additional?.MaterialGuids ?? avatar.FbxMaterialGuids;
    }

    private static VrchatFbxAsset AdditionalFbxForSlot(Slot root, Slot slot, VrchatAvatar avatar)
    {
        for (Slot current = slot; current != null && current != root; current = current.Parent)
        {
            foreach (VrchatFbxAsset additional in avatar.AdditionalFbxs)
            {
                if (string.Equals(current.Name, additional.InstanceName, StringComparison.Ordinal))
                {
                    return additional;
                }
            }
        }
        return null;
    }

    private static async Task<XiexeToonMaterial> BuildMaterial(Slot assetsSlot, string guid,
        UnityPackage package, Dictionary<string, StaticTexture2D> textureCache)
    {
        LilToonInfo info = ResolveMaterialInfo(guid, package, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (info == null)
        {
            return null;
        }

        Slot slot = assetsSlot.AddSlot($"Material: {info.Name}");
        XiexeToonMaterial material = slot.AttachComponent<XiexeToonMaterial>();

        // --- Alpha / culling / queue ---
        switch (info.AlphaMode)
        {
            case "cutout":
                material.BlendMode.Value = BlendMode.Cutout;
                material.AlphaClip.Value = info.Cutoff;
                material.ZWrite.Value = ZWrite.On;
                break;
            case "transparent":
                material.BlendMode.Value = BlendMode.Alpha;
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
            case "additive":
                material.BlendMode.Value = BlendMode.Additive;
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
            case "multiply":
                material.BlendMode.Value = BlendMode.Multiply;
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
            default:
                material.BlendMode.Value = BlendMode.Opaque;
                material.ZWrite.Value = ZWrite.On;
                break;
        }
        if (info.RenderQueue > 0)
        {
            material.RenderQueue.Value = info.RenderQueue;
        }
        material.Culling.Value = info.Cull switch
        {
            0 => Culling.Off,
            1 => Culling.Front,
            _ => Culling.Back,
        };
        material.ColorMask.Value = (ColorMask)(byte)info.ColorMask;

        // --- Base color + main texture ---
        material.Color.Value = ToColor(info.Color, ColorProfile.sRGB);
        material.MainTextureScale.Value = ToFloat2(info.MainTexScale);
        material.MainTextureOffset.Value = ToFloat2(info.MainTexOffset);
        StaticTexture2D mainTex = await GetTexture(assetsSlot, package, info.MainTexGuid, textureCache, "MainTex");
        if (mainTex != null)
        {
            material.MainTexture.Target = mainTex;
        }
        StaticTexture2D normal = await GetTexture(assetsSlot, package, info.NormalMapGuid, textureCache, "NormalMap");
        if (normal != null)
        {
            normal.IsNormalMap.Value = true;
            material.NormalMap.Target = normal;
            material.NormalMapScale.Value = ToFloat2(info.NormalMapScale);
            material.NormalMapOffset.Value = ToFloat2(info.NormalMapOffset);
            material.NormalScale.Value = info.NormalScale;
        }

        material.Saturation.Value = 1f;
        if (info.UseReflection)
        {
            material.Metallic.Value = MathX.Clamp01(info.Metallic);
            material.Reflectivity.Value = MathX.Clamp01(info.Reflectance);
            material.SpecularIntensity.Value = info.ApplySpecular ? 1f : 0f;
            material.SpecularArea.Value = info.SpecularToon
                ? MathX.Clamp01(MathX.Max(info.Smoothness, 1f - info.SpecularBorder))
                : MathX.Clamp01(info.Smoothness);
            material.Glossiness.Value = info.ApplyReflection ? MathX.Clamp01(info.Smoothness) : 0f;
        }
        else
        {
            material.Metallic.Value = 0f;
            material.Glossiness.Value = 0f;
            material.Reflectivity.Value = 0f;
            material.SpecularIntensity.Value = 0f;
        }
        material.UseVertexColors.Value = false;

        // --- Shadow ramp ---
        if (info.UseShadow)
        {
            material.ShadowRamp.Target = await GenerateShadowRamp(assetsSlot, info);
            material.ShadowSharpness.Value = 0f;
            material.ShadowRim.Value = colorX.White;
            StaticTexture2D shadowMask = await GetTexture(assetsSlot, package, info.ShadowStrengthMaskGuid,
                textureCache, "ShadowStrengthMask");
            shadowMask ??= await GetSolidTexture(assetsSlot, textureCache, "__liltoon_white", color.White,
                "LilToon White");
            material.ShadowRampMask.Target = shadowMask;
            material.ShadowRampMaskScale.Value = ToFloat2(info.ShadowStrengthMaskScale);
            material.ShadowRampMaskOffset.Value = ToFloat2(info.ShadowStrengthMaskOffset);

            StaticTexture2D occlusion = await GetTexture(assetsSlot, package, info.ShadowBorderMaskGuid,
                textureCache, "ShadowBorderMask");
            if (occlusion != null)
            {
                material.OcclusionMap.Target = occlusion;
                material.OcclusionMapScale.Value = ToFloat2(info.ShadowBorderMaskScale);
                material.OcclusionMapOffset.Value = ToFloat2(info.ShadowBorderMaskOffset);
            }
            material.OcclusionColor.Value = ToColor(LerpColor(Vec4.One, info.ShadowColor,
                MathX.Clamp01(info.ShadowStrength)), ColorProfile.sRGB);
        }
        else
        {
            material.ShadowRamp.Target = null;
            material.ShadowSharpness.Value = 0f;
        }

        // --- Rim ---
        if (info.UseRim)
        {
            float intensity = MathX.Max(info.RimColor.X, info.RimColor.Y, info.RimColor.Z);
            material.RimColor.Value = ToColor(info.RimColor, ColorProfile.sRGB);
            material.RimIntensity.Value = MathX.Clamp(intensity, 0f, 1f);
            material.RimAlbedoTint.Value = 0f;
            material.RimAttenuationEffect.Value = 0f;
            // liltoon rim is a fresnel gated by _RimBorder with _RimBlur softness; map the band onto
            // XiexeToon's smoothstep(range±sharpness, 1 - N·V).
            float fresnelPower = MathX.Max(info.RimFresnelPower, 0.001f);
            float rimLo = MathF.Pow(MathX.Clamp01(info.RimBorder - info.RimBlur * 0.5f), 1f / fresnelPower);
            float rimHi = MathF.Pow(MathX.Clamp01(info.RimBorder + info.RimBlur * 0.5f), 1f / fresnelPower);
            material.RimRange.Value = MathX.Clamp01((rimLo + rimHi) * 0.5f);
            material.RimSharpness.Value = MathX.Clamp((rimHi - rimLo) * 0.5f, 0.001f, 1f);
            material.RimThreshold.Value = 0f;
        }
        else
        {
            material.RimIntensity.Value = 0f;
        }

        // --- Emission ---
        if (info.UseEmission)
        {
            float emissionScale = MathX.Clamp01(info.EmissionBlend) * info.EmissionColor.W;
            emissionScale *= MathX.Lerp(1f, 0.375f, MathX.Clamp01(info.EmissionFluorescence));
            Vec4 emissionColor = new(info.EmissionColor.X * emissionScale, info.EmissionColor.Y * emissionScale,
                info.EmissionColor.Z * emissionScale, info.EmissionColor.W);
            material.EmissionColor.Value = ToColor(emissionColor, ColorProfile.Linear);

            StaticTexture2D emission;
            if (info.EmissionMapGuid != null)
            {
                emission = await GetRgbTimesAlphaTexture(assetsSlot, package, info.EmissionMapGuid,
                    textureCache, "EmissionMap");
                material.EmissionMapScale.Value = ToFloat2(info.EmissionMapScale);
                material.EmissionMapOffset.Value = ToFloat2(info.EmissionMapOffset);
            }
            else if (info.EmissionBlendMaskGuid != null)
            {
                emission = await GetRgbTimesAlphaTexture(assetsSlot, package, info.EmissionBlendMaskGuid,
                    textureCache, "EmissionBlendMask");
                material.EmissionMapScale.Value = float2.One;
                material.EmissionMapOffset.Value = float2.Zero;
            }
            else if (info.EmissionMainStrength > 0f && info.MainTexGuid != null)
            {
                emission = await GetRgbTimesAlphaTexture(assetsSlot, package, info.MainTexGuid,
                    textureCache, "EmissionMainTexture");
                material.EmissionMapScale.Value = float2.One;
                material.EmissionMapOffset.Value = float2.Zero;
            }
            else
            {
                emission = await GetSolidTexture(assetsSlot, textureCache, "__liltoon_white", color.White,
                    "LilToon White");
                material.EmissionMapScale.Value = float2.One;
                material.EmissionMapOffset.Value = float2.Zero;
            }
            material.EmissionMap.Target = emission;
        }

        // --- Outline ---
        if (info.UseOutline && info.OutlineWidth > 0.00001f)
        {
            material.Outline.Value = info.OutlineLit
                ? XiexeToonMaterial.OutlineStyle.Lit
                : XiexeToonMaterial.OutlineStyle.Emissive;
            material.OutlineAlbedoTint.Value = info.OutlineAlbedoTint;
            // liltoon and XiexeToon both extrude by width * 0.01 in object space, so the width
            // maps across roughly 1:1.
            float outlineWidth = info.OutlineFixWidth ? info.OutlineWidth : info.OutlineWidth * 0.5f;
            material.OutlineWidth.Value = MathX.Clamp(outlineWidth, 0f, 5f);
            material.OutlineColor.Value = ToColor(info.OutlineColor, ColorProfile.sRGB);
            StaticTexture2D mask = await GetTexture(assetsSlot, package, info.OutlineMaskGuid, textureCache, "OutlineMask");
            if (mask != null)
            {
                material.OutlineMask.Target = mask;
            }
        }
        else
        {
            material.Outline.Value = XiexeToonMaterial.OutlineStyle.None;
        }

        // --- Matcap ---
        if (info.UseMatcap && info.MatcapBlendMode == 1 && info.MatcapBlendMaskGuid == null)
        {
            StaticTexture2D matcap = await GetRgbTimesAlphaTexture(assetsSlot, package, info.MatcapGuid,
                textureCache, "Matcap");
            if (matcap != null)
            {
                material.Matcap.Target = matcap;
                float matcapScale = info.MatcapBlend * info.MatcapColor.W;
                Vec4 tint = new(info.MatcapColor.X * matcapScale, info.MatcapColor.Y * matcapScale,
                    info.MatcapColor.Z * matcapScale, info.MatcapColor.W);
                material.MatcapTint.Value = ToColor(tint, ColorProfile.sRGB);
            }
        }

        return material;
    }

    private static LilToonInfo ResolveMaterialInfo(string guid, UnityPackage package, HashSet<string> resolving)
    {
        if (string.IsNullOrEmpty(guid) || !resolving.Add(guid))
        {
            return null;
        }

        UnityAsset matAsset = package.ByGuid(guid);
        string text = package.ReadText(matAsset);
        if (text == null)
        {
            return null;
        }
        YamlDocument matDoc = UnityYaml.ParseDocuments(text).FirstOrDefault(d => d.TypeName == "Material");
        if (matDoc == null)
        {
            return null;
        }

        LilToonInfo parent = null;
        string parentGuid = matDoc.Root?["m_Parent"]?.Guid;
        if (!string.IsNullOrEmpty(parentGuid) && parentGuid != "0000000000000000f000000000000000")
        {
            parent = ResolveMaterialInfo(parentGuid, package, resolving);
        }

        if (!LilToonConverter.IsLilToon(matDoc) && parent == null)
        {
            return null;
        }
        return LilToonConverter.Parse(matDoc, parent);
    }

    private static colorX ToColor(Vec4 c, ColorProfile profile) => new(c.X, c.Y, c.Z, c.W, profile);
    private static float2 ToFloat2(System.Numerics.Vector2 value) => new(value.X, value.Y);

    private static Vec4 LerpColor(Vec4 from, Vec4 to, float amount) =>
        new(MathX.Lerp(from.X, to.X, amount), MathX.Lerp(from.Y, to.Y, amount),
            MathX.Lerp(from.Z, to.Z, amount), MathX.Lerp(from.W, to.W, amount));

    // ---------------------------------------------------------------- shadow ramp

    private static async Task<StaticTexture2D> GenerateShadowRamp(Slot assetsSlot, LilToonInfo info)
    {
        Engine engine = assetsSlot.Engine;
        await default(ToBackground);
        const int width = 256;
        const int height = 256;
        var bitmap = new Bitmap2D(width, height, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);

        // The fully-shadowed multiplier: liltoon's _ShadowColor is the in-shadow tint, scaled by strength.
        var shadowTint = new color(
            MathX.Lerp(1f, info.ShadowColor.X, info.ShadowStrength),
            MathX.Lerp(1f, info.ShadowColor.Y, info.ShadowStrength),
            MathX.Lerp(1f, info.ShadowColor.Z, info.ShadowStrength),
            1f);
        float lo = info.ShadowBorder - info.ShadowBlur * 0.5f;
        float hi = info.ShadowBorder + info.ShadowBlur * 0.5f;
        for (int x = 0; x < width; x++)
        {
            float light = x / (width - 1f); // ~ saturate(N·L*0.5 + 0.5)
            float lit = MathX.Clamp01(SmoothStep(lo, hi, light));
            var pixel = new color(
                MathX.Lerp(shadowTint.r, 1f, lit),
                MathX.Lerp(shadowTint.g, 1f, lit),
                MathX.Lerp(shadowTint.b, 1f, lit),
                1f);
            for (int y = 0; y < height; y++)
            {
                float mask = y / (height - 1f);
                var maskedPixel = new color(
                    MathX.Lerp(1f, pixel.r, mask),
                    MathX.Lerp(1f, pixel.g, mask),
                    MathX.Lerp(1f, pixel.b, mask),
                    1f);
                bitmap.SetPixel(x, y, in maskedPixel);
            }
        }
        Uri uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        await default(ToWorld);

        Slot rampSlot = assetsSlot.AddSlot($"ShadowRamp {info.Name}");
        StaticTexture2D texture = rampSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        texture.Uncompressed.Value = true;
        texture.WrapMode = TextureWrapMode.Clamp;
        return texture;
    }

    private static float SmoothStep(float lo, float hi, float x)
    {
        if (hi - lo < 1e-5f)
        {
            return x >= hi ? 1f : 0f;
        }
        float t = MathX.Clamp01((x - lo) / (hi - lo));
        return t * t * (3f - 2f * t);
    }

    // ---------------------------------------------------------------- texture import

    private static async Task<StaticTexture2D> GetTexture(Slot assetsSlot, UnityPackage package, string guid,
        Dictionary<string, StaticTexture2D> cache, string label)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }
        if (cache.TryGetValue(guid, out StaticTexture2D cached))
        {
            return cached;
        }
        cache[guid] = null;
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.HasContent != true)
        {
            UniLog.Warning($"テクスチャ(guid {guid}, {label})がパッケージに見つかりません。");
            return null;
        }

        Engine engine = assetsSlot.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            string extension = Path.GetExtension(asset.LogicalPath);
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".png";
            }
            string tempFile = engine.LocalDB.GetTempFilePath(extension);
            File.Copy(asset.DiskPath, tempFile, overwrite: true);
            uri = await engine.LocalDB.ImportLocalAssetAsync(tempFile, LocalDB.ImportLocation.Move);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"テクスチャの取り込みに失敗しました ({label}, guid {guid}): {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null)
        {
            return null;
        }
        Slot textureSlot = assetsSlot.AddSlot($"{label}: {Path.GetFileNameWithoutExtension(asset.LogicalPath)}");
        StaticTexture2D texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        cache[guid] = texture;
        return texture;
    }

    private static async Task<StaticTexture2D> GetRgbTimesAlphaTexture(Slot assetsSlot, UnityPackage package,
        string guid, Dictionary<string, StaticTexture2D> cache, string label)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }

        string cacheKey = $"{guid}|rgb-times-alpha";
        if (cache.TryGetValue(cacheKey, out StaticTexture2D cached))
        {
            return cached;
        }
        cache[cacheKey] = null;

        UnityAsset asset = package.ByGuid(guid);
        if (asset?.HasContent != true)
        {
            UniLog.Warning($"{label} texture (guid {guid}) was not found in the Unity package.");
            return null;
        }

        Engine engine = assetsSlot.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            string extension = Path.GetExtension(asset.LogicalPath);
            using var source = File.OpenRead(asset.DiskPath);
            Bitmap2D bitmap = TextureDecoder.Decode(source, extension, generateMipMaps: false);
            MultiplyRgbByAlpha(bitmap);
            uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Failed to post-process {label} texture (guid {guid}): {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null)
        {
            return null;
        }

        Slot textureSlot = assetsSlot.AddSlot($"{label}: {Path.GetFileNameWithoutExtension(asset.LogicalPath)}");
        StaticTexture2D texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        cache[cacheKey] = texture;
        return texture;
    }

    private static void MultiplyRgbByAlpha(Bitmap2D bitmap)
    {
        for (int y = 0; y < bitmap.Size.y; y++)
        {
            for (int x = 0; x < bitmap.Size.x; x++)
            {
                color pixel = bitmap.GetPixel(x, y);
                var processed = new color(pixel.r * pixel.a, pixel.g * pixel.a, pixel.b * pixel.a, pixel.a);
                bitmap.SetPixel(x, y, in processed);
            }
        }
    }

    private static async Task<StaticTexture2D> GetSolidTexture(Slot assetsSlot,
        Dictionary<string, StaticTexture2D> cache, string key, color pixel, string name)
    {
        if (cache.TryGetValue(key, out StaticTexture2D cached))
        {
            return cached;
        }

        Engine engine = assetsSlot.Engine;
        await default(ToBackground);
        var bitmap = new Bitmap2D(1, 1, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);
        bitmap.SetPixel(0, 0, in pixel);
        Uri uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        await default(ToWorld);

        Slot textureSlot = assetsSlot.AddSlot(name);
        StaticTexture2D texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        texture.Uncompressed.Value = true;
        cache[key] = texture;
        return texture;
    }
}
