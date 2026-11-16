using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class NativeMeshSceneChecks
{
    public static async Task CheckReal(Slot parent, string prefab, string output, string avatarName = null,
        bool primaryOnly = false)
    {
        using var package = UnityPackage.Open(prefab);
        var avatar = VrchatAvatarParser.Parse(package, avatarName);
        var expected = avatar.MeshCopies.Where(c => c.IsSkinned && !c.RendererRemoved &&
            (!primaryOnly || c.FbxGuid == avatar.FbxGuid)).ToArray();
        if (primaryOnly) Console.WriteLine($"Checking primary model skins only: {avatar.FbxGuid}, copies={expected.Length}");
        if (expected.Length == 0 || avatarName == null && !expected.Any(c => c.GeneratedMesh != null))
            throw new Exception("No expected meshes in test avatar.");
        var sourceScene = package.ReadScene(package.InputPrefab ?? package.ByGuid(expected[0].PrefabGuid));
        string SourcePath(long gameObject)
        {
            var names = new List<string>();
            for (var transform = sourceScene.TransformOfGameObject(gameObject); transform != null;
                transform = sourceScene.Doc(transform.Root["m_Father"].FileID ?? 0))
                names.Add(sourceScene.Doc(transform.Root["m_GameObject"].FileID.Value).Root["m_Name"].AsString());
            names.Reverse();
            return string.Join("/", names);
        }
        string SavedPath(Slot slot) => slot == parent ? "" : SavedPath(slot.Parent) + "/" + slot.Name;
        var sourceWorld = new Dictionary<long, System.Numerics.Matrix4x4>();
        System.Numerics.Matrix4x4 World(long id)
        {
            if (id == 0) return System.Numerics.Matrix4x4.Identity;
            if (sourceWorld.TryGetValue(id, out var cached)) return cached;
            var document = sourceScene.Doc(id) ?? throw new Exception("Missing source Transform: " + id);
            var local = sourceScene.GetLocalTransform(document.Root["m_GameObject"].FileID.Value);
            return sourceWorld[id] = System.Numerics.Matrix4x4.CreateScale(local.Scale) *
                System.Numerics.Matrix4x4.CreateFromQuaternion(local.Rotation) *
                System.Numerics.Matrix4x4.CreateTranslation(local.Position) * World(document.Root["m_Father"].FileID ?? 0);
        }
        await PackageImporter.ImportPackage(output, parent);
        await default(ToWorld);
        var renderers = parent.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
        for (int frame = 0; frame < 7200 && renderers.Any(r => r.Mesh.Target != null && !r.Mesh.IsAssetAvailable); frame++) await default(NextUpdate);
        foreach (var copy in expected)
        {
            string sourcePath = SourcePath(copy.GameObjectFileId);
            Console.WriteLine($"Checking {sourcePath}, model={copy.FbxGuid}, source={copy.SourcePath}");
            var matches = renderers.Where(r => r.Slot.Name == copy.Name).ToArray();
            if (matches.Length > 1)
                matches = matches.Where(r => SavedPath(r.Slot).EndsWith("/" + sourcePath, StringComparison.Ordinal)).ToArray();
            // A retained imported skeleton lives below RootNode instead of the
            // authored descriptor wrapper. Keep every path segment below that root.
            if (matches.Length == 0 && sourcePath.Count(c => c == '/') > 1)
                matches = renderers.Where(r => SavedPath(r.Slot).EndsWith(sourcePath[sourcePath.IndexOf('/')..], StringComparison.Ordinal)).ToArray();
            var renderer = matches.Single();
            var actual = renderer.Mesh.Asset?.Data ?? throw new Exception("Mesh not loaded: " + copy.Name);
            var source = copy.GeneratedMesh == null ? null : UnityNativeMesh.Decode(copy.GeneratedMesh);
            float units = copy.GeneratedMesh != null ? 1f : copy.FbxGuid == avatar.FbxGuid ? avatar.FbxImportScale :
                avatar.AdditionalFbxs.Single(model => model.Guid == copy.FbxGuid).ImportScale;
            if (source != null && (actual.VertexCount != source.VertexCount || actual.BoneCount != source.BoneCount ||
                !actual.RawPositions.Take(actual.VertexCount).SequenceEqual(source.RawPositions.Take(source.VertexCount)) ||
                !actual.BlendShapes.Select(s => s.Name).SequenceEqual(source.BlendShapes.Select(s => s.Name))))
                throw new Exception("Saved generated geometry/morph mismatch: " + copy.Name);
            var indices = source != null ? Enumerable.Range(0, actual.BoneCount).ToArray() :
                VrchatSceneSetup.MapSourceBoneIndices(copy.SourceBoneNames,
                    Enumerable.Range(0, actual.BoneCount).Select(i => actual.GetBone(i).Name).ToArray());
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
                if (i >= renderer.Bones.Count || renderer.Bones[i] == null || renderer.Bones[i].Name != copy.BoneTargets[indices[i]].Name)
                    throw new Exception($"Saved generated skin mismatch: {copy.Name}, bone {i}");
            // Independently reconstruct the Unity hierarchy from the original YAML,
            // not the converter's captured pose records. Compare affine bone bases
            // in renderer space, so export wrappers and eye pivots cannot hide errors.
            long rendererTransform = sourceScene.TransformOfGameObject(copy.GameObjectFileId)!.FileId;
            if (!System.Numerics.Matrix4x4.Invert(World(rendererTransform), out var rendererInverse))
                throw new Exception("Singular source renderer transform");
            float maxError = 0;
            foreach (int i in used)
            {
                var basis = World(copy.BoneTargets[indices[i]].TransformFileId) * rendererInverse;
                foreach (var point in new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX,
                             System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ })
                {
                    var wanted = System.Numerics.Vector3.Transform(point * units, basis);
                    var got = renderer.Slot.GlobalPointToLocal(renderer.Bones[i].LocalPointToGlobal(new float3(point.X, point.Y, point.Z)));
                    float error = MathX.Distance(got, new float3(wanted.X, wanted.Y, wanted.Z));
                    maxError = Math.Max(maxError, error);
                    if (error > .0001f) throw new Exception($"Saved authored bone pose mismatch: {copy.Name}/{copy.BoneTargets[indices[i]].Name}, error={error}, expected={wanted}, actual={got}");
                    if (source != null && MathX.Distance(
                            actual.GetBone(i).BindPose.TransformPoint3x4(new float3(point.X, point.Y, point.Z)),
                            source.GetBone(i).BindPose.TransformPoint3x4(new float3(point.X, point.Y, point.Z))) > .00001f)
                        throw new Exception($"Saved generated bind pose mismatch: {copy.Name}, bone {i}");
                }
            }
            Console.WriteLine($"PASS: saved {(source == null ? "FBX" : "generated")} mesh {copy.Name}, vertices={actual.VertexCount}, bones={actual.BoneCount}, weightedBones={used.Count}, shapes={actual.BlendShapes.Count()}");
            Console.WriteLine($"PASS: saved Unity bone transforms {copy.Name}, maximum affine basis error={maxError:G6}");
        }
        Console.WriteLine($"PASS: {expected.Count(c => c.GeneratedMesh != null)} generated meshes and {expected.Length} skin poses reloaded and compared with source assets");
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
