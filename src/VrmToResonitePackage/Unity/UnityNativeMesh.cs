using System.Buffers.Binary;
using Elements.Assets;
using Elements.Core;

namespace VrmToResonitePackage.Unity;

/// <summary>Unity 2019+ uncompressed Mesh serialization, in Unity's local meters and bone index order.</summary>
internal static class UnityNativeMesh
{
    public static MeshX Decode(YamlNode root)
    {
        if ((root["m_MeshCompression"]?.AsInt() ?? 0) != 0 ||
            (root["m_CompressedMesh"]?["m_Vertices"]?["m_NumItems"]?.AsInt() ?? 0) != 0)
            throw new InvalidDataException("Compressed Unity Mesh assets are not supported.");
        if ((root["m_StreamData"]?["size"]?.AsLong() ?? 0) != 0)
            throw new InvalidDataException("Streamed Unity Mesh assets are not supported.");
        var data = root["m_VertexData"] ?? throw new InvalidDataException("Unity Mesh has no vertex data.");
        int count = data["m_VertexCount"]?.AsInt() ?? 0;
        byte[] bytes = Buffer(data["m_DataSize"]?.Bytes != null ? data["m_DataSize"] : data["_typelessdata"] ?? data["m_DataSize"]);
        var channels = data["m_Channels"]?.Items ?? Array.Empty<YamlNode>();
        if (count < 0 || count > bytes.Length / 4 || channels.Count < 1)
            throw new InvalidDataException("Invalid Unity Mesh vertex count/channels.");
        var strides = new Dictionary<int, int>();
        foreach (var c in channels.Where(c => Dimension(c) > 0))
        {
            int stream = c["stream"].AsInt(), offset = c["offset"].AsInt();
            if (stream < 0 || stream > 15 || offset < 0) throw new InvalidDataException("Invalid Unity vertex stream.");
            strides[stream] = Math.Max(strides.GetValueOrDefault(stream), checked(offset + Dimension(c) * Size(c["format"].AsInt())));
        }
        var starts = new Dictionary<int, int>();
        int end = 0;
        foreach (var (stream, stride) in strides.OrderBy(p => p.Key))
        {
            starts[stream] = end;
            end = checked(end + stride * count);
            if (end > bytes.Length) throw new InvalidDataException("Truncated Unity vertex buffer.");
            end = checked((end + 15) & ~15);
        }
        bool Has(int channel, int dimension) => channel < channels.Count && Dimension(channels[channel]) >= dimension;
        float Value(int vertex, int channel, int component)
        {
            var c = channels[channel];
            int format = c["format"].AsInt(), stream = c["stream"].AsInt();
            int offset = checked(starts[stream] + vertex * strides[stream] + c["offset"].AsInt() + component * Size(format));
            return Read(bytes.AsSpan(offset, Size(format)), format);
        }
        float3 V3(int v, int c) => new(Value(v, c, 0), Value(v, c, 1), Value(v, c, 2));
        if (!Has(0, 3)) throw new InvalidDataException("Unity Mesh has no positions.");
        var mesh = new MeshX { HasNormals = Has(1, 3), HasTangents = Has(2, 4), HasColors = Has(3, 4),
            HasUV0s = Has(4, 2), HasUV1s = Has(5, 2), HasUV2s = Has(6, 2), HasUV3s = Has(7, 2) };
        mesh.SetVertexCount(count);
        for (int v = 0; v < count; v++)
        {
            mesh.SetVertex(v, V3(v, 0));
            if (mesh.HasNormals) mesh.SetNormal(v, V3(v, 1));
            if (mesh.HasTangents) mesh.SetTangent(v, new float4(V3(v, 2), Value(v, 2, 3)));
            if (mesh.HasColors) mesh.SetColor(v, new color(Value(v, 3, 0), Value(v, 3, 1), Value(v, 3, 2), Value(v, 3, 3)));
            for (int uv = 0; uv < 4; uv++)
                if (Has(4 + uv, 2)) mesh.SetUV(v, uv, new float2(Value(v, 4 + uv, 0), Value(v, 4 + uv, 1)));
        }
        var indices = Buffer(root["m_IndexBuffer"]);
        int indexSize = root["m_IndexFormat"]?.AsInt() switch { null or 0 => 2, 1 => 4, _ => throw new InvalidDataException("Invalid Unity index format.") };
        foreach (var sub in root["m_SubMeshes"].Items)
        {
            if (sub["topology"].AsInt() != 0) throw new InvalidDataException("Only triangle Unity submeshes are supported.");
            int start = sub["firstByte"].AsInt(), length = sub["indexCount"].AsInt(), baseVertex = sub["baseVertex"]?.AsInt() ?? 0;
            if (start < 0 || length < 0 || length % 3 != 0 || (long)start + (long)length * indexSize > indices.Length)
                throw new InvalidDataException("Invalid Unity submesh index range.");
            int Index(int i)
            {
                var span = indices.AsSpan(start + i * indexSize, indexSize);
                long value = (indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)) + (long)baseVertex;
                if (value < 0 || value >= count) throw new InvalidDataException("Unity mesh index outside vertex buffer.");
                return (int)value;
            }
            var target = mesh.AddSubmesh<TriangleSubmesh>();
            for (int i = 0; i < length; i += 3) target.AddTriangle(Index(i), Index(i + 1), Index(i + 2));
        }
        var poses = root["m_BindPose"]?.Items ?? Array.Empty<YamlNode>();
        mesh.HasBoneBindings = poses.Count > 0;
        for (int b = 0; b < poses.Count; b++)
            mesh.AddBone($"Bone{b}").BindPose = new float4x4(Enumerable.Range(0, 16).Select(i => poses[b][$"e{i / 4}{i % 4}"].AsFloat()).ToArray());
        if (poses.Count > 0)
        {
            // Unity omits weights for a rigid (one-index) skin: its weight is implicitly 1.
            bool rigid = Has(13, 1) && Dimension(channels[13]) == 1 && !Has(12, 1);
            if (!rigid && (!Has(12, 1) || !Has(13, 1) || Dimension(channels[12]) != Dimension(channels[13]) || Dimension(channels[12]) > 4))
                throw new InvalidDataException("Unsupported Unity skin weight/index channels.");
            for (int v = 0; v < count; v++)
            {
                var binding = new BoneBinding();
                for (int b = 0; b < Dimension(channels[13]); b++)
                {
                    float weight = rigid ? 1f : Value(v, 12, b);
                    int index = checked((int)Value(v, 13, b));
                    if (!float.IsFinite(weight) || weight < 0 || weight > 0 && (index < 0 || index >= poses.Count))
                        throw new InvalidDataException("Invalid Unity skin weight/index.");
                    binding.SetBinding(b, weight > 0 ? index : 0, weight);
                }
                mesh.RawBoneBindings[v] = binding;
            }
            mesh.SortTrimAndNormalizeBoneWeights();
            mesh.FillInEmptyBindings(0);
        }
        var shapes = root["m_Shapes"];
        var deltas = shapes?["vertices"]?.Items ?? Array.Empty<YamlNode>();
        var frames = shapes?["shapes"]?.Items ?? Array.Empty<YamlNode>();
        var weights = shapes?["fullWeights"]?.Items ?? Array.Empty<YamlNode>();
        foreach (var channel in shapes?["channels"]?.Items ?? Array.Empty<YamlNode>())
        {
            var shape = mesh.AddBlendShape(channel["name"].AsString());
            int first = channel["frameIndex"].AsInt(), frameCount = channel["frameCount"].AsInt();
            if (first < 0 || frameCount < 0 || (long)first + frameCount > Math.Min(frames.Count, weights.Count))
                throw new InvalidDataException("Invalid Unity blendshape frame range.");
            for (int f = first; f < first + frameCount; f++)
            {
                var source = frames[f];
                bool frameNormals = source["hasNormals"]?.AsBool() == true;
                bool frameTangents = source["hasTangents"]?.AsBool() == true;
                shape.HasNormals |= frameNormals;
                shape.HasTangents |= frameTangents;
                var frame = shape.AddFrame(weights[f].AsFloat() / 100f);
                int start = source["firstVertex"].AsInt(), length = source["vertexCount"].AsInt();
                if (start < 0 || length < 0 || (long)start + length > deltas.Count)
                    throw new InvalidDataException("Invalid Unity blendshape delta range.");
                for (int i = start; i < start + length; i++)
                {
                    var delta = deltas[i]; int index = delta["index"].AsInt();
                    if (index < 0 || index >= count) throw new InvalidDataException("Unity blendshape vertex outside mesh.");
                    frame.SetPositionDelta(index, Vector(delta["vertex"]));
                    if (frameNormals) frame.SetNormalDelta(index, Vector(delta["normal"]));
                    if (frameTangents) frame.SetTangentDelta(index, Vector(delta["tangent"]));
                }
            }
        }
        // Match modular-avatar-resonite's workaround for mixed morph normal/tangent flags.
        bool normals = mesh.BlendShapes.Any(s => s.HasNormals), tangents = mesh.BlendShapes.Any(s => s.HasTangents);
        foreach (var shape in mesh.BlendShapes) { shape.HasNormals = normals; shape.HasTangents = tangents; }
        return mesh;
    }

    private static float3 Vector(YamlNode node) => new(node?.Vec("x") ?? 0, node?.Vec("y") ?? 0, node?.Vec("z") ?? 0);
    private static int Dimension(YamlNode channel) => (channel["dimension"]?.AsInt() ?? 0) & 15;
    internal static byte[] Buffer(YamlNode node) => node?.Bytes ??
        (node?.ScalarValue is string hex ? Convert.FromHexString(hex) :
            node?.Seq != null ? node.Seq.Select(n => checked((byte)n.AsInt())).ToArray() : Array.Empty<byte>());
    private static int Size(int format) => format switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2,
        2 or 3 or 6 or 7 => 1, _ => throw new InvalidDataException($"Unsupported Unity vertex format: {format}") };
    private static float Read(ReadOnlySpan<byte> data, int format) => format switch
    {
        0 => BinaryPrimitives.ReadSingleLittleEndian(data), 1 => (float)BinaryPrimitives.ReadHalfLittleEndian(data),
        2 => data[0] / 255f, 3 => Math.Max(-1, (sbyte)data[0] / 127f),
        4 => BinaryPrimitives.ReadUInt16LittleEndian(data) / 65535f,
        5 => Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(data) / 32767f),
        6 => data[0], 7 => (sbyte)data[0], 8 => BinaryPrimitives.ReadUInt16LittleEndian(data),
        9 => BinaryPrimitives.ReadInt16LittleEndian(data), 10 => BinaryPrimitives.ReadUInt32LittleEndian(data),
        11 => BinaryPrimitives.ReadInt32LittleEndian(data), _ => throw new InvalidDataException("Unsupported Unity vertex format.")
    };
}
