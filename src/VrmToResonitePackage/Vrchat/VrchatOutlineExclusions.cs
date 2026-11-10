using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Unity;
using Vec2 = System.Numerics.Vector2;

namespace VrmToResonitePackage.Vrchat;

internal static partial class VrchatMaterialBuilder
{
    // lilToon discards an outline primitive when a vertex's masked width is zero.
    // XiexeToon still draws the reversed triangle at width zero, exposing dark eye
    // backing surfaces. Keep the surface, skin and morphs; give only these triangles
    // a copy of the same material with its outline disabled.
    internal static async Task ApplyOutlineExclusions(Slot root, Slot assets, UnityPackage package,
        Dictionary<XiexeToonMaterial, LilToonInfo> settings)
    {
        using var profile = ConversionProfile.Start("outline-exclusions");
        if (settings.Count == 0) return;
        var masks = new Dictionary<XiexeToonMaterial, BakeTexture>();
        foreach (var (material, info) in settings)
        {
            // Vertex colors are deliberately not imported on the VRChat path.
            if (info.OutlineVertexR2Width != 0)
            {
                UniLog.Warning($"Outline mesh exclusion requires unsupported vertex colors on {info.Name}.");
                continue;
            }
            await default(ToBackground);
            try
            {
                // lilGetOutlineWidth uses main UV and a fixed linear-repeat sampler at LOD 0.
                masks[material] = ReadBakeTexture(package, info.OutlineMaskGuid,
                    sampler: new BakeSampler(0, 0, false));
            }
            catch (Exception error)
            {
                UniLog.Warning($"Could not read outline mask on {info.Name}: {error.Message}");
            }
            await default(ToWorld);
        }
        var plainMaterials = new Dictionary<XiexeToonMaterial, XiexeToonMaterial>();
        var providers = new List<StaticMesh>();
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>().ToArray())
        {
            var assigned = renderer.Materials.ToArray();
            if (!assigned.OfType<XiexeToonMaterial>().Any(masks.ContainsKey) || renderer.Mesh.Asset?.Data == null)
                continue;
            var asset = renderer.Mesh.Asset;
            // Appended submeshes must not take over an existing extra material slot.
            if (assigned.Length != asset.Data.SubmeshCount)
            {
                UniLog.Warning($"Outline mesh exclusion skipped on {renderer.Slot.Name}: material/submesh count mismatch.");
                continue;
            }
            var engine = renderer.Engine;
            using var meshProfile = ConversionProfile.Start($"outline-mesh:{renderer.Slot.Name}");
            await default(ToBackground);
            var readLock = new object();
            await asset.RequestReadLock(readLock).ConfigureAwait(false);
            MeshX mesh;
            try { mesh = new MeshX(asset.Data); }
            finally { asset.ReleaseReadLock(readLock); }
            meshProfile?.Mark("clone");
            var changes = new List<(int index, XiexeToonMaterial material, int triangles)>();
            for (int i = 0; i < assigned.Length; i++)
            {
                if (assigned[i] is not XiexeToonMaterial material || !masks.TryGetValue(material, out var mask)) continue;
                var info = settings[material];
                var split = SplitOutlineTriangles(mesh, i, uv =>
                {
                    var mainUv = new Vec2(uv.x, uv.y) * info.MainTexScale + info.MainTexOffset;
                    // Mesh UVs use Unity's bottom-left origin; decoded bitmap rows are top-down.
                    mainUv.Y = 1f - mainUv.Y;
                    float width = info.OutlineWidth * 0.01f * (mask?.Sample(mainUv).X ?? 1f);
                    return width > -0.000001f && width < 0.000001f;
                });
                if (split.triangles > 0) changes.Add((split.index, material, split.triangles));
            }
            meshProfile?.Mark("split-triangles");
            Uri uri = changes.Any(c => c.index >= assigned.Length)
                ? await engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false) : null;
            meshProfile?.Mark("save-asset");
            await default(ToWorld);
            if (changes.Count == 0) continue;
            if (uri != null)
            {
                var provider = assets.AddSlot($"Outline exclusions: {renderer.Slot.Name}").AttachComponent<StaticMesh>();
                provider.URL.Value = uri;
                renderer.Mesh.Target = provider;
                providers.Add(provider);
            }
            foreach (var change in changes)
            {
                if (!plainMaterials.TryGetValue(change.material, out var plain))
                {
                    plain = change.material.Slot.Duplicate(assets).GetComponent<XiexeToonMaterial>();
                    plain.Slot.Name += " (no outline)";
                    plain.Outline.Value = XiexeToonMaterial.OutlineStyle.None;
                    plainMaterials[change.material] = plain;
                }
                while (renderer.Materials.Count <= change.index) renderer.Materials.Add();
                renderer.Materials[change.index] = plain;
            }
            UniLog.Log($"Excluded zero-width outlines on {renderer.Slot.Name}: {changes.Sum(c => c.triangles)} triangle(s); surface geometry retained.");
        }
        profile?.Mark("edit-meshes");
        for (int frame = 0; frame < 7200 && providers.Any(p => !p.IsAssetAvailable); frame++)
            await default(NextUpdate);
        if (providers.Any(p => !p.IsAssetAvailable))
            throw new InvalidOperationException("Outline exclusion meshes did not finish loading.");
    }

    internal static (int index, int triangles) SplitOutlineTriangles(MeshX mesh, int submeshIndex,
        Func<float2, bool> discardVertex)
    {
        if (!mesh.HasUV0s || mesh.GetSubmesh(submeshIndex) is not TriangleSubmesh source)
            return (-1, 0);
        var removed = new List<(int triangle, int a, int b, int c)>();
        for (int i = 0; i < source.Count; i++)
        {
            var triangle = source.GetTriangleUnsafe(i);
            int a = triangle.Vertex0IndexUnsafe, b = triangle.Vertex1IndexUnsafe, c = triangle.Vertex2IndexUnsafe;
            if (discardVertex(mesh.GetVertex(a).UV0) || discardVertex(mesh.GetVertex(b).UV0) ||
                discardVertex(mesh.GetVertex(c).UV0)) removed.Add((i, a, b, c));
        }
        if (removed.Count == 0) return (-1, 0);
        // A fully masked submesh needs only a material change, preserving its index.
        if (removed.Count == source.Count) return (submeshIndex, removed.Count);
        int index = mesh.SubmeshCount;
        var plain = mesh.AddSubmesh<TriangleSubmesh>();
        foreach (var triangle in removed) plain.AddTriangle(triangle.a, triangle.b, triangle.c);
        for (int i = removed.Count - 1; i >= 0; i--) source.Remove(removed[i].triangle);
        return (index, removed.Count);
    }
}
