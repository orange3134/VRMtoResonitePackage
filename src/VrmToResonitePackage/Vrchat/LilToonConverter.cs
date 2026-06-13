using Vec4 = System.Numerics.Vector4;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Engine-independent liltoon material properties, normalized for XiexeToon conversion.</summary>
public sealed class LilToonInfo
{
    public string Name { get; set; }

    public Vec4 Color { get; set; } = new(1f, 1f, 1f, 1f);
    public string MainTexGuid { get; set; }
    public string NormalMapGuid { get; set; }

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

    public bool UseRim { get; set; }
    public Vec4 RimColor { get; set; } = new(1f, 1f, 1f, 1f);
    public float RimBorder { get; set; } = 0.5f;
    public float RimBlur { get; set; } = 0.1f;
    public float RimFresnelPower { get; set; } = 3f;

    public bool UseOutline { get; set; }
    public float OutlineWidth { get; set; }
    public Vec4 OutlineColor { get; set; } = new(0f, 0f, 0f, 1f);
    public bool OutlineLit { get; set; } = true;
    public string OutlineMaskGuid { get; set; }

    public bool UseMatcap { get; set; }
    public Vec4 MatcapColor { get; set; } = new(1f, 1f, 1f, 1f);
    public int MatcapBlendMode { get; set; } = 1; // 0 = add, 1 = multiply
    public string MatcapGuid { get; set; }

    public bool UseEmission { get; set; }
    public Vec4 EmissionColor { get; set; } = new(0f, 0f, 0f, 1f);
    public string EmissionMapGuid { get; set; }
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

    public static LilToonInfo Parse(YamlDocument material)
    {
        YamlNode root = material.Root;
        YamlNode props = root?["m_SavedProperties"];
        YamlNode floats = FlattenProps(props?["m_Floats"]);
        YamlNode colors = FlattenProps(props?["m_Colors"]);
        YamlNode texEnvs = FlattenProps(props?["m_TexEnvs"]);

        float F(string name, float fallback = 0f) => floats?[name]?.AsFloat(fallback) ?? fallback;
        bool B(string name) => F(name) >= 0.5f;
        Vec4 C(string name, Vec4 fallback) => ReadColor(colors?[name], fallback);
        string Tex(string name) => texEnvs?[name]?["m_Texture"]?.Guid is string g && g != "0000000000000000f000000000000000" ? g : null;

        var info = new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString(),
            Color = C("_Color", new Vec4(1f, 1f, 1f, 1f)),
            MainTexGuid = Tex("_MainTex"),
            NormalMapGuid = B("_UseBumpMap") ? Tex("_BumpMap") : null,
            Cull = (int)F("_Cull", 2f),
            Cutoff = F("_Cutoff", 0.5f),
            ZWrite = F("_ZWrite", 1f) >= 0.5f,
            RenderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? -1,

            UseShadow = B("_UseShadow"),
            ShadowColor = C("_ShadowColor", new Vec4(1f, 1f, 1f, 1f)),
            ShadowBorder = F("_ShadowBorder", 0.5f),
            ShadowBlur = F("_ShadowBlur", 0.1f),
            ShadowStrength = F("_ShadowStrength", 1f),
            ShadowColorTexGuid = Tex("_ShadowColorTex"),

            UseRim = B("_UseRim"),
            RimColor = C("_RimColor", new Vec4(1f, 1f, 1f, 1f)),
            RimBorder = F("_RimBorder", 0.5f),
            RimBlur = F("_RimBlur", 0.1f),
            RimFresnelPower = F("_RimFresnelPower", 3f),

            UseOutline = B("_UseOutline"),
            OutlineWidth = F("_OutlineWidth", 0.08f),
            OutlineColor = C("_OutlineColor", new Vec4(0f, 0f, 0f, 1f)),
            OutlineLit = B("_OutlineEnableLighting"),
            OutlineMaskGuid = Tex("_OutlineWidthMask"),

            UseMatcap = B("_UseMatCap"),
            MatcapColor = C("_MatCapColor", new Vec4(1f, 1f, 1f, 1f)),
            MatcapBlendMode = (int)F("_MatCapBlendMode", 1f),
            MatcapGuid = B("_UseMatCap") ? Tex("_MatCap") : null,

            UseEmission = B("_UseEmission"),
            EmissionColor = C("_EmissionColor", new Vec4(0f, 0f, 0f, 1f)),
            EmissionMapGuid = B("_UseEmission") ? Tex("_EmissionMap") : null,
        };

        info.AlphaMode = DetermineAlphaMode(info, F("_AlphaToMask"));
        return info;
    }

    private static string DetermineAlphaMode(LilToonInfo info, float alphaToMask)
    {
        // liltoon picks the mode from the chosen shader variant / render queue.
        if (alphaToMask >= 0.5f || info.RenderQueue == 2450)
        {
            return "cutout";
        }
        if (info.RenderQueue >= 2451)
        {
            return "transparent";
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
}
