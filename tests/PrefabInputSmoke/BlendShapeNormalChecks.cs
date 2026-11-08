using Elements.Assets;
using Elements.Core;
using VrmToResonitePackage.Vrchat;

internal static class BlendShapeNormalChecks
{
    public static void Run()
    {
        string Meta(int mode) => $"ModelImporter:\n  tangentSpace:\n    blendShapeNormalImportMode: {mode}\n    normalCalculationMode: 4\n";
        Require(VrchatBlendShapeNormals.CalculationMode(Meta(1)) == 4 &&
            VrchatBlendShapeNormals.CalculationMode(Meta(0)) == null &&
            VrchatBlendShapeNormals.CalculationMode(Meta(2)) == null &&
            VrchatBlendShapeNormals.CalculationMode("guid: example") == null, "only explicit Calculate requests normal regeneration");
        foreach (int mode in new[] { 0, 1, 2, 3, 4 })
        {
            var mesh = new MeshX { HasNormals = true };
            // A folded helper has zero-area triangles and invalid shading in its rest pose.
            for (int i = 0; i < 4; i++) mesh.AddVertex(float3.Zero);
            mesh.RawNormals[0] = new float3(1, 0, 0);
            mesh.AddVertex(new float3(3, 0, 0)); mesh.AddVertex(new float3(4, 0, 0)); mesh.AddVertex(new float3(3, 1, 0));
            for (int i = 4; i < 7; i++) mesh.RawNormals[i] = new float3(0, 1, 0); // authored custom normals
            var triangles = mesh.AddSubmesh<TriangleSubmesh>();
            triangles.AddTriangle(0, 1, 2); triangles.AddTriangle(0, 2, 3); triangles.AddTriangle(4, 5, 6);
            var open = mesh.AddBlendShape("Open helper").AddFrame(1);
            open.RawPositions[1] = new float3(1, 0, 0);
            open.RawPositions[2] = new float3(1, 1, 0);
            open.RawPositions[3] = new float3(0, 1, 0);
            var other = mesh.AddBlendShape("Unrelated expression"); other.HasNormals = true;
            var otherFrame = other.AddFrame(1); otherFrame.RawPositions[4] = new float3(0, 0, .1f);
            var empty = mesh.AddBlendShape("Empty").AddFrame(1);
            var basePositions = mesh.RawPositions.Take(mesh.VertexCount).ToArray();
            var baseNormals = mesh.RawNormals.Take(mesh.VertexCount).ToArray();
            var deltas = open.RawPositions.Take(mesh.VertexCount).ToArray();
            Require(VrchatBlendShapeNormals.Recalculate(mesh, mode) == 2, "only moving frames processed");
            for (int i = 0; i < 4; i++)
            {
                Require((mesh.RawNormals[i] + open.RawNormals[i] - new float3(0, 0, 1)).Magnitude < 1e-5f,
                    "expanded helper gets its geometric normal, including stationary adjacent vertices");
                Require(otherFrame.RawNormals[i] == float3.Zero, "unrelated active shapes cannot accumulate helper rest-normal corrections");
            }
            for (int i = 4; i < 7; i++) Require(open.RawNormals[i] == float3.Zero, "untouched custom normals preserved");
            Require(basePositions.SequenceEqual(mesh.RawPositions.Take(mesh.VertexCount)) &&
                baseNormals.SequenceEqual(mesh.RawNormals.Take(mesh.VertexCount)) &&
                deltas.SequenceEqual(open.RawPositions.Take(mesh.VertexCount)), "normal repair preserves base geometry and morph positions");
            Require(!empty.BlendShape.HasNormals, "empty indexed placeholders stay empty");
            var normals = open.RawNormals.Take(mesh.VertexCount).ToArray();
            VrchatBlendShapeNormals.Recalculate(mesh, mode);
            Require(normals.SequenceEqual(open.RawNormals.Take(mesh.VertexCount)), "recalculation is idempotent");
        }
        Console.WriteLine("PASS: Calculated blendshape normals restore collapsed helpers and preserve unrelated shapes");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
