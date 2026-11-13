using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ModelObjectNameChecks
{
    public static void Run(Func<string, string, string, string> asset, string modelGuid)
    {
        const string wrapperGuid = "83838383838383838383838383838383";
        const string variantGuid = "84848484848484848484848484848484";
        long parent = UnityModelFileIdResolver.Compute("GameObject", "//RootNode/Left", 0);
        long child = UnityModelFileIdResolver.Compute("GameObject", "//RootNode/Left/Shared", 0);
        string Instance(long id, string guid, string modifications = "") => $$"""

--- !u!1001 &{{id}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{guid}}}
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
{{modifications}}
""";
        string Rename(long id, string guid, string name) => $$"""
    - target: {fileID: {{id}}, guid: {{guid}}}
      propertyPath: m_Name
      value: {{name}}
      objectReference: {fileID: 0}
""";
        string wrapper = asset("Assets/ModelNames.prefab", wrapperGuid,
            Instance(10, modelGuid, Rename(parent, modelGuid, "Left.1") + "\n" + Rename(child, modelGuid, "Shared.1")) + $$"""

--- !u!1 &90 stripped
GameObject:
  m_CorrespondingSourceObject: {fileID: {{child}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 10}
""");
        string original = File.ReadAllText(wrapper);
        string input = asset("Assets/ModelNamesVariant.prefab", variantGuid,
            Instance(20, wrapperGuid, Rename(90, wrapperGuid, "Shared.2")) + Instance(30, wrapperGuid));
        using var package = UnityPackage.Open(input);
        var avatar = new VrchatAvatar { FbxGuid = modelGuid };
        typeof(VrchatAvatarParser).GetMethod("ParseRenderers", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { package, variantGuid, avatar });
        var names = avatar.ModelObjectNames;
        var children = names.Where(p => p.Key.Path == "RootNode/Left/Shared").ToArray();
        if (names.Count != 4 || children.Length != 2 ||
            children.Select(p => p.Key.FbxGuid).Distinct().Count() != 2 ||
            !children.Select(p => p.Value).Order().SequenceEqual(new[] { "Shared.1", "Shared.2" }) ||
            names.Count(p => p.Key.Path == "RootNode/Left" && p.Value == "Left.1") != 2 ||
            File.ReadAllText(wrapper) != original)
            throw new Exception("Model renames must fold base/variant stripped references per occurrence using original full paths.");
        Console.WriteLine("PASS: nested FBX name overrides retain original paths and separate repeated model occurrences");
    }
}
