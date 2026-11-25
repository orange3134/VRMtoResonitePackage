using System.IO.Compression;
using System.Text;

namespace VrmToResonitePackage.Unity;

/// <summary>
/// Reads BlendShapeChannel.DeformPercent values that Unity applies as model-prefab defaults but
/// Assimp/Resonite do not expose as renderer weights.
/// </summary>
internal static class UnityFbxBlendShapeDefaults
{
    internal readonly record struct Channel(string Name, float Weight, string RendererPath, string ShapeName = null,
        long? ExclusiveShapeId = null);

    public static IReadOnlyList<Channel> Read(string path) => Read(path, out _);

    // Material-split meshes can expose Geometry names for every Assimp attachment.
    // Resolve collisions before import, while each shape still has its FBX object ID.
    internal static Dictionary<long, string> ChannelNamesForImport(IEnumerable<Channel> channels)
    {
        var names = new Dictionary<long, string>();
        foreach (var mesh in channels.GroupBy(c => c.RendererPath))
        {
            if (!mesh.GroupBy(c => c.ShapeName).Any(g => g.Count() > 1)) continue;
            if (mesh.Any(c => c.ExclusiveShapeId == null || string.IsNullOrEmpty(c.Name)) ||
                mesh.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != mesh.Count()) continue;
            foreach (var channel in mesh)
                if (channel.ShapeName != channel.Name)
                    names[channel.ExclusiveShapeId.Value] = channel.Name;
        }
        return names;
    }

    internal static IReadOnlyList<Channel> Read(string path, out IReadOnlyDictionary<string, string[]> skinBonePaths)
        => Read(path, out skinBonePaths, out _);

    internal static IReadOnlyList<Channel> Read(string path, out IReadOnlyDictionary<string, string[]> skinBonePaths,
        out IReadOnlyDictionary<string, System.Numerics.Matrix4x4> localTransforms)
        => Read(path, out skinBonePaths, out localTransforms, out _);

