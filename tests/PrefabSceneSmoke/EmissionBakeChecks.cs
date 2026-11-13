using System.Numerics;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using Renderite.Shared;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class EmissionBakeChecks
{
    public static async Task Run(Slot parent, string temp)
    {
        string folder = Path.Combine(temp, "EmissionBakeFixtures");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        string white = Texture(true, color.White, color.White);
        string red = Texture(true, new color(1, 0, 0, 1));
        string stripes = Texture(true, new color(0, 1, 0, 1), new color(1, 0, 0, 1));
        string main = Texture(true, new color(0, 0, 1, 1), color.White);
        string halfBlack = Texture(true, new color(0, 0, 0, 0.5f));
        string halfWhite = Texture(true, new color(1, 1, 1, 0.5f));
        string grayLinear = Texture(false, new color(0.5f, 0.5f, 0.5f, 1));
        string graySrgb = Texture(true, new color(0.5f, 0.5f, 0.5f, 1));
        string mirrored = WrappedTexture(2), clamped = WrappedTexture(1), mirrorOnce = WrappedTexture(3);
        string baseMaterial = Material("Base", $$"""
              m_Floats:
              - _lilToonVersion: 44
              - _UseEmission: 1
              - _EmissionBlend: 0.5
              m_Colors:
              - _EmissionColor: {r: 0.4, g: 0.8, b: 1, a: 0.5}
              m_TexEnvs:
              - _EmissionMap:
                  m_Texture: {fileID: 2800000, guid: {{white}}}
                  m_Scale: {x: 2, y: 3}
                  m_Offset: {x: 0.1, y: 0.2}
              - _EmissionBlendMask:
                  m_Texture: {fileID: 2800000, guid: {{stripes}}}
                  m_Scale: {x: 2, y: 1}
                  m_Offset: {x: 0.25, y: 0}
            """);
        string inherited = Material("Inherited", "  m_Floats: []", baseMaterial);
        string fractionalSource = Material("FractionalSource", $$"""
              m_TexEnvs:
              - _EmissionMap:
                  m_Texture: {fileID: 2800000, guid: {{stripes}}}
                  m_Scale: {x: 0.5, y: 1}
                  m_Offset: {x: 0, y: 0}
              - _EmissionBlendMask:
                  m_Texture: {fileID: 0}
            """, baseMaterial);
        string fractional = Material("Fractional", $$"""
              m_TexEnvs:
              - _EmissionBlendMask:
                  m_Texture: {fileID: 2800000, guid: {{white}}}
            """, fractionalSource);
        string uv1 = Material("UV1", "  m_Floats:\n  - _EmissionMap_UVMode: 1", baseMaterial);
        string oversized = Material("Oversized", $$"""
              m_TexEnvs:
              - _EmissionBlendMask:
                  m_Texture: {fileID: 2800000, guid: {{stripes}}}
                  m_Scale: {x: 10000, y: 1}
            """, baseMaterial);
        string mainBake = Material("MainBake", $$"""
              m_Floats:
              - _EmissionMainStrength: 1
              m_Colors:
              - _MainTexHSVG: {r: 0.33333333, g: 1, b: 1, a: 1}
              m_TexEnvs:
              - _MainTex:
                  m_Texture: {fileID: 2800000, guid: {{stripes}}}
                  m_Offset: {x: 0.5, y: 0}
              - _EmissionMap:
                  m_Texture: {fileID: 2800000, guid: {{white}}}
                  m_Scale: {x: 1, y: 1}
                  m_Offset: {x: 0, y: 0}
              - _EmissionBlendMask:
                  m_Texture: {fileID: 0}
            """, baseMaterial);
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: ffffffffffffffffffffffffffffffff\n");
        using var package = UnityPackage.Open(prefab);
        var builder = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder")!;
        var composite = builder.GetMethod("CompositeEmissionMap", BindingFlags.Static | BindingFlags.NonPublic)!;
        Bitmap2D Bake(LilToonInfo info) => (Bitmap2D)composite.Invoke(null, new object[] { package, info, null })!;
        var info = new LilToonInfo { EmissionMapGuid = white, EmissionBlendMaskGuid = stripes,
            EmissionBlendMaskOffset = new Vector2(0.5f, 0), MainTexGuid = main, EmissionMainStrength = 0.5f };
        var pixels = Bake(info);
        Check(Near(pixels.GetPixel(0, 0).r, 0.735f) && Near(pixels.GetPixel(0, 0).g, 0) &&
              Near(pixels.GetPixel(1, 0).g, 1), "mask offset and partial main multiplication combine in linear light");
        info.MainTexScale = new Vector2(-1, 1); info.MainTexOffset = new Vector2(1, 0);
        pixels = Bake(info);
        Check(Near(pixels.GetPixel(0, 0).r, 1) && Near(pixels.GetPixel(1, 0).g, 0.735f),
            "main texture uses its own negative tiling and offset");
        info.MainTexGuid = null; info.EmissionBlendMaskScale = new Vector2(2, 1);
        pixels = Bake(info);
        Check(pixels.Size.x == 4 && Near(pixels.GetPixel(0, 0).r, 1) && Near(pixels.GetPixel(1, 0).g, 1) &&
              Near(pixels.GetPixel(2, 0).r, 1) && Near(pixels.GetPixel(3, 0).g, 1),
            "mask tiling determines output density without dropping stripes");
        info = new LilToonInfo { EmissionMapGuid = halfWhite, EmissionBlendMaskGuid = halfBlack,
            MainTexGuid = halfBlack, EmissionMainStrength = 0.5f };
        var pixel = Bake(info).GetPixel(0, 0);
        Check(Near(pixel.r, 0.323f) && Near(pixel.a, 0.502f),
            "mask/main alpha weights are blended while emission alpha multiplies encoded RGB once");
        info.EmissionMapGuid = null; info.EmissionBlendMaskGuid = null; info.EmissionMainStrength = 0;
        Check(Near(Bake(info).GetPixel(0, 0).r, 1), "missing emission starts white and zero main strength has no effect");
        info.MainTexGuid = red; info.EmissionMainStrength = 2;
        pixel = Bake(info).GetPixel(0, 0);
        Check(Near(pixel.r, 1) && Near(pixel.g, 0), "main-only emission clamps strength at one");
        info.MainTexGuid = null; info.EmissionBlendMaskGuid = grayLinear;
        float linearValue = Bake(info).GetPixel(0, 0).r;
        info.EmissionBlendMaskGuid = graySrgb;
        Check(Near(linearValue, 0.737f) && Near(Bake(info).GetPixel(0, 0).r, 0.502f),
            "emission mask honors TextureImporter sRGB setting");
        info.EmissionBlendMaskGuid = "dddddddddddddddddddddddddddddddd";
        Check(Near(Bake(info).GetPixel(0, 0).r, 1), "missing mask is neutral white");

        var root = parent.AddSlot("Emission bake regression");
        var build = builder.GetMethod("BuildMaterial", BindingFlags.Static | BindingFlags.NonPublic)!;
        var textures = new Dictionary<string, StaticTexture2D>();
        var cache = Activator.CreateInstance(build.GetParameters()[4].ParameterType);
        var original = await Build(baseMaterial);
        var variant = await Build(inherited);
        var differentUv = await Build(uv1);
        var failed = await Build(oversized);
        var correctedMain = await Build(mainBake);
        var sourceMaterial = await Build(fractionalSource);
        var fractionalMaterial = await Build(fractional);
        Check(fractionalMaterial.EmissionMap.Target == sourceMaterial.EmissionMap.Target &&
              fractionalMaterial.EmissionMapScale.Value == new float2(0.5f, 1) &&
              fractionalMaterial.EmissionMapOffset.Value == float2.Zero,
            "neutral mask does not shorten fractional emission tiling to one UV0 tile");
        foreach (string input in new[] { "emission", "mask", "main" })
        {
            var fractionalInfo = new LilToonInfo { EmissionMapGuid = stripes, EmissionBlendMaskGuid = stripes,
                MainTexGuid = stripes, EmissionMainStrength = 1 };
            if (input == "emission") fractionalInfo.EmissionMapScale = new Vector2(-0.5f, 1);
            if (input == "mask") fractionalInfo.EmissionBlendMaskScale = new Vector2(1.5f, 1);
            if (input == "main") fractionalInfo.MainTexScale = new Vector2(0.5f, 1);
            Reject(fractionalInfo, $"fractional {input} period is rejected instead of truncated");
        }
        foreach (string wrapped in new[] { mirrored, clamped, mirrorOnce })
            Reject(new LilToonInfo { EmissionMapGuid = wrapped }, "nonperiodic wrap at scale one is rejected");
        pixels = Bake(new LilToonInfo { EmissionMapGuid = mirrored, EmissionMapScale = new Vector2(2, 1) });
        Check(pixels.Size.x == 4 && Near(pixels.GetPixel(0, 0).g, 1) && Near(pixels.GetPixel(1, 0).r, 1) &&
              Near(pixels.GetPixel(2, 0).r, 1) && Near(pixels.GetPixel(3, 0).g, 1),
            "even mirror tiling preserves its complete two-image period");
        pixels = Bake(new LilToonInfo { EmissionMapGuid = clamped,
            EmissionMapScale = Vector2.Zero, EmissionMapOffset = new Vector2(2, 0) });
        Check(Near(pixels.GetPixel(0, 0).r, 1), "zero scale is constant even for clamp wrapping");
        Check(original.EmissionUV.Value == 0 && variant.EmissionMapScale.Value == float2.One &&
              variant.EmissionMapOffset.Value == float2.Zero && original.EmissionMap.Target != null,
            "successful production bake resets ST and keeps UV0");
        Check(differentUv.EmissionUV.Value == 1 && differentUv.EmissionMapScale.Value == new float2(2, 3) &&
              differentUv.EmissionMapOffset.Value == new float2(0.1f, 0.2f) &&
              failed.EmissionMap.Target == differentUv.EmissionMap.Target &&
              failed.EmissionMapScale.Value == new float2(2, 3) && failed.EmissionMapOffset.Value == new float2(0.1f, 0.2f),
            "non-UV0 and failed bakes retain the source emission texture and ST");
        Check(correctedMain.EmissionColor.Value == new colorX(0.1f, 0.2f, 0.25f, 0.5f, ColorProfile.Linear),
            "emission tint, blend and color alpha remain on the material exactly once");

        var graph = root.SaveObject(DependencyHandling.CollectAssets);
        var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(root.Name, root.World.LocalUser.MachineID, null);
        var engine = root.Engine;
        string output = Path.Combine(folder, "EmissionBake.resonitepackage");
        await default(ToBackground);
        using (var stream = File.Create(output))
            await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
        await default(ToWorld);
        Check(PackageInspector.Inspect(output, verbose: false) == 0, "emission bake package inspection succeeds");
        using (var saved = RecordPackage.Decode(output))
        {
            using var objectStream = saved.ReadAsset(RecordPackage.GetAssetSignature(new Uri(saved.MainRecord.AssetURI)));
            using var objectMemory = new MemoryStream(); objectStream.CopyTo(objectMemory); objectMemory.Position = 0;
            var tree = DataTreeConverter.LoadAuto(objectMemory);
            DataTreeNode Field(DataTreeDictionary node, string name) =>
                (node.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data");
            Bitmap2D Saved(XiexeToonMaterial material)
            {
                // PackageCreator rewrites local:// URLs in the saved graph to packdb:// URLs.
                string slotName = ((StaticTexture2D)material.EmissionMap.Target).Slot.Name;
                var slot = tree.EnumerateTree().OfType<DataTreeDictionary>()
                    .Single(node => (Field(node, "Name") as DataTreeValue)?.Extract<string>() == slotName);
                var component = ((DataTreeList)Field(slot, "Components")).Children.OfType<DataTreeDictionary>().Single();
                var data = (DataTreeDictionary)component.TryGetNode("Data");
                var uri = ((DataTreeValue)Field(data, "URL")).Extract<Uri>();
                using var stream = saved.ReadAsset(RecordPackage.GetAssetSignature(uri));
                using var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
                return Bitmap2D.Load(memory, ".webp", false);
            }
            var correctedPixels = Saved(correctedMain);
            pixel = correctedPixels.GetPixel(0, 0);
            var nextPixel = correctedPixels.GetPixel(1, 0);
            Check(Near(pixel.r, 0) && Near(pixel.g, 1) && Near(pixel.b, 0) &&
                  Near(nextPixel.r, 0) && Near(nextPixel.g, 0) && Near(nextPixel.b, 1),
                "saved emission uses HSVG main bake and does not apply baked main offset twice");
            var first = Saved(original); var child = Saved(variant);
            Check(first.Size == child.Size && first.Size.x == 4 &&
                  Near(child.GetPixel(0, 0).r, 1) && Near(child.GetPixel(1, 0).g, 1),
                "variant inherits mask ST and saved pixels include its offset/tiling");
            var fractionalPixels = Saved(fractionalMaterial);
            color SampleFractional(float meshU)
            {
                float textureU = meshU * fractionalMaterial.EmissionMapScale.Value.x +
                    fractionalMaterial.EmissionMapOffset.Value.x;
                textureU -= MathF.Floor(textureU);
                return fractionalPixels.GetPixel((int)(textureU * fractionalPixels.Size.x), 0);
            }
            Check(Near(SampleFractional(0.5f).g, 1) && Near(SampleFractional(1.5f).r, 1) &&
                  Near(SampleFractional(-0.5f).r, 1) && Near(SampleFractional(2.5f).g, 1),
                "saved fractional emission samples the correct colors outside positive and negative UV0 tiles");
        }
        var restored = parent.AddSlot("Restored emission bake");
        await PackageImporter.ImportPackage(output, restored);
        await default(ToWorld);
        var reloaded = restored.GetComponentsInChildren<XiexeToonMaterial>().Single(m => m.Slot.Name == "Material: MainBake");
        Check(reloaded.EmissionMap.Target != null && reloaded.EmissionMapScale.Value == float2.One &&
              reloaded.EmissionMapOffset.Value == float2.Zero && reloaded.EmissionUV.Value == 0,
            "baked emission texture assignment and identity ST survive package reload");
        var reloadedFractional = restored.GetComponentsInChildren<XiexeToonMaterial>()
            .Single(m => m.Slot.Name == "Material: Fractional");
        Check(reloadedFractional.EmissionMap.Target != null &&
              reloadedFractional.EmissionMapScale.Value == new float2(0.5f, 1) &&
              reloadedFractional.EmissionMapOffset.Value == float2.Zero,
            "fractional emission sampling period survives package reload");
        restored.Destroy(); root.Destroy();

        async Task<XiexeToonMaterial> Build(string guid)
        {
            var task = (Task)build.Invoke(null, new object[] { root, guid, package, textures, cache })!;
            await task;
            return (XiexeToonMaterial)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        void Reject(LilToonInfo candidate, string message)
        {
            bool rejected = false;
            try { Bake(candidate); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException error &&
                error.Message.Contains("periodic")) { rejected = true; }
            Check(rejected, message);
        }
        string WrappedTexture(int wrap)
        {
            string guid = Texture(true, new color(0, 1, 0, 1), new color(1, 0, 0, 1));
            File.AppendAllText(Path.Combine(folder, "Assets", guid + ".png.meta"),
                $"    wrapU: {wrap}\n    wrapV: {wrap}\n");
            return guid;
        }
        string Texture(bool srgb, params color[] values)
        {
            string guid = Guid.NewGuid().ToString("N"), path = Path.Combine(folder, "Assets", guid + ".png");
            var image = new Bitmap2D(values.Length, 1, TextureFormat.RGBA32, false, ColorProfile.sRGB);
            for (int x = 0; x < values.Length; x++) image.SetPixel(x, 0, values[x]);
            if (!image.Save(path)) throw new Exception("Could not save emission fixture");
            File.WriteAllText(path + ".meta", $"guid: {guid}\nTextureImporter:\n  mipmaps:\n    sRGBTexture: {(srgb ? 1 : 0)}\n  textureSettings:\n    filterMode: 0\n");
            return guid;
        }
        string Material(string name, string properties, string parentGuid = null)
        {
            string guid = Guid.NewGuid().ToString("N"), path = Path.Combine(folder, "Assets", guid + ".mat");
            File.WriteAllText(path, "--- !u!21 &2100000\nMaterial:\n  m_Name: " + name + "\n" +
                (parentGuid == null ? "" : $"  m_Parent: {{fileID: 2100000, guid: {parentGuid}}}\n") +
                "  m_SavedProperties:\n" + string.Join("\n", properties.Split('\n').Select(line => "  " + line)));
            File.WriteAllText(path + ".meta", "guid: " + guid);
            return guid;
        }
    }

    private static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.012f;
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
