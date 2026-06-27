using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Engine-independent liltoon material properties, normalized for XiexeToon conversion.</summary>
public sealed class LilToonInfo
{
    public string Name { get; set; }

    public Vec4 Color { get; set; } = new(1f, 1f, 1f, 1f);
    public string MainTexGuid { get; set; }
    public Vec2 MainTexScale { get; set; } = Vec2.One;
    public Vec2 MainTexOffset { get; set; }
    public string NormalMapGuid { get; set; }
    public Vec2 NormalMapScale { get; set; } = Vec2.One;
    public Vec2 NormalMapOffset { get; set; }
    public float NormalScale { get; set; } = 1f;

    // opaque / cutout / transparent (+ ZWrite for transparent).
    public string AlphaMode { get; set; } = "opaque";
    public float Cutoff { get; set; } = 0.5f;
    public bool ZWrite { get; set; } = true;
    public int RenderQueue { get; set; } = -1;
    public int Cull { get; set; } = 2; // 0 = off (double sided), 1 = front, 2 = back

    public bool UseShadow { get; set; }
    public Vec4 ShadowColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float ShadowBorder { get; set; } = 0.5f;
    public float ShadowBlur { get; set; } = 0.1f;
    public float ShadowStrength { get; set; } = 1f;
    public string ShadowColorTexGuid { get; set; }
    public string ShadowStrengthMaskGuid { get; set; }
    public Vec2 ShadowStrengthMaskScale { get; set; } = Vec2.One;
    public Vec2 ShadowStrengthMaskOffset { get; set; }
    public string ShadowBorderMaskGuid { get; set; }
    public Vec2 ShadowBorderMaskScale { get; set; } = Vec2.One;
    public Vec2 ShadowBorderMaskOffset { get; set; }

    public bool UseRim { get; set; }
    public Vec4 RimColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float RimBorder { get; set; } = 0.5f;
    public float RimBlur { get; set; } = 0.1f;
    public float RimFresnelPower { get; set; } = 3f;

    public bool UseOutline { get; set; }
    public float OutlineWidth { get; set; }
    public Vec4 OutlineColor { get; set; } = new(0f, 0f, 0f, 1f);
    public bool OutlineLit { get; set; } = true;
    public bool OutlineAlbedoTint { get; set; }
    public string OutlineMaskGuid { get; set; }

    public bool UseMatcap { get; set; }
    public Vec4 MatcapColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float MatcapBlend { get; set; } = 1f;
    public int MatcapBlendMode { get; set; } = 1; // 1 = add
    public string MatcapGuid { get; set; }
    public string MatcapBlendMaskGuid { get; set; }

    public bool UseEmission { get; set; }
    public Vec4 EmissionColor { get; set; } = new(0f, 0f, 0f, 1f);
    public float EmissionBlend { get; set; } = 1f;
    public float EmissionFluorescence { get; set; }
    public string EmissionMapGuid { get; set; }
    public Vec2 EmissionMapScale { get; set; } = Vec2.One;
    public Vec2 EmissionMapOffset { get; set; }
    public string EmissionBlendMaskGuid { get; set; }
    public float EmissionMainStrength { get; set; }

    public bool UseReflection { get; set; }
    public float Metallic { get; set; }
    public float Reflectance { get; set; }
    public bool ApplySpecular { get; set; }
    public bool ApplyReflection { get; set; }
    public bool SpecularToon { get; set; }
    public float Smoothness { get; set; }
    public float SpecularBorder { get; set; }

    public int ColorMask { get; set; } = 15;
}

/// <summary>
/// Parses a liltoon .mat (Unity YAML) into <see cref="LilToonInfo"/>. liltoon ships many shader
/// variants (different guids per render mode), so a material is recognized by its characteristic
/// property set rather than its shader guid.
/// </summary>
public static class LilToonConverter
{
    public static bool IsLilToon(YamlDocument material)
    {
        YamlNode floats = FlattenProps(material?.Root?["m_SavedProperties"]?["m_Floats"]);
        // These properties are specific to liltoon's lighting model.
        return floats != null && (floats["_ShadowBorder"] != null || floats["_LightMinLimit"] != null);
    }

