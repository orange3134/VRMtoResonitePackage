using Elements.Assets;
using Elements.Core;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class OutlineExclusionChecks
{
    public static void Run()
    {
        var parent = LilToonConverter.Parse(Doc("- _OutlineDeleteMesh: 1\n- _OutlineVertexR2Width: 2"));
        var inherited = LilToonConverter.Parse(Doc("[]"), parent);
        var disabled = LilToonConverter.Parse(Doc("- _OutlineDeleteMesh: 0\n- _OutlineVertexR2Width: 0"), parent);
        Check(parent.OutlineDeleteMesh && inherited.OutlineDeleteMesh && inherited.OutlineVertexR2Width == 2 &&
            !disabled.OutlineDeleteMesh && disabled.OutlineVertexR2Width == 0 &&
            !LilToonConverter.Parse(Doc("[]")).OutlineDeleteMesh, "outline exclusion inherits variants and respects explicit zero/default off");

        var source = new MeshX { HasUV0s = true, HasNormals = true, HasBoneBindings = true };
        for (int i = 0; i < 9; i++)
        {
            source.AddVertex(new float3(i, i % 2, 0));
            source.RawUV0s[i] = new float2(i < 3 || i == 6 ? 0 : 1, 0);
            source.RawNormals[i] = new float3(0, 0, 1);
            source.RawBoneBindings[i] = new BoneBinding { boneIndex0 = 0, weight0 = 1 };
        }
        var triangles = source.AddSubmesh<TriangleSubmesh>();
        triangles.AddTriangle(0, 1, 2); triangles.AddTriangle(3, 4, 5); triangles.AddTriangle(6, 7, 8);
        source.AddSubmesh<TriangleSubmesh>().AddTriangle(2, 1, 0);
        var frame = source.AddBlendShape("Eye backing off").AddFrame(1);
        frame.RawPositions[0] = new float3(0, 0, -1);
        var mesh = new MeshX(source);
        var split = VrchatMaterialBuilder.SplitOutlineTriangles(mesh, 0, uv => uv.x == 0);
        Check(split == (2, 2) && mesh.GetSubmesh(0).Count == 1 && mesh.GetSubmesh(1).Count == 1 &&
            mesh.GetSubmesh(2).RawIndicies.Take(6).SequenceEqual(new[] { 0, 1, 2, 6, 7, 8 }),
            "zero-width and mixed triangles move to a new submesh; existing material indices and winding stay intact");
        Check(source.GetSubmesh(0).Count == 3 && source.SubmeshCount == 2 &&
            source.RawPositions.Take(9).SequenceEqual(mesh.RawPositions.Take(9)) &&
            source.RawNormals.Take(9).SequenceEqual(mesh.RawNormals.Take(9)) &&
            source.RawUV0s.Take(9).SequenceEqual(mesh.RawUV0s.Take(9)) &&
            source.RawBoneBindings.Take(9).SequenceEqual(mesh.RawBoneBindings.Take(9)) &&
            frame.RawPositions.Take(9).SequenceEqual(mesh.BlendShapes.Single().Frames.Single().RawPositions.Take(9)),
            "source mesh, vertices, UVs, normals, skin weights and morph deltas remain unchanged");
        Check(VrchatMaterialBuilder.SplitOutlineTriangles(mesh, 0, _ => false) == (-1, 0) && mesh.SubmeshCount == 3,
            "unmasked submesh is unchanged");
        Check(VrchatMaterialBuilder.SplitOutlineTriangles(mesh, 2, _ => true) == (2, 2) && mesh.SubmeshCount == 3,
            "fully masked submesh only changes material without an empty or duplicate submesh");
    }

    private static YamlDocument Doc(string floats) => UnityYaml.ParseDocuments(
        "--- !u!21 &2100000\nMaterial:\n  m_Name: Outline test\n  m_SavedProperties:\n    m_Floats:\n" +
        string.Join("\n", floats.Split('\n').Select(line => "    " + line))).Single();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