    internal static IReadOnlyList<Channel> Read(string path, out IReadOnlyDictionary<string, string[]> skinBonePaths,
        out IReadOnlyDictionary<string, System.Numerics.Matrix4x4> localTransforms, out bool hasEmbeddedShapes)
    {
        skinBonePaths = null;
        localTransforms = null;
        hasEmbeddedShapes = false;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 27 ||
                !Encoding.ASCII.GetString(data, 0, 18).StartsWith("Kaydara FBX Binary", StringComparison.Ordinal))
            {
                return Array.Empty<Channel>();
            }
            int version = BitConverter.ToInt32(data, 23);
            var reader = new Reader(data, version >= 7500);
            return reader.ReadChannels(out skinBonePaths, out localTransforms, out hasEmbeddedShapes);
        }
        catch
        {
            return Array.Empty<Channel>();
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly bool _wide;
        private long _position = 27;

        public Reader(byte[] data, bool wide)
        {
            _data = data;
            _wide = wide;
        }

        public IReadOnlyList<Channel> ReadChannels(out IReadOnlyDictionary<string, string[]> skinBonePaths,
            out IReadOnlyDictionary<string, System.Numerics.Matrix4x4> localTransforms, out bool hasEmbeddedShapes)
        {
            var nodes = new List<Node>();
            void Collect(Node node)
            {
                nodes.Add(node);
                foreach (var child in node.Children) Collect(child);
            }
            while (TryReadNode(out Node node)) Collect(node);
            hasEmbeddedShapes = nodes.Any(n => n.Name == "Geometry" && n.Children.Any(c => c.Name == "Shape"));
            var objects = nodes.Where(n => n.Name is "Model" or "Geometry" or "Deformer" &&
                n.Properties.Count >= 3 && n.Properties[0] is long).ToDictionary(n => (long)n.Properties[0]);
            var connections = nodes.Where(n => n.Name == "C" && n.Properties.Count >= 3 &&
                n.Properties[0] as string == "OO" && n.Properties[1] is long && n.Properties[2] is long)
                .Select(n => (Child: (long)n.Properties[1], Parent: (long)n.Properties[2])).Distinct().ToArray();
            var parents = connections.GroupBy(c => c.Child).ToDictionary(g => g.Key, g => g.Select(c => c.Parent).ToArray());
            // A cluster can have earlier links to other objects. Enumerate this
            // parent's incoming connections in file order, not global child order.
            var children = connections.ToLookup(c => c.Parent, c => c.Child);
            var skinPaths = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var skin in objects.Where(e => e.Value.Name == "Deformer" && e.Value.Properties[2] as string == "Skin"))
            {
                // Connection order is the cluster index order; object names alone cannot
                // distinguish a mesh from a joint or repeated names in separate branches.
                var clusters = children[skin.Key].Where(id => objects.GetValueOrDefault(id)?.Properties[2] as string == "Cluster");
                var paths = new List<string>();
                bool valid = true;
                foreach (long cluster in clusters)
                {
                    var bones = children[cluster].Where(id => objects.GetValueOrDefault(id)?.Name == "Model").ToArray();
                    string path = bones.Length == 1 ? ModelPath(bones[0], new()) : null;
                    if (path == null) { valid = false; break; }
                    paths.Add(path);
                }
                if (!valid) continue;
                foreach (long model in ModelsAbove(skin.Key, new()))
                    if (ModelPath(model, new()) is string path)
                    {
                        if (skinPaths.ContainsKey(path)) skinPaths[path] = null; // ambiguous multiple skins
                        else skinPaths.Add(path, paths.ToArray());
                    }
            }
            skinBonePaths = skinPaths.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var transforms = new Dictionary<string, System.Numerics.Matrix4x4>();
            var ambiguous = new HashSet<string>();
            foreach (var entry in objects.Where(e => e.Value.Name == "Model"))
                if (ModelPath(entry.Key, new()) is string path &&
                    UnityFbxLocalTransform.TryRead(entry.Value.Children
                        .FirstOrDefault(n => n.Name == "Properties70")?.Children
                        .Where(n => n.Name == "P").Select(n => (IReadOnlyList<object>)n.Properties), out var matrix,
                        entry.Value.Properties[2] as string == "Mesh" && !skinPaths.ContainsKey(path) &&
                        !children[entry.Key].Any(id => objects.GetValueOrDefault(id)?.Name == "Model")))
                {
                    if (!transforms.TryAdd(path, matrix)) ambiguous.Add(path);
                }
            foreach (string path in ambiguous) transforms.Remove(path);
            localTransforms = transforms;
            var result = new List<Channel>();
            foreach (var entry in objects.Where(e => e.Value.Name == "Deformer" &&
                         e.Value.Properties[2] as string == "BlendShapeChannel"))
            {
                var channel = entry.Value;
                double percent = channel.Children.FirstOrDefault(child => child.Name == "DeformPercent")
                    ?.Properties.FirstOrDefault() as double? ?? 0d;
                double endWeight = GetEndFrameWeight(channel);
                float weight = endWeight > 0 ? (float)(percent / endWeight * 100d) : (float)percent;
                var shapes = children[entry.Key].Select(objects.GetValueOrDefault)
                    .Where(o => o?.Name == "Geometry" && o.Properties[2] as string == "Shape").ToArray();
                string shapeName = shapes.Length == 1 ? NormalizeObjectName(shapes[0].Properties[1] as string) : null;
                long? shapeId = shapes.Length == 1 && shapes[0].Properties is [long id, string, "Shape"] &&
                    parents[id].Length == 1 ? id : null;
                foreach (long model in ModelsAbove(entry.Key, new()))
                    if (ModelPath(model, new()) is string path)
                        result.Add(new Channel(NormalizeObjectName(channel.Properties[1] as string), weight, path, shapeName, shapeId));
            }
            return result;

            HashSet<long> ModelsAbove(long id, HashSet<long> visited)
            {
                var models = new HashSet<long>();
                if (!visited.Add(id) || !objects.TryGetValue(id, out var current)) return models;
                if (current.Name == "Model") { models.Add(id); return models; }
                foreach (long parent in parents.GetValueOrDefault(id) ?? Array.Empty<long>())
                    models.UnionWith(ModelsAbove(parent, visited));
                return models;
            }

            string ModelPath(long id, HashSet<long> visited)
            {
                if (!visited.Add(id)) return null;
                var modelParents = (parents.GetValueOrDefault(id) ?? Array.Empty<long>())
                    .Where(parent => objects.GetValueOrDefault(parent)?.Name == "Model").ToArray();
                if (modelParents.Length > 1) return null;
                string parentPath = modelParents.Length == 0 ? "RootNode" : ModelPath(modelParents[0], visited);
                string name = NormalizeObjectName(objects[id].Properties[1] as string);
                return parentPath == null || string.IsNullOrEmpty(name) ? null : parentPath + "/" + name;
            }
        }

        private static double GetEndFrameWeight(Node channel)
        {
            object weights = channel.Children
                .FirstOrDefault(child => child.Name == "FullWeights")
                ?.Properties.FirstOrDefault();
            return weights switch
            {
                double[] values when values.Length > 0 => values[^1],
                float[] values when values.Length > 0 => values[^1],
                _ => 100d,
            };
        }

        private static string NormalizeObjectName(string name)
        {
            if (name == null)
            {
                return null;
            }
            int separator = name.IndexOf('\0');
            return separator >= 0 ? name[..separator] : name;
        }

        private bool TryReadNode(out Node node)
        {
            node = null;
            int headerSize = _wide ? 25 : 13;
            if (_position + headerSize > _data.Length)
            {
                return false;
            }

            long end;
            long propertyCount;
            long propertyLength;
            byte nameLength;
            if (_wide)
            {
                end = BitConverter.ToInt64(_data, (int)_position);
                propertyCount = BitConverter.ToInt64(_data, (int)_position + 8);
                propertyLength = BitConverter.ToInt64(_data, (int)_position + 16);
                nameLength = _data[_position + 24];
                _position += 25;
            }
            else
            {
                end = BitConverter.ToUInt32(_data, (int)_position);
                propertyCount = BitConverter.ToUInt32(_data, (int)_position + 4);
                propertyLength = BitConverter.ToUInt32(_data, (int)_position + 8);
                nameLength = _data[_position + 12];
                _position += 13;
            }
            if (end == 0 || end > _data.Length)
            {
                return false;
            }

            string name = Encoding.UTF8.GetString(_data, (int)_position, nameLength);
            _position += nameLength;
            long propertyEnd = _position + propertyLength;
            var properties = new List<object>((int)Math.Min(propertyCount, int.MaxValue));
            for (long i = 0; i < propertyCount; i++)
            {
                properties.Add(ReadProperty());
            }
            _position = propertyEnd;

            var children = new List<Node>();
            int nullSize = _wide ? 25 : 13;
            while (_position < end - nullSize && TryReadNode(out Node child))
            {
                children.Add(child);
            }
            _position = end;
            node = new Node(name, properties, children);
            return true;
        }

        private object ReadProperty()
        {
            char type = (char)_data[_position++];
            switch (type)
            {
                case 'Y':
                    return ReadInt16();
                case 'C':
                    return _data[_position++] != 0;
                case 'I':
                    return ReadInt32();
                case 'F':
                    return ReadSingle();
                case 'D':
                    return ReadDouble();
                case 'L':
                    return ReadInt64();
                case 'S':
                    return Encoding.UTF8.GetString(ReadRaw());
                case 'R':
                    return ReadRaw();
                case 'f':
                    return ReadArray(BitConverter.ToSingle);
                case 'd':
                    return ReadArray(BitConverter.ToDouble);
                case 'l':
                case 'i':
                case 'b':
                    SkipArray();
                    return null;
                default:
                    throw new InvalidDataException($"Unknown FBX property type '{type}'.");
            }
        }

        private short ReadInt16()
        {
            short value = BitConverter.ToInt16(_data, (int)_position);
            _position += 2;
            return value;
        }

        private int ReadInt32()
        {
            int value = BitConverter.ToInt32(_data, (int)_position);
            _position += 4;
            return value;
        }

        private long ReadInt64()
        {
            long value = BitConverter.ToInt64(_data, (int)_position);
            _position += 8;
            return value;
        }

        private float ReadSingle()
        {
            float value = BitConverter.ToSingle(_data, (int)_position);
            _position += 4;
            return value;
        }

        private double ReadDouble()
        {
            double value = BitConverter.ToDouble(_data, (int)_position);
            _position += 8;
            return value;
        }

        private byte[] ReadRaw()
        {
            int length = ReadInt32();
            byte[] value = _data.AsSpan((int)_position, length).ToArray();
            _position += length;
            return value;
        }

        private void SkipArray()
        {
            int length = ReadInt32();
            int encoding = ReadInt32();
            int storedLength = ReadInt32();
            if (length < 0 || encoding < 0 || storedLength < 0)
            {
                throw new InvalidDataException("Invalid FBX array.");
            }
            _position += storedLength;
        }

        private T[] ReadArray<T>(Func<byte[], int, T> readValue)
        {
            int length = ReadInt32();
            int encoding = ReadInt32();
            int storedLength = ReadInt32();
            if (length < 0 || storedLength < 0)
            {
                throw new InvalidDataException("Invalid FBX array.");
            }

            byte[] stored = _data.AsSpan((int)_position, storedLength).ToArray();
            _position += storedLength;
            byte[] raw;
            if (encoding == 0)
            {
                raw = stored;
            }
            else if (encoding == 1)
            {
                using var input = new MemoryStream(stored);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                raw = output.ToArray();
            }
            else
            {
                throw new InvalidDataException($"Unsupported FBX array encoding {encoding}.");
            }

            int elementSize = raw.Length / Math.Max(length, 1);
            var result = new T[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = readValue(raw, i * elementSize);
            }
            return result;
        }
    }

    private sealed record Node(string Name, List<object> Properties, List<Node> Children);
}
