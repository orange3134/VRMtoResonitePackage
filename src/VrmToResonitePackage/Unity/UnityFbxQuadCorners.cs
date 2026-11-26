using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace VrmToResonitePackage.Unity;

internal static partial class UnityFbxMorphSeams
{
    // Unity splits an FBX quad along authored corners 0 and 2. ModelImporter
    // reverses the polygon before splitting. Cycle the starting corner without
    // changing winding, polygon IDs, control points or the number of edges.
    private static bool RotateQuadCorners(Node mesh)
    {
        var polygon = mesh.Children.FirstOrDefault(n => n.Name == "PolygonVertexIndex");
        if (polygon?.Values is not [int[] corners]) return false;
        List<int> starts = new();
        for (int i = 0, first = 0; i < corners.Length; i++)
        {
            if (corners[i] >= 0) continue;
            if (i - first == 3) starts.Add(first);
            first = i + 1;
        }
        if (starts.Count == 0) return false;

        // Direct layers store tuples per corner. Indexed layers store only
        // their lookup indices per corner; the direct attribute table stays put.
        var arrays = new List<Node>();
        foreach (var layer in mesh.Children.Where(n => n.Name.StartsWith("LayerElement", StringComparison.Ordinal)))
        {
            string Property(string name) => layer.Children.FirstOrDefault(n => n.Name == name)?.Values.FirstOrDefault() as string;
            if (Property("MappingInformationType") != "ByPolygonVertex") continue;
            var selected = MappedArrays(layer, Property("ReferenceInformationType"));
            if (selected.Length == 0 || selected.Any(n => ArrayCount(n) != 0 && ArrayCount(n) % corners.Length != 0))
                return false;
            arrays.AddRange(selected.Where(n => ArrayCount(n) != 0));
        }

        // Edges refer to the polygon corner at which the directed edge starts.
        var edges = mesh.Children.FirstOrDefault(n => n.Name == "Edges");
        if (edges != null)
        {
            if (edges.Values is not [int[] indices] || indices.Any(i => (uint)i >= corners.Length)) return false;
            int[] remap = Enumerable.Range(0, corners.Length).ToArray();
            foreach (int p in starts)
                for (int j = 0; j < 4; j++) remap[p + j] = p + (j + 3) % 4;
            for (int i = 0; i < indices.Length; i++) indices[i] = remap[indices[i]];
            StoreArray(edges, MemoryMarshal.AsBytes(indices.AsSpan()));
        }
        foreach (var field in arrays)
        {
            byte[] data = ReadArrayBytes(field);
            int stride = data.Length / corners.Length;
            byte[] first = new byte[stride];
            foreach (int p in starts)
            {
                var quad = data.AsSpan(p * stride, 4 * stride);
                quad[..stride].CopyTo(first);
                quad[stride..].CopyTo(quad);
                first.CopyTo(quad[(3 * stride)..]);
            }
            StoreArray(field, data);
        }
        foreach (int p in starts)
            (corners[p], corners[p + 1], corners[p + 2], corners[p + 3]) =
                (corners[p + 1], corners[p + 2], ~corners[p + 3], ~corners[p]);
        StoreArray(polygon, MemoryMarshal.AsBytes(corners.AsSpan()));
        return true;
    }

    private static int ArrayCount(Node n) => BinaryPrimitives.ReadInt32LittleEndian(n.Properties.AsSpan(1));

    private static Node[] MappedArrays(Node layer, string reference)
    {
        var fields = layer.Children.Where(n => n.PropertyCount == 1 && n.Properties.Length >= 13 &&
            (char)n.Properties[0] is 'd' or 'f' or 'i' or 'l' or 'b' or 'c');
        return reference switch
        {
            "Direct" => fields.ToArray(),
            "IndexToDirect" or "Index" => fields.Where(n => n.Name.EndsWith("Index", StringComparison.Ordinal) ||
                n.Name is "Materials" or "Textures" or "PolygonGroup").ToArray(),
            _ => []
        };
    }

    private static int ElementSize(Node field) => (char)field.Properties[0] switch { 'd' or 'l' => 8, 'f' or 'i' => 4, _ => 1 };

    private static byte[] ReadArrayBytes(Node field) => FbxNumericArray.Decode<byte>(field.Properties.AsSpan(13),
        checked(ArrayCount(field) * ElementSize(field)), BinaryPrimitives.ReadInt32LittleEndian(field.Properties.AsSpan(5)));

    private static void StoreArray(Node node, ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        output.Write(node.Properties, 0, 13);
        using (var compressed = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compressed.Write(data);
        node.Properties = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(node.Properties.AsSpan(1), data.Length / ElementSize(node));
        BinaryPrimitives.WriteInt32LittleEndian(node.Properties.AsSpan(5), 1);
        BinaryPrimitives.WriteInt32LittleEndian(node.Properties.AsSpan(9), node.Properties.Length - 13);
    }
}
