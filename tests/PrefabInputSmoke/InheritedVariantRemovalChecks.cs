using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class InheritedVariantRemovalChecks
{
    public static void Run(Func<string, string, string, string> asset, string regularCopy)
    {
        const string baseGuid = "ad130000000000000000000000000001";
        const string wrapperGuid = "ad130000000000000000000000000002";
        const string outerGuid = "ad130000000000000000000000000003";
        string original = File.ReadAllText(regularCopy);
        var scene = UnityScene.Parse(original);
        var mesh = scene.RendererMesh(scene.MeshRenderers.Single());
        string baseText = original + $$"""

--- !u!1 &501
GameObject:
  m_Name: RemovedAccessory
--- !u!4 &502
Transform:
  m_GameObject: {fileID: 501}
  m_Father: {fileID: 33}
--- !u!137 &503
SkinnedMeshRenderer:
  m_GameObject: {fileID: 501}
  m_Mesh: {fileID: {{mesh.FileID}}, guid: {{mesh.Guid}}}
--- !u!1 &511
GameObject:
  m_Name: RemovedChild
--- !u!4 &512
Transform:
  m_GameObject: {fileID: 511}
  m_Father: {fileID: 502}
--- !u!137 &513
SkinnedMeshRenderer:
  m_GameObject: {fileID: 511}
  m_Mesh: {fileID: {{mesh.FileID}}, guid: {{mesh.Guid}}}
""";
        string basePath = asset("Assets/InheritedRemovalBase.prefab", baseGuid, baseText);
        string Instance(string guid, string modifications = "") =>
            $"--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {guid}}}\n  m_Modification:\n" + modifications;
        asset("Assets/InheritedRemovalWrapper.prefab", wrapperGuid, Instance(baseGuid));
        foreach (bool nested in new[] { false, true })
        {
            string sourceGuid = nested ? wrapperGuid : baseGuid;
            long removedId = nested ? 501 ^ 100 : 501;
            string variantText = Instance(sourceGuid,
                $"    m_RemovedGameObjects:\n    - {{fileID: {removedId}, guid: {sourceGuid}}}\n" +
                $"    m_Modifications:\n    - target: {{fileID: 34, guid: {baseGuid}}}\n" +
                "      propertyPath: ViewPosition.y\n      value: 1.75\n");
            string path = asset("Assets/InheritedRemovalVariant.prefab", outerGuid, variantText);
            using (var package = UnityPackage.Open(path))
            {
                Check(VrchatAvatarParser.ListAvatars(package).Single().Name == "InheritedRemovalVariant",
                    "Variant with removed objects remains selectable");
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    var avatar = VrchatAvatarParser.Parse(package);
                    Check(avatar.Name == "InheritedRemovalVariant" && avatar.ViewPosition?.Y == 1.75f,
                        "Inherited descriptor retains variant identity and overrides");
                    Check(avatar.MeshCopies.Count == 1 && avatar.MeshCopies.Single().Name == "Body_Base",
                        "Variant removes accessory and descendants while retaining body geometry");
                }
                Check(package.ReadScene(package.ByGuid(baseGuid)).Doc(501) != null &&
                      File.ReadAllText(basePath) == baseText && File.ReadAllText(path) == variantText,
                    "Variant deletion preserves cached scenes and source files");
            }
            // Removing the descriptor owner must still fail after graph composition.
            asset("Assets/InheritedRemovalVariant.prefab", outerGuid,
                Instance(sourceGuid, $"    m_RemovedGameObjects:\n    - {{fileID: {(nested ? 32 ^ 100 : 32)}, guid: {sourceGuid}}}\n"));
            using var removedRoot = UnityPackage.Open(path);
            bool rejected = false;
            try { VrchatAvatarParser.Parse(removedRoot); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Removed descriptor root cannot be resurrected by inherited candidate discovery");
        }

        // The descriptor lives on a stripped FBX root, while the immediate source
        // composes that avatar with another model. The outer Variant has one instance.
        const string accessoryGuid = "ad130000000000000000000000000004";
        asset("Assets/InheritedRemovalAccessory.fbx", accessoryGuid, "");
        asset("Assets/InheritedRemovalBase.prefab", baseGuid, Instance(mesh.Guid) + $$"""
--- !u!1 &32 stripped
GameObject:
  m_CorrespondingSourceObject: {fileID: 919132149155446097, guid: {{mesh.Guid}}}
  m_PrefabInstance: {fileID: 100}
--- !u!114 &34
MonoBehaviour:
  m_GameObject: {fileID: 32}
  m_Script: {fileID: 11500000, guid: 67cc4cb7839cd3741b63733d5adf0442}
""");
        asset("Assets/InheritedRemovalWrapper.prefab", wrapperGuid,
            Instance(baseGuid) + Instance(accessoryGuid).Replace("&100", "&200"));
        foreach (bool removeAccessory in new[] { false, true })
        {
            string path = asset("Assets/InheritedRemovalVariant.prefab", outerGuid,
                Instance(wrapperGuid, removeAccessory ?
                    $"    m_RemovedGameObjects:\n    - {{fileID: 919132149155446097, guid: {accessoryGuid}}}\n" : ""));
            using var package = UnityPackage.Open(path);
            Check(VrchatAvatarParser.ListAvatars(package).Single() is
                { Name: "InheritedRemovalVariant", IsComposedPrefab: true },
                "Single-instance Variant of a multi-model composition remains selectable");
            var avatar = VrchatAvatarParser.Parse(package);
            Check(avatar.FbxGuid == mesh.Guid && avatar.Name == "InheritedRemovalVariant" &&
                  avatar.AdditionalFbxs.Any(fbx => fbx.Guid == accessoryGuid) == !removeAccessory,
                "Composed Variant preserves descriptor body identity and applies accessory removal");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
