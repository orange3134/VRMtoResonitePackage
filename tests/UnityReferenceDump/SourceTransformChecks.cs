using System.Numerics;
using System.Text;
using VrmToResonitePackage.Unity;

internal static class SourceTransformChecks
{
    private sealed record N(string Name, object[] Values, params N[] Children);

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (int version in new[] { 7400, 7500 })
        {
            string path = Path.Combine(directory, $"tiny-{version}.fbx");
            using (var file = new FileStream(path, FileMode.CreateNew))
            using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); writer.Write(version);
                Node(writer, new("Objects", [],
                    new N("Model", [1L, "Tiny\0\x01Model", "LimbNode"], new N("Properties70", [],
                        P("PreRotation", -.000162, .000002, 0),
                        P("Lcl Scaling", 1, .99999994, 1.0000152587890625),
                        P("Lcl Translation", 2, 3, 4))),
                    new N("Model", [2L, "Static\0\x01Model", "Mesh"], new N("Properties70", [],
                        P("RotationPivot", 0, 2, 3), P("ScalingPivot", 0, 2, 3))),
                    new N("Model", [3L, "Skinned\0\x01Model", "Mesh"], new N("Properties70", [],
                        P("RotationPivot", 0, 2, 3), P("ScalingPivot", 0, 2, 3))),
                    new N("Geometry", [10L, "Mesh\0\x01Geometry", "Mesh"]),
                    new N("Deformer", [11L, "Skin\0\x01Deformer", "Skin"]),
                    new N("Deformer", [12L, "Cluster\0\x01Deformer", "Cluster"])), version);
                Node(writer, new("Connections", [], new N("C", ["OO", 1L, 0L]),
                    new N("C", ["OO", 2L, 0L]), new N("C", ["OO", 3L, 0L]),
                    new N("C", ["OO", 10L, 3L]), new N("C", ["OO", 11L, 10L]),
                    new N("C", ["OO", 12L, 11L]), new N("C", ["OO", 1L, 12L])), version);
                writer.Write(new byte[version >= 7500 ? 25 : 13]);
            }
            UnityFbxBlendShapeDefaults.Read(path, out _, out var source);
            Require(source?.Count == 3, "Binary source transforms must be read for both FBX headers");
            Require(source["RootNode/Static"].Translation == new Vector3(0, 2, 3),
                "Static leaf mesh must retain its shared pivot as object origin");
            Require(source["RootNode/Skinned"].Translation == Vector3.Zero,
                "Skin cluster connections must prevent the static mesh pivot adjustment");
            var scene = new Assimp.Scene { RootNode = new Assimp.Node("RootNode") };
            var helper = new Assimp.Node("Tiny_$AssimpFbx$_PreRotation", scene.RootNode);
            scene.RootNode.Children.Add(helper);
            helper.Children.Add(new Assimp.Node("Tiny", helper));
            var before = UnityModelNode.Capture(scene).Single(n => n.Name == "Tiny");
            var after = UnityModelNode.Capture(scene, true, source).Single(n => n.Name == "Tiny");
            Require(before.Rotation.X == 0 && before.Scale.Z == 1, "Fixture reproduces omitted transforms");
            Require(Math.Abs(after.Rotation.X - (-.000162 * Math.PI / 360)) < 1e-10,
                "Small authored pre-rotation must survive instead of becoming identity");
            Require(Math.Abs(after.Scale.Z - 1.0000152587890625) < 2e-7 &&
                    Vector3.Distance(after.Position, new(-2, 3, 4)) < 1e-6,
                "Authored scale and mirrored translation must survive helper collapse");
        }
        for (int order = 0; order < 6; order++)
        {
            IReadOnlyList<object>[] props = [new object[] { "RotationOrder", "enum", "", "", order },
                P("Lcl Rotation", 20, 30, 40).Values];
            Require(UnityFbxLocalTransform.TryRead(props, out var matrix), "Euler order supported");
            int[][] indices = [[0,1,2], [0,2,1], [1,2,0], [1,0,2], [2,0,1], [2,1,0]];
            Vector3 value = new(1, 2, 3);
            Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
            float[] degrees = [20, 30, 40];
            foreach (int axis in indices[order])
                value = Vector3.Transform(value, Quaternion.CreateFromAxisAngle(axes[axis], degrees[axis] * MathF.PI / 180));
            Require(Vector3.Distance(value, Vector3.Transform(new Vector3(1,2,3), matrix)) < 1e-6,
                "Euler order must apply all rotations in the authored order");
        }
        Require(!UnityFbxLocalTransform.TryRead([new object[] { "RotationOrder", "enum", "", "", 6 }], out _),
            "Unsupported spherical rotation must use the importer fallback");
        Require(!UnityFbxLocalTransform.TryRead([P("GeometricScaling", 2, 1, 1).Values], out _),
            "Separate geometry frames must not be applied as node transforms");
        Require(!UnityFbxLocalTransform.TryRead([P("Lcl Scaling", double.NaN, 1, 1).Values], out _),
            "Malformed transforms must fall back without discarding other model metadata");
        Console.WriteLine("PASS: source FBX tiny transforms, helpers, Euler orders and fallback checks");
    }

    private static N P(string name, double x, double y, double z) => new("P", [name, "Vector3D", "Vector", "", x,y,z]);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Node(BinaryWriter writer, N node, int version)
    {
        bool wide = version >= 7500;
        long start = writer.BaseStream.Position;
        writer.Write(new byte[wide ? 25 : 13]); writer.Write(Encoding.UTF8.GetBytes(node.Name));
        long props = writer.BaseStream.Position;
        foreach (var value in node.Values)
            switch (value)
            {
                case string s: writer.Write((byte)'S'); byte[] bytes = Encoding.UTF8.GetBytes(s); writer.Write(bytes.Length); writer.Write(bytes); break;
                case long n: writer.Write((byte)'L'); writer.Write(n); break;
                case double d: writer.Write((byte)'D'); writer.Write(d); break;
                default: throw new Exception("Unsupported fixture property");
            }
        long length = writer.BaseStream.Position - props;
        foreach (var child in node.Children) Node(writer, child, version);
        writer.Write(new byte[wide ? 25 : 13]);
        long end = writer.BaseStream.Position; writer.BaseStream.Position = start;
        if (wide) { writer.Write(end); writer.Write((long)node.Values.Length); writer.Write(length); }
        else { writer.Write((uint)end); writer.Write((uint)node.Values.Length); writer.Write((uint)length); }
        writer.Write((byte)Encoding.UTF8.GetByteCount(node.Name)); writer.BaseStream.Position = end;
    }
}
