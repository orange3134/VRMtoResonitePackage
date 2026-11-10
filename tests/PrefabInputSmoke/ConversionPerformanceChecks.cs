using Elements.Assets;
using Elements.Core;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;

internal static class ConversionPerformanceChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        string prefab = asset("Assets/CacheScope.prefab", "cafe0000000000000000000000000001", "%YAML 1.1\n");
        string model = asset("Assets/CacheScope.obj", "cafe0000000000000000000000000002", "# meta-only model\n");
        string Meta(string name) => "ModelImporter:\n  fileIDToRecycleName:\n    400001: " + name + "\n";
        File.WriteAllText(model + ".meta", "guid: cafe0000000000000000000000000002\n" + Meta("Original"));
        using var package = UnityPackage.Open(prefab);
        var source = package.ByGuid("cafe0000000000000000000000000002");
        var first = package.ModelFileIds(source.Guid);
        using (var view = package.CreateView(package.InputPrefab.Guid))
        {
            view.SetViewAsset(new UnityAsset { Guid = "occurrence", SourceGuid = source.Guid,
                LogicalPath = source.LogicalPath, DiskPath = source.DiskPath, MetaPath = source.MetaPath });
            var repeated = view.ModelFileIds("occurrence");
            Require(ReferenceEquals(first, repeated) && repeated.ResolveName(400001) == "Original",
                "repeated occurrences share source analysis across views");
            string otherMeta = model + ".other.meta";
            File.WriteAllText(otherMeta, Meta("Other"));
            view.SetViewAsset(new UnityAsset { Guid = "different-meta", SourceGuid = source.Guid,
                LogicalPath = source.LogicalPath, DiskPath = source.DiskPath, MetaPath = otherMeta });
            Require(view.ModelFileIds("different-meta").ResolveName(400001) == "Other" &&
                first.ResolveName(400001) == "Original", "distinct importer metadata cannot alias a cached model");
            Require(view.ModelFileIds("missing").ResolveName(400001) == null, "missing source remains unresolved");
            view.SetViewAsset(new UnityAsset { Guid = "missing", LogicalPath = source.LogicalPath,
                DiskPath = source.DiskPath, MetaPath = source.MetaPath });
            Require(ReferenceEquals(first, view.ModelFileIds("missing")), "missing lookup does not poison a later occurrence");
        }
        Require(ReferenceEquals(first, package.ModelFileIds(source.Guid)), "disposing a view preserves its owner's cache");
        File.WriteAllText(model + ".meta", "guid: cafe0000000000000000000000000002\n" + Meta("Updated"));
        using var reopened = UnityPackage.Open(prefab);
        Require(reopened.ModelFileIds(source.Guid).ResolveName(400001) == "Updated",
            "a new package observes source changes instead of a process-wide cache");

        var mesh = new MeshX { HasNormals = true, HasTangents = true, HasColors = true,
            HasUV0s = true, HasBoneBindings = true };
        mesh.AddBone("Bone").BindPose = float4x4.Identity;
        var triangles = mesh.AddSubmesh<TriangleSubmesh>();
        for (int i = 0; i < 1026; i++)
        {
            mesh.AddVertex(new float3(i / 7f, i % 7, 0));
            mesh.RawNormals[i] = new float3(0, 0, 1);
            mesh.RawTangents[i] = new float4(1, 0, 0, -1);
            mesh.RawColors[i] = new color(0.3f, 0.6f, 0.9f, 0.5f);
            mesh.RawUV0s[i] = new float2(i % 3, 0.25f);
            mesh.RawBoneBindings[i] = new BoneBinding { boneIndex0 = 0, weight0 = 1 };
            if (i % 3 == 2) triangles.AddTriangle(i - 2, i - 1, i);
        }
        mesh.AddSubmesh<TriangleSubmesh>();
        for (int shapeIndex = 0; shapeIndex < 12; shapeIndex++)
        {
            var shape = mesh.AddBlendShape("Shape " + shapeIndex);
            shape.HasNormals = shape.HasTangents = true;
            foreach (float weight in new[] { 0.5f, 1f })
            {
                var frame = shape.AddFrame(weight);
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    frame.RawPositions[i] = new float3(weight * shapeIndex, i % 5, 0);
                    frame.RawNormals[i] = new float3(0, 0.1f, 0);
                    frame.RawTangents[i] = new float3(0.1f, 0, 0);
                }
            }
        }
        string output = Path.Combine(Path.GetDirectoryName(prefab)!, "fast.meshx");
        var expected = mesh.SaveToArray(MeshX.Encoding.Plain);
        MeshAssetWriter.SaveToFile(mesh, output);
        Require(new MeshX(output).SaveToArray(MeshX.Encoding.Plain).SequenceEqual(expected),
            "fast stream roundtrip preserves all geometry, skin, attributes, submeshes and multi-frame morphs");
        Require(new FileInfo(output).Length < expected.Length, "fast mesh saves remain compressed");
        MeshAssetWriter.SaveToFile(new MeshX(), output);
        Require(new MeshX(output).VertexCount == 0, "empty mesh overwrites and decodes correctly");
        Console.WriteLine("PASS: Source model cache isolation and compressed mesh roundtrips");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
