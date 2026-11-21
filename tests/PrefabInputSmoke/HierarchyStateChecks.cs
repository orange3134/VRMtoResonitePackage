using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class HierarchyStateChecks
{
    public static void Run(Func<string, string, string, string> asset, string modelGuid)
    {
        const string prefabGuid = "b5202020202020202020202020202020";
        long left = UnityModelFileIdResolver.Compute("GameObject", "//RootNode/Left/Shared", 0);
        long leftTransform = UnityModelFileIdResolver.Compute("Transform", "//RootNode/Left/Shared/Transform", 0);
        long rootTransform = UnityModelFileIdResolver.Compute("Transform", "//RootNode/Transform", 0);
        string Instance(long id, string value) => $$"""

--- !u!1001 &{{id}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{modelGuid}}}
  m_Modification:
    m_TransformParent: {fileID: 2}
    m_Modifications:
    - target: {fileID: {{left}}, guid: {{modelGuid}}}
      propertyPath: m_IsActive
      value: {{value}}
      objectReference: {fileID: 0}
""";
        string input = asset("Assets/HierarchyStates.prefab", prefabGuid, Instance(10, "0") + Instance(20, "1") + $$"""

--- !u!1 &1
GameObject:
  m_Name: Avatar
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
  m_Children: [{fileID: 93}, {fileID: 92}]
--- !u!4 &92 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{rootTransform}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 10}
--- !u!4 &93 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{rootTransform}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 20}

--- !u!1 &91 stripped
GameObject:
  m_CorrespondingSourceObject: {fileID: {{left}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 10}
--- !u!4 &90 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{leftTransform}}, guid: {{modelGuid}}}
  m_PrefabInstance: {fileID: 10}
  m_GameObject: {fileID: 91}
--- !u!1 &95
GameObject:
  m_Name: Shared
  m_IsActive: 0
--- !u!4 &96
Transform:
  m_GameObject: {fileID: 95}
  m_Father: {fileID: 2}
""");
        using var source = UnityPackage.Open(input);
        string meta = source.ByGuid(modelGuid).MetaPath;
        string original = File.ReadAllText(meta);
        try
        {
            foreach (bool sort in new[] { true, false })
            {
                File.WriteAllText(meta, "guid: " + modelGuid + "\nModelImporter:\n  meshes:\n    sortHierarchyByName: " + (sort ? 1 : 0) + "\n");
                using var fresh = UnityPackage.Open(input);
                using var view = UnityPrefabInstances.CreateView(fresh, prefabGuid, null);
                var guids = view.PrefabGraph.Scenes.SelectMany(e => e.Scene.Documents.Values)
                    .Where(d => d.ClassId == 1001).Select(d => d.Root["m_SourcePrefab"].Guid).ToArray();
                var avatar = new VrchatAvatar { FbxGuid = guids[0] };
                avatar.AdditionalFbxs.Add(new() { Guid = guids[1] });
                var localScene = view.PrefabGraph.Scenes.Single(e => e.Guid == prefabGuid).Scene;
                typeof(VrchatAvatarParser).GetMethod("ParseInactiveGameObjects", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { localScene, localScene.GameObjects.Select(g => g.FileId).ToHashSet(), avatar, prefabGuid });
                if (avatar.InactiveGameObjects.Any())
                    throw new Exception("A disabled local namesake must not disable imported bones by name.");
                typeof(VrchatAvatarParser).GetMethod("ParsePhysics", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { view, prefabGuid, avatar, null });
                if (avatar.HierarchyActiveStates.Single(p => p.Key.PrefabGuid == prefabGuid && p.Key.TransformFileId == 96).Value)
                    throw new Exception("The local namesake must retain its own disabled state by prefab identity.");
                var leftStates = avatar.HierarchyActiveStates.Where(p => p.Key.Path == "RootNode/Left/Shared").ToArray();
                var placed = avatar.HierarchyOrders.Single(o => o.Parent.PrefabGuid == prefabGuid && o.Parent.TransformFileId == 2);
                if (!placed.Children.Select(t => t.FbxGuid).SequenceEqual(guids.Reverse()) || placed.Children.Any(t => t.Path != ""))
                    throw new Exception("Stripped model roots must follow authored child order using PrefabInstance parent identity.");
                if (leftStates.Length != 2 || leftStates.Select(p => p.Value).Distinct().Count() != 2 ||
                    avatar.HierarchyActiveStates.Where(p => p.Key.Path == "RootNode/Right/Shared").Any(p => !p.Value))
                    throw new Exception("Active state must distinguish same-named branches and separate FBX occurrences, including explicit true.");
                foreach (var guid in guids)
                {
                    var order = avatar.HierarchyOrders.Single(o => o.Parent.FbxGuid == guid && o.Parent.Path == "RootNode");
                    var expected = sort ? new[] { "Body", "Left", "Right" } : new[] { "Left", "Right", "Body" };
                    if (!order.Children.Select(t => t.Name).SequenceEqual(expected))
                        throw new Exception("Model hierarchy must honor sortHierarchyByName, including explicit false.");
                }
            }
        }
        finally { File.WriteAllText(meta, original); }
        Console.WriteLine("PASS: FBX child order honors importer policy and active state distinguishes paths and occurrences");
    }
}
