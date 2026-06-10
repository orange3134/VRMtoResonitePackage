using Assimp;
using Assimp.Configs;

namespace VrmToResonitePackage;

/// <summary>
/// Diagnostic: imports a model with the same Assimp configuration Resonite uses and
/// dumps the per-mesh morph target (anim mesh) structure. Useful to spot files where
/// Assimp produces different attachment counts per primitive, which crashes
/// FrooxEngine's submesh merge.
/// </summary>
internal static class AssimpDump
{
    public static void Dump(string file)
    {
        Console.WriteLine($"### Assimp dump: {file}");
        string glbPath = file;
        string tempPath = null;
        if (!file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".glb");
            Vrm.GlbPreprocessor.CreateImportableGlb(file, tempPath);
            glbPath = tempPath;
        }
        try
        {
            var context = new AssimpContext();
            context.SetConfig(new NormalSmoothingAngleConfig(66f));
            context.SetConfig(new TangentSmoothingAngleConfig(10f));
            PostProcessSteps steps = PostProcessSteps.JoinIdenticalVertices
                                     | PostProcessSteps.ImproveCacheLocality
                                     | PostProcessSteps.PopulateArmatureData
                                     | PostProcessSteps.GenerateUVCoords
                                     | PostProcessSteps.FindInstances
                                     | PostProcessSteps.FlipWindingOrder
                                     | PostProcessSteps.LimitBoneWeights;
            Scene scene = context.ImportFile(glbPath, steps);
            Console.WriteLine($"meshes: {scene.MeshCount}");
            for (int i = 0; i < scene.MeshCount; i++)
            {
                Mesh mesh = scene.Meshes[i];
                Console.WriteLine($"[{i}] {mesh.Name}: verts={mesh.VertexCount} animMeshes={mesh.MeshAnimationAttachmentCount}");
                int unnamed = 0;
                var attachmentVertexCounts = new Dictionary<int, int>();
                for (int j = 0; j < mesh.MeshAnimationAttachmentCount; j++)
                {
                    MeshAnimationAttachment attachment = mesh.MeshAnimationAttachments[j];
                    if (string.IsNullOrEmpty(attachment.Name))
                    {
                        unnamed++;
                    }
                    attachmentVertexCounts[attachment.VertexCount] =
                        attachmentVertexCounts.GetValueOrDefault(attachment.VertexCount) + 1;
                }
                if (unnamed > 0)
                {
                    Console.WriteLine($"     unnamed animMeshes: {unnamed}");
                }
                foreach ((int vertexCount, int count) in attachmentVertexCounts)
                {
                    string marker = vertexCount == mesh.VertexCount ? "OK" : "** MISMATCH **";
                    Console.WriteLine($"     animMesh verts={vertexCount} x{count} {marker}");
                }
            }
            context.Dispose();
        }
        finally
        {
            if (tempPath != null)
            {
                File.Delete(tempPath);
            }
        }
    }
}
