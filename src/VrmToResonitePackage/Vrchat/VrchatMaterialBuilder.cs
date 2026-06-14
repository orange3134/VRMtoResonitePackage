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
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string guid in uniqueGuids)
        {
            XiexeToonMaterial material = await BuildMaterial(assetsSlot, guid, package, textureCache);
            if (material != null)
            {
                materialCache[guid] = material;
            }
        }

        // FBX prefab variants keep their renderer hierarchy as stripped objects, so the prefab
        // doesn't contain normal SkinnedMeshRenderer documents to read. ModelImporter.externalObjects
        // still maps every embedded FBX material name to its external .mat; replace the imported
        // placeholder materials by that name before applying any per-renderer prefab overrides.
        int assigned = 0;
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                IAssetProvider<FrooxEngine.Material> placeholder = renderer.Materials[i];
                string name = MaterialName(placeholder?.Slot?.Name);
                if (name != null &&
                    avatar.FbxMaterialGuids.TryGetValue(name, out string guid) &&
                    materialCache.TryGetValue(guid, out XiexeToonMaterial material))
                {
                    renderer.Materials[i] = material;
                    assigned++;
                }
            }
        }

        // Assign the built materials to the imported renderers (matched by GameObject/slot name).
        var renderersByName = new Dictionary<string, MeshRenderer>(StringComparer.Ordinal);
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            string name = renderer.Slot.Name;
            if (name != null && !renderersByName.ContainsKey(name))
            {
                renderersByName[name] = renderer;
            }
        }

        foreach (VrchatRendererMaterials rm in avatar.RendererMaterials)
        {
            if (!renderersByName.TryGetValue(rm.RendererGameObjectName, out MeshRenderer renderer))
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

    private static async Task<XiexeToonMaterial> BuildMaterial(Slot assetsSlot, string guid,
        UnityPackage package, Dictionary<string, StaticTexture2D> textureCache)
    {
        UnityAsset matAsset = package.ByGuid(guid);
        string text = package.ReadText(matAsset);
        if (text == null)
        {
            return null;
        }
        YamlDocument matDoc = UnityYaml.ParseDocuments(text).FirstOrDefault(d => d.TypeName == "Material");
        if (matDoc == null || !LilToonConverter.IsLilToon(matDoc))
        {
            return null;
        }
        LilToonInfo info = LilToonConverter.Parse(matDoc);

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
            default:
                material.BlendMode.Value = BlendMode.Opaque;
                material.ZWrite.Value = ZWrite.On;
                break;
        }
        if (info.RenderQueue > 0)
        {
            material.RenderQueue.Value = info.RenderQueue;
        }
        material.Culling.Value = info.Cull == 0 ? Culling.Off : Culling.Back;

        // --- Base color + main texture ---
        material.Color.Value = ToColor(info.Color, ColorProfile.sRGB);
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
        }

        // Neutral PBR-ish features a toon material shouldn't express.
        material.Saturation.Value = 1f;
        material.Metallic.Value = 0f;
        material.Glossiness.Value = 0f;
        material.Reflectivity.Value = 0f;
        material.SpecularIntensity.Value = 0f;
        material.UseVertexColors.Value = false;

        // --- Shadow ramp ---
        if (info.UseShadow)
        {
            material.ShadowRamp.Target = await GenerateShadowRamp(assetsSlot, info);
            material.ShadowSharpness.Value = 0f;
            material.ShadowRim.Value = colorX.White;
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
            material.RimRange.Value = MathX.Clamp01(info.RimBorder);
            material.RimSharpness.Value = MathX.Clamp(MathX.Max(info.RimBlur, 0.01f), 0.01f, 1f);
            material.RimThreshold.Value = 0f;
        }
        else
        {
            material.RimIntensity.Value = 0f;
        }

        // --- Emission ---
        if (info.UseEmission && (info.EmissionColor.X + info.EmissionColor.Y + info.EmissionColor.Z) > 0.001f)
        {
            material.EmissionColor.Value = ToColor(info.EmissionColor, ColorProfile.Linear);
            StaticTexture2D emission = await GetTexture(assetsSlot, package, info.EmissionMapGuid, textureCache, "EmissionMap");
            if (emission != null)
            {
                material.EmissionMap.Target = emission;
            }
        }

        // --- Outline ---
        if (info.UseOutline && info.OutlineWidth > 0.00001f)
        {
            material.Outline.Value = info.OutlineLit
                ? XiexeToonMaterial.OutlineStyle.Lit
                : XiexeToonMaterial.OutlineStyle.Emissive;
            material.OutlineAlbedoTint.Value = info.OutlineLit;
            // liltoon and XiexeToon both extrude by width * 0.01 in object space, so the width
            // maps across roughly 1:1.
            material.OutlineWidth.Value = MathX.Clamp(info.OutlineWidth, 0f, 5f);
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
        if (info.UseMatcap)
        {
            StaticTexture2D matcap = await GetTexture(assetsSlot, package, info.MatcapGuid, textureCache, "Matcap");
            if (matcap != null)
            {
                material.Matcap.Target = matcap;
                material.MatcapTint.Value = ToColor(info.MatcapColor, ColorProfile.sRGB);
            }
        }

        return material;
    }

    private static colorX ToColor(Vec4 c, ColorProfile profile) => new(c.X, c.Y, c.Z, c.W, profile);

    // ---------------------------------------------------------------- shadow ramp

    private static async Task<StaticTexture2D> GenerateShadowRamp(Slot assetsSlot, LilToonInfo info)
    {
        Engine engine = assetsSlot.Engine;
        await default(ToBackground);
        const int width = 256;
        const int height = 8;
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
}
