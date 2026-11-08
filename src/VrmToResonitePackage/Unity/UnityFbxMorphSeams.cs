using System.IO.Compression;
using System.Text;
using Elements.Core;

namespace VrmToResonitePackage.Unity;

/// <summary>Preserves morph seams through Assimp's base-pose-only vertex welding.</summary>
internal static class UnityFbxMorphSeams
{
    public static string Prepare(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 27 || !Encoding.ASCII.GetString(bytes, 0, 23).StartsWith("Kaydara FBX Binary", StringComparison.Ordinal)) return path;
        var document = new Document(bytes);
        var objects = document.Roots.FirstOrDefault(n => n.Name == "Objects")?.Children;
        var connections = document.Roots.FirstOrDefault(n => n.Name == "Connections")?.Children;
        if (objects == null || connections == null) return path;
        var byId = objects.Where(n => n.Values.FirstOrDefault() is long).ToDictionary(n => (long)n.Values[0]);
        var parents = connections.Where(n => n.Name == "C" && n.Values.Length >= 3 && n.Values[0] as string == "OO")
            .GroupBy(n => (long)n.Values[1]).ToDictionary(g => g.Key, g => g.Select(n => (long)n.Values[2]).ToArray());
        var shapes = new Dictionary<long, List<Node>>();
        foreach (var shape in objects.Where(n => IsGeometry(n, "Shape")))
        {
            var visited = new HashSet<long>();
            void Visit(long id)
            {
                if (!visited.Add(id) || !byId.TryGetValue(id, out var node)) return;
                if (IsGeometry(node, "Mesh"))
                {
                    if (!shapes.TryGetValue(id, out var list)) shapes[id] = list = new();
                    list.Add(shape); return;
                }
                foreach (long parent in parents.GetValueOrDefault(id) ?? []) Visit(parent);
            }
            Visit((long)shape.Values[0]);
        }
        int protectedMeshes = 0;
        foreach (var (id, meshShapes) in shapes)
        {
            var mesh = byId[id];
            var positions = Array<double>(mesh, "Vertices");
            if (positions == null || positions.Length % 3 != 0) continue;
            int count = positions.Length / 3;
            var coincident = Enumerable.Range(0, count)
                .GroupBy(i => ((float)positions[i * 3], (float)positions[i * 3 + 1], (float)positions[i * 3 + 2]))
                .Where(g => g.Count() > 1).Select(g => g.ToArray()).ToArray();
            if (!meshShapes.Any(shape => HasMorphSeam(shape, coincident, count))) continue;

            // ImportVertexColors is disabled in the VRChat conversion path. Preserve all
            // authored color layers and use an unused one only as a temporary weld key.
            var used = mesh.Children.Where(n => n.Name == "LayerElementColor").Select(n => (int)n.Values[0]).ToHashSet();
            int channel = Enumerable.Range(0, 8).FirstOrDefault(i => !used.Contains(i), -1);
            if (channel < 0) throw new InvalidDataException("Cannot protect FBX morph seams: all eight color channels are occupied.");
            var colors = new double[count * 4];
            for (int i = 0; i < count; i++) { colors[i * 4] = i; colors[i * 4 + 3] = 1; }
            mesh.Children.Add(Node.New("LayerElementColor", channel,
                Node.New("Version", 101), Node.New("Name", "ResoPonMorphVertex"),
                Node.New("MappingInformationType", "ByVertice"), Node.New("ReferenceInformationType", "Direct"),
                Node.New("Colors", colors)));
            var layer = mesh.Children.FirstOrDefault(n => n.Name == "Layer" &&
                !n.Children.Any(c => c.Name == "LayerElement" && c.Children.Any(t => t.Name == "Type" && t.Values[0] as string == "LayerElementColor")));
            if (layer == null)
            {
                int index = mesh.Children.Where(n => n.Name == "Layer").Select(n => (int)n.Values[0]).DefaultIfEmpty(-1).Max() + 1;
                layer = Node.New("Layer", index, Node.New("Version", 100)); mesh.Children.Add(layer);
            }
            layer.Children.Add(Node.New("LayerElement", null, Node.New("Type", "LayerElementColor"), Node.New("TypedIndex", channel)));
            protectedMeshes++;
        }
        if (protectedMeshes == 0) return path;
        string output = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".morph-seams.fbx");
        document.Save(output);
        UniLog.Log($"Protected morph seams in {protectedMeshes} FBX mesh(es): {Path.GetFileName(path)}.");
        return output;
    }

    private static bool IsGeometry(Node n, string kind) => n.Name == "Geometry" && n.Values.Length >= 3 && n.Values[2] as string == kind;
    private static T[] Array<T>(Node n, string name) => n.Children.FirstOrDefault(c => c.Name == name)?.Values.FirstOrDefault() as T[];
    private static bool HasMorphSeam(Node shape, int[][] groups, int vertexCount)
    {
        var indices = Array<int>(shape, "Indexes");
        if (indices == null) return false;
        foreach (string attribute in new[] { "Vertices", "Normals" })
        {
            var values = Array<double>(shape, attribute);
            if (values == null) continue;
            if (values.Length != indices.Length * 3 || indices.Any(i => i < 0 || i >= vertexCount))
                throw new InvalidDataException("Invalid FBX morph vertex array.");
            var deltas = indices.Select((index, i) => (index, value: ((float)values[i * 3], (float)values[i * 3 + 1], (float)values[i * 3 + 2])))
                .ToDictionary(p => p.index, p => p.value);
            if (groups.Any(group => group.Skip(1).Any(i => deltas.GetValueOrDefault(i) != deltas.GetValueOrDefault(group[0])))) return true;
        }
        return false;
    }

    // Keep original property bytes (including compressed/unknown arrays) verbatim.
    // Only node offsets and the new color layer are serialized again.
    private sealed class Node
    {
        public string Name;
        public byte[] Properties;
        public int PropertyCount;
        public object[] Values;
        public bool Terminator;
        public List<Node> Children = new();
        public static Node New(string name, object value, params Node[] children)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            switch (value)
            {
                case int i: writer.Write((byte)'I'); writer.Write(i); break;
                case string s: writer.Write((byte)'S'); byte[] utf8 = Encoding.UTF8.GetBytes(s); writer.Write(utf8.Length); writer.Write(utf8); break;
                case double[] a:
                    writer.Write((byte)'d'); writer.Write(a.Length); writer.Write(0); writer.Write(a.Length * 8);
                    foreach (double d in a) writer.Write(d); break;
                case null: break;
                default: throw new InvalidDataException("Unsupported generated FBX property.");
            }
            return new() { Name = name, Properties = stream.ToArray(), PropertyCount = value == null ? 0 : 1,
                Values = value == null ? [] : [value], Children = children.ToList(), Terminator = children.Length > 0 };
        }
    }

    private sealed class Document
    {
        private readonly byte[] _bytes;
        private readonly bool _wide;
        private readonly long _footer;
        public List<Node> Roots = new();
        private int HeaderSize => _wide ? 25 : 13;
        public Document(byte[] bytes)
        {
            _bytes = bytes; _wide = BitConverter.ToInt32(bytes, 23) >= 7500;
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            reader.BaseStream.Position = 27;
            while (true)
            {
                long start = reader.BaseStream.Position;
                var node = ReadNode(reader);
                if (node == null) { _footer = start; break; }
                Roots.Add(node);
            }
        }
        private Node ReadNode(BinaryReader reader)
        {
            long end = _wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
            long count = _wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
            long length = _wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
            byte nameLength = reader.ReadByte();
            if (end == 0) return null;
            if (end > _bytes.Length || end <= reader.BaseStream.Position || count > int.MaxValue || length > int.MaxValue)
                throw new InvalidDataException("Invalid FBX node bounds.");
            string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            byte[] raw = reader.ReadBytes((int)length);
            var values = new object[(int)count];
            using (var props = new BinaryReader(new MemoryStream(raw), Encoding.UTF8))
                for (int i = 0; i < values.Length; i++) values[i] = ReadProperty(props);
            var node = new Node { Name = name, Properties = raw, PropertyCount = (int)count, Values = values };
            while (reader.BaseStream.Position < end)
            {
                if (end - reader.BaseStream.Position < HeaderSize) throw new InvalidDataException("Truncated FBX child node.");
                var child = ReadNode(reader);
                if (child == null) { node.Terminator = true; break; }
                node.Children.Add(child);
            }
            if (reader.BaseStream.Position != end) throw new InvalidDataException("Invalid FBX node end.");
            return node;
        }
        private static object ReadProperty(BinaryReader r)
        {
            char type = (char)r.ReadByte();
            switch (type)
            {
                case 'Y': return r.ReadInt16(); case 'C': return r.ReadBoolean(); case 'I': return r.ReadInt32();
                case 'F': return r.ReadSingle(); case 'D': return r.ReadDouble(); case 'L': return r.ReadInt64();
                case 'S': return Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
                case 'R': return r.ReadBytes(r.ReadInt32());
                case 'd': case 'i': case 'f': case 'l': case 'b': case 'c':
                    int count = r.ReadInt32(), encoding = r.ReadInt32(), length = r.ReadInt32();
                    byte[] bytes = r.ReadBytes(length);
                    if (type is not ('d' or 'i')) return null;
                    using (var stream = new MemoryStream(bytes))
                    using (Stream data = encoding == 0 ? stream : encoding == 1 ? new ZLibStream(stream, CompressionMode.Decompress) : throw new InvalidDataException("Invalid FBX array encoding."))
                    using (var reader = new BinaryReader(data))
                    {
                        if (type == 'd') { var array = new double[count]; for (int i = 0; i < count; i++) array[i] = reader.ReadDouble(); return array; }
                        else { var array = new int[count]; for (int i = 0; i < count; i++) array[i] = reader.ReadInt32(); return array; }
                    }
                default: throw new InvalidDataException($"Unknown FBX property {type}.");
            }
        }
        public void Save(string path)
        {
            using var stream = File.Create(path); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(_bytes, 0, 27);
            foreach (var node in Roots) WriteNode(writer, node);
            writer.Write(_bytes, (int)_footer, _bytes.Length - (int)_footer);
        }
        private void WriteNode(BinaryWriter writer, Node node)
        {
            long start = writer.BaseStream.Position;
            writer.Write(new byte[HeaderSize]); byte[] name = Encoding.UTF8.GetBytes(node.Name);
            writer.Write(name); writer.Write(node.Properties);
            foreach (var child in node.Children) WriteNode(writer, child);
            if (node.Terminator || node.Children.Count > 0) writer.Write(new byte[HeaderSize]);
            long end = writer.BaseStream.Position; writer.BaseStream.Position = start;
            if (_wide) { writer.Write((ulong)end); writer.Write((ulong)node.PropertyCount); writer.Write((ulong)node.Properties.Length); }
            else { writer.Write(checked((uint)end)); writer.Write((uint)node.PropertyCount); writer.Write((uint)node.Properties.Length); }
            writer.Write(checked((byte)name.Length)); writer.BaseStream.Position = end;
        }
    }
}
