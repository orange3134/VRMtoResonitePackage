using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ModelObjectScaleChecks
{
    public static void Run(Func<string, string, string, string> asset, string modelGuid)
    {
        const string wrapperGuid = "85858585858585858585858585858585";
        const string variantGuid = "86868686868686868686868686868686";
        long bone = UnityModelFileIdResolver.Compute("Transform", "//RootNode/Left/Shared/Transform", 0);
        long root = UnityModelFileIdResolver.Compute("Transform", "//RootNode/Transform", 0);
        string Instance(long id, string guid, string modifications = "") => $$"""

--- !u!1001 &{{id}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{guid}}}
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
{{modifications}}
""";
        string Scale(long id, string guid, string axis, string value) => $$"""
    - target: {fileID: {{id}}, guid: {{guid}}}
      propertyPath: m_LocalScale.{{axis}}
      value: {{value}}
      objectReference: {fileID: 0}
""";
        string wrapper = asset("Assets/ModelScales.prefab", wrapperGuid,
            Instance(10, modelGuid, Scale(bone, modelGuid, "x", "1.05") + "\n" +
                Scale(bone, modelGuid, "y", "0.9") + "\n" + Scale(root, modelGuid, "x", "2")) + $$"""

--- !u!4 &90 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{bone}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 10}
""");
        string original = File.ReadAllText(wrapper);
        string input = asset("Assets/ModelScalesVariant.prefab", variantGuid,
            Instance(20, wrapperGuid, Scale(90, wrapperGuid, "x", "1") + "\n" + Scale(90, wrapperGuid, "z", "-1")) +
            Instance(30, wrapperGuid));
        using var package = UnityPackage.Open(input);
        var avatar = new VrchatAvatar { FbxGuid = modelGuid };
        typeof(VrchatAvatarParser).GetMethod("ParseRenderers", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { package, variantGuid, avatar });
        var scales = avatar.ModelObjectScales;
        if (scales.Count != 2 || scales.Keys.Any(k => k.Path != "RootNode/Left/Shared") ||
            scales.Keys.Select(k => k.FbxGuid).Distinct().Count() != 2 ||
            !scales.Values.Contains(new(1.05f, .9f, null)) || !scales.Values.Contains(new(1, .9f, -1)) ||
            File.ReadAllText(wrapper) != original)
            throw new Exception("Model scales must fold individual axes per occurrence, retain explicit one and skip model root placement.");
        Console.WriteLine("PASS: nested model scale overrides preserve axes, stripped references and occurrence scope");
    }
}
