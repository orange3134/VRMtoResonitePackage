using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class RendererRemovalChecks
{
    public static void Run(Func<string, string, string, string> asset, string regularCopy)
    {
        const string leafGuid = "ad120000000000000000000000000001";
        const string wrapperGuid = "ad120000000000000000000000000002";
        const string outerGuid = "ad120000000000000000000000000003";
        string original = File.ReadAllText(regularCopy);
        var sourceScene = UnityScene.Parse(original);
        var mesh = sourceScene.RendererMesh(sourceScene.MeshRenderers.Single());
        string leaf = asset("Assets/RemovedRendererBase.prefab", leafGuid, $$"""
--- !u!1 &1
GameObject:
  m_Name: ParentRenderer
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!23 &3
MeshRenderer:
  m_GameObject: {fileID: 1}
  m_Materials:
  - {guid: removedMaterial}
--- !u!33 &4
MeshFilter:
  m_GameObject: {fileID: 1}
  m_Mesh: {fileID: {{mesh.FileID}}, guid: {{mesh.Guid}}}
--- !u!1 &11
GameObject:
  m_Name: ChildRenderer
--- !u!4 &12
Transform:
  m_GameObject: {fileID: 11}
  m_Father: {fileID: 2}
--- !u!23 &13
MeshRenderer:
  m_GameObject: {fileID: 11}
  m_Materials:
  - {guid: survivingMaterial}
--- !u!33 &14
MeshFilter:
  m_GameObject: {fileID: 11}
  m_Mesh: {fileID: {{mesh.FileID}}, guid: {{mesh.Guid}}}
""");
        string Instance(long id, string guid, string removal = "") =>
            $"--- !u!1001 &{id}\nPrefabInstance:\n  m_SourcePrefab: {{guid: {guid}}}\n  m_Modification:\n" + removal;
        foreach (int removedId in new[] { 3, 4 })
        foreach (string alias in new[] { "direct", "omitted", "explicit" })
        {
            string wrapper = Instance(100, leafGuid);
            if (alias == "explicit") wrapper += $"--- !u!{(removedId == 3 ? 23 : 33)} &900 stripped\nComponent:\n  m_CorrespondingSourceObject: {{guid: {leafGuid}, fileID: {removedId}}}\n  m_PrefabInstance: {{fileID: 100}}\n";
            asset("Assets/RemovedRendererWrapper.prefab", wrapperGuid, wrapper);
            string targetGuid = alias == "direct" ? leafGuid : wrapperGuid;
            int targetId = alias == "direct" ? removedId : alias == "omitted" ? removedId ^ 100 : 900;
            string outer = asset("Assets/RemovedRendererOuter.prefab", outerGuid,
                Instance(200, wrapperGuid, $"    m_RemovedComponents:\n    - {{guid: {targetGuid}, fileID: {targetId}}}\n") +
                Instance(201, wrapperGuid));
            using var package = UnityPackage.Open(outer);
            using var view = (UnityPackage)typeof(UnityPackage).Assembly.GetType("VrmToResonitePackage.Unity.UnityPrefabInstances")!
                .GetMethod("CreateView")!.Invoke(null, new object?[] { package, outerGuid, null })!;
            var avatar = new VrchatAvatar();
            typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { view, outerGuid, avatar });
            if (avatar.MeshCopies.Count != 4 || avatar.MeshCopies.Count(c => c.RendererRemoved) != 1 ||
                avatar.MeshCopies.Single(c => c.RendererRemoved).Name != "ParentRenderer" ||
                avatar.RendererMaterials.Count != 3 || avatar.MeshCopies.Any(c => !c.ReplaceSourceRenderer))
                throw new Exception($"Renderer component removal failed: {removedId}, {alias}");
            if (package.ReadScene(package.ByGuid(leafGuid)).Doc(removedId) == null || File.ReadAllText(regularCopy) != original)
                throw new Exception("Renderer removal mutated its source");
            Console.WriteLine($"PASS: Removed component {removedId} via {alias} alias omits only its instance renderer and materials, preserving child/sibling objects and source");
        }
    }
}
