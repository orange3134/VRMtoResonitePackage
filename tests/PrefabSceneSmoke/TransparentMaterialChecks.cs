using System.Collections.ObjectModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class TransparentMaterialChecks
{
    public static async Task Run(Slot parent, string temp)
    {
        string folder = Path.Combine(temp, "TransparentFixtures");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: ffffffffffffffffffffffffffffffff\n");
        Material(1, "Duplicate", 10, 3005);
        Material(2, "Duplicate", 10, 3010);
        Material(3, "Inherited", null, null, 1);
        Material(4, "Opaque", 0, 2000);
        Material(5, "ExistingCutout", 0, 2450, alphaToMask: 1);
        Material(6, "Additive", 1, 3100);
        Material(7, "Unreferenced", 10, 3000);
        string archive = Path.Combine(folder, "Test.unitypackage");
        using (var gzip = new GZipStream(File.Create(archive), CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
        {
            foreach (string asset in Directory.GetFiles(Path.Combine(folder, "Assets"))
                         .Where(path => !path.EndsWith(".meta", StringComparison.Ordinal)))
            {
                string meta = File.ReadAllText(asset + ".meta");
                string guid = meta.Trim()[6..];
                Write(guid + "/asset", File.ReadAllBytes(asset));
                Write(guid + "/asset.meta", Encoding.UTF8.GetBytes(meta));
                Write(guid + "/pathname", Encoding.UTF8.GetBytes("Assets/" + Path.GetFileName(asset)));
            }
            void Write(string path, byte[] bytes)
            {
                using var stream = new MemoryStream(bytes);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = stream });
            }
        }

        var parsed = CliOptions.Parse(new[] { prefab, "--transparent-cutout", "Assets/1.mat",
            "--mtoon-transparent-cutout", "Inherited" });
        Check(parsed.TransparentCutoutMaterials.SetEquals(new[] { "Assets/1.mat", "Inherited" }),
            "new CLI option and legacy alias accumulate material selections");
        var forwarded = new Collection<string>();
        // Exercise the same child-process argument forwarding used by GUI conversions.
        typeof(GuiApp).Assembly.GetType("VrmToResonitePackage.MainWindow")!
            .GetMethod("AddCliArguments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { forwarded, parsed, "Resonite" });
        Check(CliOptions.Parse(forwarded.ToArray()).TransparentCutoutMaterials.SetEquals(parsed.TransparentCutoutMaterials),
            "GUI child-process arguments preserve all material selections");

        foreach (string input in new[] { prefab, archive })
        {
            using var package = UnityPackage.Open(input);
            var avatar = new VrchatAvatar();
            var materials = new VrchatRendererMaterials { RendererGameObjectName = "Surface" };
            materials.MaterialGuids.AddRange(Enumerable.Range(1, 6).Select(GuidFor));
            avatar.RendererMaterials.Add(materials);
            avatar.FbxMaterialGuids["Duplicate"] = GuidFor(1);
            var additional = new VrchatFbxAsset { Guid = "additional" };
            additional.MaterialGuids["Inherited"] = GuidFor(3);
            avatar.AdditionalFbxs.Add(additional);
            Check(VrchatMaterialBuilder.ListTransparentMaterials(avatar, package)
                    .SequenceEqual(new[] { "Assets/1.mat", "Assets/2.mat", "Assets/3.mat" }),
                "Alpha choices distinguish namesakes, inherit variant modes and exclude unused/non-Alpha materials: " + Path.GetExtension(input));

            Slot root = parent.AddSlot("Transparent regression " + Path.GetExtension(input));
            var baseline = await Build(root.AddSlot("Default"), null);
            Check(baseline[0].BlendMode.Value == BlendMode.Alpha && baseline[0].ZWrite.Value == ZWrite.Off &&
                  baseline[0].RenderQueue.Value == 3005 && baseline[2].BlendMode.Value == BlendMode.Alpha,
                "unselected Alpha materials retain their authored mode, queue and depth writing");
            var selected = await Build(root.AddSlot("Selected"), new(StringComparer.Ordinal)
                { "Assets/1.mat", GuidFor(3), "Opaque", "ExistingCutout", "Additive", "missing" });
            AssertSelection(selected);
            var byName = await Build(root.AddSlot("ByName"), new(StringComparer.Ordinal) { "Duplicate" });
            Check(byName[0].BlendMode.Value == BlendMode.Cutout && byName[1].BlendMode.Value == BlendMode.Cutout &&
                  byName[2].BlendMode.Value == BlendMode.Alpha,
                "name selection changes matching Alpha materials only");

            var graph = root.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(root.Name, root.World.LocalUser.MachineID, null);
            string output = Path.Combine(folder, "Transparent" + Path.GetExtension(input) + ".resonitepackage");
            var engine = root.Engine;
            await default(ToBackground);
            using (var stream = File.Create(output))
                await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            Check(PackageInspector.Inspect(output, verbose: false) == 0, "transparent mode package inspection succeeds");
            Slot restored = parent.AddSlot("Restored transparent modes");
            await PackageImporter.ImportPackage(output, restored);
            await default(ToWorld);
            var renderer = restored.GetComponentsInChildren<MeshRenderer>()
                .Single(r => r.Slot.Parent.Name == "Selected");
            AssertSelection(renderer.Materials.Select(m => (XiexeToonMaterial)m).ToArray());
            restored.Destroy();
            root.Destroy();

            async Task<XiexeToonMaterial[]> Build(Slot target, HashSet<string> selection)
            {
                var renderer = target.AddSlot("Surface").AttachComponent<MeshRenderer>();
                await VrchatMaterialBuilder.Apply(target, target.AddSlot("Assets"), avatar, package,
                    new Dictionary<Slot, string>(), transparentCutoutMaterials: selection);
                return renderer.Materials.Select(m => (XiexeToonMaterial)m).ToArray();
            }
        }

        void Material(int id, string name, int? dstBlend, int? queue, int? parentId = null, int alphaToMask = 0)
        {
            string path = Path.Combine(folder, "Assets", id + ".mat");
            File.WriteAllText(path, "--- !u!21 &2100000\nMaterial:\n  m_Name: " + name + "\n" +
                (parentId.HasValue ? $"  m_Parent: {{fileID: 2100000, guid: {GuidFor(parentId.Value)}}}\n" : "") +
                (queue.HasValue ? $"  m_CustomRenderQueue: {queue}\n" : "") +
                "  m_SavedProperties:\n    m_Floats:\n    - _lilToonVersion: 44\n" +
                (dstBlend.HasValue ? $"    - _SrcBlend: 5\n    - _DstBlend: {dstBlend}\n" +
                    $"    - _ZWrite: 0\n    - _Cutoff: 0.37\n    - _AlphaToMask: {alphaToMask}\n" : "") +
                "    m_Colors:\n    - _Color: {r: 0.2, g: 0.3, b: 0.4, a: 0.65}\n    m_TexEnvs: []\n");
            File.WriteAllText(path + ".meta", "guid: " + GuidFor(id) + "\n");
        }
    }

    private static string GuidFor(int id) => id.ToString("x32");

    private static void AssertSelection(XiexeToonMaterial[] materials)
    {
        foreach (int index in new[] { 0, 2 })
            Check(materials[index].BlendMode.Value == BlendMode.Cutout &&
                  MathF.Abs(materials[index].AlphaClip.Value - 0.37f) < 0.0001f &&
                  materials[index].ZWrite.Value == ZWrite.On && materials[index].RenderQueue.Value == 2450 &&
                  MathF.Abs(materials[index].Color.Value.a - 0.65f) < 0.0001f,
                "selected material keeps inherited cutoff and color alpha, and uses Cutout queue/depth writing");
        Check(materials[1].BlendMode.Value == BlendMode.Alpha && materials[1].RenderQueue.Value == 3010 &&
              materials[1].ZWrite.Value == ZWrite.Off, "path selection isolates an identically named Alpha material");
        Check(materials[3].BlendMode.Value == BlendMode.Opaque && materials[4].BlendMode.Value == BlendMode.Cutout &&
              materials[4].ZWrite.Value == ZWrite.Off && materials[5].BlendMode.Value == BlendMode.Additive,
            "non-Alpha materials are unchanged even when explicitly selected");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
