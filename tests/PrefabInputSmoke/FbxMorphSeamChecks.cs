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
        foreach (int version in new[] { 7400, 7500 })
        foreach (bool existingColor in new[] { false, true })
        {
            string source = Path.Combine(directory, $"seam-{version}-{existingColor}.fbx");
            Write(source, version, existingColor, seam: true);
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
            string noSeam = Path.Combine(directory, $"no-seam-{version}-{existingColor}.fbx");
            Write(noSeam, version, existingColor, seam: false);
            Require(UnityFbxMorphSeams.Prepare(noSeam) == noSeam, "matching morph deltas need no protection");
        }
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
    private static void Write(string path, int version, bool color, bool seam)
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
        var connections = new Node("Connections", [], new[] { (20L, 0L), (10L, 20L), (30L, 10L), (40L, 30L), (50L, 40L) }
            .Select(p => new Node("C", new object[] { "OO", p.Item1, p.Item2 })).ToArray());
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
