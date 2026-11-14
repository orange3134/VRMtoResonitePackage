using System.Collections;
using System.Numerics;
using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ModelRootPlacementChecks
{
    public static void Run(Func<string, string, string, string> asset, string modelGuid)
    {
        const string baseGuid = "b8de250c99fc408995693e25f318f61a";
        const string variantGuid = "ff289c28cb7e4fba9d9df2d6f0d2cf1a";
        const string outerGuid = "e779837cab3a40e0ab6d686e93b78d19";
        const long rootId = -8679921383154817045L;
        string Change(string guid, long id, string property, string value) => $$"""
    - target: {fileID: {{id}}, guid: {{guid}}}
      propertyPath: {{property}}
      value: {{value}}
      objectReference: {fileID: 0}

""";
        string Instance(long id, string guid, string modifications) => $$"""
--- !u!1001 &{{id}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{guid}}}
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
{{modifications}}

""";
        string Alias(long id, long owner, string guid, long source) => $$"""
--- !u!4 &{{id}} stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{source}}, guid: {{guid}}}
  m_PrefabInstance: {fileID: {{owner}}}

""";
        string basePath = asset("Assets/RootPlacement.prefab", baseGuid,
            Instance(10, modelGuid,
                Change(modelGuid, rootId, "m_LocalScale.x", "0.87") +
                Change(modelGuid, rootId, "m_LocalScale.y", "0.87") +
                Change(modelGuid, rootId, "m_LocalScale.z", "0.87") +
                Change(modelGuid, rootId, "m_LocalPosition.y", "2") +
                Change(modelGuid, rootId, "m_LocalRotation.y", "0.6") +
                Change(modelGuid, rootId, "m_LocalRotation.w", "0.8")) + Alias(90, 10, modelGuid, rootId));
        string original = File.ReadAllText(basePath);
        string variantPath = asset("Assets/RootPlacementVariant.prefab", variantGuid,
            Instance(20, baseGuid, Change(baseGuid, 90, "m_LocalPosition.y", "0")) +
            Alias(91, 20, baseGuid, 90));
        Check(variantPath, variantGuid, new Vector3(.87f), 0, .6f, .8f);
        string outerPath = asset("Assets/RootPlacementOuter.prefab", outerGuid,
            Instance(30, variantGuid, Change(variantGuid, 91, "m_LocalScale.x", "1")) +
            Instance(40, baseGuid, Change(baseGuid, 90, "m_LocalPosition.y", "3")));
        Check(outerPath, outerGuid, new Vector3(1, .87f, .87f), 0, .6f, .8f, sibling: true);
        if (File.ReadAllText(basePath) != original) throw new Exception("Placement parsing mutated the source prefab.");
        Console.WriteLine("PASS: model root placement inherits axes, explicit zero/one, rotation and occurrence identity");
    }

    private static void Check(string input, string guid, Vector3 scale, float y, float ry, float rw, bool sibling = false)
    {
        using var source = UnityPackage.Open(input);
        using var view = UnityPrefabInstances.CreateView(source, guid, null);
        var method = typeof(VrchatAvatarParser).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "CollectFbxPlacements" && m.GetParameters().Length == 2);
        var placements = (IDictionary)method.Invoke(null, new object[] { view, guid })!;
        var values = placements.Values.Cast<object>().ToArray();
        T Field<T>(object placement, string name) => (T)placement.GetType().GetField(name)!.GetValue(placement)!;
        bool Matches(object placement, Vector3 expectedScale, float expectedY) =>
            Field<Vector3>(placement, "LocalScale") == expectedScale &&
            Field<Vector3>(placement, "LocalPosition").Y == expectedY &&
            Field<Quaternion>(placement, "LocalRotation") == new Quaternion(0, ry, 0, rw);
        if (values.Length != (sibling ? 2 : 1) || !values.Any(p => Matches(p, scale, y)) ||
            sibling && !values.Any(p => Matches(p, new Vector3(.87f), 3)))
            throw new Exception("Model root placement lost inherited axes or leaked a sibling's overrides.");
    }
}