    public static LilToonInfo Parse(YamlDocument material, LilToonInfo parent = null, bool isOutlineShader = false)
    {
        YamlNode root = material.Root;
        YamlNode props = root?["m_SavedProperties"];
        YamlNode floats = FlattenProps(props?["m_Floats"]);
        YamlNode colors = FlattenProps(props?["m_Colors"]);
        YamlNode texEnvs = FlattenProps(props?["m_TexEnvs"]);

        float F(string name, float fallback = 0f) => floats?[name]?.AsFloat(fallback) ?? fallback;
        bool B(string name, bool fallback = false) => floats?[name] != null ? F(name) >= 0.5f : fallback;
        Vec4 C(string name, Vec4 fallback) => colors?[name] != null ? ReadColor(colors[name], fallback) : fallback;
        string Tex(string name) => texEnvs?[name]?["m_Texture"]?.Guid is string g && g != "0000000000000000f000000000000000" ? g : null;
        string TexAny(params string[] names) => names.Select(Tex).FirstOrDefault(g => !string.IsNullOrEmpty(g));
        Vec2 TexScale(string name, Vec2 fallback) => ReadVector2(texEnvs?[name]?["m_Scale"], fallback);
        Vec2 TexOffset(string name, Vec2 fallback) => ReadVector2(texEnvs?[name]?["m_Offset"], fallback);

        // Material Variants often serialize only the overridden outline values and omit
        // _UseOutline. Treat those overrides as enabling the feature, while preserving an
        // explicitly serialized _UseOutline value. lilToon's dedicated Outline shaders enable
        // it independently of _UseOutline, matching Resonite.UnitySDK's converter.
        bool hasOutlineProperties = floats?["_OutlineWidth"] != null ||
            floats?["_OutlineEnableLighting"] != null || floats?["_OutlineLitApplyTex"] != null ||
            colors?["_OutlineColor"] != null || texEnvs?["_OutlineWidthMask"] != null;
        bool useOutline = isOutlineShader || (floats?["_UseOutline"] != null
            ? B("_UseOutline")
            : hasOutlineProperties || parent?.UseOutline == true);

        int customRenderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? -1;

        var info = new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString(),
            Color = C("_Color", parent?.Color ?? new Vec4(1f, 1f, 1f, 1f)),
            MainTexGuid = TexAny("_MainTex", "_BaseMap", "_BaseColorMap") ?? parent?.MainTexGuid,
            MainTexScale = TexScale("_MainTex", parent?.MainTexScale ?? Vec2.One),
            MainTexOffset = TexOffset("_MainTex", parent?.MainTexOffset ?? Vec2.Zero),
            NormalMapGuid = B("_UseBumpMap", parent?.NormalMapGuid != null)
                ? Tex("_BumpMap") ?? parent?.NormalMapGuid
                : null,
            NormalMapScale = TexScale("_BumpMap", parent?.NormalMapScale ?? Vec2.One),
            NormalMapOffset = TexOffset("_BumpMap", parent?.NormalMapOffset ?? Vec2.Zero),
            NormalScale = F("_BumpScale", parent?.NormalScale ?? 1f),
            Cull = (int)F("_Cull", parent?.Cull ?? 2f),
            Cutoff = F("_Cutoff", parent?.Cutoff ?? 0.5f),
            ZWrite = F("_ZWrite", parent?.ZWrite == false ? 0f : 1f) >= 0.5f,
            RenderQueue = customRenderQueue >= 0 ? customRenderQueue : parent?.RenderQueue ?? -1,
            ColorMask = (int)F("_ColorMask", parent?.ColorMask ?? 15f),

            UseShadow = B("_UseShadow", parent?.UseShadow ?? false),
            ShadowColor = C("_ShadowColor", parent?.ShadowColor ?? new Vec4(1f, 1f, 1f, 1f)),
            ShadowBorder = F("_ShadowBorder", parent?.ShadowBorder ?? 0.5f),
            ShadowBlur = F("_ShadowBlur", parent?.ShadowBlur ?? 0.1f),
            ShadowStrength = F("_ShadowStrength", parent?.ShadowStrength ?? 1f),
            ShadowColorTexGuid = Tex("_ShadowColorTex") ?? parent?.ShadowColorTexGuid,
            ShadowStrengthMaskGuid = Tex("_ShadowStrengthMask") ?? parent?.ShadowStrengthMaskGuid,
            ShadowStrengthMaskScale = TexScale("_ShadowStrengthMask", parent?.ShadowStrengthMaskScale ?? Vec2.One),
            ShadowStrengthMaskOffset = TexOffset("_ShadowStrengthMask", parent?.ShadowStrengthMaskOffset ?? Vec2.Zero),
            ShadowBorderMaskGuid = Tex("_ShadowBorderMask") ?? parent?.ShadowBorderMaskGuid,
            ShadowBorderMaskScale = TexScale("_ShadowBorderMask", parent?.ShadowBorderMaskScale ?? Vec2.One),
            ShadowBorderMaskOffset = TexOffset("_ShadowBorderMask", parent?.ShadowBorderMaskOffset ?? Vec2.Zero),

            UseRim = B("_UseRim", parent?.UseRim ?? false),
            RimColor = C("_RimColor", parent?.RimColor ?? new Vec4(1f, 1f, 1f, 1f)),
            RimBorder = F("_RimBorder", parent?.RimBorder ?? 0.5f),
            RimBlur = F("_RimBlur", parent?.RimBlur ?? 0.1f),
            RimFresnelPower = F("_RimFresnelPower", parent?.RimFresnelPower ?? 3f),

            UseOutline = useOutline,
            OutlineWidth = F("_OutlineWidth", parent?.OutlineWidth ?? 0.08f),
            OutlineColor = C("_OutlineColor", parent?.OutlineColor ?? new Vec4(0f, 0f, 0f, 1f)),
            OutlineLit = B("_OutlineEnableLighting", parent?.OutlineLit ?? true),
            OutlineAlbedoTint = B("_OutlineLitApplyTex", parent?.OutlineAlbedoTint ?? false),
            OutlineMaskGuid = Tex("_OutlineWidthMask") ?? parent?.OutlineMaskGuid,

            UseMatcap = B("_UseMatCap", parent?.UseMatcap ?? false),
            MatcapColor = C("_MatCapColor", parent?.MatcapColor ?? new Vec4(1f, 1f, 1f, 1f)),
            MatcapBlend = F("_MatCapBlend", parent?.MatcapBlend ?? 1f),
            MatcapBlendMode = (int)F("_MatCapBlendMode", parent?.MatcapBlendMode ?? 1f),
            MatcapGuid = B("_UseMatCap", parent?.MatcapGuid != null)
                ? TexAny("_MatCapTex", "_MatCap") ?? parent?.MatcapGuid
                : null,
            MatcapBlendMaskGuid = Tex("_MatCapBlendMask") ?? parent?.MatcapBlendMaskGuid,

            UseEmission = B("_UseEmission", parent?.UseEmission ?? false),
            EmissionColor = C("_EmissionColor", parent?.EmissionColor ?? new Vec4(0f, 0f, 0f, 1f)),
            EmissionBlend = F("_EmissionBlend", parent?.EmissionBlend ?? 1f),
            EmissionFluorescence = F("_EmissionFluorescence", parent?.EmissionFluorescence ?? 0f),
            EmissionMapGuid = B("_UseEmission", parent?.EmissionMapGuid != null)
                ? Tex("_EmissionMap") ?? parent?.EmissionMapGuid
                : null,
            EmissionMapScale = TexScale("_EmissionMap", parent?.EmissionMapScale ?? Vec2.One),
            EmissionMapOffset = TexOffset("_EmissionMap", parent?.EmissionMapOffset ?? Vec2.Zero),
            EmissionBlendMaskGuid = Tex("_EmissionBlendMask") ?? parent?.EmissionBlendMaskGuid,
            EmissionMainStrength = F("_EmissionMainStrength", parent?.EmissionMainStrength ?? 0f),

            UseReflection = B("_UseReflection", parent?.UseReflection ?? false),
            Metallic = F("_Metallic", parent?.Metallic ?? 0f),
            Reflectance = F("_Reflectance", parent?.Reflectance ?? 0f),
            ApplySpecular = B("_ApplySpecular", parent?.ApplySpecular ?? false),
            ApplyReflection = B("_ApplyReflection", parent?.ApplyReflection ?? false),
            SpecularToon = B("_SpecularToon", parent?.SpecularToon ?? false),
            Smoothness = F("_Smoothness", parent?.Smoothness ?? 0f),
            SpecularBorder = F("_SpecularBorder", parent?.SpecularBorder ?? 0.5f),
        };

