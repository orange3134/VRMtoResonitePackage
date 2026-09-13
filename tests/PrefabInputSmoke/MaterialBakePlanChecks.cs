using System.Numerics;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class MaterialBakePlanChecks
{
    public static void Run()
    {
        var info = new LilToonInfo { Color = new Vector4(0.3f, 0.4f, 0.5f, 0.6f) };
        Check(!Plan().Required, "1st tint alone stays on the material without baking");
        info.Main2nd.Color = info.Main3rd.Color = new Vector4(0.2f);
        Check(!Plan().Required, "Colors on disabled 2nd/3rd layers do not request a bake");
        for (int uv = 0; uv <= 4; uv++)
        {
            info.Main2nd = new() { Enabled = true, UVMode = uv, TextureGuid = "second" };
            info.Main3rd = new() { Enabled = true, UVMode = 0, TextureGuid = "third" };
            Check(Plan().Main2nd == (uv == 0) && Plan().Main3rd && Plan().Color,
                $"UV mode {uv} is evaluated separately for 2nd and 3rd");
            info.Main2nd.TextureGuid = null;
            Check(Plan().Main2nd, $"Color-only layer uses white regardless of UV mode {uv}");
            info.Main2nd = new() { Enabled = true, TextureGuid = "second" };
            info.Main3rd.UVMode = uv;
            Check(Plan().Main2nd && Plan().Main3rd == (uv == 0), $"3rd UV mode {uv} does not disable eligible 2nd");
        }
        info.Main2nd.UVMode = info.Main3rd.UVMode = 1;
        Check(!Plan().Required && Plan().Warnings.Count == 2, "Both non-UV0 images are excluded with individual diagnostics");
        info.AlphaMaskGuid = "mask";
        Check(!Plan().Alpha, "Assigned alpha mask with mode zero does not trigger a bake");
        info.AlphaMaskMode = 2;
        Check(Plan().Alpha && !Plan().Color, "Alpha mask bakes independently while non-UV0 layers stay excluded");
        info.AlphaMaskGuid = null;
        Check(!Plan().Required, "Nonzero alpha mask mode without a texture does not trigger a bake");
        info.MainTexHSVG = new Vector4(0.1f, 1, 1, 1);
        Check(Plan().Main && Plan().Color, "HSVG correction triggers 1st color baking without eligible layers");
        info.MainTexHSVG = new Vector4(0, 1, 1, 1);
        info.MainGradationStrength = 0.5f;
        Check(Plan().Main, "Gradation strength triggers 1st color baking");
        info.AlphaMaskGuid = "missing-mask";
        var missing = LilToonMainTextureBakePlan.Create(info, _ => false);
        Check(!missing.Alpha && missing.Main && missing.Main2nd && missing.Main3rd,
            "Unresolved texture references behave like Unity null: missing alpha cannot block color, missing layers use white");
        info.IsLilToon = false;
        Check(!Plan().Required, "Non-lilToon materials never use this bake policy");

        var parent = LilToonConverter.Parse(Doc("""
              m_Floats:
              - _AlphaMaskMode: 2
              - _AlphaMaskScale: 0.4
              - _AlphaMaskValue: 0.1
              - _MainGradationStrength: 0.3
              - _UseMain2ndTex: 1
              - _Main2ndTex_UVMode: 2
              m_Colors:
              - _Color: {r: 0.3, g: 0.4, b: 0.5, a: 0.6}
              - _MainTexHSVG: {r: 0.1, g: 0.2, b: 0.3, a: 0.4}
              m_TexEnvs:
              - _AlphaMask:
                  m_Texture: {fileID: 2800000, guid: mask}
                  m_Scale: {x: 2, y: 3}
                  m_Offset: {x: 0.1, y: 0.2}
              - _MainGradationTex:
                  m_Texture: {fileID: 2800000, guid: gradient}
              - _MainColorAdjustMask:
                  m_Texture: {fileID: 2800000, guid: adjust}
              - _Main2ndTex:
                  m_Texture: {fileID: 2800000, guid: layer}
            """));
        var child = LilToonConverter.Parse(Doc("""
              m_Floats:
              - _AlphaMaskMode: 0
              - _MainGradationStrength: 0
              m_TexEnvs:
              - _Main2ndTex:
                  m_Texture: {fileID: 0}
              - _MainColorAdjustMask:
                  m_Texture: {fileID: 0}
            """), parent);
        Check(child.MainTexHSVG == parent.MainTexHSVG && child.Color == parent.Color &&
              child.AlphaMaskScale == 0.4f && child.AlphaMaskValue == 0.1f &&
              child.AlphaMaskTexScale == new Vector2(2, 3) && child.AlphaMaskTexOffset == new Vector2(0.1f, 0.2f) &&
              child.AlphaMaskGuid == "mask" && child.MainGradationTexGuid == "gradient" && child.MainColorAdjustMaskGuid == null,
            "Material variants inherit HSVG, color, alpha mask transforms and gradation textures; explicit null clears masks");
        var inheritedPlan = LilToonMainTextureBakePlan.Create(child);
        Check(!inheritedPlan.Alpha && child.MainGradationStrength == 0 && inheritedPlan.Main2nd &&
              child.Main2nd.UVMode == 2 && parent.AlphaMaskMode == 2 && parent.Main2nd.TextureGuid == "layer",
            "Explicit zero disables inherited alpha/gradation, and clearing a non-UV0 image permits a color-only bake");
        LilToonMainTextureBakePlan Plan() => LilToonMainTextureBakePlan.Create(info);
    }

    private static YamlDocument Doc(string properties) => UnityYaml.ParseDocuments(
        "--- !u!21 &2100000\nMaterial:\n  m_Name: Bake policy\n  m_SavedProperties:\n" +
        string.Join("\n", properties.Split('\n').Select(line => "  " + line))).Single();
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
