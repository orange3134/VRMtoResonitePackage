using System.Runtime.InteropServices;

namespace VrmToResonitePackage.Unity;

internal static partial class UnityFbxMorphSeams
{
    private static bool TriangulateNgons(Node mesh)
    {
        var polygon = mesh.Children.FirstOrDefault(n => n.Name == "PolygonVertexIndex");
        if (polygon?.Values is not [int[] corners] || corners.Length == 0 || corners[^1] >= 0 ||
            Array<double>(mesh, "Vertices") is not {} vertices) return false;
        var replacements = new Dictionary<int, FbxPolygonTessellator.Result>();
        int polygons = 0;
        for (int i = 0, start = 0; i < corners.Length; i++)
        {
            if (corners[i] >= 0) continue;
            int count = i - start + 1;
            if (count > 4 && FbxPolygonTessellator.Triangulate(corners.AsSpan(start, count), vertices) is {} triangles)
                replacements.Add(start, triangles);
            polygons++;
            start = i + 1;
        }
        if (replacements.Count == 0) return false;
        var newCorners = new List<int>(corners.Length);
        var cornerMap = new List<int>(corners.Length);
        var polygonMap = new List<int>(polygons);
        int[] boundary = new int[corners.Length];
        var interior = new List<int>();
        for (int i = 0, start = 0, face = 0; i < corners.Length; i++)
        {
            if (corners[i] >= 0) continue;
            if (replacements.TryGetValue(start, out var tessellation))
            {
                int[] triangles = tessellation.Triangles;
                var exterior = tessellation.Boundary.ToHashSet();
                for (int k = 0; k < tessellation.Boundary.Length; k++)
                {
                    int offset = tessellation.Boundary[k];
                    boundary[start + k] = offset < 0 ? -1 : newCorners.Count + offset;
                }
                var diagonals = new HashSet<(int, int)>();
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    polygonMap.Add(face);
                    for (int j = 0; j < 3; j++)
                    {
                        int a = triangles[t + j], b = triangles[t + (j + 1) % 3];
                        int index = newCorners.Count;
                        if (!exterior.Contains(t + j) && diagonals.Add((Math.Min(a, b), Math.Max(a, b)))) interior.Add(index);
                        int original = corners[start + a];
                        if (original < 0) original = ~original;
                        newCorners.Add(j == 2 ? ~original : original);
                        cornerMap.Add(start + a);
                    }
                }
            }
            else
            {
                polygonMap.Add(face);
                for (int k = start; k <= i; k++)
                {
                    boundary[k] = newCorners.Count;
                    newCorners.Add(corners[k]);
                    cornerMap.Add(k);
                }
            }
            face++;
            start = i + 1;
        }
        var edges = mesh.Children.FirstOrDefault(n => n.Name == "Edges");
        var edgeIndices = edges?.Values.FirstOrDefault() as int[];
        if (edges != null && (edgeIndices == null || edgeIndices.Any(i => (uint)i >= corners.Length))) return false;
        var keptEdges = new List<int>();
        var updatedEdges = new List<int>();
        if (edgeIndices != null)
        {
            // An edge may have been indexed from a removed face. Reanchor shared
            // edges on their surviving neighbour before dropping unused ones.
            if (edgeIndices.Any(i => boundary[i] < 0))
            {
                var endpoints = new (int, int)[corners.Length];
                var survivors = new Dictionary<(int, int), int>();
                for (int i = 0, start = 0; i < corners.Length; i++)
                {
                    int a = corners[i], b = corners[a < 0 ? start : i + 1];
                    if (a < 0) { a = ~a; start = i + 1; }
                    if (b < 0) b = ~b;
                    endpoints[i] = (Math.Min(a, b), Math.Max(a, b));
                    if (boundary[i] >= 0) survivors.TryAdd(endpoints[i], boundary[i]);
                }
                foreach (int i in edgeIndices)
                    if (boundary[i] < 0) boundary[i] = survivors.GetValueOrDefault(endpoints[i], -1);
            }
            for (int i = 0; i < edgeIndices.Length; i++)
                if (boundary[edgeIndices[i]] >= 0)
                {
                    keptEdges.Add(i);
                    updatedEdges.Add(boundary[edgeIndices[i]]);
                }
            updatedEdges.AddRange(interior);
        }
        // Validate every affected layer before changing the mesh. Unknown per-edge
        // data has no defined value on an introduced diagonal.
        List<(Node Field, List<int> Map, int Count)> layers = new();
        List<Node> edgeLayers = new();
        foreach (var layer in mesh.Children.Where(n => n.Name.StartsWith("LayerElement", StringComparison.Ordinal)))
        {
            string Property(string name) => layer.Children.FirstOrDefault(n => n.Name == name)?.Values.FirstOrDefault() as string;
            string mapping = Property("MappingInformationType");
            if (mapping is "ByVertice" or "ByVertex" or "ByControlPoint" or "AllSame" or "NoMappingInformation") continue;
            if (mapping is not ("ByPolygonVertex" or "ByPolygon" or "ByEdge")) return false;
            var selected = MappedArrays(layer, Property("ReferenceInformationType"));
            if (selected.Length == 0) return false;
            foreach (var field in selected)
            {
                if (ArrayCount(field) == 0) continue;
                if (mapping == "ByEdge")
                {
                    if (edgeIndices?.Length != ArrayCount(field) || Property("ReferenceInformationType") != "Direct" ||
                        field.Name is not ("Smoothing" or "EdgeCrease") ||
                        (field.Name == "Smoothing" && (char)field.Properties[0] is not ('i' or 'l' or 'b' or 'c'))) return false;
                    edgeLayers.Add(field);
                }
                else
                {
                    int count = mapping == "ByPolygon" ? polygons : corners.Length;
                    if (ArrayCount(field) % count != 0) return false;
                    layers.Add((field, mapping == "ByPolygon" ? polygonMap : cornerMap, count));
                }
            }
        }
        foreach (var (field, map, count) in layers)
        {
            var data = ReadArrayBytes(field);
            int stride = data.Length / count;
            byte[] output = new byte[checked(map.Count * stride)];
            for (int i = 0; i < map.Count; i++)
                data.AsSpan(map[i] * stride, stride).CopyTo(output.AsSpan(i * stride));
            StoreArray(field, output);
        }
        foreach (var field in edgeLayers)
        {
            int size = ElementSize(field);
            var original = ReadArrayBytes(field);
            var data = new byte[checked(updatedEdges.Count * size)];
            for (int i = 0; i < keptEdges.Count; i++)
                original.AsSpan(keptEdges[i] * size, size).CopyTo(data.AsSpan(i * size));
            if (field.Name == "Smoothing")
                for (int i = keptEdges.Count * size; i < data.Length; i += size) data[i] = 1;
            StoreArray(field, data);
        }
        if (edges != null)
        {
            int[] updated = updatedEdges.ToArray();
            edges.Values = [updated];
            StoreArray(edges, MemoryMarshal.AsBytes(updated.AsSpan()));
        }
        int[] indices = newCorners.ToArray();
        polygon.Values = [indices];
        StoreArray(polygon, MemoryMarshal.AsBytes(indices.AsSpan()));
        return true;
    }
}
