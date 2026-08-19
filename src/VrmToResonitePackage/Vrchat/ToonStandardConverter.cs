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
        string TexOrParent(string name, string parentGuid) =>
            texEnvs?.Map?.ContainsKey(name) == true ? Tex(name) : parentGuid;
        float F(string name, float fallback) =>
            ShaderPropertyFallbackConverter.Float(floats, fallback, name);
        Vec4 C(string name, Vec4 fallback) =>
            ShaderPropertyFallbackConverter.ReadColor(colors, fallback, name);
        Vec2 TexScale(string name, Vec2 fallback) =>
            LilToonConverter.ReadVector2(texEnvs?[name]?["m_Scale"], fallback);
        Vec2 TexOffset(string name, Vec2 fallback) =>
            LilToonConverter.ReadVector2(texEnvs?[name]?["m_Offset"], fallback);
        bool Feature(string keyword, bool fallback) =>
            ShaderPropertyFallbackConverter.HasKeyword(root, keyword) ?? fallback;

        string rampGuid = TexOrParent("_Ramp", parent?.ShadowRampGuid);
        bool useNormalMap = Feature("USE_NORMAL_MAP",
            parent?.UseNormalMap ?? Tex("_BumpMap") != null);
        bool useSpecular = Feature("USE_SPECULAR", parent?.UseReflection ?? false);
        bool useMatcap = Feature("USE_MATCAP", parent?.UseMatcap ?? false);
        bool useOcclusion = Feature("USE_OCCLUSION_MAP", parent?.OcclusionMapGuid != null);
        bool useRim = Feature("USE_RIMLIGHT", parent?.UseRim ?? false);
        bool useEmission = Feature("USE_EMISSION", parent?.UseEmission ?? false);
        float parentMatcapType = parent == null || parent.MatcapBlendMode == 1 ? 0f : 1f;
        int matcapType = (int)F("_MatcapType", parentMatcapType);
        string matcapMaskGuid = TexOrParent("_MatcapMask", parent?.MatcapBlendMaskGuid);

        string metallicMapGuid = TexOrParent("_MetallicMap", parent?.MetallicMapGuid);
        string glossMapGuid = TexOrParent("_GlossMap", parent?.GlossMapGuid);
        Vec2 metallicMapScale = TexScale("_MetallicMap", parent?.MetallicMapScale ?? Vec2.One);
        Vec2 metallicMapOffset = TexOffset("_MetallicMap", parent?.MetallicMapOffset ?? Vec2.Zero);
        Vec2 glossMapScale = TexScale("_GlossMap", parent?.GlossMapScale ?? Vec2.One);
        Vec2 glossMapOffset = TexOffset("_GlossMap", parent?.GlossMapOffset ?? Vec2.Zero);
        Vec2 metallicGlossMapScale;
        Vec2 metallicGlossMapOffset;
        if (metallicMapGuid != null && glossMapGuid != null &&
            (!NearlyEqual(metallicMapScale, glossMapScale) ||
             !NearlyEqual(metallicMapOffset, glossMapOffset)))
        {
            // XiexeToon has one transform for the packed texture. Bake both source transforms
            // into the generated channels in the material's base UV space.
            metallicGlossMapScale = Vec2.One;
            metallicGlossMapOffset = Vec2.Zero;
        }
        else if (metallicMapGuid != null)
        {
            metallicGlossMapScale = metallicMapScale;
            metallicGlossMapOffset = metallicMapOffset;
        }
        else if (glossMapGuid != null)
        {
            metallicGlossMapScale = glossMapScale;
            metallicGlossMapOffset = glossMapOffset;
        }
        else
        {
            metallicGlossMapScale = Vec2.One;
            metallicGlossMapOffset = Vec2.Zero;
        }

        Vec4 emissionColor = C("_EmissionColor", parent?.EmissionColor ?? new Vec4(0f, 0f, 0f, 1f));
        float emissionStrength = F("_EmissionStrength", parent?.EmissionStrength ?? 1f);
        string emissionMapGuid = TexOrParent("_EmissionMap", parent?.EmissionMapGuid);
        int renderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? -1;

        return new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString() ?? parent?.Name,
            IsLilToon = false,
            IsToonStandard = true,
            Color = C("_Color", parent?.Color ?? Vec4.One),
            MainTexGuid = TexOrParent("_MainTex", parent?.MainTexGuid),
            MainTexScale = TexScale("_MainTex", parent?.MainTexScale ?? Vec2.One),
            MainTexOffset = TexOffset("_MainTex", parent?.MainTexOffset ?? Vec2.Zero),
            NormalMapGuid = TexOrParent("_BumpMap", parent?.NormalMapGuid),
            UseNormalMap = useNormalMap,
            NormalMapScale = TexScale("_BumpMap", parent?.NormalMapScale ?? Vec2.One),
            NormalMapOffset = TexOffset("_BumpMap", parent?.NormalMapOffset ?? Vec2.Zero),
            NormalScale = F("_BumpScale", parent?.NormalScale ?? 1f),
            AlphaMode = "opaque",
            ZWrite = true,
            RenderQueue = renderQueue >= 0 ? renderQueue : parent?.RenderQueue ?? -1,
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
            MetallicMapScale = metallicMapScale,
            MetallicMapOffset = metallicMapOffset,
            GlossMapGuid = glossMapGuid,
            GlossMapChannel = (int)F("_GlossMapChannel", parent?.GlossMapChannel ?? 3f),
            GlossMapScale = glossMapScale,
            GlossMapOffset = glossMapOffset,
            MetallicGlossMapScale = metallicGlossMapScale,
            MetallicGlossMapOffset = metallicGlossMapOffset,

            UseMatcap = useMatcap && matcapType == 0 && matcapMaskGuid == null,
            MatcapGuid = TexOrParent("_Matcap", parent?.MatcapGuid),
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

            UseEmission = useEmission && (emissionMapGuid != null || HasVisibleRgb(emissionColor)),
            EmissionColor = emissionColor,
            EmissionStrength = emissionStrength,
            EmissionMapGuid = emissionMapGuid,
            EmissionMapScale = TexScale("_EmissionMap", parent?.EmissionMapScale ?? Vec2.One),
            EmissionMapOffset = TexOffset("_EmissionMap", parent?.EmissionMapOffset ?? Vec2.Zero),

            OcclusionMapGuid = useOcclusion
                ? TexOrParent("_OcclusionMap", parent?.OcclusionMapGuid)
                : null,
            OcclusionMapScale = TexScale("_OcclusionMap", parent?.OcclusionMapScale ?? Vec2.One),
            OcclusionMapOffset = TexOffset("_OcclusionMap", parent?.OcclusionMapOffset ?? Vec2.Zero),
            OcclusionMapChannel = (int)F("_OcclusionMapChannel", parent?.OcclusionMapChannel ?? 1f),
            OcclusionStrength = F("_OcclusionStrength", parent?.OcclusionStrength ?? 1f),

            UseOutline = isOutline,
            OutlineWidth = F("_OutlineThickness", parent?.OutlineWidth ?? 0.05f),
            OutlineColor = C("_OutlineColor", parent?.OutlineColor ?? new Vec4(0f, 0f, 0f, 1f)),
            // Toon Standard's outline pass is not multiplied by scene lighting.
            OutlineLit = false,
            OutlineAlbedoTint = F("_OutlineFromAlbedo", parent?.OutlineAlbedoTint == true ? 1f : 0f) >= 0.5f,
            OutlineMaskGuid = TexOrParent("_OutlineMask", parent?.OutlineMaskGuid),
            OutlineMaskChannel = (int)F("_OutlineMaskChannel", parent?.OutlineMaskChannel ?? 0f),
        };
    }

    private static bool HasVisibleRgb(Vec4 color)
        => color.X != 0f || color.Y != 0f || color.Z != 0f;

    private static bool NearlyEqual(Vec2 left, Vec2 right)
        => MathF.Abs(left.X - right.X) < 1e-6f && MathF.Abs(left.Y - right.Y) < 1e-6f;
}
