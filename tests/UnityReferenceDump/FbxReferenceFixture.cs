using Assimp;
using System.Runtime.CompilerServices;

// Self-authored source data, exported by the already-referenced Assimp library.
internal static class FbxReferenceFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void WriteSkinRoots(string path)
    {
        if (File.Exists(path)) throw new IOException("Fixture output already exists.");
        var scene = new Scene { RootNode = new Node("RootNode") };
        scene.Materials.Add(new Material { Name = "FixtureMaterial" });
        var skeleton = new Node("Skeleton", scene.RootNode);
        scene.RootNode.Children.Add(skeleton);
        foreach (string name in new[] { "A", "B" }) skeleton.Children.Add(new Node(name, skeleton));
        foreach (var (name, bones) in new[] { ("Forward", new[] { "A", "B" }),
                     ("Reverse", new[] { "B", "A" }), ("WithAncestor", new[] { "A", "Skeleton", "B" }) })
        {
            var mesh = new Mesh(name, PrimitiveType.Triangle) { MaterialIndex = 0 };
            mesh.Vertices.AddRange(new[] { new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0) });
            mesh.Normals.AddRange(Enumerable.Repeat(new Vector3D(0, 0, 1), 3));
            mesh.Faces.Add(new Face(new[] { 0, 1, 2 }));
            foreach (string boneName in bones)
            {
                var bone = new Bone { Name = boneName, OffsetMatrix = Matrix4x4.Identity };
                for (int i = 0; i < 3; i++) bone.VertexWeights.Add(new VertexWeight(i, 1f / bones.Length));
                mesh.Bones.Add(bone);
            }
            var node = new Node(name, scene.RootNode);
            scene.RootNode.Children.Add(node); node.MeshIndices.Add(scene.MeshCount); scene.Meshes.Add(mesh);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var context = new AssimpContext();
        if (!context.ExportFile(scene, path, "fbxa", PostProcessSteps.None))
            throw new InvalidDataException("Skin root fixture export failed.");
        // The exporter sorts skin clusters by scene traversal. Reverse only the second
        // skin's connection order so Unity, not the generator's assumptions, tests it.
        var regex = new System.Text.RegularExpressions.Regex(@"Deformer: (\d+), [^\r\n]+, ""Skin""");
        string text = File.ReadAllText(path);
        string skin = regex.Matches(text)[1].Groups[1].Value;
        var links = new System.Text.RegularExpressions.Regex(@"C: ""OO"", \d+, " + skin + @"(?=\r?\n)");
        var reversed = new Queue<string>(links.Matches(text).Select(m => m.Value).Reverse());
        File.WriteAllText(path, links.Replace(text, _ => reversed.Dequeue()));
        Console.WriteLine("Wrote self-authored skin root fixture: " + path);
    }

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
