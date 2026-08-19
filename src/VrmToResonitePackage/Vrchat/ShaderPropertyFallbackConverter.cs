using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Reads common material properties when no shader-specific converter is available. This mirrors
/// Resonite.UnitySDK's property-based fallback: property names, rather than the shader name, decide
/// which subset can be preserved.
/// </summary>
internal static class ShaderPropertyFallbackConverter
{
    public static LilToonInfo Parse(YamlDocument material, LilToonInfo parent = null)
    {
        YamlNode root = material.Root;
        YamlNode props = root?["m_SavedProperties"];
        YamlNode floats = MergeProperties(
            LilToonConverter.FlattenProps(props?["m_Floats"]),
            LilToonConverter.FlattenProps(props?["m_Ints"]));
        YamlNode colors = LilToonConverter.FlattenProps(props?["m_Colors"]);
        YamlNode texEnvs = LilToonConverter.FlattenProps(props?["m_TexEnvs"]);

        string mainTexName = FindName(texEnvs, "_BaseMap", "_BaseColorMap", "_MainTex",
            "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse");
        string normalMapName = FindName(texEnvs, "_BumpMap", "_NormalMap", "_NormalTex");
        string metallicMapName = FindName(texEnvs, "_MetallicGlossMap");
        string emissionMapName = FindName(texEnvs, "_EmissionMap", "_EmissiveColorMap", "_EmissiveMap");
        string occlusionMapName = FindName(texEnvs, "_OcclusionMap", "_OcclusionTex");

        string Tex(string name) => name == null ? null : TextureGuid(texEnvs?[name]);
        string TexOrParent(string name, string parentGuid) => name == null ? parentGuid : Tex(name);
        Vec2 TexScale(string name, Vec2 fallback) => name == null
            ? fallback
            : LilToonConverter.ReadVector2(texEnvs?[name]?["m_Scale"], fallback);
        Vec2 TexOffset(string name, Vec2 fallback) => name == null
            ? fallback
            : LilToonConverter.ReadVector2(texEnvs?[name]?["m_Offset"], fallback);

        Vec4 color = ReadColor(colors, parent?.Color ?? Vec4.One,
            "_BaseColor", "_Color", "_TintColor", "_MainColor");
        Vec4 emissionColor = ReadColor(colors, parent?.EmissionColor ?? new Vec4(0f, 0f, 0f, 1f),
            "_EmissionColor", "_EmissiveColor");
        string mainTexGuid = TexOrParent(mainTexName, parent?.MainTexGuid);
        string normalMapGuid = TexOrParent(normalMapName, parent?.NormalMapGuid);
        string metallicMapGuid = TexOrParent(metallicMapName, parent?.MetallicGlossMapGuid);
        string emissionMapGuid = TexOrParent(emissionMapName, parent?.EmissionMapGuid);
        string occlusionMapGuid = TexOrParent(occlusionMapName, parent?.OcclusionMapGuid);
        int defaultOcclusionChannel = occlusionMapName != null
            ? Normalize(occlusionMapName) == Normalize("_OcclusionMap") ? 1 : 0
            : parent?.OcclusionMapChannel ?? 0;

        bool hasMetallic = TryFloat(floats, out float metallic, "_Metallic");
        bool hasGlossiness = TryFloat(floats, out float glossiness, "_Glossiness");
        bool hasUrpSmoothness = TryFloat(floats, out float urpSmoothness, "_Smoothness");
        bool hasGlossMapScale = TryFloat(floats, out float glossMapScale, "_GlossMapScale");
        float smoothnessWithoutMap = hasGlossiness
            ? glossiness
            : hasUrpSmoothness
                ? urpSmoothness
                : parent?.SmoothnessWithoutMap ?? parent?.Smoothness ?? 0f;
        float smoothnessWithMap = hasGlossMapScale
            ? glossMapScale
            : hasUrpSmoothness
                ? urpSmoothness
                : parent?.SmoothnessWithMap ?? parent?.Smoothness ?? 1f;
        bool hasSpecularControl = TryFloat(floats, out float specularHighlights, "_SpecularHighlights");
        bool hasReflectionControl = TryFloat(floats, out float glossyReflections,
            "_GlossyReflections", "_EnvironmentReflections");
        bool useReflection = hasMetallic || hasGlossiness || hasUrpSmoothness || hasGlossMapScale ||
            metallicMapGuid != null ||
            (hasSpecularControl && specularHighlights >= 0.5f) ||
            (hasReflectionControl && glossyReflections >= 0.5f) ||
            parent?.UseReflection == true;
        bool applySpecular = hasSpecularControl
            ? specularHighlights >= 0.5f
            : parent?.ApplySpecular ?? useReflection;
        bool applyReflection = hasReflectionControl
            ? glossyReflections >= 0.5f
            : parent?.ApplyReflection ?? useReflection;
        bool hasEmissionMap = emissionMapGuid != null;

        int renderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? -1;
        string alphaMode = DetermineAlphaMode(floats, renderQueue, parent?.AlphaMode);
        bool hasZWrite = TryFloat(floats, out float zWrite, "_ZWrite");

        return new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString() ?? parent?.Name,
            IsLilToon = false,
            Color = color,
            MainTexGuid = mainTexGuid,
            MainTexScale = TexScale(mainTexName, parent?.MainTexScale ?? Vec2.One),
            MainTexOffset = TexOffset(mainTexName, parent?.MainTexOffset ?? Vec2.Zero),
            NormalMapGuid = normalMapGuid,
            NormalMapScale = TexScale(normalMapName, parent?.NormalMapScale ?? Vec2.One),
            NormalMapOffset = TexOffset(normalMapName, parent?.NormalMapOffset ?? Vec2.Zero),
            NormalScale = Float(floats, parent?.NormalScale ?? 1f, "_BumpScale", "_NormalScale"),
            AlphaMode = alphaMode,
            Cutoff = Float(floats, parent?.Cutoff ?? 0.5f,
                "_Cutoff", "_AlphaClipThreshold", "_AlphaCutoff"),
            ZWrite = hasZWrite ? zWrite >= 0.5f : parent?.ZWrite ?? (alphaMode is "opaque" or "cutout"),
            RenderQueue = renderQueue >= 0 ? renderQueue : parent?.RenderQueue ?? -1,
            Cull = (int)Float(floats, parent?.Cull ?? 2f, "_Cull", "_CullMode"),
            ColorMask = (int)Float(floats, parent?.ColorMask ?? 15f, "_ColorMask"),

            UseReflection = useReflection,
            Metallic = hasMetallic ? metallic : parent?.Metallic ?? 0f,
            Reflectance = parent?.Reflectance ?? 0.5f,
            Smoothness = metallicMapGuid != null ? smoothnessWithMap : smoothnessWithoutMap,
            SmoothnessWithoutMap = smoothnessWithoutMap,
            SmoothnessWithMap = smoothnessWithMap,
            ApplySpecular = applySpecular,
            ApplyReflection = applyReflection,
            MetallicGlossMapGuid = metallicMapGuid,
            MetallicGlossMapScale = TexScale(metallicMapName, parent?.MetallicGlossMapScale ?? Vec2.One),
            MetallicGlossMapOffset = TexOffset(metallicMapName, parent?.MetallicGlossMapOffset ?? Vec2.Zero),

            UseEmission = hasEmissionMap || HasVisibleRgb(emissionColor),
            EmissionColor = emissionColor,
            EmissionBlend = 1f,
            EmissionMapGuid = emissionMapGuid,
            EmissionMapScale = TexScale(emissionMapName, parent?.EmissionMapScale ?? Vec2.One),
            EmissionMapOffset = TexOffset(emissionMapName, parent?.EmissionMapOffset ?? Vec2.Zero),

            OcclusionMapGuid = occlusionMapGuid,
            OcclusionMapScale = TexScale(occlusionMapName, parent?.OcclusionMapScale ?? Vec2.One),
            OcclusionMapOffset = TexOffset(occlusionMapName, parent?.OcclusionMapOffset ?? Vec2.Zero),
            OcclusionMapChannel = (int)Float(floats, defaultOcclusionChannel, "_OcclusionMapChannel"),
            OcclusionStrength = Float(floats, parent?.OcclusionStrength ?? 1f, "_OcclusionStrength"),
        };
    }

    private static string DetermineAlphaMode(YamlNode floats, int renderQueue, string parentMode)
    {
        bool hasAlphaClip = TryFloat(floats, out float alphaClip,
            "_AlphaClip", "_AlphaTest", "_AlphaToMask", "_AlphaCutoffEnable");
        bool useAlphaClip = hasAlphaClip && alphaClip >= 0.5f;
        if (TryFloat(floats, out float surface, "_Surface", "_SurfaceType"))
        {
            return surface >= 0.5f
                ? DetermineTransparentBlendMode(floats)
                : useAlphaClip ? "cutout" : "opaque";
        }
        if (TryFloat(floats, out float mode, "_Mode"))
        {
            if (mode >= 2.5f) return "premultiply";
            if (mode >= 1.5f) return "transparent";
            if (mode >= 0.5f) return "cutout";
            return useAlphaClip ? "cutout" : "opaque";
        }
        string blendMode = DetermineBlendFactorMode(floats);
        if (blendMode != null)
        {
            return useAlphaClip && blendMode == "opaque" ? "cutout" : blendMode;
        }
        if (renderQueue == 2450) return "cutout";
        if (renderQueue >= 2501) return "transparent";
        if (useAlphaClip) return "cutout";
        if (hasAlphaClip && parentMode == "cutout") return "opaque";
        return parentMode ?? "opaque";
    }

    private static string DetermineTransparentBlendMode(YamlNode floats)
    {
        if (TryFloat(floats, out float blend, "_Blend"))
        {
            // Unity URP BlendMode: Alpha=0, Premultiply=1, Additive=2, Multiply=3.
            return (int)blend switch
            {
                1 => "premultiply",
                2 => "additive",
                3 => "multiply",
                _ => "transparent",
            };
        }
        return DetermineBlendFactorMode(floats) ?? "transparent";
    }

    private static string DetermineBlendFactorMode(YamlNode floats)
    {
        if (!TryFloat(floats, out float dstBlend, "_DstBlend"))
        {
            return null;
        }
        TryFloat(floats, out float srcBlend, "_SrcBlend");
        // UnityEngine.Rendering.BlendMode: Zero=0, One=1, DstColor=2,
        // OneMinusSrcAlpha=10.
        if ((int)dstBlend == 10) return (int)srcBlend == 1 ? "premultiply" : "transparent";
        if ((int)dstBlend == 1) return (int)srcBlend == 2 ? "multiply" : "additive";
        if ((int)dstBlend == 0) return (int)srcBlend == 2 ? "multiply" : "opaque";
        return null;
    }

    internal static Vec4 ReadColor(YamlNode properties, Vec4 fallback, params string[] names)
    {
        string name = FindName(properties, names);
        return name == null ? fallback : LilToonConverter.ReadColor(properties[name], fallback);
    }

    internal static float Float(YamlNode properties, float fallback, params string[] names)
        => TryFloat(properties, out float value, names) ? value : fallback;

    internal static bool TryFloat(YamlNode properties, out float value, params string[] names)
    {
        string name = FindName(properties, names);
        if (name != null)
        {
            value = properties[name].AsFloat();
            return true;
        }
        value = default;
        return false;
    }

    internal static string FindName(YamlNode properties, params string[] names)
    {
        if (properties?.Map == null)
        {
            return null;
        }
        foreach (string candidate in names)
        {
            if (properties.Map.ContainsKey(candidate))
            {
                return candidate;
            }
            string normalized = Normalize(candidate);
            string match = properties.Map.Keys.FirstOrDefault(key => Normalize(key) == normalized);
            if (match != null)
            {
                return match;
            }
        }
        return null;
    }

    internal static string TextureGuid(YamlNode textureEnv)
    {
        string guid = textureEnv?["m_Texture"]?.Guid;
        return string.IsNullOrEmpty(guid) || guid == "0000000000000000f000000000000000" ? null : guid;
    }

    private static string Normalize(string propertyName)
        => propertyName.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    internal static YamlNode MergeProperties(YamlNode primary, YamlNode secondary)
    {
        var map = new Dictionary<string, YamlNode>();
        if (secondary?.Map != null)
        {
            foreach ((string name, YamlNode value) in secondary.Map)
            {
                map[name] = value;
            }
        }
        if (primary?.Map != null)
        {
            foreach ((string name, YamlNode value) in primary.Map)
            {
                map[name] = value;
            }
        }
        return new YamlNode { Map = map };
    }

    private static bool HasVisibleRgb(Vec4 color)
        => color.X != 0f || color.Y != 0f || color.Z != 0f;
}
