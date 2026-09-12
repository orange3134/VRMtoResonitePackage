using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class FbxRendererRemovalChecks
{
    public static void Run(Func<string, string, string, string> asset, string sourcePrefab, string modelGuid)
    {
        const string wrapperGuid = "ae120000000000000000000000000001";
        const string outerGuid = "ae120000000000000000000000000002";
        const string ownerPath = "RootNode/Left/Shared/Body";
        long Id(string type, string path) => (long)typeof(UnityModelFileIdResolver)
            .GetMethod("Compute", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { type, "//" + path + "/" + type, 0 })!;
        string Instance(long id, string guid, string modifications = "") =>
            $"--- !u!1001 &{id}\nPrefabInstance:\n  m_SourcePrefab: {{guid: {guid}}}\n  m_Modification:\n" + modifications;
        string Override(long id, string guid, string material) =>
            $"    - target: {{fileID: {id}, guid: {guid}}}\n      propertyPath: m_Materials.Array.data[0]\n      objectReference: {{guid: {material}}}\n";
        VrchatAvatar Read(string path)
        {
            using var package = UnityPackage.Open(path);
            var avatar = new VrchatAvatar();
            typeof(VrchatAvatarParser).GetMethod("ParseRenderers", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { package, outerGuid, avatar });
            return avatar;
        }

        foreach (var (type, classId) in new[] { ("MeshRenderer", 23), ("SkinnedMeshRenderer", 137), ("MeshFilter", 33) })
        foreach (string alias in new[] { "direct", "omitted", "explicit" })
        {
            long removedId = Id(type, ownerPath);
            string wrapper = Instance(100, modelGuid);
            if (alias == "explicit") wrapper += $"--- !u!{classId} &900 stripped\n{type}:\n  m_CorrespondingSourceObject: {{guid: {modelGuid}, fileID: {removedId}}}\n  m_PrefabInstance: {{fileID: 100}}\n";
            asset("Assets/FbxRemovalWrapper.prefab", wrapperGuid, wrapper);
            string targetGuid = alias == "direct" ? modelGuid : wrapperGuid;
            long targetId = alias == "direct" ? removedId : alias == "omitted" ? removedId ^ 100 : 900;
            string rendererType = type == "MeshFilter" ? "MeshRenderer" : type;
            string outer = asset("Assets/FbxRemovalOuter.prefab", outerGuid,
                Instance(200, wrapperGuid, $"    m_RemovedComponents:\n    - {{guid: {targetGuid}, fileID: {targetId}}}\n" +
                    "    m_Modifications:\n" + Override(Id(rendererType, ownerPath), modelGuid, "removedMaterial") +
                    Override(Id(rendererType, "RootNode/Right/Shared/Body"), modelGuid, "survivingMaterial")) +
                Instance(201, wrapperGuid));
            var avatar = Read(outer);
            if (!avatar.RemovedModelRenderers.SetEquals(new[] { new VrchatModelRendererReference(modelGuid, ownerPath) }) ||
                avatar.RendererMaterials.Count != 1 || avatar.RendererMaterials[0].SourcePath != "RootNode/Right/Shared/Body")
                throw new Exception($"FBX {type} removal via {alias} must exclude exactly one occurrence/path and its overrides.");
            Console.WriteLine($"PASS: Direct FBX {type} removal via {alias} preserves other branches and occurrences");
        }

        foreach (string type in new[] { "Transform", "Mesh", "Animator" })
        {
            string outer = asset("Assets/FbxRemovalOuter.prefab", outerGuid,
                Instance(200, modelGuid, $"    m_RemovedComponents:\n    - {{guid: {modelGuid}, fileID: {Id(type, ownerPath)}}}\n"));
            if (Read(outer).RemovedModelRenderers.Count != 0)
                throw new Exception($"A removed {type} is not a renderer component removal.");
            Console.WriteLine($"PASS: FBX {type} identity is not treated as a removed renderer");
        }

        using var source = UnityPackage.Open(sourcePrefab);
        const string metaGuid = "ae120000000000000000000000000003";
        string fbx = asset("Assets/MetaRendererRemoval.fbx", metaGuid,
            File.ReadAllText(source.ByGuid(modelGuid).DiskPath).Replace("Model: 5, \"Model::Body\"", "Model: 5, \"Model::Unique\""));
        File.AppendAllText(fbx + ".meta", "\nModelImporter:\n  internalIDToNameTable:\n  - first:\n      23: 1234567\n    second: Unique\n  - first:\n      95: 9876543\n    second: Unique\n  fileIDToRecycleName:\n    3300000: Unique\n");
        foreach (long id in new[] { 1234567L, 3300000L, 9876543L })
        {
            string outer = asset("Assets/FbxRemovalOuter.prefab", outerGuid,
                Instance(200, metaGuid, $"    m_RemovedComponents:\n    - {{guid: {metaGuid}, fileID: {id}}}\n"));
            var avatar = Read(outer);
            bool renderer = id != 9876543;
            if (renderer ? !avatar.RemovedModelRenderers.SetEquals(new[] { new VrchatModelRendererReference(metaGuid, "RootNode/Left/Shared/Unique") }) :
                avatar.RemovedModelRenderers.Count != 0)
                throw new Exception("FBX metadata must retain component class and unique owner path.");
            Console.WriteLine($"PASS: FBX metadata component {id} respects its type and unique owner path");
        }
    }
}
