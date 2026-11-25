using System.IO.Compression;
using System.Text;
using Assimp;
using VrmToResonitePackage.Unity;

internal static class FbxMorphSeamChecks
{
    public static void Run()
    {
        string directory = Path.GetFullPath(Path.Combine(".tmp_verify", "morph-seams", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string model = Path.Combine(directory, "import-settings.fbx");
        Write(model, 7400, false, true, false);
        var asset = new UnityAsset { DiskPath = model, LogicalPath = "Assets/import-settings.fbx", MetaPath = model + ".meta" };
        CheckEmbeddedShapes(directory, model);
        CheckChannelNames(directory);
        foreach (bool enabled in new[] { true, false })
        {
            File.WriteAllText(asset.MetaPath, $"ModelImporter:\n  meshes:\n    importBlendShapes: {(enabled ? 1 : 0)}\n");
            var resolver = new UnityModelFileIdResolver(asset);
            Require(resolver.Hierarchy.Single(node => node.HasMesh).Skinned == enabled &&
                resolver.BlendShapeNamesByPath.Any() == enabled && resolver.BlendShapeNames.Any() == enabled &&
                resolver.PossibleBlendShapeNames.Any() == enabled,
                "ModelImporter controls morph-only renderers, their channels and animation target candidates");
        }
        foreach (int version in new[] { 7400, 7500 })
        {
            string binary = Path.Combine(directory, $"disable-{version}.fbx");
            Write(binary, version, false, true, false);
            CheckDisabled(binary);
        }
        string ascii = Path.Combine(directory, "disable-ascii.fbx");
        File.WriteAllText(ascii, """
            ; FBX 7.4.0 project file
            FBXHeaderExtension: {
                FBXHeaderVersion: 1003
                FBXVersion: 7400
            }
            Objects: {
                Model: 20, "Model::Body", "Mesh" {}
                Geometry: 10, "Geometry::Body", "Mesh" {
                    Vertices: *9 { a: 0,0,0,1,0,0,0,1,0 }
                    PolygonVertexIndex: *3 { a: 0,1,-3 }
                }
                Deformer: 30, "Deformer::Face", "BlendShape" {}
                Deformer: 40, "Deformer::Face", "BlendShapeChannel" {
                    DeformPercent: 0
                    FullWeights: *1 { a: 100 }
                }
                Geometry: 50, "Geometry::Face", "Shape" {
                    Indexes: *1 { a: 0 }
                    Vertices: *3 { a: 0,0,1 }
                    Normals: *3 { a: 0,0,0 }
                }
            }
            Connections: {
                C: "OO",20,0
                C: "OO",10,20
                C: "OO",030,10
                C: "OO",40,30
                C: "OO",50,40
            }
            """);
        CheckDisabled(ascii);
        foreach (int version in new[] { 7400, 7500 })
        foreach (bool existingColor in new[] { false, true })
        foreach (bool duplicateTextures in new[] { false, true })
        {
            string source = Path.Combine(directory, $"seam-{version}-{existingColor}-{duplicateTextures}.fbx");
            Write(source, version, existingColor, seam: true, duplicateTextures);
            byte[] originalBytes = File.ReadAllBytes(source);
            using var context = new AssimpContext();
            var original = context.ImportFile(source, PostProcessSteps.None).Meshes.Single();
            var broken = context.ImportFile(source, PostProcessSteps.JoinIdenticalVertices).Meshes.Single();
            Require(Errors(original, broken) > 0, "fixture reproduces morph damage during vertex welding");
            string prepared = UnityFbxMorphSeams.Prepare(source);
            Require(prepared != source && originalBytes.SequenceEqual(File.ReadAllBytes(source)), "source FBX stays unchanged");
            var fixedMesh = context.ImportFile(prepared, PostProcessSteps.JoinIdenticalVertices).Meshes.Single();
            Require(Errors(original, fixedMesh) == 0, $"FBX {version}, existing colors={existingColor}: morph corners preserved");
            if (existingColor)
                Require(fixedMesh.VertexColorChannels[0].All(c => c == new Color4D(.25f, .5f, .75f, 1)), "authored colors preserved");
            if (duplicateTextures)
            {
                byte[] preparedBytes = File.ReadAllBytes(prepared);
                foreach (string marker in new[] { "TextureDuplicateA", "TextureDuplicateB" })
                    Require(preparedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0,
                        "unrelated duplicate texture records are preserved");
            }
            string noSeam = Path.Combine(directory, $"no-seam-{version}-{existingColor}-{duplicateTextures}.fbx");
            Write(noSeam, version, existingColor, seam: false, duplicateTextures);
            Require(UnityFbxMorphSeams.Prepare(noSeam) == noSeam, "matching morph deltas need no protection");
        }
    }

    private static void CheckEmbeddedShapes(string directory, string modern)
    {
        string legacy = Path.Combine(directory, "embedded.fbx");
        Write(legacy, 7100, false, true, false, embedded: true);
        byte[] original = File.ReadAllBytes(legacy);
        using var context = new AssimpContext();
        var expected = context.ImportFile(modern, PostProcessSteps.None).Meshes.Single();
        var restored = context.ImportFile(UnityFbxMorphSeams.Prepare(legacy), PostProcessSteps.JoinIdenticalVertices).Meshes.Single();
        Require(Errors(expected, restored) == 0, "embedded shapes preserve each corner's authored delta after welding");
        var resolver = new UnityModelFileIdResolver(new UnityAsset { DiskPath = legacy, LogicalPath = "Assets/embedded.fbx" });
        Require(resolver.BlendShapeNames["Body"].Single() == "Sweat" &&
            resolver.BlendShapeDefaultWeightsByPath["RootNode/Body"].Single() == 25 &&
            resolver.Hierarchy.Single(n => n.HasMesh).Skinned, "analysis sees the embedded shape, its default and morph-only renderer");
        var disabled = context.ImportFile(UnityFbxMorphSeams.Prepare(legacy, false), PostProcessSteps.None).Meshes.Single();
        Require(disabled.MeshAnimationAttachmentCount == 0 && original.SequenceEqual(File.ReadAllBytes(legacy)),
            "disabling embedded shapes leaves the source FBX intact");
    }

    private static void CheckChannelNames(string directory)
    {
        string path = Path.Combine(directory, "colliding-geometry-names.fbx");
        Write(path, 7400, false, false, false, collidingNames: true);
        var bytes = File.ReadAllBytes(path);
        var resolver = new UnityModelFileIdResolver(new UnityAsset { DiskPath = path, LogicalPath = "Assets/Face.fbx" });
        var names = resolver.ShapeGeometryNames;
        Require(names.Count == 2 && names[50] == "mouth.Smile" && names[51] == "eye.Smile",
            "Same-named geometries resolve by FBX object connections in reverse declaration order");
        string import = UnityFbxMorphSeams.Prepare(path, shapeGeometryNames: names);
        var source = UnityFbxBlendShapeDefaults.Read(path);
        var prepared = UnityFbxBlendShapeDefaults.Read(import);
        Require(prepared.All(c => c.ShapeName == c.Name) && prepared.Count == 2 &&
            resolver.BlendShapeNames["Body"].ToHashSet().SetEquals(names.Values) &&
            bytes.SequenceEqual(File.ReadAllBytes(path)), "Parser and temporary FBX agree on both channel names");
        using var context = new AssimpContext();
        var before = context.ImportFile(path, PostProcessSteps.None).Meshes.Single().MeshAnimationAttachments;
        var after = context.ImportFile(import, PostProcessSteps.None).Meshes.Single().MeshAnimationAttachments;
        Require(before.Count == 2 && after.Count == 2 && !before[0].Vertices.SequenceEqual(before[1].Vertices) &&
            before.Zip(after).All(pair => pair.First.Vertices.SequenceEqual(pair.Second.Vertices)),
            "Distinct morph deltas retain their indices when geometry names change");
        Require(UnityFbxBlendShapeDefaults.ChannelNamesForImport(source.Select(c => c with { Name = "Smile" })).Count == 0 &&
            UnityFbxBlendShapeDefaults.ChannelNamesForImport(source.Select(c => c with { ExclusiveShapeId = null })).Count == 0,
            "Ambiguous channels and shared geometry stay on the existing import path");
    }

    private static void CheckDisabled(string source)
    {
        byte[] original = File.ReadAllBytes(source);
        using var context = new AssimpContext();
        var before = context.ImportFile(source, PostProcessSteps.None).Meshes.Single();
        string filtered = UnityFbxMorphSeams.Prepare(source, importBlendShapes: false);
        var after = context.ImportFile(filtered, PostProcessSteps.None).Meshes.Single();
        Require(before.MeshAnimationAttachmentCount == 1 && after.MeshAnimationAttachmentCount == 0 &&
            before.Vertices.SequenceEqual(after.Vertices) && before.FaceCount == after.FaceCount &&
            original.SequenceEqual(File.ReadAllBytes(source)), "Only the temporary FBX loses its morph channels: " + source);
    }

    private static int Errors(Mesh original, Mesh imported)
    {
        Require(original.FaceCount == imported.FaceCount && original.MeshAnimationAttachmentCount == 1 &&
            imported.MeshAnimationAttachmentCount == 1, $"mesh and shape structure retained: faces {original.FaceCount}/{imported.FaceCount}, shapes {original.MeshAnimationAttachmentCount}/{imported.MeshAnimationAttachmentCount}");
        int errors = 0;
        for (int f = 0; f < original.FaceCount; f++)
        for (int c = 0; c < original.Faces[f].IndexCount; c++)
        {
            int a = original.Faces[f].Indices[c], b = imported.Faces[f].Indices[c];
            Require(original.Vertices[a] == imported.Vertices[b], "base corner unchanged");
            if ((original.MeshAnimationAttachments[0].Vertices[a] - imported.MeshAnimationAttachments[0].Vertices[b]).Length() > 1e-6) errors++;
        }
        return errors;
    }

    private sealed record Node(string Name, object[] Values, params Node[] Children);
    private static Node N(string name, object value) => new(name, new[] { value });
    private static void Write(string path, int version, bool color, bool seam, bool duplicateTextures, bool embedded = false,
        bool collidingNames = false)
    {
        // Two overlapping triangles have identical base attributes. Only control point 0
        // moves; point 3 must stay still, despite occupying the same base position.
        var geometry = new List<Node>
        {
            N("Vertices", new double[] { 0,0,0, 1,0,0, 0,1,0, 0,0,0, 1,0,0, 0,1,0 }),
            N("PolygonVertexIndex", new int[] { 0,1,-3, 3,4,-6 }),
            new("LayerElementNormal", new object[] { 0 }, N("Version", 101), N("Name", ""),
                N("MappingInformationType", "ByVertice"), N("ReferenceInformationType", "Direct"),
                N("Normals", Enumerable.Range(0, 6).SelectMany(_ => new double[] { 0, 0, 1 }).ToArray())),
            new("Layer", new object[] { 0 }, N("Version", 100),
                new Node("LayerElement", [], N("Type", "LayerElementNormal"), N("TypedIndex", 0))),
        };
        if (color)
        {
            geometry.Add(new("LayerElementColor", new object[] { 0 }, N("Version", 101), N("Name", "Authored"),
                N("MappingInformationType", "ByVertice"), N("ReferenceInformationType", "Direct"),
                N("Colors", Enumerable.Range(0, 6).SelectMany(_ => new double[] { .25, .5, .75, 1 }).ToArray())));
            geometry.Add(new("Layer", new object[] { 1 }, N("Version", 100),
                new Node("LayerElement", [], N("Type", "LayerElementColor"), N("TypedIndex", 0))));
        }
        var objects = new Node("Objects", [],
            new("Geometry", new object[] { 10L, "Body\0\u0001Geometry", "Mesh" }, geometry.ToArray()),
            new("Model", new object[] { 20L, "Body\0\u0001Model", "Mesh" }, N("Version", 232)),
            new("Deformer", new object[] { 30L, "Face\0\u0001Deformer", "BlendShape" }, N("Version", 100)),
            new("Deformer", new object[] { 40L, "Sweat\0\u0001Deformer", "BlendShapeChannel" },
                N("Version", 100), N("DeformPercent", 0d), N("FullWeights", new double[] { 100 })),
            new("Geometry", new object[] { 50L, "Sweat\0\u0001Geometry", "Shape" }, N("Version", 100),
                N("Indexes", seam ? new[] { 0 } : new[] { 0, 3 }),
                N("Vertices", seam ? new double[] { 0, 0, .2 } : new double[] { 0, 0, .2, 0, 0, .2 }),
                N("Normals", seam ? new double[3] : new double[6])));
        // Nagma contains repeated Texture IDs. They are unrelated to the morph graph
        // and must neither prevent seam protection nor be removed from the saved FBX.
        if (duplicateTextures)
            objects = objects with { Children = objects.Children.Concat(new Node[]
            {
                new("Texture", new object[] { 60L, "TextureDuplicateA\0\u0001Texture", "" }),
                new("Texture", new object[] { 60L, "TextureDuplicateB\0\u0001Texture", "" }),
            }).ToArray() };
        var connections = new Node("Connections", [], new[] { (20L, 0L), (10L, 20L), (30L, 10L), (40L, 30L), (50L, 40L) }
            .Select(p => new Node("C", new object[] { "OO", p.Item1, p.Item2 })).ToArray());
        if (collidingNames)
        {
            var channel = objects.Children[3];
            var shape = objects.Children[4];
            objects.Children[3] = channel with { Values = [40L, "mouth.Smile\0\u0001Deformer", "BlendShapeChannel"] };
            objects = objects with { Children = [
                shape with { Values = [51L, shape.Values[1], "Shape"], Children = [
                    N("Indexes", new[] { 1 }), N("Vertices", new double[] { 0, .3, 0 }), N("Normals", new double[3])] },
                ..objects.Children, channel with { Values = [41L, "eye.Smile\0\u0001Deformer", "BlendShapeChannel"] }] };
            connections = connections with { Children = [..connections.Children,
                new("C", ["OO", 41L, 30L]), new("C", ["OO", 51L, 41L])] };
        }
        if (embedded)
        {
            geometry.Add(new Node("Shape", ["Sweat"], objects.Children[4].Children.Skip(1).ToArray()));
            geometry.Add(new Node("Properties70", [], new Node("P", ["Sweat", "Number", "", "A", 25d])));
            objects = new Node("Objects", [], objects.Children[0] with { Children = geometry.ToArray() }, objects.Children[1]);
            connections = connections with { Children = connections.Children.Take(2).ToArray() };
        }
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        bool wide = version >= 7500;
        int header = wide ? 25 : 13;
        writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\u001a\0")); writer.Write(version);
        WriteNode(new("FBXHeaderExtension", [], N("FBXHeaderVersion", 1003), N("FBXVersion", version)));
        WriteNode(objects); WriteNode(connections); writer.Write(new byte[header]);
        void Number(long value) { if (wide) writer.Write((ulong)value); else writer.Write((uint)value); }
        void WriteNode(Node node)
        {
            long start = stream.Position;
            writer.Write(new byte[header]);
            byte[] name = Encoding.UTF8.GetBytes(node.Name); writer.Write(name);
            long props = stream.Position;
            foreach (object value in node.Values)
            {
                switch (value)
                {
                    case int i: writer.Write((byte)'I'); writer.Write(i); break;
                    case long l: writer.Write((byte)'L'); writer.Write(l); break;
                    case double d: writer.Write((byte)'D'); writer.Write(d); break;
                    case string s: var text = Encoding.UTF8.GetBytes(s); writer.Write((byte)'S'); writer.Write(text.Length); writer.Write(text); break;
                    case Array a:
                        using (var raw = new MemoryStream())
                        {
                            using (var data = new BinaryWriter(raw, Encoding.UTF8, true))
                                foreach (object item in a) { if (item is int n) data.Write(n); else data.Write((double)item); }
                            byte[] bytes = raw.ToArray();
                            if (wide)
                            {
                                using var compressed = new MemoryStream();
                                using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(bytes);
                                bytes = compressed.ToArray();
                            }
                            writer.Write((byte)(a is int[] ? 'i' : 'd')); writer.Write(a.Length); writer.Write(wide ? 1 : 0);
                            writer.Write(bytes.Length); writer.Write(bytes);
                        }
                        break;
                }
            }
            long length = stream.Position - props;
            foreach (var child in node.Children) WriteNode(child);
            if (node.Children.Length > 0) writer.Write(new byte[header]);
            long end = stream.Position; stream.Position = start;
            Number(end); Number(node.Values.Length); Number(length); writer.Write((byte)name.Length); stream.Position = end;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
