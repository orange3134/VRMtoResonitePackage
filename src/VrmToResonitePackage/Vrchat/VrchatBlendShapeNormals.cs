using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

internal static class VrchatBlendShapeNormals
{
    internal static int? CalculationMode(string meta)
    {
        var settings = UnityYaml.ParseFlatDocument(meta)?["ModelImporter"]?["tangentSpace"];
        return settings?["blendShapeNormalImportMode"]?.AsInt(-1) == 1
            ? settings["normalCalculationMode"]?.AsInt(4) ?? 4 : null;
    }

    public static async Task<int> Apply(Slot root, Slot assets, VrchatAvatar avatar, UnityPackage package,
        IReadOnlyDictionary<Slot, string> sources, IReadOnlyDictionary<string, Slot> authoredObjects)
    {
        var modes = new Dictionary<string, int?>();
        var providers = new Dictionary<(StaticMesh, int), StaticMesh>();
        int changed = 0;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray())
        {
            string objectKey = authoredObjects.FirstOrDefault(p => p.Value == renderer.Slot).Key;
            var copy = objectKey == null ? null : avatar.MeshCopies.FirstOrDefault(c => c.Transform?.GameObjectKey == objectKey);
            string guid = copy?.FbxGuid ?? VrchatSceneSetup.FbxGuidForSlot(root, renderer.Slot, avatar, sources);
            if (guid == null || renderer.Mesh.Target is not StaticMesh original || renderer.Mesh.Asset?.Data == null) continue;
            if (!modes.TryGetValue(guid, out int? mode))
            {
                string meta = package.ByGuid(guid)?.MetaPath;
                modes[guid] = mode = meta != null && File.Exists(meta) ? CalculationMode(File.ReadAllText(meta)) : null;
            }
            if (mode == null) continue;
            if (providers.TryGetValue((original, mode.Value), out var cached))
            {
                renderer.Mesh.Target = cached;
                continue;
            }
            var asset = renderer.Mesh.Asset;
            var engine = renderer.Engine;
            await default(ToBackground);
            var readLock = new object();
            await asset.RequestReadLock(readLock).ConfigureAwait(false);
            MeshX mesh;
            try { mesh = new MeshX(asset.Data); }
            finally { asset.ReleaseReadLock(readLock); }
            int frames = Recalculate(mesh, mode.Value);
            Uri uri = frames > 0 ? await engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false) : null;
            await default(ToWorld);
            if (uri == null)
            {
                providers[(original, mode.Value)] = original;
                continue;
            }
            // Other renderers can share this provider but have different importer settings.
            var provider = assets.AddSlot($"Blendshape normals: {renderer.Slot.Name}").AttachComponent<StaticMesh>();
            provider.URL.Value = uri;
            renderer.Mesh.Target = provider;
            providers[(original, mode.Value)] = provider;
            changed++;
            UniLog.Log($"Recalculated {frames} blendshape normal frame(s) on {renderer.Slot.Name} (Unity Calculate).");
        }
        // Superseded providers can unload once no renderer uses them. Wait only for
        // the new providers, not all assets (which also include those old providers).
        var replacements = providers.Where(p => p.Key.Item1 != p.Value).Select(p => p.Value).Distinct().ToArray();
        for (int frame = 0; frame < 7200 && replacements.Any(p => !p.IsAssetAvailable); frame++)
            await default(NextUpdate);
        if (replacements.Any(p => !p.IsAssetAvailable))
            throw new InvalidOperationException("Recalculated blendshape meshes did not finish loading.");
        return changed;
    }

    internal static int Recalculate(MeshX mesh, int calculationMode)
    {
        if (!mesh.HasNormals || mesh.BlendShapeCount == 0) return 0;
        var triangles = mesh.Triangles.Select(t => (A: t.Vertex0IndexUnsafe, B: t.Vertex1IndexUnsafe, C: t.Vertex2IndexUnsafe)).ToArray();
        int count = mesh.VertexCount, changed = 0;
        foreach (var shape in mesh.BlendShapes)
        foreach (var frame in shape.Frames)
        {
            var moved = Enumerable.Range(0, count).Select(i => frame.RawPositions[i] != float3.Zero).ToArray();
            if (!moved.Any(v => v)) continue;
            var positions = Enumerable.Range(0, count).Select(i => mesh.RawPositions[i] + frame.RawPositions[i]).ToArray();
            var sums = new float3[count];
            var affected = new bool[count];
            foreach (var (a, b, c) in triangles)
            {
                bool changes = moved[a] || moved[b] || moved[c];
                affected[a] |= changes; affected[b] |= changes; affected[c] |= changes;
                var cross = MathX.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                float area = cross.Magnitude;
                if (!(area > 1e-20f) || !float.IsFinite(area)) continue;
                var normal = cross / area;
                Add(a, b, c); Add(b, c, a); Add(c, a, b);
                void Add(int vertex, int next, int last)
                {
                    float weight = calculationMode is 2 or 4 ? area : 1f;
                    if (calculationMode is 3 or 4)
                    {
                        var u = positions[next] - positions[vertex]; var v = positions[last] - positions[vertex];
                        weight *= MathF.Atan2(MathX.Cross(u, v).Magnitude, MathX.Dot(u, v));
                    }
                    sums[vertex] += normal * weight;
                }
            }
            // Smooth UV seams only when both rest normals and rest/target positions agree.
            // In particular, do not merge different morph endpoints of collapsed helpers.
            var groups = Enumerable.Range(0, count).GroupBy(i => (mesh.RawPositions[i], mesh.RawNormals[i], positions[i]));
            shape.HasNormals = true;
            foreach (var group in groups)
            {
                if (!group.Any(i => affected[i])) continue;
                float3 sum = float3.Zero;
                foreach (int i in group) sum += sums[i];
                float length = sum.Magnitude;
                if (!(length > 1e-20f) || !float.IsFinite(length)) continue;
                var normal = sum / length;
                foreach (int i in group) frame.RawNormals[i] = normal - mesh.RawNormals[i];
            }
            // Keep authored deltas on untouched geometry. Recalculating every vertex would
            // add the same rest-normal correction once for each simultaneously active shape.
            changed++;
        }
        return changed;
    }
}
