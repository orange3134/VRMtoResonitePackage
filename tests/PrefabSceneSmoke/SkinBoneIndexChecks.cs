using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class SkinBoneIndexChecks
{
    public static void Run(Slot importedRoot, string fbxPath)
    {
        var resolver = new UnityModelFileIdResolver(new UnityAsset
            { LogicalPath = "Assets/Test.fbx", DiskPath = fbxPath });
        var roots = new Dictionary<string, Slot> { ["model"] = importedRoot };
        var sources = VrchatSceneSetup.CaptureImportedObjects(roots);
        var paths = VrchatSceneSetup.CaptureImportedPaths(roots);
        foreach (var original in importedRoot.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray())
        {
            if (!resolver.MeshBoneNames.TryGetValue(original.Slot.Name, out var names)) continue;
            var mesh = original.Mesh.Asset.Data;
            var originalBones = original.Bones.ToArray();
            var avatar = new VrchatAvatar { FbxGuid = "model" };
            var copy = new VrchatMeshCopy("model", original.Slot.Name, "Bone table copy", true, true)
                { SourcePath = paths[original.Slot], Transform = new VrchatPrefabTransform() };
            copy.SourceBoneNames.AddRange(names);
            // Author references in the unsplit source table, even if the imported mesh
            // has dropped unused entries or changed their order. Use exact paths.
            for (int i = 0; i < names.Length; i++)
            {
                var matching = Enumerable.Range(0, mesh.BoneCount)
                    .Where(j => mesh.GetBone(j).Name == names[i]).ToArray();
                if (matching.Length != 1 || names.Count(name => name == names[i]) != 1) continue;
                Slot bone = originalBones[matching[0]];
                if (bone != null) copy.BoneTargets[i] = new VrchatBoneTarget("model", bone.Name, paths[bone]);
            }
            avatar.MeshCopies.Add(copy);
            var build = VrchatSceneSetup.InstantiateMeshCopies(avatar, sources, _ => importedRoot, paths);
            build.Bind();
            var result = importedRoot.FindChild("Bone table copy").GetComponent<SkinnedMeshRenderer>();
            if (!result.Bones.SequenceEqual(originalBones))
                throw new Exception($"Imported bone table mismatch: {original.Slot.Name} ({names.Length} -> {mesh.BoneCount})");
            // Verify actual deformation with the imported bind poses and vertex weights.
            float maximumError = 0;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var binding = mesh.RawBoneBindings[i];
                float3 before = float3.Zero, after = float3.Zero;
                for (int j = 0; j < 4; j++)
                {
                    float weight = binding.GetWeight(j);
                    if (weight == 0) continue;
                    int index = binding.GetBoneIndex(j);
                    var position = mesh.GetBone(index).BindPose.TransformPoint3x4(mesh.GetPosition(i));
                    before += originalBones[index].LocalPointToGlobal(position) * weight;
                    after += result.Bones[index].LocalPointToGlobal(position) * weight;
                }
                maximumError = Math.Max(maximumError, MathX.Distance(before, after));
            }
            if (!float.IsFinite(maximumError) || maximumError > 0.00001f)
                throw new Exception($"Copied skin deformation changed: {original.Slot.Name}: {maximumError}");
            Console.WriteLine($"PASS: {original.Slot.Name} source bones={names.Length}, imported={mesh.BoneCount}, vertices={mesh.VertexCount}, maximum error={maximumError}");
            result.Slot.Destroy();
        }
    }
}
