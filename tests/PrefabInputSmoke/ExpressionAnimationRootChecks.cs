using VrmToResonitePackage.Unity;

internal static class ExpressionAnimationRootChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        const string guid = "ade00000000000000000000000000001";
        string file = asset("Assets/AnimationRoot.fbx", guid, """
; FBX 7.4.0 project file
FBXHeaderExtension: {
    FBXHeaderVersion: 1003
    FBXVersion: 7400
}
Objects: {
    Model: 1, "Model::Payload", "Null" { }
    Model: 2, "Model::Face", "Mesh" { }
    Geometry: 3, "Geometry::Triangle", "Mesh" {
        Vertices: *9 { a: 0,0,0,1,0,0,0,1,0 }
        PolygonVertexIndex: *3 { a: 0,1,-3 }
    }
}
Connections: {
    C: "OO",1,0
    C: "OO",2,1
    C: "OO",3,2
}
""");
        UnityModelFileIdResolver Read() => new(new UnityAsset { Guid = guid, LogicalPath = "Assets/AnimationRoot.fbx", DiskPath = file });
        long root = UnityModelFileIdResolver.Compute("GameObject", "//RootNode/root", 0);
        long face = UnityModelFileIdResolver.Compute("SkinnedMeshRenderer", "//RootNode/root/Face/SkinnedMeshRenderer", 0);
        var resolver = Read();
        Check(resolver.ResolveAnimationRootPath(root, face, "Face") == "RootNode/Payload", "folded model root recovered from real component ID");
        Check(resolver.ResolveAnimationRootPath(root, face, "Missing/Face") == null, "a matching leaf cannot bypass a missing authored parent");
        Check(resolver.ResolveAnimationRootPath(root, face, "../Face") == null, "parent traversal is rejected");
        Check(resolver.ResolveAnimationRootPath(root, face, "/Face") == null, "absolute binding is rejected");
        Check(resolver.ResolveAnimationRootPath(root, UnityModelFileIdResolver.Compute("GameObject", "//RootNode/root/Face", 0), "Face") == null, "GameObject reference cannot masquerade as a renderer");
        Check(resolver.ResolveAnimationRootPath(123, face, "Face") == null, "unrelated root identity cannot authorize correction");
        string source = File.ReadAllText(file);
        File.WriteAllText(file, source.Replace("C: \"OO\",2,1", "C: \"OO\",2,0"));
        Check(Read().ResolveAnimationRootPath(root, face, "Face") == "RootNode", "unfolded hierarchy retains its existing basis");
        File.WriteAllText(file, source.Replace("    Model: 1,", "    Model: 4, \"Model::OtherPayload\", \"Null\" { }\n    Model: 5, \"Model::Face\", \"Mesh\" { }\n    Model: 1,")
            .Replace("    C: \"OO\",1,0", "    C: \"OO\",4,0\n    C: \"OO\",5,4\n    C: \"OO\",3,5\n    C: \"OO\",1,0"));
        Check(Read().ResolveAnimationRootPath(root, face, "Face") == null, "duplicate folded face paths remain unresolved");
        Console.WriteLine("Expression animation root identity checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Animation root: " + message);
    }
}
