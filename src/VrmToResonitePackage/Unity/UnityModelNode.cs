using Assimp;

namespace VrmToResonitePackage.Unity;

// Read-only source metadata from the same import used for stable IDs and skin order.
// No Unity expectations or composed prefab state enter this cache.
internal sealed record UnityModelNode(string Path, string ParentPath, string Name,
    System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale,
    bool HasMesh, bool Skinned, string[] MaterialNames)
{
    internal static IReadOnlyList<UnityModelNode> Capture(Scene scene)
    {
        var nodes = new List<UnityModelNode>();
        var axes = AxisConversion(scene);
        Walk(scene.RootNode, "", System.Numerics.Matrix4x4.Identity);
        return nodes;

        void Walk(Node node, string parent, System.Numerics.Matrix4x4 prefix)
        {
            var m = node.Transform;
            // Transpose Assimp's column-vector matrix into System.Numerics row-vector
            // storage. AssimpNet.Decompose reads rotated nonuniform scales by row.
            var local = new System.Numerics.Matrix4x4(m.A1,m.B1,m.C1,m.D1, m.A2,m.B2,m.C2,m.D2,
                m.A3,m.B3,m.C3,m.D3, m.A4,m.B4,m.C4,m.D4) * prefix;
            if (node.Parent == scene.RootNode) local *= axes;
            if (UnityModelPath.IsTransformHelper(node.Name))
            {
                foreach (var child in node.Children) Walk(child, parent, local);
                return;
            }
            string name = node.Name == "//RootNode" ? "RootNode" : node.Name;
            string path = parent.Length == 0 ? name : parent + "/" + name;
            var mirror = System.Numerics.Matrix4x4.CreateScale(-1, 1, 1);
            // Decompose after handedness conversion: partial quaternion overrides
            // depend on the imported quaternion's sign, even though q/-q rotate alike.
            if (!System.Numerics.Matrix4x4.Decompose(mirror * local * mirror,
                    out var scale, out var rotation, out var position))
                throw new InvalidDataException("Cannot decompose model local transform: " + path);
            var meshes = node.MeshIndices.Select(i => scene.Meshes[i]).ToArray();
            nodes.Add(new(path, parent, name, new(position.X, position.Y, position.Z),
                new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(scale.X, scale.Y, scale.Z),
                meshes.Length != 0, meshes.Any(m => m.HasBones || m.MeshAnimationAttachmentCount > 0),
                meshes.Select(m => scene.Materials[m.MaterialIndex].Name).ToArray()));
            foreach (var child in node.Children) Walk(child, path, System.Numerics.Matrix4x4.Identity);
        }
    }

    private static System.Numerics.Matrix4x4 AxisConversion(Scene scene)
    {
        int Value(string key, int fallback) => scene.Metadata.TryGetValue(key, out var entry)
            ? Convert.ToInt32(entry.Data) : fallback;
        var axes = new[] { Value("CoordAxis", 0), Value("UpAxis", 1), Value("FrontAxis", 2) };
        var signs = new[] { Value("CoordAxisSign", 1), Value("UpAxisSign", 1), Value("FrontAxisSign", 1) };
        if (axes.Distinct().Count() != 3 || axes.Any(a => a < 0 || a > 2) || signs.Any(s => s != -1 && s != 1))
            throw new InvalidDataException("Invalid FBX coordinate axes.");
        var matrix = System.Numerics.Matrix4x4.Identity;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                matrix[row, column] = axes[column] == row ? signs[column] : 0;
        return matrix;
    }
}
