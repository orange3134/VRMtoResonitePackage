using System.Runtime.InteropServices;

namespace VrmToResonitePackage.Unity;

// Windows GLU tessellation of a single FBX polygon. Corner IDs, not positions,
// are returned so UV seams, normals and face materials can be remapped exactly.
internal static class FbxPolygonTessellator
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Begin(uint primitive);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Vertex(nint corner);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Edge(byte boundary);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Error(uint error);

    internal sealed record Result(int[] Triangles, int[] Boundary);

    internal static Result Triangulate(ReadOnlySpan<int> corners, double[] vertices)
    {
        int count = corners.Length;
        var triangles = new List<int>(checked(3 * (count - 2)));
        bool valid = true;
        Begin begin = primitive => valid &= primitive == 4; // GL_TRIANGLES
        Vertex vertex = corner => triangles.Add((int)corner - 1);
        // Requesting edge flags disables GLU's convex triangle-fan shortcut.
        Edge edge = _ => { };
        bool needsIntersection = false;
        Error error = code =>
        {
            if (code == 100156) needsIntersection = true; // GLU_TESS_NEED_COMBINE_CALLBACK
            else valid = false;
        };
        var positions = new (double X, double Y, double Z)[count];
        nint tess = gluNewTess();
        if (tess == 0) return null;
        try
        {
            gluTessCallback(tess, 100100, Marshal.GetFunctionPointerForDelegate(begin));
            gluTessCallback(tess, 100101, Marshal.GetFunctionPointerForDelegate(vertex));
            gluTessCallback(tess, 100103, Marshal.GetFunctionPointerForDelegate(error));
            gluTessCallback(tess, 100104, Marshal.GetFunctionPointerForDelegate(edge));
            gluTessBeginPolygon(tess, 0);
            gluTessBeginContour(tess);
            var position = new double[3];
            for (int i = 0; i < count; i++)
            {
                int index = corners[i] < 0 ? ~corners[i] : corners[i];
                if ((uint)index >= vertices.Length / 3) throw new InvalidDataException("Invalid FBX polygon control point.");
                position[0] = -vertices[index * 3];
                position[1] = vertices[index * 3 + 1];
                position[2] = vertices[index * 3 + 2];
                positions[i] = (position[0], position[1], position[2]);
                gluTessVertex(tess, position, i + 1);
            }
            gluTessEndContour(tess);
            gluTessEndPolygon(tess);
        }
        finally
        {
            gluDeleteTess(tess);
            GC.KeepAlive(begin); GC.KeepAlive(vertex); GC.KeepAlive(edge); GC.KeepAlive(error);
        }
        if (!valid) return null;
        int[] boundary = Enumerable.Repeat(-1, count).ToArray();
        // Unity discards a contour that would require inventing an intersection
        // vertex (including its UVs, skin weights and morph deltas).
        if (needsIntersection) return triangles.Count == 0 ? new([], boundary) : null;
        var emitted = triangles.ToHashSet();
        if (emitted.Any(i => (uint)i >= count) || triangles.Count != 3 * (emitted.Count - 2)) return null;
        // GLU coalesces adjacent identical positions. Keep the corner it actually
        // emitted, including that corner's UV and normal. Map surviving boundary edges.
        int[] representative = new int[count];
        int first = triangles[0];
        int previous = first;
        for (int i = 0; i < count; i++)
        {
            int corner = (first + i) % count;
            if (emitted.Contains(corner)) previous = corner;
            representative[corner] = previous;
        }
        var directed = new Dictionary<(int, int), int>();
        for (int i = 0; i < triangles.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[i + k], b = triangles[i + (k + 1) % 3];
                if (a == b || !directed.TryAdd((a, b), i + k)) return null;
            }
        int next = first;
        for (int step = 0; step < count; step++)
        {
            int i = (first - step + count) % count;
            if (emitted.Contains(i)) next = i;
            else if (positions[i] == positions[next]) representative[i] = next;
            else if (positions[i] != positions[representative[i]]) return null;
        }
        for (int i = 0; i < count; i++)
        {
            int a = representative[i], b = representative[(i + 1) % count];
            if (a != b && !directed.TryGetValue((a, b), out boundary[i])) return null;
        }
        return new(triangles.ToArray(), boundary);
    }

    private const string Glu = "glu32.dll";
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern nint gluNewTess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluDeleteTess(nint tess);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessCallback(nint tess, uint which, nint callback);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessBeginPolygon(nint tess, nint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessBeginContour(nint tess);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessVertex(nint tess, [In] double[] position, nint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessEndContour(nint tess);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport(Glu)] private static extern void gluTessEndPolygon(nint tess);
}
