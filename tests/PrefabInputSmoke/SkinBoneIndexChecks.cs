using VrmToResonitePackage.Vrchat;

internal static class SkinBoneIndexChecks
{
    public static void Run()
    {
        Check(new[] { "Hips", "Eye", "Shoulder", "Skirt" }, new[] { "Hips", "Shoulder", "Skirt" }, new[] { 0, 2, 3 });
        Check(new[] { "Hips", "Eye", "Shoulder", "Skirt" }, new[] { "Skirt", "Shoulder", "Hips" }, new[] { 3, 2, 0 });
        Check(new[] { "Shared", "Shared", "Tip" }, new[] { "Shared", "Shared", "Tip" }, new[] { 0, 1, 2 });
        Check(new[] { "Shared", "Shared", "Tip" }, new[] { "Tip" }, new[] { 2 });
        foreach (var imported in new[] { new[] { "Shared" }, new[] { "Missing" } })
        {
            try { VrchatSceneSetup.MapSourceBoneIndices(new[] { "Shared", "Shared", "Tip" }, imported); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Ambiguous or missing original bone index must not silently bind another bone");
        }
        // Overrides remain indexed by the original table, including a renamed target
        // and an explicit null. Matching the target name would lose both overrides.
        var copy = new VrchatMeshCopy("model", "Clothes", "Clothes", true, true);
        copy.BoneTargets[2] = new VrchatBoneTarget("model", "RenamedShoulder");
        copy.BoneTargets[3] = new VrchatBoneTarget(null, null);
        var indices = VrchatSceneSetup.MapSourceBoneIndices(
            new[] { "Hips", "Eye", "Shoulder", "Skirt" }, new[] { "Shoulder", "Skirt" });
        if (copy.BoneTargets[indices[0]].Name != "RenamedShoulder" || copy.BoneTargets[indices[1]].Name != null)
            throw new Exception("Compacted skin must preserve original-index prefab overrides");
        Console.WriteLine("PASS: Compacted/reordered skin tables retain prefab indices, duplicates and explicit overrides");
        CheckUnusedWeights();
    }

    private static void CheckUnusedWeights()
    {
        var source = new Assimp.Mesh();
        source.Vertices.AddRange(new Assimp.Vector3D[2]);
        void Bone(string name, int vertex, float weight)
        {
            var bone = new Assimp.Bone { Name = name };
            bone.VertexWeights.Add(new Assimp.VertexWeight(vertex, weight));
            source.Bones.Add(bone);
        }
        Bone("Main", 0, 1.2f);
        Bone("Elbow", 0, .001f); // At the threshold before normalization, below it afterwards.
        Bone("Unused", 0, .0008f);
        Bone("Only", 1, .0004f); // No larger influence at this vertex.
        var unused = VrmToResonitePackage.Unity.UnityModelFileIdResolver.FindUnusedWeightBones([source], .001f);
        if (!unused.SetEquals(["Unused"])) throw new Exception("Threshold must use raw FBX weights");
        var mesh = new Elements.Assets.MeshX { HasBoneBindings = true };
        foreach (string name in new[] { "Elbow", "Unused", "Main" }) mesh.AddBone(name);
        mesh.AddVertex(Elements.Core.float3.Zero);
        mesh.RawBoneBindings[0] = new Elements.Assets.BoneBinding {
            boneIndex0 = 2, weight0 = .9985f, boneIndex1 = 0, weight1 = .0008f, boneIndex2 = 1, weight2 = .0007f };
        if (VrchatSkinRetargeter.RemoveUnusedWeights(mesh, unused) != 1 ||
            VrchatSkinRetargeter.RemoveUnusedWeights(mesh, unused) != 0)
            throw new Exception("Unused weight removal must be idempotent");
        var saved = mesh.RawBoneBindings[0];
        if (saved.boneIndex1 != 0 || Math.Abs(saved.weight1 - .0008f / .9993f) > 1e-8f ||
            Math.Abs(saved.TotalWeight - 1) > 1e-6f || saved.weight2 != 0f || saved.weight3 != 0f)
            throw new Exception("Normalize surviving influences without pruning the normalized elbow weight");
        Console.WriteLine("PASS: FBX weight threshold precedes normalization and preserves supported bones");
    }

    private static void Check(string[] source, string[] imported, int[] expected)
    {
        if (!VrchatSceneSetup.MapSourceBoneIndices(source, imported).SequenceEqual(expected))
            throw new Exception("Incorrect original bone indices after mesh import");
    }
}
