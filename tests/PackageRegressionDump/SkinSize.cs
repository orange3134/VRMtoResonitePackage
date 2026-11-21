using Elements.Assets;
using Elements.Core;

internal sealed record SkinSize(float[] sizeMeters, int weightedVertices, string reason = null)
{
    internal static SkinSize Measure(RecordPackage package, string url, string[] boneIds,
        IReadOnlyDictionary<string, float4x4> world)
    {
        try
        {
            if (!Uri.TryCreate(url?.TrimStart('@'), UriKind.Absolute, out var uri) || uri.Scheme != "packdb")
                return new(null, 0, "missing embedded StaticMesh");
            using var input = package.ReadAsset(RecordPackage.GetAssetSignature(uri));
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            buffer.Position = 0;
            var mesh = new MeshX();
            mesh.Decode(buffer);
            if (!mesh.HasBoneBindings) return new(null, 0, "no bone bindings");
            var frames = new float4x4?[mesh.BoneCount];
            for (int i = 0; i < frames.Length && i < boneIds.Length; i++)
                if (boneIds[i] != null && world.TryGetValue(boneIds[i], out var transform))
                    frames[i] = transform * mesh.GetBone(i).BindPose;
            int count = 0;
            float3 min = float3.Zero, max = float3.Zero;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var binding = mesh.RawBoneBindings[v];
                var source = mesh.GetPosition(v);
                var point = float3.Zero;
                float sum = 0;
                for (int j = 0; j < 4; j++)
                {
                    float weight = binding.GetWeight(j);
                    if (weight == 0) continue;
                    int i = binding.GetBoneIndex(j);
                    if (i < 0 || i >= frames.Length || frames[i] is not {} frame)
                        return new(null, 0, "missing weighted bone");
                    point += frame.TransformPoint3x4(source) * weight;
                    sum += weight;
                }
                if (sum == 0) continue;
                if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
                    return new(null, 0, "nonfinite vertex");
                if (count++ == 0) min = max = point;
                else
                {
                    min = new(Math.Min(min.x, point.x), Math.Min(min.y, point.y), Math.Min(min.z, point.z));
                    max = new(Math.Max(max.x, point.x), Math.Max(max.y, point.y), Math.Max(max.z, point.z));
                }
            }
            var size = max - min;
            return count == 0 ? new(null, 0, "no weighted vertices") : new(new[] { size.x, size.y, size.z }, count);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(null, 0, "cannot decode skin: " + error.Message);
        }
    }
}
