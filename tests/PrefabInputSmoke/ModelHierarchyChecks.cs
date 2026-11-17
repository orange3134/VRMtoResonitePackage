using Assimp;
using VrmToResonitePackage.Unity;

internal static class ModelHierarchyChecks
{
    public static void Run()
    {
        Check(UnityModelFileIdResolver.Compute("Mesh", "髪", 0) == -4629627581608801798L &&
              UnityModelFileIdResolver.Compute("Mesh", "頭アクセサリ", 0) == -3902853503596086816L &&
              UnityModelFileIdResolver.Compute("Mesh", "服", 0) == -2425155630869339911L,
            "UTF-8 stable IDs use an unsigned four-byte hash tail (Unity reference values)");
        var scene = new Scene { RootNode = new Node("RootNode") };
        scene.Materials.Add(new Material { Name = "SourceMaterial" });
        Node Add(string name, Node parent, Matrix4x4 transform)
        {
            var child = new Node(name, parent) { Transform = transform };
            parent.Children.Add(child);
            return child;
        }
        var translation = Add("Bone_$AssimpFbx$_Translation", scene.RootNode, Matrix4x4.FromTranslation(new Vector3D(1, 2, 3)));
        var rotation = Add("Bone_$AssimpFbx$_PreRotation", translation, Matrix4x4.FromRotationX(MathF.PI / 2));
        var bone = Add("Bone", rotation, Matrix4x4.Identity);
        Add("Tip", bone, Matrix4x4.FromTranslation(new Vector3D(0, 4, 0)));
        var mesh = new Mesh("MorphOnly", PrimitiveType.Triangle) { MaterialIndex = 0 };
        mesh.MeshAnimationAttachments.Add(new MeshAnimationAttachment { Name = "Smile" });
        scene.Meshes.Add(mesh);
        var renderer = Add("MorphOnly", scene.RootNode, Matrix4x4.Identity);
        renderer.MeshIndices.Add(0);
        var nodes = UnityModelNode.Capture(scene);
        var captured = nodes.Single(n => n.Path == "RootNode/Bone");
        Check(nodes.Count == 4 && captured.ParentPath == "RootNode", "model metadata omits helper identities but preserves authored hierarchy");
        Check(System.Numerics.Vector3.Distance(captured.Position, new(1, 2, 3)) < 1e-6f,
            "pre-rotation does not rotate a preceding FBX translation");
        var direction = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, captured.Rotation);
        Check(System.Numerics.Vector3.Distance(direction, System.Numerics.Vector3.UnitZ) < 1e-6f,
            "helper rotation is included in the authored local pose");
        Check(nodes.Single(n => n.Name == "Tip").Position == new System.Numerics.Vector3(0, 4, 0),
            "a descendant local pose does not inherit the folded parent pose twice");
        var morph = nodes.Single(n => n.Name == "MorphOnly");
        Check(morph.Skinned && morph.HasMesh && morph.MaterialNames.SequenceEqual(new[] { "SourceMaterial" }),
            "morph-only meshes retain renderer classification and material slot order");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
