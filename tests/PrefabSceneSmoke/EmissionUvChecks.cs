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

internal static class EmissionUvChecks
{
    public static async Task Run(Slot parent, string temp)
    {
        string folder = Path.Combine(temp, "EmissionUvFixtures");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        const string textureGuid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        string texturePath = Path.Combine(folder, "Assets/Emission.png");
        var bitmap = new Bitmap2D(1, 1, TextureFormat.RGBA32, false, ColorProfile.sRGB);
        bitmap.SetPixel(0, 0, color.White);
        Check(bitmap.Save(texturePath), "emission fixture texture saved");
        File.WriteAllText(texturePath + ".meta", "guid: " + textureGuid);
        string original = Material(1, """
              m_Floats:
              - _lilToonVersion: 44
              - _UseEmission: 1
              - _EmissionMap_UVMode: 1
              m_Colors:
              - _EmissionColor: {r: 1, g: 1, b: 1, a: 1}
              m_TexEnvs:
              - _EmissionMap:
                  m_Texture: {fileID: 2800000, guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee}
                  m_Scale: {x: 2, y: 3}
                  m_Offset: {x: 0.25, y: 0.5}
            """);
        Material(2, "  m_Floats: []", original);
        Material(3, "  m_Floats:\n  - _EmissionMap_UVMode: 0", original);
        Material(4, "  m_Floats:\n  - _UseEmission: 0", original);
        Material(5, "  m_TexEnvs:\n  - _EmissionMap:\n      m_Texture: {fileID: 0}", original);
        Material(6, """
              m_TexEnvs:
              - _EmissionMap:
                  m_Texture: {fileID: 0}
              - _EmissionBlendMask:
                  m_Texture: {fileID: 2800000, guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee}
            """, original);
        foreach (int mode in new[] { 2, 3, 4, -1 })
            Material(10 + mode, $"  m_Floats:\n  - _EmissionMap_UVMode: {mode}", original);
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: ffffffffffffffffffffffffffffffff\n");
        using var package = UnityPackage.Open(prefab);
        var root = parent.AddSlot("Emission UV regression");
        var builder = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder")!;
        var build = builder.GetMethod("BuildMaterial", BindingFlags.NonPublic | BindingFlags.Static)!;
        var textures = new Dictionary<string, StaticTexture2D>();
        var metallicCache = Activator.CreateInstance(build.GetParameters()[4].ParameterType);
        var first = await Build(1);
        var inherited = await Build(2);
        var reset = await Build(3);
        Check(first.EmissionUV.Value == 1 && inherited.EmissionUV.Value == 1 && reset.EmissionUV.Value == 0,
            "emission UV1 survives material variant inheritance and explicit UV0 override");
        Check(first.EmissionMap.Target != null && first.EmissionMap.Target == inherited.EmissionMap.Target &&
              first.EmissionMap.Target == reset.EmissionMap.Target &&
              inherited.EmissionMapScale.Value == new float2(2, 3) &&
              inherited.EmissionMapOffset.Value == new float2(0.25f, 0.5f),
            "shared emission image retains independent material UV choice and inherited transform");
        var disabled = await Build(4);
        Check(disabled.EmissionMap.Target == null, "disabled emission does not acquire a map");
        foreach (int id in new[] { 5, 6 })
        {
            var fallback = await Build(id);
            Check(fallback.EmissionMap.Target != null && fallback.EmissionUV.Value == 0 &&
                  fallback.EmissionMapScale.Value == float2.One && fallback.EmissionMapOffset.Value == float2.Zero,
                "cleared emission map uses UV0 for white/mask fallback despite inherited UV1");
        }
        foreach (int mode in new[] { 2, 3, 4, -1 })
            Check((await Build(10 + mode)).EmissionUV.Value == 0,
                $"unsupported emission UV mode {mode} falls back to UV0");

        var graph = root.SaveObject(DependencyHandling.CollectAssets);
        var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(root.Name, root.World.LocalUser.MachineID, null);
        string output = Path.Combine(folder, "EmissionUV.resonitepackage");
        var engine = root.Engine;
        await default(ToBackground);
        using (var stream = File.Create(output))
            await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
        await default(ToWorld);
        Check(PackageInspector.Inspect(output, verbose: false) == 0, "emission UV package inspection succeeds");
        var restored = parent.AddSlot("Restored emission UV");
        await PackageImporter.ImportPackage(output, restored);
        await default(ToWorld);
        Check(restored.GetComponentsInChildren<XiexeToonMaterial>().Count(m => m.EmissionUV.Value == 1) == 2,
            "both UV1 material settings survive package save and reload");
        restored.Destroy();
        root.Destroy();

        async Task<XiexeToonMaterial> Build(int id)
        {
            var task = (Task)build.Invoke(null, new object[] { root, id.ToString("x32"), package, textures, metallicCache })!;
            await task;
            return (XiexeToonMaterial)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        string Material(int id, string properties, string parentGuid = null)
        {
            string guid = id.ToString("x32");
            string path = Path.Combine(folder, "Assets", guid + ".mat");
            File.WriteAllText(path, "--- !u!21 &2100000\nMaterial:\n  m_Name: Emission " + id + "\n" +
                (parentGuid == null ? "" : $"  m_Parent: {{fileID: 2100000, guid: {parentGuid}}}\n") +
                "  m_SavedProperties:\n" + string.Join("\n", properties.Split('\n').Select(line => "  " + line)));
            File.WriteAllText(path + ".meta", "guid: " + guid);
            return guid;
        }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
