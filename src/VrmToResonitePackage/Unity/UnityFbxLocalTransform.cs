using System.Numerics;

namespace VrmToResonitePackage.Unity;

// Independent implementation of the FBX local transform formula, transposed for row vectors.
// Concept reference (no SDK code copied): Autodesk FBX Developer Help, Computing transformation matrices:
// https://help.autodesk.com/cloudhelp/2020/ENU/FBX-Developer-Help/files/nodes_and_scene_graph/fbx_nodes/FBX_Developer_Help_nodes_and_scene_graph_fbx_nodes_computing_transformation_matrix_html.html
// Assimp can omit small rotation/scale properties as identity. Read the authored values without
// that optimization for metadata projection; never derive these values from Unity expectations.
internal static class UnityFbxLocalTransform
{
    internal static bool TryRead(IEnumerable<IReadOnlyList<object>> properties, out Matrix4x4 matrix, bool leafMesh = false)
    {
        try { return TryReadCore(properties, out matrix, leafMesh); }
        catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException or InvalidCastException)
        {
            // Unsupported metadata must not discard otherwise valid skin/blendshape data.
            matrix = Matrix4x4.Identity;
            return false;
        }
    }

    private static bool TryReadCore(IEnumerable<IReadOnlyList<object>> properties, out Matrix4x4 matrix, bool leafMesh)
    {
        matrix = Matrix4x4.Identity;
        var values = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
        foreach (var property in properties ?? Array.Empty<IReadOnlyList<object>>())
            if (property.Count >= 5 && property[0] is string name && !values.TryAdd(name, property)) return false;
        int order = values.TryGetValue("RotationOrder", out var entry) ? Convert.ToInt32(entry[4]) : 0;
        if (order is < 0 or > 5) return false; // Spheric order is not Euler.
        Vector3 V(string name, Vector3 fallback = default)
        {
            if (!values.TryGetValue(name, out var p)) return fallback;
            if (p.Count != 7) throw new InvalidDataException("Invalid FBX vector: " + name);
            var result = new Vector3(Convert.ToSingle(p[4]), Convert.ToSingle(p[5]), Convert.ToSingle(p[6]));
            if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
                throw new InvalidDataException("Nonfinite FBX vector: " + name);
            return result;
        }
        // Geometric transforms affect node attributes only. Retain the importer path for
        // those models until the separate geometry frame is represented in the projection.
        if (V("GeometricTranslation") != Vector3.Zero || V("GeometricRotation") != Vector3.Zero ||
            V("GeometricScaling", Vector3.One) != Vector3.One) return false;
        var rp = V("RotationPivot"); var sp = V("ScalingPivot");
        matrix = Matrix4x4.CreateTranslation(-sp) * Matrix4x4.CreateScale(V("Lcl Scaling", Vector3.One)) *
            Matrix4x4.CreateTranslation(sp) * Matrix4x4.CreateTranslation(V("ScalingOffset")) *
            Matrix4x4.CreateTranslation(-rp) * Matrix4x4.Transpose(Rotation(V("PostRotation"), 0)) *
            Rotation(V("Lcl Rotation"), order) * Rotation(V("PreRotation"), 0) *
            Matrix4x4.CreateTranslation(rp) * Matrix4x4.CreateTranslation(V("RotationOffset")) *
            Matrix4x4.CreateTranslation(V("Lcl Translation"));
        // Unity retains an unskinned leaf mesh's shared rotation/scaling pivot as its object origin,
        // baking the inverse into its vertices instead of leaving the object at the FBX origin.
        if (leafMesh && rp == sp) matrix = Matrix4x4.CreateTranslation(rp) * matrix;
        return true;
    }

    private static Matrix4x4 Rotation(Vector3 degrees, int order)
    {
        var radians = degrees * (MathF.PI / 180f);
        var axes = new[] { Matrix4x4.CreateRotationX(radians.X), Matrix4x4.CreateRotationY(radians.Y),
            Matrix4x4.CreateRotationZ(radians.Z) };
        int[] indices = order switch { 0 => [0,1,2], 1 => [0,2,1], 2 => [1,2,0],
            3 => [1,0,2], 4 => [2,0,1], 5 => [2,1,0], _ => throw new ArgumentOutOfRangeException(nameof(order)) };
        return axes[indices[0]] * axes[indices[1]] * axes[indices[2]];
    }
}
