using System.Text.Json;
using VrmToResonitePackage.Unity;

// Self-authored FBX/YAML: two distinct renderer paths share one mesh subasset.
internal static class SharedMeshChecks
{
    public static void Run(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use a new fixture directory.");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        Directory.CreateDirectory(Path.Combine(directory, "ProjectSettings"));
        const string modelGuid = "33333333333333333333333333333333";
        const string prefabGuid = "44444444444444444444444444444444";
        string model = Path.Combine(directory, "Assets", "Shared.fbx");
        string prefab = Path.Combine(directory, "Assets", "Selected.prefab");
        File.WriteAllText(model, """
; FBX 7.4.0 project file
FBXHeaderExtension: {
    FBXHeaderVersion: 1003
    FBXVersion: 7400
}
Objects: {
    Model: 1, "Model::Left", "Null" { }
    Model: 2, "Model::Right", "Null" { }
    Model: 3, "Model::Shared", "Mesh" { }
    Model: 4, "Model::Shared", "Mesh" { }
    Geometry: 10, "Geometry::Shared", "Mesh" {
        Vertices: *9 { a: 0,0,0,1,0,0,0,1,0 }
        PolygonVertexIndex: *3 { a: 0,1,-3 }
    }
    Geometry: 11, "Geometry::Smile", "Shape" {
        Version: 100
        Indexes: *1 { a: 0 }
        Vertices: *3 { a: 0,0,1 }
        Normals: *3 { a: 0,0,0 }
    }
    Deformer: 12, "Deformer::Smile", "BlendShape" { Version: 100 }
    Deformer: 13, "Deformer::Smile", "BlendShapeChannel" {
        Version: 100
        DeformPercent: 0
        FullWeights: *1 { a: 100 }
    }
}
Connections: {
    C: "OO",1,0
    C: "OO",2,0
    C: "OO",3,1
    C: "OO",4,2
    C: "OO",10,3
    C: "OO",10,4
    C: "OO",12,10
    C: "OO",13,12
    C: "OO",11,13
}
""");
        File.WriteAllText(model + ".meta", $$"""
guid: {{modelGuid}}
ModelImporter:
  internalIDToNameTable:
  - first:
      43: 4300000
    second: Shared
  meshes:
    preserveHierarchy: 1
    fileIdsGeneration: 2
""");
        File.WriteAllText(prefab + ".meta", "guid: " + prefabGuid);
        long Renderer(string branch) => UnityModelFileIdResolver.Compute("SkinnedMeshRenderer",
            "//RootNode/root/" + branch + "/Shared/SkinnedMeshRenderer", 0);
        File.WriteAllText(prefab, $$"""
--- !u!1001 &100
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{modelGuid}}, type: 3}
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: {{Renderer("Left")}}, guid: {{modelGuid}}, type: 3}
      propertyPath: m_BlendShapeWeights.Array.data[0]
      value: 25
      objectReference: {fileID: 0}
    - target: {fileID: {{Renderer("Right")}}, guid: {{modelGuid}}, type: 3}
      propertyPath: m_BlendShapeWeights.Array.data[0]
      value: 75
      objectReference: {fileID: 0}
""");
        using var package = UnityPackage.Open(prefab);
        using var view = UnityPrefabInstances.CreateView(package, prefabGuid, null);
        var resolver = view.ModelFileIds(modelGuid);
        var modelSnapshot = new ModelSnapshot(view, view.ByGuid(modelGuid), resolver,
            new HashSet<long> { Renderer("Left"), Renderer("Right") });
        foreach (string branch in new[] { "Left", "Right" })
            Require(modelSnapshot.ResolveMeshPath(4300000, Renderer(branch)) == "RootNode/" + branch + "/Shared",
                "shared mesh lookup retains renderer context");
        Require(modelSnapshot.Canonical(4300000) == 4300000, "canonical shared mesh identity retained");
        Require(modelSnapshot.ResolveMeshPath(999, Renderer("Right")) == null,
            "replaced mesh must not reuse unrelated renderer metadata");
        Require(modelSnapshot.ResolveMeshPath(4300000, 0) != null,
            "identical shared metadata also resolves without a source renderer");
        var snapshot = JsonSerializer.SerializeToElement(ResolvedSnapshot.Capture(view, prefabGuid));
        var renderers = snapshot.GetProperty("sections").GetProperty("renderers").EnumerateArray().ToArray();
        Require(renderers.Length == 2, "both shared-mesh renderers must be captured");
        foreach (var (branch, weight) in new[] { ("Left", 25f), ("Right", 75f) })
        {
            var renderer = renderers.Single(r => r.GetProperty("path").GetString() == branch + "/Shared");
            Require(renderer.GetProperty("mesh").GetProperty("FileId").GetString() == "4300000", "shared mesh ID retained");
            Require(renderer.GetProperty("originalSource").GetProperty("FileId").GetString() == Renderer(branch).ToString(),
                "distinct renderer identity retained");
            var shapes = renderer.GetProperty("blendShapes").EnumerateArray().ToArray();
            Require(shapes.Length == 1 && shapes[0].GetProperty("name").GetString() == "Smile" &&
                shapes[0].GetProperty("weight").GetSingle() == weight, "renderer-specific shape weight retained");
        }
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), snapshot.ToString());
        Console.WriteLine("PASS: shared FBX mesh, distinct renderer identities and independent blendshape overrides");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
