using Assimp;
using System.Runtime.CompilerServices;

// Self-authored source data, exported by the already-referenced Assimp library.
internal static class FbxReferenceFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Write(string path)
    {
        if (File.Exists(path)) throw new IOException("Fixture output already exists.");
        var scene = new Scene { RootNode = new Node("RootNode") };
        scene.Materials.Add(new Material { Name = "FixtureMaterial" });
        var armature = new Node("armature", scene.RootNode);
        scene.RootNode.Children.Add(armature);
        armature.Transform = Matrix4x4.FromRotationX(MathF.PI / 6) * Matrix4x4.FromTranslation(new Vector3D(0, 1, 2));
        foreach (var (name, parent) in new[] { ("Body", armature), ("Hairpin", scene.RootNode),
                     ("Hair_Front", scene.RootNode), ("髪", scene.RootNode) })
        {
            var mesh = new Mesh(name, PrimitiveType.Triangle) { MaterialIndex = 0 };
            mesh.Vertices.AddRange(new[] { new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0) });
            mesh.Normals.AddRange(Enumerable.Repeat(new Vector3D(0, 0, 1), 3));
            mesh.Faces.Add(new Face(new[] { 0, 1, 2 }));
            var node = new Node(name, parent);
            parent.Children.Add(node);
            node.MeshIndices.Add(scene.MeshCount);
            scene.Meshes.Add(mesh);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using var context = new AssimpContext();
            if (!context.ExportFile(scene, temporary, "fbxa", PostProcessSteps.None)) throw new InvalidDataException("FBX fixture export failed.");
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine("Wrote self-authored FBX fixture: " + path);
    }
}
