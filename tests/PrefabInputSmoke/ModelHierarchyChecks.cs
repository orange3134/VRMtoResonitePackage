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
        Check(System.Numerics.Vector3.Distance(captured.Position, new(-1, 2, 3)) < 1e-6f,
            "pre-rotation does not rotate a preceding FBX translation");
        var direction = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, captured.Rotation);
        Check(System.Numerics.Vector3.Distance(direction, System.Numerics.Vector3.UnitZ) < 1e-6f,
            "helper rotation is included in the authored local pose");
        Check(nodes.Single(n => n.Name == "Tip").Position == new System.Numerics.Vector3(0, 4, 0),
            "a descendant local pose does not inherit the folded parent pose twice");
        var morph = nodes.Single(n => n.Name == "MorphOnly");
        Check(morph.Skinned && morph.HasMesh && morph.MaterialNames.SequenceEqual(new[] { "SourceMaterial" }),
            "morph-only meshes retain renderer classification and material slot order");
        Check(!UnityModelNode.Capture(scene, false).Single(n => n.Name == "MorphOnly").Skinned,
            "ignored morphs alone do not create a skinned renderer");
        mesh.Bones.Add(new Bone { Name = "Bone" });
        Check(UnityModelNode.Capture(scene, false).Single(n => n.Name == "MorphOnly").Skinned,
            "disabled morph import preserves a renderer with bones");
        Add("Nonuniform", scene.RootNode, Matrix4x4.FromScaling(new Vector3D(.9f, .8f, 1)) * Matrix4x4.FromRotationX(-MathF.PI / 2));
        var scaled = UnityModelNode.Capture(scene).Single(n => n.Name == "Nonuniform");
        Check(System.Numerics.Vector3.Distance(scaled.Scale, new(.9f, .8f, 1)) < 1e-6f,
            "rotated nonuniform scale retains its local axes");
        var nearHalfTurn = Add("HalfTurn", scene.RootNode, Matrix4x4.FromRotationY(3.0f));
        var half = UnityModelNode.Capture(scene).Single(n => n.Name == "HalfTurn");
        Check(half.Rotation.Y > 0 && half.Rotation.W < 0,
            "handedness conversion precedes quaternion decomposition for partial overrides");
        scene.Metadata.Add("UpAxis", new Metadata.Entry(MetaDataType.Int32, 2));
        scene.Metadata.Add("FrontAxis", new Metadata.Entry(MetaDataType.Int32, 1));
        scene.Metadata.Add("CoordAxisSign", new Metadata.Entry(MetaDataType.Int32, -1));
        Add("ZUp", scene.RootNode, Matrix4x4.FromRotationZ(MathF.PI));
        var zUp = UnityModelNode.Capture(scene).Single(n => n.Name == "ZUp");
        var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, zUp.Rotation);
        Check(System.Numerics.Vector3.Distance(up, -System.Numerics.Vector3.UnitZ) < 1e-6f,
            "Z-up FBX axis conversion applies once at model roots");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
