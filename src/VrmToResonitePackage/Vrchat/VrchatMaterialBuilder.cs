using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Unity;
using ColorProfile = Renderite.Shared.ColorProfile;
using TextureFormat = Renderite.Shared.TextureFormat;
using TextureWrapMode = Renderite.Shared.TextureWrapMode;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Builds XiexeToon materials from the avatar's Unity .mat files (textures + tone parameters)
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
        var metallicGlossCache = new Dictionary<string, MetallicGlossResult>(
            StringComparer.OrdinalIgnoreCase);
        var materialCache = new Dictionary<string, IAssetProvider<FrooxEngine.Material>>(
            StringComparer.OrdinalIgnoreCase);

        // Build one XiexeToon material per unique Unity .mat referenced by the avatar.
        IEnumerable<string> uniqueGuids = avatar.RendererMaterials
            .SelectMany(r => r.MaterialGuids)
            .Concat(avatar.FbxMaterialGuids.Values)
            .Concat(avatar.AdditionalFbxs.SelectMany(f => f.MaterialGuids.Values))
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string guid in uniqueGuids)
        {
            IAssetProvider<FrooxEngine.Material> material = await BuildMaterial(
                assetsSlot, guid, package, textureCache, metallicGlossCache);
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
                string name = MaterialName((placeholder as Component)?.Slot?.Name);
                if (name != null &&
                    fbxMaterialGuids.TryGetValue(name, out string guid) &&
                    materialCache.TryGetValue(guid, out IAssetProvider<FrooxEngine.Material> material))
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
                if (guid == null || !materialCache.TryGetValue(
                        guid, out IAssetProvider<FrooxEngine.Material> material))
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

    private static async Task<IAssetProvider<FrooxEngine.Material>> BuildMaterial(
        Slot assetsSlot, string guid,
        UnityPackage package, Dictionary<string, StaticTexture2D> textureCache,
        Dictionary<string, MetallicGlossResult> metallicGlossCache)
    {
        LilToonInfo info = ResolveMaterialInfo(guid, package, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (info == null)
        {
            return null;
        }

        Slot slot = assetsSlot.AddSlot($"Material: {info.Name}");
        if (info.IsFakeShadow)
        {
            // Match Resonite.UnitySDK's LilToonFakeShadowConverter. FakeShadow is a projected
            // helper overlay, so approximating it as a surface XiexeToon material is visibly wrong.
            UnlitMaterial clear = slot.AttachComponent<UnlitMaterial>();
            clear.TintColor.Value = new colorX(0f, 0f, 0f, 0f, ColorProfile.sRGB);
            clear.BlendMode.Value = BlendMode.Cutout;
            clear.AlphaCutoff.Value = 1f;
            clear.Sidedness.Value = Sidedness.Front;
            clear.Texture.Target = null;
            return clear;
        }

        XiexeToonMaterial material = slot.AttachComponent<XiexeToonMaterial>();

        // --- Alpha / culling / queue ---
        switch (info.AlphaMode)
        {
            case "cutout":
                material.BlendMode.Value = BlendMode.Cutout;
                material.AlphaClip.Value = info.Cutoff;
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
            case "transparent":
                material.BlendMode.Value = BlendMode.Alpha;
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
            case "premultiply":
                material.BlendMode.Value = BlendMode.Transparent;
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
                material.ZWrite.Value = info.ZWrite ? ZWrite.On : ZWrite.Off;
                break;
        }
        if (info.RenderQueue >= 0)
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
        StaticTexture2D normal = await GetTexture(assetsSlot, package,
            info.UseNormalMap ? info.NormalMapGuid : null, textureCache, "NormalMap", isNormalMap: true);
        if (normal != null)
        {
            material.NormalMap.Target = normal;
            material.NormalMapScale.Value = ToFloat2(info.NormalMapScale);
            material.NormalMapOffset.Value = ToFloat2(info.NormalMapOffset);
            material.NormalScale.Value = info.NormalScale;
        }

        material.Saturation.Value = 1f;
        if (info.UseReflection)
        {
            material.Metallic.Value = MathX.Clamp01(info.Metallic);
            material.SpecularIntensity.Value = info.ApplySpecular ? 1f : 0f;
            material.SpecularArea.Value = info.SpecularToon
                ? MathX.Clamp01(MathX.Max(info.Smoothness, 1f - info.SpecularBorder))
                : MathX.Clamp01(info.Smoothness);
            material.Glossiness.Value = info.ApplyReflection ? MathX.Clamp01(info.Smoothness) : 0f;
            MetallicGlossResult packed = info.IsToonStandard || info.SmoothnessFromAlbedoAlpha ||
                info.GlossMapGuid != null
                ? await GetMetallicGlossTexture(assetsSlot, package, info, metallicGlossCache)
                : new MetallicGlossResult(await GetTexture(assetsSlot, package,
                    info.UseMetallicGlossMap ? info.MetallicGlossMapGuid : null,
                    textureCache, "MetallicGlossMap", preferredProfile: ColorProfile.Linear), null);
            material.Reflectivity.Value = MathX.Clamp01(packed.SpecularReflectance ?? info.Reflectance);
            StaticTexture2D metallicGloss = packed.Texture;
            if (metallicGloss != null)
            {
                material.MetallicGlossMap.Target = metallicGloss;
                material.MetallicGlossMapScale.Value = ToFloat2(info.MetallicGlossMapScale);
                material.MetallicGlossMapOffset.Value = ToFloat2(info.MetallicGlossMapOffset);
            }
        }
        else
        {
            material.Metallic.Value = 0f;
            material.Glossiness.Value = 0f;
            material.Reflectivity.Value = 0f;
            material.SpecularIntensity.Value = 0f;
        }
        material.UseVertexColors.Value = info.UseVertexColors;

        // --- Shadow ramp ---
        if (info.UseShadow)
        {
            material.ShadowRamp.Target = info.IsToonStandard
                ? await GetTexture(assetsSlot, package, info.ShadowRampGuid, textureCache, "ShadowRamp",
                    TextureWrapMode.Clamp)
                : await GenerateShadowRamp(assetsSlot, info);
            material.ShadowSharpness.Value = 0f;
            material.ShadowRim.Value = colorX.White;
            StaticTexture2D shadowMask = info.IsToonStandard
                ? null
                : await GetTexture(assetsSlot, package, info.ShadowStrengthMaskGuid,
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

        string occlusionMapGuid = info.UseOcclusionMap ? info.OcclusionMapGuid : null;
        StaticTexture2D genericOcclusion = info.IsLilToon
            ? await GetTexture(assetsSlot, package, occlusionMapGuid, textureCache, "OcclusionMap")
            : await GetChannelTexture(assetsSlot, package, occlusionMapGuid, info.OcclusionMapChannel,
                info.OcclusionStrength, textureCache, "OcclusionMap");
        if (genericOcclusion != null)
        {
            material.OcclusionMap.Target = genericOcclusion;
            material.OcclusionMapScale.Value = ToFloat2(info.OcclusionMapScale);
            material.OcclusionMapOffset.Value = ToFloat2(info.OcclusionMapOffset);
        }

        if (info.UseRim)
        {
            material.RimColor.Value = ToColor(info.RimColor, ColorProfile.sRGB);
            material.RimIntensity.Value = info.RimIntensity;
            material.RimRange.Value = MathX.Clamp01(info.RimRange);
            material.RimSharpness.Value = MathX.Clamp01(info.RimSharpness);
            material.RimAlbedoTint.Value = MathX.Clamp01(info.RimAlbedoTint);
        }
        else
        {
            // lilToon's rim model cannot be represented reliably by XiexeToon. Match
            // Resonite.UnitySDK by leaving rim conversion disabled.
            material.RimIntensity.Value = 0f;
        }

        // --- Emission ---
        if (info.UseEmission)
        {
            float emissionScale = info.IsLilToon
                ? MathX.Clamp01(info.EmissionBlend) * info.EmissionColor.W *
                  MathX.Lerp(1f, 0.375f, MathX.Clamp01(info.EmissionFluorescence))
                : info.IsToonStandard ? info.EmissionStrength : 1f;
            Vec4 emissionColor = new(info.EmissionColor.X * emissionScale, info.EmissionColor.Y * emissionScale,
                info.EmissionColor.Z * emissionScale, info.EmissionColor.W);
            material.EmissionColor.Value = ToColor(emissionColor,
                info.IsLilToon ? ColorProfile.Linear : ColorProfile.sRGB);

            StaticTexture2D emission;
            if (info.EmissionMapGuid != null)
            {
                emission = info.IsLilToon
                    ? await GetRgbTimesAlphaTexture(assetsSlot, package, info.EmissionMapGuid,
                        textureCache, "EmissionMap")
                    : await GetTexture(assetsSlot, package, info.EmissionMapGuid,
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
        if (info.UseOutline)
        {
            material.Outline.Value = info.OutlineLit
                ? XiexeToonMaterial.OutlineStyle.Lit
                : XiexeToonMaterial.OutlineStyle.Emissive;
            material.OutlineAlbedoTint.Value = info.OutlineAlbedoTint;
            // lilToon and XiexeToon both use the serialized outline width directly.
            material.OutlineWidth.Value = info.OutlineWidth;
            material.OutlineColor.Value = ToColor(info.OutlineColor, ColorProfile.sRGB);
            StaticTexture2D mask = info.IsToonStandard
                ? await GetChannelTexture(assetsSlot, package, info.OutlineMaskGuid,
                    info.OutlineMaskChannel, 1f, textureCache, "OutlineMask")
                : await GetTexture(assetsSlot, package, info.OutlineMaskGuid, textureCache, "OutlineMask");
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

        if (LilToonConverter.IsLilToon(matDoc) || parent?.IsLilToon == true)
        {
            return LilToonConverter.Parse(matDoc, parent, IsOutlineShader(matDoc, package));
        }

        string shaderName = GetShaderName(matDoc, package);
        bool isToonStandard = ToonStandardConverter.IsToonStandard(matDoc, shaderName);
        if (isToonStandard || parent?.IsToonStandard == true)
        {
            bool isOutline = isToonStandard
                ? ToonStandardConverter.IsOutline(matDoc, shaderName)
                : parent?.UseOutline == true;
            return ToonStandardConverter.Parse(matDoc, parent, isOutline);
        }

        if (MobileToonLitConverter.IsMobileToonLit(matDoc, shaderName) ||
            parent?.IsMobileToonLit == true)
        {
            return MobileToonLitConverter.Parse(matDoc, parent);
        }

        return ShaderPropertyFallbackConverter.Parse(matDoc, parent, GetShaderSource(matDoc, package));
    }

    private static bool IsOutlineShader(YamlDocument material, UnityPackage package)
    {
        string shaderGuid = material.Root?["m_Shader"]?.Guid;
        if (VrchatConstants.LilToonOutlineShaderGuids.Contains(shaderGuid))
        {
            return true;
        }

        UnityAsset shader = package.ByGuid(shaderGuid);
        if (shader == null)
        {
            return false;
        }

        if (Path.GetFileNameWithoutExtension(shader.LogicalPath)?
            .Contains("Outline", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        string shaderName = GetShaderName(material, package);
        return shaderName?.Contains("Outline", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetShaderName(YamlDocument material, UnityPackage package)
    {
        string source = GetShaderSource(material, package);
        int declaration = source?.IndexOf("Shader \"", StringComparison.Ordinal) ?? -1;
        if (declaration < 0)
        {
            return null;
        }

        int nameStart = declaration + "Shader \"".Length;
        int nameEnd = source.IndexOf('"', nameStart);
        return nameEnd > nameStart ? source[nameStart..nameEnd] : null;
    }

    private static string GetShaderSource(YamlDocument material, UnityPackage package)
    {
        string shaderGuid = material.Root?["m_Shader"]?.Guid;
        return package.ReadText(package.ByGuid(shaderGuid));
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
        Dictionary<string, StaticTexture2D> cache, string label, TextureWrapMode? wrapMode = null,
        ColorProfile? preferredProfile = null, bool isNormalMap = false)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }
        string cacheKey = guid;
        if (wrapMode.HasValue)
        {
            cacheKey += $"|wrap-{wrapMode.Value}";
        }
        if (preferredProfile.HasValue)
        {
            cacheKey += $"|profile-{preferredProfile.Value}";
        }
        if (isNormalMap)
        {
            cacheKey += "|normal-map";
        }
        if (cache.TryGetValue(cacheKey, out StaticTexture2D cached))
        {
            return cached;
        }
        cache[cacheKey] = null;
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
        texture.IsNormalMap.Value = isNormalMap;
        if (preferredProfile.HasValue)
        {
            texture.PreferredProfile.Value = preferredProfile.Value;
        }
        if (wrapMode.HasValue)
        {
            texture.WrapMode = wrapMode.Value;
        }
        texture.URL.Value = uri;
        cache[cacheKey] = texture;
        return texture;
    }

    private static async Task<StaticTexture2D> GetChannelTexture(Slot assetsSlot, UnityPackage package,
        string guid, int channel, float strength, Dictionary<string, StaticTexture2D> cache, string label)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }

        channel = Math.Clamp(channel, 0, 3);
        strength = MathX.Clamp01(strength);
        string cacheKey = $"{guid}|channel-{channel}|strength-{strength:R}";
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
            using FileStream source = File.OpenRead(asset.DiskPath);
            Bitmap2D bitmap = TextureDecoder.Decode(source, Path.GetExtension(asset.LogicalPath),
                generateMipMaps: false);
            var output = new Bitmap2D(bitmap.Size.x, bitmap.Size.y, TextureFormat.RGBA32,
                mipmaps: false, ColorProfile.Linear);
            for (int y = 0; y < bitmap.Size.y; y++)
            {
                for (int x = 0; x < bitmap.Size.x; x++)
                {
                    float value = Channel(bitmap.GetPixel(x, y), channel);
                    value = MathX.Lerp(1f, value, strength);
                    var pixel = new color(value, value, value, 1f);
                    output.SetPixel(x, y, in pixel);
                }
            }
            uri = await engine.LocalDB.SaveAssetAsync(output);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Failed to extract {label} channel (guid {guid}): {ex.Message}");
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

    private readonly record struct MetallicGlossResult(
        StaticTexture2D Texture, float? SpecularReflectance);

    private static async Task<MetallicGlossResult> GetMetallicGlossTexture(Slot assetsSlot,
        UnityPackage package, LilToonInfo info, Dictionary<string, MetallicGlossResult> cache)
    {
        bool useSpecularReflectance = info.IsSpecularWorkflow && info.UseSpecGlossMap &&
            info.SpecGlossMapGuid != null;
        if (info.MetallicMapGuid == null && info.GlossMapGuid == null && !useSpecularReflectance)
        {
            return default;
        }

        string cacheKey = $"{info.MetallicMapGuid}|{info.MetallicMapChannel}|" +
            $"{info.MetallicMapScale.X:R},{info.MetallicMapScale.Y:R}|" +
            $"{info.MetallicMapOffset.X:R},{info.MetallicMapOffset.Y:R}|" +
            $"{info.GlossMapGuid}|{info.GlossMapChannel}|" +
            $"{info.GlossMapScale.X:R},{info.GlossMapScale.Y:R}|" +
            $"{info.GlossMapOffset.X:R},{info.GlossMapOffset.Y:R}|" +
            $"{info.MetallicGlossMapScale.X:R},{info.MetallicGlossMapScale.Y:R}|" +
            $"{info.MetallicGlossMapOffset.X:R},{info.MetallicGlossMapOffset.Y:R}|metallic-gloss";
        cacheKey += useSpecularReflectance
            ? $"|{info.SpecGlossMapGuid}|" +
              $"{info.SpecGlossMapScale.X:R},{info.SpecGlossMapScale.Y:R}|" +
              $"{info.SpecGlossMapOffset.X:R},{info.SpecGlossMapOffset.Y:R}|specular-reflectance"
            : "|no-specular-reflectance";
        if (cache.TryGetValue(cacheKey, out MetallicGlossResult cached))
        {
            return cached;
        }

        UnityAsset metallicAsset = package.ByGuid(info.MetallicMapGuid);
        UnityAsset glossAsset = package.ByGuid(info.GlossMapGuid);
        UnityAsset specularAsset = package.ByGuid(useSpecularReflectance ? info.SpecGlossMapGuid : null);
        Engine engine = assetsSlot.Engine;
        Uri uri = null;
        float? specularReflectance = null;
        try
        {
            await default(ToBackground);
            Bitmap2D metallic = DecodeBitmap(metallicAsset);
            Bitmap2D gloss = DecodeBitmap(glossAsset);
            Bitmap2D specular = !useSpecularReflectance
                ? null
                : string.Equals(info.SpecGlossMapGuid, info.GlossMapGuid,
                    StringComparison.OrdinalIgnoreCase)
                    ? gloss
                    : DecodeBitmap(specularAsset);
            if (specular != null)
            {
                specularReflectance = AverageSpecularReflectance(specular,
                    info.SpecGlossMapScale, info.SpecGlossMapOffset);
            }

            if (metallic != null || gloss != null)
            {
                int width = Math.Max(metallic?.Size.x ?? 1, gloss?.Size.x ?? 1);
                int height = Math.Max(metallic?.Size.y ?? 1, gloss?.Size.y ?? 1);
                var output = new Bitmap2D(width, height, TextureFormat.RGBA32, mipmaps: false,
                    ColorProfile.Linear);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float metallicValue = SampleChannel(metallic, x, y, width, height,
                            info.MetallicMapChannel, info.MetallicMapScale, info.MetallicMapOffset,
                            info.MetallicGlossMapScale, info.MetallicGlossMapOffset);
                        color glossPixel = gloss == null
                            ? color.White
                            : SamplePixel(gloss, x, y, width, height,
                                info.GlossMapScale, info.GlossMapOffset,
                                info.MetallicGlossMapScale, info.MetallicGlossMapOffset);
                        float glossValue = Channel(glossPixel, Math.Clamp(info.GlossMapChannel, 0, 3));
                        var pixel = new color(metallicValue, metallicValue, metallicValue, glossValue);
                        output.SetPixel(x, y, in pixel);
                    }
                }
                uri = await engine.LocalDB.SaveAssetAsync(output);
            }
            else if (specular == null)
            {
                throw new InvalidDataException("No source texture was found in the Unity package.");
            }
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Failed to combine Toon Standard metallic/gloss textures: {ex.Message}");
        }
        await default(ToWorld);
        StaticTexture2D texture = null;
        if (uri != null)
        {
            Slot textureSlot = assetsSlot.AddSlot($"MetallicGlossMap: {info.Name}");
            texture = textureSlot.AttachComponent<StaticTexture2D>();
            texture.URL.Value = uri;
        }
        if (texture == null && !specularReflectance.HasValue)
        {
            return default;
        }
        var result = new MetallicGlossResult(texture, specularReflectance);
        cache[cacheKey] = result;
        return result;
    }

    private static Bitmap2D DecodeBitmap(UnityAsset asset)
    {
        if (asset?.HasContent != true)
        {
            return null;
        }
        using FileStream source = File.OpenRead(asset.DiskPath);
        return TextureDecoder.Decode(source, Path.GetExtension(asset.LogicalPath), generateMipMaps: false);
    }

    private static float SampleChannel(Bitmap2D bitmap, int x, int y, int width, int height, int channel,
        Vec2 sourceScale, Vec2 sourceOffset, Vec2 outputScale, Vec2 outputOffset)
    {
        if (bitmap == null)
        {
            return 1f;
        }

        return Channel(SamplePixel(bitmap, x, y, width, height,
            sourceScale, sourceOffset, outputScale, outputOffset), Math.Clamp(channel, 0, 3));
    }

    private static color SamplePixel(Bitmap2D bitmap, int x, int y, int width, int height,
        Vec2 sourceScale, Vec2 sourceOffset, Vec2 outputScale, Vec2 outputOffset)
    {
        float outputU = (x + 0.5f) / width;
        float outputV = (y + 0.5f) / height;
        float sourceU = TransformUv(outputU, sourceScale.X, sourceOffset.X,
            outputScale.X, outputOffset.X);
        float sourceV = TransformUv(outputV, sourceScale.Y, sourceOffset.Y,
            outputScale.Y, outputOffset.Y);
        int sourceX = WrappedPixel(sourceU, bitmap.Size.x);
        int sourceY = WrappedPixel(sourceV, bitmap.Size.y);
        return bitmap.GetPixel(sourceX, sourceY);
    }

    private static float AverageSpecularReflectance(Bitmap2D bitmap, Vec2 scale, Vec2 offset)
    {
        double total = 0d;
        for (int y = 0; y < bitmap.Size.y; y++)
        {
            for (int x = 0; x < bitmap.Size.x; x++)
            {
                color pixel = SamplePixel(bitmap, x, y, bitmap.Size.x, bitmap.Size.y,
                    scale, offset, Vec2.One, Vec2.Zero);
                total += Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
            }
        }
        return (float)(total / (bitmap.Size.x * (double)bitmap.Size.y));
    }

    private static float TransformUv(float outputUv, float sourceScale, float sourceOffset,
        float outputScale, float outputOffset)
    {
        if (MathF.Abs(outputScale) < 1e-6f)
        {
            return sourceOffset;
        }
        float materialUv = (outputUv - outputOffset) / outputScale;
        return materialUv * sourceScale + sourceOffset;
    }

    private static int WrappedPixel(float uv, int size)
    {
        float wrapped = uv - MathF.Floor(uv);
        return Math.Min(size - 1, (int)(wrapped * size));
    }

    private static float Channel(color pixel, int channel) => channel switch
    {
        1 => pixel.g,
        2 => pixel.b,
        3 => pixel.a,
        _ => pixel.r,
    };

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
