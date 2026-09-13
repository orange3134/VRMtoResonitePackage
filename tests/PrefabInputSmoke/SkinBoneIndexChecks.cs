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
    }

    private static void Check(string[] source, string[] imported, int[] expected)
    {
        if (!VrchatSceneSetup.MapSourceBoneIndices(source, imported).SequenceEqual(expected))
            throw new Exception("Incorrect original bone indices after mesh import");
    }
}
