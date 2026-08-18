using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Parses VRChat's mobile Toon Standard shaders into XiexeToon-compatible properties.</summary>
internal static class ToonStandardConverter
{
    private const string ShaderName = "VRChat/Mobile/Toon Standard";
    private const string ShaderGuid = "e765db0afa7ecfc44ade2e4e2491f65a";
    private const string OutlineShaderGuid = "051a0ed2f2aedd741aa8186ae92f97e0";

    public static bool IsToonStandard(YamlDocument material, string shaderName)
    {
        string guid = material?.Root?["m_Shader"]?.Guid;
        return string.Equals(guid, ShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guid, OutlineShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            shaderName?.StartsWith(ShaderName, StringComparison.Ordinal) == true;
    }

    public static bool IsOutline(YamlDocument material, string shaderName)
        => string.Equals(material?.Root?["m_Shader"]?.Guid, OutlineShaderGuid,
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(shaderName, $"{ShaderName} (Outline)", StringComparison.Ordinal);

    public static LilToonInfo Parse(YamlDocument material, LilToonInfo parent, bool isOutline)
    {
        YamlNode root = material.Root;
        YamlNode props = root?["m_SavedProperties"];
        YamlNode floats = ShaderPropertyFallbackConverter.MergeProperties(
            LilToonConverter.FlattenProps(props?["m_Floats"]),
            LilToonConverter.FlattenProps(props?["m_Ints"]));
        YamlNode colors = LilToonConverter.FlattenProps(props?["m_Colors"]);
        YamlNode texEnvs = LilToonConverter.FlattenProps(props?["m_TexEnvs"]);

        string Tex(string name) => ShaderPropertyFallbackConverter.TextureGuid(texEnvs?[name]);
        float F(string name, float fallback) =>
            ShaderPropertyFallbackConverter.Float(floats, fallback, name);
        Vec4 C(string name, Vec4 fallback) =>
            ShaderPropertyFallbackConverter.ReadColor(colors, fallback, name);
        Vec2 TexScale(string name, Vec2 fallback) =>
            LilToonConverter.ReadVector2(texEnvs?[name]?["m_Scale"], fallback);
        Vec2 TexOffset(string name, Vec2 fallback) =>
            LilToonConverter.ReadVector2(texEnvs?[name]?["m_Offset"], fallback);
        bool Feature(string keyword, bool fallback) => HasKeyword(root, keyword) ?? fallback;

        string rampGuid = Tex("_Ramp") ?? parent?.ShadowRampGuid;
        bool useSpecular = Feature("USE_SPECULAR", parent?.UseReflection ?? false);
        bool useMatcap = Feature("USE_MATCAP", parent?.UseMatcap ?? false);
        bool useOcclusion = Feature("USE_OCCLUSION_MAP", parent?.OcclusionMapGuid != null);
        bool useRim = Feature("USE_RIMLIGHT", parent?.UseRim ?? false);
        float parentMatcapType = parent == null || parent.MatcapBlendMode == 1 ? 0f : 1f;
        int matcapType = (int)F("_MatcapType", parentMatcapType);
        string matcapMaskGuid = Tex("_MatcapMask") ?? parent?.MatcapBlendMaskGuid;

        string metallicMapGuid = Tex("_MetallicMap") ?? parent?.MetallicMapGuid;
        string glossMapGuid = Tex("_GlossMap") ?? parent?.GlossMapGuid;
        string metallicGlossTransformName = metallicMapGuid != null ? "_MetallicMap" : "_GlossMap";

        Vec4 emissionColor = C("_EmissionColor", parent?.EmissionColor ?? new Vec4(0f, 0f, 0f, 1f));
        float emissionStrength = F("_EmissionStrength", parent?.EmissionStrength ?? 1f);
        string emissionMapGuid = Tex("_EmissionMap") ?? parent?.EmissionMapGuid;

        return new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString() ?? parent?.Name,
            IsLilToon = false,
            IsToonStandard = true,
            Color = C("_Color", parent?.Color ?? Vec4.One),
            MainTexGuid = Tex("_MainTex") ?? parent?.MainTexGuid,
            MainTexScale = TexScale("_MainTex", parent?.MainTexScale ?? Vec2.One),
            MainTexOffset = TexOffset("_MainTex", parent?.MainTexOffset ?? Vec2.Zero),
            NormalMapGuid = Tex("_BumpMap") ?? parent?.NormalMapGuid,
            NormalMapScale = TexScale("_BumpMap", parent?.NormalMapScale ?? Vec2.One),
            NormalMapOffset = TexOffset("_BumpMap", parent?.NormalMapOffset ?? Vec2.Zero),
            NormalScale = F("_BumpScale", parent?.NormalScale ?? 1f),
            AlphaMode = "opaque",
            ZWrite = true,
            RenderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? parent?.RenderQueue ?? -1,
            Cull = (int)F("_Culling", parent?.Cull ?? 2f),
            UseVertexColors = F("_VertexColor", parent?.UseVertexColors == true ? 1f : 0f) >= 0.5f,

            UseShadow = rampGuid != null,
            ShadowRampGuid = rampGuid,

            UseReflection = useSpecular,
            Metallic = F("_MetallicStrength", parent?.Metallic ?? 0f),
            Smoothness = F("_GlossStrength", parent?.Smoothness ?? 0.5f),
            Reflectance = F("_Reflectance", parent?.Reflectance ?? 0.5f),
            ApplySpecular = useSpecular,
            ApplyReflection = useSpecular,
            MetallicMapGuid = metallicMapGuid,
            MetallicMapChannel = (int)F("_MetallicMapChannel", parent?.MetallicMapChannel ?? 0f),
            GlossMapGuid = glossMapGuid,
            GlossMapChannel = (int)F("_GlossMapChannel", parent?.GlossMapChannel ?? 3f),
            MetallicGlossMapScale = TexScale(metallicGlossTransformName,
                parent?.MetallicGlossMapScale ?? Vec2.One),
            MetallicGlossMapOffset = TexOffset(metallicGlossTransformName,
                parent?.MetallicGlossMapOffset ?? Vec2.Zero),

            UseMatcap = useMatcap && matcapType == 0 && matcapMaskGuid == null,
            MatcapGuid = Tex("_Matcap") ?? parent?.MatcapGuid,
            MatcapBlend = F("_MatcapStrength", parent?.MatcapBlend ?? 1f),
            MatcapColor = Vec4.One,
            MatcapBlendMode = matcapType == 0 ? 1 : 0,
            MatcapBlendMaskGuid = matcapMaskGuid,

            UseRim = useRim,
            RimColor = C("_RimColor", parent?.RimColor ?? Vec4.One),
            RimIntensity = F("_RimIntensity", parent?.RimIntensity ?? 0.5f),
            RimRange = 1f - F("_RimRange", parent != null ? 1f - parent.RimRange : 0.3f),
            RimSharpness = F("_RimSharpness", parent?.RimSharpness ?? 0.1f),
            RimAlbedoTint = F("_RimAlbedoTint", parent?.RimAlbedoTint ?? 0f),

            UseEmission = emissionMapGuid != null || HasVisibleRgb(emissionColor),
            EmissionColor = emissionColor,
            EmissionStrength = emissionStrength,
            EmissionMapGuid = emissionMapGuid,
            EmissionMapScale = TexScale("_EmissionMap", parent?.EmissionMapScale ?? Vec2.One),
            EmissionMapOffset = TexOffset("_EmissionMap", parent?.EmissionMapOffset ?? Vec2.Zero),

            OcclusionMapGuid = useOcclusion ? Tex("_OcclusionMap") ?? parent?.OcclusionMapGuid : null,
            OcclusionMapScale = TexScale("_OcclusionMap", parent?.OcclusionMapScale ?? Vec2.One),
            OcclusionMapOffset = TexOffset("_OcclusionMap", parent?.OcclusionMapOffset ?? Vec2.Zero),
            OcclusionMapChannel = (int)F("_OcclusionMapChannel", parent?.OcclusionMapChannel ?? 1f),
            OcclusionStrength = F("_OcclusionStrength", parent?.OcclusionStrength ?? 1f),

            UseOutline = isOutline || parent?.UseOutline == true,
            OutlineWidth = F("_OutlineThickness", parent?.OutlineWidth ?? 0.05f),
            OutlineColor = C("_OutlineColor", parent?.OutlineColor ?? new Vec4(0f, 0f, 0f, 1f)),
            // Toon Standard's outline pass is not multiplied by scene lighting.
            OutlineLit = false,
            OutlineAlbedoTint = F("_OutlineFromAlbedo", parent?.OutlineAlbedoTint == true ? 1f : 0f) >= 0.5f,
            OutlineMaskGuid = Tex("_OutlineMask") ?? parent?.OutlineMaskGuid,
            OutlineMaskChannel = (int)F("_OutlineMaskChannel", parent?.OutlineMaskChannel ?? 0f),
        };
    }

    private static bool? HasKeyword(YamlNode root, string keyword)
    {
        YamlNode valid = root?["m_ValidKeywords"];
        if (valid?.Seq != null)
        {
            return valid.Seq.Any(item => string.Equals(item.AsString(), keyword, StringComparison.Ordinal));
        }
        string keywords = root?["m_ShaderKeywords"]?.AsString();
        if (keywords != null)
        {
            return keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(keyword,
                StringComparer.Ordinal);
        }
        return null;
    }

    private static bool HasVisibleRgb(Vec4 color)
        => color.X != 0f || color.Y != 0f || color.Z != 0f;
}
