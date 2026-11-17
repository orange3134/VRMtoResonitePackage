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
        Walk(scene.RootNode, "", Matrix4x4.Identity);
        return nodes;

        void Walk(Node node, string parent, Matrix4x4 prefix)
        {
            // AssimpNet's multiplication operator composes in the opposite order
            // to its column-vector storage: preserve the parent's translation.
            var local = node.Transform * prefix;
            if (UnityModelPath.IsTransformHelper(node.Name))
            {
                foreach (var child in node.Children) Walk(child, parent, local);
                return;
            }
            string name = node.Name == "//RootNode" ? "RootNode" : node.Name;
            string path = parent.Length == 0 ? name : parent + "/" + name;
            local.Decompose(out var scale, out var rotation, out var position);
            var meshes = node.MeshIndices.Select(i => scene.Meshes[i]).ToArray();
            nodes.Add(new(path, parent, name, new(position.X, position.Y, position.Z),
                new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(scale.X, scale.Y, scale.Z),
                meshes.Length != 0, meshes.Any(m => m.HasBones || m.MeshAnimationAttachmentCount > 0),
                meshes.Select(m => scene.Materials[m.MaterialIndex].Name).ToArray()));
            foreach (var child in node.Children) Walk(child, path, Matrix4x4.Identity);
        }
    }
}
