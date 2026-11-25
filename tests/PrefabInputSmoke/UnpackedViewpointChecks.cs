using System.Numerics;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class UnpackedViewpointChecks
{
    public static void Run()
    {
        // The descriptor is translated, rotated 90 degrees about Z and scaled in X and Y.
        // Children of the hips also inherit its displacement along Z.
        static string Transform(int id, int parent, string position,
            string rotation = "{x: 0, y: 0, z: 0, w: 1}", string scale = "{x: 1, y: 1, z: 1}") => $$"""
            --- !u!4 &{{id}}
            Transform:
              m_GameObject: {fileID: {{100 + id}}}
              m_Father: {fileID: {{parent}}}
              m_LocalPosition: {{position}}
              m_LocalRotation: {{rotation}}
              m_LocalScale: {{scale}}
            """;
        var scene = UnityScene.Parse(string.Join("\n",
            Transform(1, 0, "{x: 7, y: 8, z: 9}", "{x: 0, y: 0, z: 0.70710678, w: 0.70710678}", "{x: 2, y: 3, z: 1}"),
            Transform(2, 1, "{x: 0, y: 1, z: 0.1}"),
            Transform(3, 2, "{x: 0, y: 1, z: 0}"),
            Transform(4, 2, "{x: -0.2, y: 0.5, z: 0}"),
            Transform(5, 2, "{x: 0.2, y: 0.5, z: 0}")));
        var avatar = new VrchatAvatar { FbxGuid = "model", ViewPosition = new(0, 1.8f, .2f) };
        int id = 2;
        foreach (string role in new[] { "hips", "head", "leftUpperArm", "rightUpperArm" })
        {
            string path = role == "hips" ? role : $"hips/{role}";
            var bone = new VrchatBoneTarget("model", role, path);
            avatar.HumanBoneTargets.Add(role, bone);
            avatar.AuthoredImportedBones.Add(bone with { PrefabGuid = "prefab", TransformFileId = id++ });
        }
        VrchatViewpoint Capture() => VrchatViewpoint.CaptureUnpacked(avatar, scene, "prefab", 101);
        var hips = avatar.AuthoredImportedBones.Single(b => b.Name == "hips");
        avatar.AuthoredImportedBones.Add(hips with { PrefabGuid = "otherPrefab" });
        avatar.AuthoredImportedBones.Add(hips with { FbxGuid = "otherModel" });
        var captured = Capture();
        Vector3[] expected = { new(-3, 0, .1f), new(-6, 0, .1f), new(-4.5f, -.4f, .1f), new(-4.5f, .4f, .1f) };
        if (captured == null || captured.View != avatar.ViewPosition ||
            !captured.Anchors.Zip(expected).All(pair => Vector3.Distance(pair.First, pair.Second) < .00001f))
            throw new Exception("Unpacked viewpoint lost the authored bone transforms or descriptor offset.");
        avatar.AuthoredImportedBones.Add(hips with { TransformFileId = 6 });
        if (Capture() != null)
            throw new Exception("Ambiguous unpacked bone path was accepted for viewpoint alignment.");
        Console.WriteLine("PASS: unpacked viewpoint uses scoped bone poses relative to the descriptor origin");
    }
}
