using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class UnweightedSkeletonChecks
{
    public static void Run(Func<string, string, string, string> asset, string fbx, string prefab, string originalGuid)
    {
        const string modelGuid = "bd191919191919191919191919191919";
        const string prefabGuid = "bd202020202020202020202020202020";
        // Two weighted bones named Shared exist under Left and Right. These ends
        // carry no skin weights; one also owns a deeper, unweighted helper.
        fbx = fbx.Replace("Objects: {", """
Objects: {
    Model: 100, "Model::End", "Null" { }
    Model: 101, "Model::End", "Null" { }
    Model: 102, "Model::Tip", "Null" { }
""").Replace("Connections: {", """
Connections: {
    C: "OO",100,3
    C: "OO",101,4
    C: "OO",102,100
""");
        asset("Assets/UnweightedEnds.fbx", modelGuid, fbx);
        prefab = prefab.Replace(originalGuid, modelGuid);
        foreach (var (id, name, parent) in new[] { (300, "End", 121), (310, "End", 141),
                     (320, "Tip", 301), (330, "NewHelper", 301) })
            prefab += $$"""

--- !u!1 &{{id}}
GameObject:
  m_Name: {{name}}
  m_IsActive: 0
--- !u!4 &{{id + 1}}
Transform:
  m_GameObject: {fileID: {{id}}}
  m_Father: {fileID: {{parent}}}
  m_LocalPosition: {x: 0.03, y: 0.04, z: 0.05}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalScale: {x: 1.1, y: 1.2, z: 1.3}
""";
        foreach (var (owner, children) in new[] { (120, "{fileID: 151}, {fileID: 301}"),
                     (140, "{fileID: 311}"), (300, "{fileID: 321}, {fileID: 331}") })
            prefab = prefab.Replace($"m_GameObject: {{fileID: {owner}}}\n",
                $"m_GameObject: {{fileID: {owner}}}\n  m_Children: [{children}]\n");
        int tipStart = prefab.IndexOf("--- !u!4 &321", StringComparison.Ordinal);
        int tipEnd = prefab.IndexOf("--- !u!1 &330", tipStart, StringComparison.Ordinal);
        string tip = prefab[tipStart..tipEnd];
        prefab = prefab.Replace(tip, string.Join('\n', tip.Split('\n').Where(line => !line.StartsWith("  m_Local"))));
        string input = asset("Assets/UnweightedEnds.prefab", prefabGuid, prefab);
        using var package = UnityPackage.Open(input);
        var avatar = new VrchatAvatar { FbxGuid = modelGuid };
        var copy = new VrchatMeshCopy(modelGuid, "Body", "Body", true, true);
        copy.BoneTargets[0] = new(modelGuid, "Shared", "Left/Shared", prefabGuid, 121);
        copy.BoneTargets[1] = new(modelGuid, "Shared", "Right/Shared", prefabGuid, 141);
        avatar.MeshCopies.Add(copy);
        typeof(VrchatAvatarParser).GetMethod("ParsePhysics", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { package, prefabGuid, avatar, null });
        var transforms = avatar.PhysicsPlacements.SelectMany(p => p.Transforms).ToArray();
        foreach (var (id, path) in new[] { (301, "Left/Shared/End"), (311, "Right/Shared/End"), (321, "Left/Shared/End/Tip") })
        {
            var entries = transforms.Where(t => t.Key == $"{prefabGuid}:{id}").ToArray();
            if (entries.Length == 0 || entries.Any(t => t.ImportedBone?.FbxGuid != modelGuid || t.ImportedBone.Path != path || t.Active))
                throw new Exception("Unweighted helper must reuse its exact imported path and active state: " + path +
                    "; entries=" + string.Join("; ", entries.Select(t => $"{t.Name}: {t.ImportedBone}, active={t.Active}")) +
                    "; source=" + string.Join(", ", package.ModelFileIds(modelGuid).Hierarchy.Select(n => n.Path)));
            if (!avatar.AuthoredImportedBones.Any(target => target.Path == path))
                throw new Exception("Authored skeleton membership must survive even without pose overrides: " + path);
            if (id == 321)
            {
                if (avatar.AuthoredBoneTransforms.Keys.Any(target => target.Path == path))
                    throw new Exception("Missing pose must not introduce a fabricated override.");
                continue;
            }
            var pose = avatar.AuthoredBoneTransforms.Single(p => p.Key.Path == path).Value;
            if (pose.Position?.Y != .04f || pose.Scale?.Z != 1.3f)
                throw new Exception("Reused unweighted helper lost authored local pose: " + path);
        }
        if (!transforms.Any(t => t.Key == $"{prefabGuid}:331" && t.ImportedBone == null && t.LocalPosition.Y == .04f))
            throw new Exception("A newly authored helper absent from the FBX must remain a separate object.");
        Console.WriteLine("PASS: unweighted ends and nested helpers reuse exact skeleton paths without duplicating same-named branches");
    }
}
