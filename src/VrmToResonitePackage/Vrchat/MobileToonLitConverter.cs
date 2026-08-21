using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Parses VRChat's legacy Quest-compatible Mobile/Toon Lit shader.</summary>
internal static class MobileToonLitConverter
{
    private const string ShaderName = "VRChat/Mobile/Toon Lit";
    private const string ShaderGuid = "affc81f3d164d734d8f13053effb1c5c";

    public static bool IsMobileToonLit(YamlDocument material, string shaderName)
    {
        string guid = material?.Root?["m_Shader"]?.Guid;
        return string.Equals(guid, ShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shaderName, ShaderName, StringComparison.Ordinal);
    }

    public static LilToonInfo Parse(YamlDocument material, LilToonInfo parent = null)
    {
        YamlNode root = material.Root;
        YamlNode texEnvs = LilToonConverter.FlattenProps(
            root?["m_SavedProperties"]?["m_TexEnvs"]);
        YamlNode mainTex = texEnvs?["_MainTex"];
        bool hasMainTex = texEnvs?.Map?.ContainsKey("_MainTex") == true;
        int renderQueue = root?["m_CustomRenderQueue"]?.AsInt(-1) ?? -1;

        return new LilToonInfo
        {
            Name = root?["m_Name"]?.AsString() ?? parent?.Name,
            IsLilToon = false,
            IsMobileToonLit = true,

            // Toon Lit only declares _MainTex. Unity can retain stale Standard-shader values
            // such as _Color when a material changes shader; the source shader never reads them.
            Color = Vec4.One,
            MainTexGuid = hasMainTex
                ? ShaderPropertyFallbackConverter.TextureGuid(mainTex)
                : parent?.MainTexGuid,
            MainTexScale = hasMainTex
                ? LilToonConverter.ReadVector2(mainTex?["m_Scale"], Vec2.One)
                : parent?.MainTexScale ?? Vec2.One,
            MainTexOffset = hasMainTex
                ? LilToonConverter.ReadVector2(mainTex?["m_Offset"], Vec2.Zero)
                : parent?.MainTexOffset ?? Vec2.Zero,

            AlphaMode = "opaque",
            ZWrite = true,
            RenderQueue = renderQueue >= 0 ? renderQueue : parent?.RenderQueue ?? -1,
            Cull = 2,
            UseVertexColors = false,

            // The VRChat shader applies direct and indirect light without N.L shading. A
            // constant-white toon ramp keeps XiexeToon's light response flat across the mesh.
            UseShadow = true,
            ShadowColor = Vec4.One,
            ShadowStrength = 1f,
            ShadowBorder = 0.5f,
            ShadowBlur = 0f,

            UseNormalMap = false,
            UseReflection = false,
            UseEmission = false,
            UseOcclusionMap = false,
            UseRim = false,
            UseOutline = false,
        };
    }
}
