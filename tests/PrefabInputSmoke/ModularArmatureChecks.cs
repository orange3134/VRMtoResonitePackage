using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ModularArmatureChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        const string clothing = "abcdef88000000000000000000000001";
        const string avatarGuid = "abcdef88000000000000000000000002";
        string Object(long go, long transform, string name, long parent) =>
            $"--- !u!1 &{go}\nGameObject:\n  m_Name: {name}\n  m_Component:\n  - component: {{fileID: {transform}}}\n" +
            $"--- !u!4 &{transform}\nTransform:\n  m_GameObject: {{fileID: {go}}}\n  m_Father: {{fileID: {parent}}}\n";
        asset("Assets/MergeClothing.prefab", clothing, Object(1, 2, "Clothing", 0) + Object(3, 4, "armature", 2) +
            "--- !u!114 &5\nMonoBehaviour:\n  m_GameObject: {fileID: 3}\n  m_Script: {guid: 2df373bf91cf30b4bbd495e11cb1a2ec}\n" +
            "  mergeTarget:\n    referencePath: Wrong/armature\n    targetObject: {fileID: 0}\n  prefix: old\n");
        string text = Object(10, 11, "Avatar", 0) + Object(12, 13, "armature", 11) +
            $"--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {clothing}}}\n  m_Modification:\n    m_TransformParent: {{fileID: 11}}\n    m_Modifications:\n" +
            $"    - target: {{fileID: 5, guid: {clothing}}}\n      propertyPath: mergeTarget.targetObject\n      objectReference: {{fileID: 12}}\n" +
            $"    - target: {{fileID: 5, guid: {clothing}}}\n      propertyPath: prefix\n      value: new\n";
        string input = asset("Assets/MergeAvatar.prefab", avatarGuid, text);
        VrchatAvatar Read()
        {
            using var package = UnityPackage.Open(input);
            var avatar = new VrchatAvatar();
            typeof(VrchatAvatarParser).GetMethod("ParseVariantModularAvatar", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { package, avatarGuid, avatar, null });
            return avatar;
        }
        var merge = Read().ModularMergeArmatures.Single();
        if (merge.SourceBoneTarget?.PrefabGuid != clothing || merge.SourceBoneTarget.TransformFileId != 4 ||
            merge.TargetBoneTarget?.PrefabGuid != avatarGuid || merge.TargetBoneTarget.TransformFileId != 13 || merge.Prefix != "new")
            throw new Exception("Merge Armature must preserve source identity and the variant's targetObject/prefix override.");
        Console.WriteLine("PASS: Merge Armature preserves scoped source and overridden target identities");
        File.WriteAllText(input, text + $"    m_RemovedComponents:\n    - {{fileID: 5, guid: {clothing}}}\n");
        if (Read().ModularMergeArmatures.Count != 0) throw new Exception("Removed Merge Armature was recreated.");
        Console.WriteLine("PASS: Removed Merge Armature remains removed");
    }
}