        info.AlphaMode = DetermineAlphaMode(info, F("_AlphaToMask"), (int)F("_SrcBlend", -1f),
            (int)F("_DstBlend", -1f), customRenderQueue >= 0, parent?.AlphaMode);
        return info;
    }

    private static string DetermineAlphaMode(LilToonInfo info, float alphaToMask, int srcBlend, int dstBlend,
        bool renderQueueSpecified, string parentAlphaMode)
    {
        // UnityEngine.Rendering.BlendMode: Zero=0, One=1, DstColor=2, OneMinusSrcAlpha=10.
        if (dstBlend == 0)
        {
            return alphaToMask >= 0.5f ? "cutout" : "opaque";
        }
        if (dstBlend == 1)
        {
            return srcBlend == 2 ? "multiply" : "additive";
        }
        if (dstBlend == 10)
        {
            // lilToon transparent passes premultiply internally. The source texture remains straight alpha.
            return "transparent";
        }
        if (alphaToMask >= 0.5f || info.RenderQueue == 2450)
        {
            return "cutout";
        }
        if (info.RenderQueue >= 2451)
        {
            return "transparent";
        }
        if (!renderQueueSpecified && parentAlphaMode != null)
        {
            return parentAlphaMode;
        }
        return "opaque";
    }

    /// <summary>
    /// Flattens a sequence of single-key maps (Unity's m_Floats/m_Colors/m_TexEnvs serialization,
    /// where each entry is "- _Prop: value") into one lookup map.
    /// </summary>
    private static YamlNode FlattenProps(YamlNode seq)
    {
        if (seq?.Seq == null)
        {
            return seq;
        }
        var map = new Dictionary<string, YamlNode>();
        foreach (YamlNode item in seq.Seq)
        {
            if (item?.Map != null)
            {
                foreach ((string key, YamlNode value) in item.Map)
                {
                    map[key] = value;
                }
            }
        }
        return new YamlNode { Map = map };
    }

    private static Vec4 ReadColor(YamlNode node, Vec4 fallback)
    {
        if (node == null || !node.IsMap)
        {
            return fallback;
        }
        return new Vec4(node.Vec("r"), node.Vec("g"), node.Vec("b"), node["a"] != null ? node.Vec("a") : 1f);
    }

    private static Vec2 ReadVector2(YamlNode node, Vec2 fallback)
    {
        if (node == null || !node.IsMap)
        {
            return fallback;
        }
        return new Vec2(node["x"]?.AsFloat(fallback.X) ?? fallback.X,
            node["y"]?.AsFloat(fallback.Y) ?? fallback.Y);
    }
}
