using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class NativeMeshSceneChecks
{
    public static async Task CheckReal(Slot parent, string prefab, string output)
    {
        using var package = UnityPackage.Open(prefab);
        var avatar = VrchatAvatarParser.Parse(package);
        var expected = avatar.MeshCopies.Where(c => c.GeneratedMesh != null && !c.RendererRemoved).ToArray();
        if (expected.Length == 0) throw new Exception("No generated meshes in test avatar.");
        await PackageImporter.ImportPackage(output, parent);
        await default(ToWorld);
        var renderers = parent.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
        for (int frame = 0; frame < 7200 && renderers.Any(r => r.Mesh.Target != null && !r.Mesh.IsAssetAvailable); frame++) await default(NextUpdate);
        foreach (var copy in expected)
        {
            var renderer = renderers.Single(r => r.Slot.Name == copy.Name);
            var actual = renderer.Mesh.Asset?.Data ?? throw new Exception("Mesh not loaded: " + copy.Name);
            var source = UnityNativeMesh.Decode(copy.GeneratedMesh);
            if (actual.VertexCount != source.VertexCount || actual.BoneCount != source.BoneCount ||
                !actual.RawPositions.Take(actual.VertexCount).SequenceEqual(source.RawPositions.Take(source.VertexCount)) ||
                !actual.BlendShapes.Select(s => s.Name).SequenceEqual(source.BlendShapes.Select(s => s.Name)))
                throw new Exception("Saved generated geometry/morph mismatch: " + copy.Name);
            var used = new HashSet<int>();
            for (int v = 0; v < actual.VertexCount; v++)
            {
                var binding = actual.RawBoneBindings[v];
                if (binding.weight0 > 0) used.Add(binding.boneIndex0);
                if (binding.weight1 > 0) used.Add(binding.boneIndex1);
                if (binding.weight2 > 0) used.Add(binding.boneIndex2);
                if (binding.weight3 > 0) used.Add(binding.boneIndex3);
            }
            foreach (int i in used)
                if (i >= renderer.Bones.Count || renderer.Bones[i] == null || renderer.Bones[i].Name != copy.BoneTargets[i].Name)
                    throw new Exception($"Saved generated skin mismatch: {copy.Name}, bone {i}");
            Console.WriteLine($"PASS: saved generated mesh {copy.Name}, vertices={actual.VertexCount}, bones={actual.BoneCount}, weightedBones={used.Count}, shapes={actual.BlendShapes.Count()}");
        }
        Console.WriteLine($"PASS: {expected.Length} generated avatar meshes reloaded and compared with source assets");
    }

    public static async Task Run(Slot parent, string temp)
    {
        var root = parent.AddSlot("Generated mesh regression");
        var assets = root.AddSlot("Assets");
        Slot boneA = root.AddSlot("SameName"), boneB = root.AddSlot("SameName");
        boneA.LocalPosition = new float3(1, 0, 0); boneB.LocalPosition = new float3(0, 2, 0);
        var slots = new Dictionary<string, Slot> { ["prefab:10"] = boneA, ["prefab:20"] = boneB };
        var avatar = new VrchatAvatar();
        var node = NativeAssetChecks.MeshFixture(true, false);
        for (int i = 0; i < 2; i++)
        {
            var copy = new VrchatMeshCopy("native", "Template", "Generated" + i, true, true)
            { GeneratedMesh = node, SourcePath = "43", PrefabGuid = "prefab", RendererFileId = 100 + i,
                IsSkinned = i == 0, ReplaceSourceRenderer = true,
                Transform = new() { Key = "prefab:" + (100 + i), GameObjectKey = "prefab:go" + i, LocalPosition = new(i, 0, 0) } };
            copy.BoneTargets[0] = new(null, "SameName", null, "prefab", 20);
            copy.BoneTargets[1] = new(null, "SameName", null, "prefab", 10);
            avatar.MeshCopies.Add(copy);
        }
        var sources = new Dictionary<Slot, string>(); var paths = new Dictionary<Slot, string>();
        await VrchatGeneratedMeshes.AddTemplates(root, assets, avatar, sources, paths);
        var build = VrchatSceneSetup.InstantiateMeshCopies(avatar, sources, _ => root, paths, slots);
        build.Bind();
        var skin = build.AuthoredObjects["prefab:go0"].GetComponent<SkinnedMeshRenderer>();
        var staticRenderer = build.AuthoredObjects["prefab:go1"].GetComponent<MeshRenderer>();
        if (skin.Bones[0] != boneB || skin.Bones[1] != boneA || staticRenderer is SkinnedMeshRenderer ||
            skin.Mesh.Target != staticRenderer.Mesh.Target || root.GetComponentsInChildren<MeshRenderer>().Count() != 2)
            throw new Exception("Native renderer identity, shared mesh, static component or exact bone index binding changed.");
        await default(NextUpdate);
        skin.BlendShapeWeights[0] = .35f;
        var graph = root.SaveObject(DependencyHandling.CollectAssets);
        var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(root.Name, root.World.LocalUser.MachineID, null);
        var engine = root.Engine;
        string output = Path.Combine(temp, "NativeMesh.resonitepackage");
        await default(ToBackground);
        using (var stream = File.Create(output)) await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
        await default(ToWorld);
        if (PackageInspector.Inspect(output, false) != 0) throw new Exception("Native mesh package inspect failed.");
        var restored = parent.AddSlot("Restored generated meshes");
        await PackageImporter.ImportPackage(output, restored);
        await default(ToWorld);
        var loaded = restored.GetComponentsInChildren<SkinnedMeshRenderer>().Single();
        for (int frame = 0; frame < 7200 && !loaded.Mesh.IsAssetAvailable; frame++) await default(NextUpdate);
        var mesh = loaded.Mesh.Asset?.Data ?? throw new Exception("Saved native mesh did not reload.");
        if (mesh.VertexCount != 4 || mesh.SubmeshCount != 2 || mesh.BoneCount != 2 ||
            mesh.GetVertex(2).Position != new float3(2, 3, 4) ||
            loaded.Bones[0].LocalPosition != new float3(0, 2, 0) || loaded.Bones[1].LocalPosition != new float3(1, 0, 0) ||
            Math.Abs(loaded.BlendShapeWeights[0] - .35f) > .0001f || mesh.BlendShapes.Single().Frames.Count() != 2)
            throw new Exception("Native mesh geometry, skin references or morphs changed after package reload.");
        restored.Destroy(); root.Destroy();
        Console.WriteLine("PASS: generated native meshes retain identity, skin indices, static instances and morphs after package reload");
    }
}
