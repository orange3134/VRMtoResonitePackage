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
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: aabbccddeeff11223344556677889900\n");
        Save("Base", baseGuid, new color(0, 0, 0, 0.4f), new color(0, 0, 0, 0.4f));
        Save("Layer", layerGuid, new color(1, 1, 1, 0.5f), new color(1, 0, 0, 0));
        Save("Mask", maskGuid, new color(1, 1, 1, 1), new color(0, 0, 0, 1));
        using var package = UnityPackage.Open(prefab);
        var builder = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder")!;
        Bitmap2D Bake(LilToonInfo info) => (Bitmap2D)builder.GetMethod("CompositeMainLayers",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { package, info, new[] { info.Main2nd, info.Main3rd }.Where(l => l.Enabled).ToArray() })!;
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
              inherited.Main2nd.BlendMode == 3 && inherited.Main2nd.UnsupportedFeatures.Length == 0 &&
              parent.Main2nd.TextureGuid == layerGuid && parent.Main2nd.UnsupportedFeatures.Length == 1,
            "Material variants inherit layer settings and can clear texture/UV restrictions without mutating parent");

        var method = builder.GetMethod("BakeMainLayers", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rejected = await (Task<StaticTexture2D>)method.Invoke(null, new object[] { root, package, parent })!;
        Check(rejected == null, "Unsupported UV sets are diagnosed instead of baked as UV0");
        var provider = await (Task<StaticTexture2D>)method.Invoke(null, new object[] { root, package, info })!;
        Check(provider?.URL.Value != null, "Composited texture is saved to LocalDB and exposed through a texture provider");

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
