using System.Numerics;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class MaterialLayerChecks
{
    public static async Task Run(Slot root, string temp)
    {
        string folder = Path.Combine(temp, "LayerFixtures");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        const string baseGuid = "11111111111111111111111111111111";
        const string layerGuid = "22222222222222222222222222222222";
        const string maskGuid = "33333333333333333333333333333333";
        const string redGuid = "44444444444444444444444444444444";
        const string gradientGuid = "55555555555555555555555555555555";
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: aabbccddeeff11223344556677889900\n");
        Save("Base", baseGuid, new color(0, 0, 0, 0.4f), new color(0, 0, 0, 0.4f));
        Save("Layer", layerGuid, new color(1, 1, 1, 0.5f), new color(1, 0, 0, 0));
        Save("Mask", maskGuid, new color(1, 1, 1, 1), new color(0, 0, 0, 1));
        Save("Red", redGuid, new color(1, 0, 0, 0.4f), new color(1, 0, 0, 0.4f));
        Save("Gradient", gradientGuid, new color(0.5f, 0.25f, 0.75f, 1));
        File.AppendAllText(Path.Combine(folder, "Assets/Gradient.png.meta"), "  mipmaps:\n    sRGBTexture: 0\n");
        const string tintOnlyGuid = "66666666666666666666666666666666";
        const string alphaOnlyGuid = "77777777777777777777777777777777";
        const string colorAlphaGuid = "88888888888888888888888888888888";
        SaveMaterial(tintOnlyGuid, 0, false);
        SaveMaterial(alphaOnlyGuid, 2, false);
        SaveMaterial(colorAlphaGuid, 2, true);
        const string missingAlphaGuid = "99999999999999999999999999999999";
        SaveMaterial(missingAlphaGuid, 2, true, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var package = UnityPackage.Open(prefab);
        var builder = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder")!;
        Bitmap2D Bake(LilToonInfo info) => (Bitmap2D)builder.GetMethod("CompositeMainLayers",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { package, info, LilToonMainTextureBakePlan.Create(info) })!;
        var info = new LilToonInfo { Name = "Layer regression", MainTexGuid = baseGuid,
            Main2nd = new() { Enabled = true, TextureGuid = layerGuid } };
        var baked = Bake(info);
        Check(Near(baked.GetPixel(0, 0).r, 0.7366f) && Near(baked.GetPixel(0, 0).a, 0.4f) &&
              Near(baked.GetPixel(1, 0).r, 0), "Half-alpha overlay blends in linear light and keeps base alpha; transparent pixels leave base intact");
        info.Main2nd.Offset = new Vector2(0.5f, 0);
        baked = Bake(info);
        Check(Near(baked.GetPixel(0, 0).r, 0) && baked.GetPixel(1, 0).r > 0.7f,
            "Layer offset and repeat wrapping move the overlay to the correct texel");
        info.Main2nd.MaskGuid = maskGuid;
        baked = Bake(info);
        Check(Near(baked.GetPixel(1, 0).r, 0), "Blend mask uses main UV, independently of layer offset");
        info.Main2nd = new() { Enabled = true, Color = new Vector4(0.5f, 0.5f, 0.5f, 1) };
        info.Main3rd = new() { Enabled = true, Color = new Vector4(0.5f, 0.5f, 0.5f, 1) };
        foreach (var (mode, expected) in new[] { (0, 0.5f), (1, 0.6858f), (2, 0.6517f), (3, 0.237f) })
        {
            info.Main3rd.BlendMode = mode;
            Check(Near(Bake(info).GetPixel(0, 0).r, expected), $"3rd layer applies blend mode {mode} after 2nd, including unassigned white textures");
        }
        info.Main3rd.Enabled = false;
        info.AlphaMode = "cutout";
        info.Main2nd.Color = new Vector4(1, 1, 1, 0.5f);
        foreach (var (mode, expected) in new[] { (1, 0.5f), (2, 0.2f), (3, 0.9f), (4, 0f) })
        {
            info.Main2nd.AlphaMode = mode;
            Check(Near(Bake(info).GetPixel(0, 0).a, expected), $"Layer alpha mode {mode} updates cutout alpha");
        }

        var parent = LilToonConverter.Parse(Doc("""
              m_Floats:
              - _UseMain2ndTex: 1
              - _Main2ndTex_UVMode: 1
              - _Main2ndTexBlendMode: 3
              m_TexEnvs:
              - _Main2ndTex:
                  m_Texture: {fileID: 2800000, guid: 22222222222222222222222222222222}
                  m_Scale: {x: 2, y: 3}
              m_Colors:
              - _Color2nd: {r: 0.2, g: 0.3, b: 0.4, a: 0.5}
            """));
        var inherited = LilToonConverter.Parse(Doc("""
              m_Floats:
              - _Main2ndTex_UVMode: 0
              m_TexEnvs:
              - _Main2ndTex:
                  m_Texture: {fileID: 0}
            """), parent);
        Check(inherited.Main2nd.Enabled && inherited.Main2nd.TextureGuid == null &&
              inherited.Main2nd.Scale == new Vector2(2, 3) && inherited.Main2nd.Color.W == 0.5f &&
              inherited.Main2nd.BlendMode == 3 && inherited.Main2nd.UVMode == 0 &&
              parent.Main2nd.TextureGuid == layerGuid && parent.Main2nd.UVMode == 1,
            "Material variants inherit layer settings and can clear texture/UV restrictions without mutating parent");

        var method = builder.GetMethod("BakeMainLayers", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rejected = await (Task<StaticTexture2D>)method.Invoke(null,
            new object[] { root, package, parent, LilToonMainTextureBakePlan.Create(parent) })!;
        Check(rejected == null, "Unsupported UV sets are diagnosed instead of baked as UV0");
        var provider = await (Task<StaticTexture2D>)method.Invoke(null,
            new object[] { root, package, info, LilToonMainTextureBakePlan.Create(info) })!;
        Check(provider?.URL.Value != null, "Composited texture is saved to LocalDB and exposed through a texture provider");

        var alphaOnly = new LilToonInfo { MainTexGuid = redGuid, Color = new Vector4(0.2f, 0.3f, 0.4f, 0.5f),
            AlphaMaskGuid = maskGuid, AlphaMaskScale = 0.5f, AlphaMaskValue = 0.1f };
        // Source alpha 0.4 is tinted by 0.5 before the mask values 0.6 / 0.1.
        foreach (var (mode, first, second) in new[] { (1, 0.6f, 0.1f), (2, 0.12f, 0.02f), (3, 0.8f, 0.3f), (4, 0f, 0.1f) })
        {
            alphaOnly.AlphaMaskMode = mode;
            baked = Bake(alphaOnly);
            Check(Near(baked.GetPixel(0, 0).r, 1) && Near(baked.GetPixel(0, 0).g, 0) &&
                  Near(baked.GetPixel(0, 0).a, first) && Near(baked.GetPixel(1, 0).a, second),
                $"Alpha-only mode {mode} preserves untinted RGB and applies tint alpha before the mask");
        }
        alphaOnly.AlphaMaskMode = 1;
        alphaOnly.AlphaMaskTexOffset = new Vector2(0.5f, 0);
        Check(Near(Bake(alphaOnly).GetPixel(0, 0).a, 0.1f), "Alpha mask UV offset is baked");

        var colored = new LilToonInfo { Color = new Vector4(0.5f, 1, 1, 0.8f),
            Main2nd = new() { Enabled = true, Color = new Vector4(1, 0, 0, 0.5f), UVMode = 2 },
            Main3rd = new() { Enabled = true, Color = new Vector4(0, 0, 1, 0.25f), UVMode = 3 },
            AlphaMaskGuid = maskGuid, AlphaMaskMode = 2, AlphaMaskScale = 0.5f, AlphaMaskValue = 0.1f };
        var pixel = Bake(colored).GetPixel(0, 0);
        Check(Near(pixel.r, 0.705f) && Near(pixel.g, 0.646f) && Near(pixel.b, 0.812f) && Near(pixel.a, 0.48f),
            "Independent 1st/2nd/3rd tints and alpha are composed once, followed by alpha mask");

        var corrected = new LilToonInfo { MainTexGuid = redGuid, MainTexHSVG = new Vector4(1f / 3f, 1, 1, 1),
            MainColorAdjustMaskGuid = maskGuid };
        baked = Bake(corrected);
        Check(Near(baked.GetPixel(0, 0).g, 1) && Near(baked.GetPixel(0, 0).r, 0) &&
              Near(baked.GetPixel(1, 0).r, 1), "HSVG hue correction respects the color-adjust mask");
        corrected.MainColorAdjustMaskGuid = null;
        corrected.MainTexHSVG = new Vector4(0, 0, 0.5f, 1);
        pixel = Bake(corrected).GetPixel(0, 0);
        Check(Near(pixel.r, 0.735f) && Near(pixel.g, 0.735f) && Near(pixel.b, 0.735f),
            "HSVG saturation/value correction works without 2nd/3rd layers");
        corrected.MainTexHSVG = new Vector4(0, 1, 1, 1);
        corrected.MainGradationTexGuid = gradientGuid;
        corrected.MainGradationStrength = 1;
        pixel = Bake(corrected).GetPixel(0, 0);
        Check(Near(pixel.r, 0.5f) && Near(pixel.g, 0.25f) && Near(pixel.b, 0.75f),
            "Gradation-only bake follows the lookup's per-channel color mapping");

        // Exercise production material assignment too: the caller must retain tint only
        // for unbaked/alpha-only textures and reset UV transforms only after a successful bake.
        var tintMaterial = await Build(tintOnlyGuid);
        var alphaMaterial = await Build(alphaOnlyGuid);
        var colorMaterial = await Build(colorAlphaGuid);
        var tint = new colorX(0.2f, 0.3f, 0.4f, 0.5f, ColorProfile.sRGB);
        Check(tintMaterial.Color.Value == tint && tintMaterial.MainTextureScale.Value == new float2(2, 3),
            "Production no-bake path retains material tint and texture transform");
        Check(alphaMaterial.Color.Value == new colorX(0.2f, 0.3f, 0.4f, 1f, ColorProfile.sRGB) &&
              alphaMaterial.MainTextureScale.Value == float2.One &&
              alphaMaterial.MainTextureOffset.Value == float2.Zero,
            "Production alpha-only path retains RGB tint, resets baked alpha and texture transforms");
        Check(colorMaterial.Color.Value == new colorX(1, 1, 1, 1, ColorProfile.sRGB) &&
              colorMaterial.MainTextureScale.Value == float2.One,
            "Production color+alpha path resets tint to white, preventing double multiplication");
        var missingAlphaMaterial = await Build(missingAlphaGuid);
        Check(missingAlphaMaterial.Color.Value == new colorX(1, 1, 1, 1, ColorProfile.sRGB) &&
              missingAlphaMaterial.MainTextureScale.Value == float2.One,
            "Missing alpha mask does not prevent production color baking");

        async Task<XiexeToonMaterial> Build(string guid)
        {
            var build = builder.GetMethod("BuildMaterial", BindingFlags.NonPublic | BindingFlags.Static)!;
            var cache = Activator.CreateInstance(build.GetParameters()[4].ParameterType);
            var task = (Task)build.Invoke(null, new object[] { root, guid, package, new Dictionary<string, StaticTexture2D>(), cache })!;
            await task;
            return (XiexeToonMaterial)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        void SaveMaterial(string guid, int alphaMode, bool second, string alphaGuid = maskGuid)
        {
            string path = Path.Combine(folder, "Assets", guid + ".mat");
            File.WriteAllText(path, $$"""
                --- !u!21 &2100000
                Material:
                  m_Name: Bake {{guid}}
                  m_SavedProperties:
                    m_Floats:
                    - _lilToonVersion: 44
                    - _UseOutline: 0
                    - _AlphaMaskMode: {{alphaMode}}
                    - _UseMain2ndTex: {{(second ? 1 : 0)}}
                    m_Colors:
                    - _Color: {r: 0.2, g: 0.3, b: 0.4, a: 0.5}
                    - _Color2nd: {r: 0.3, g: 0.4, b: 0.5, a: 0.6}
                    m_TexEnvs:
                    - _MainTex:
                        m_Texture: {fileID: 2800000, guid: {{redGuid}}}
                        m_Scale: {x: 2, y: 3}
                        m_Offset: {x: 0.1, y: 0.2}
                    - _AlphaMask:
                        m_Texture: {fileID: 2800000, guid: {{alphaGuid}}}
                """);
            File.WriteAllText(path + ".meta", "guid: " + guid);
        }

        void Save(string name, string guid, params color[] pixels)
        {
            var bitmap = new Bitmap2D(pixels.Length, 1, TextureFormat.RGBA32, false, ColorProfile.sRGB);
            for (int x = 0; x < pixels.Length; x++) bitmap.SetPixel(x, 0, pixels[x]);
            string path = Path.Combine(folder, "Assets", name + ".png");
            if (!bitmap.Save(path)) throw new Exception("Could not save texture fixture");
            File.WriteAllText(path + ".meta", $"guid: {guid}\nTextureImporter:\n  textureSettings:\n    filterMode: 0\n");
        }
    }

    private static YamlDocument Doc(string properties) => UnityYaml.ParseDocuments(
        "--- !u!21 &2100000\nMaterial:\n  m_Name: Layer test\n  m_SavedProperties:\n" +
        string.Join("\n", properties.Split('\n').Select(line => "  " + line))).Single();
    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.008f;
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
