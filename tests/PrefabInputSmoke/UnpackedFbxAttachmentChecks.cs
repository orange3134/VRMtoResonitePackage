using System.Collections;
using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class UnpackedFbxAttachmentChecks
{
    public static void Run(Func<string, string, string, string> asset, string fbx, string prefab, string bodyGuid)
    {
        const string accessoryGuid = "ba171717171717171717171717171717";
        asset("Assets/AttachedModel.fbx", accessoryGuid, fbx);
        string input = asset("Assets/AttachedModel.prefab", "ba181818181818181818181818181818", prefab + $$"""

--- !u!1001 &500
PrefabInstance:
  m_Modification:
    m_TransformParent: {fileID: 151}
    m_Modifications: []
  m_SourcePrefab: {fileID: 100100000, guid: {{accessoryGuid}}}
""");
        using var package = UnityPackage.Open(input);
        var placements = (IDictionary)typeof(VrchatAvatarParser)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "CollectFbxPlacements" && m.GetParameters().Length == 3)
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, bodyGuid })!;
        var placement = placements[accessoryGuid]!;
        var parents = (List<VrchatPrefabTransform>)placement.GetType().GetProperty("ParentTransforms")!.GetValue(placement)!;
        if ((string)placement.GetType().GetField("ParentFbxGuid")!.GetValue(placement)! != bodyGuid ||
            parents.Count != 2 || parents[0].ImportedBone?.Path != "Left/Shared" ||
            parents[0].ImportedBone.TransformFileId != 121 || parents[1].Name != "Attachment")
            throw new Exception("An attached FBX must reuse the exact unpacked body bone and retain only new attachment ancestors.");
        if (parents[0].Active)
            throw new Exception("Attached FBX lost the authored inactive bone state.");
        Console.WriteLine("PASS: FBX instance attachment and copied skin share the unpacked skeleton identity and unit context");
    }
}
