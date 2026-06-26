using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Restores blendshapes removed by Resonite ModelImporter.StripEmptyBlendshapes so Unity-authored
/// numeric blendshape indices still refer to the same entries after FBX import.
/// </summary>
internal static class VrchatBlendShapeRepair
{
    public static async Task<int> Apply(Slot root, VrchatAvatar avatar)
    {
        int repaired = await NormalizeRepeatedBlendShapeNames(root);
        if (avatar.FbxBlendShapeNames.Count == 0)
        {
            return repaired;
        }

        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (!avatar.FbxBlendShapeNames.TryGetValue(renderer.Slot.Name, out IReadOnlyList<string> expected) ||
                expected.Count == 0 || renderer.Mesh.Target == null || renderer.Mesh.Asset?.Data == null)
            {
                continue;
            }
            int maxReferencedIndex = MaxReferencedBlendShapeIndex(avatar, renderer.Slot.Name);
            if (maxReferencedIndex < 0)
            {
                continue;
            }
            int requiredCount = Math.Min(expected.Count, maxReferencedIndex + 1);
            if (requiredCount == 0)
            {
                continue;
            }
            UniLog.Log($"Checking indexed blendshapes on {renderer.Slot.Name}: " +
                       $"required through {requiredCount - 1}, FBX={expected.Count}, " +
                       $"imported={renderer.MeshBlendshapeCount}.");

            MeshX mesh;
            var oldWeights = new Dictionary<string, Queue<float>>(StringComparer.Ordinal);
            for (int i = 0; i < renderer.MeshBlendshapeCount; i++)
            {
                string name = renderer.BlendShapeName(i);
                if (!oldWeights.TryGetValue(name, out Queue<float> values))
                {
                    values = new Queue<float>();
                    oldWeights.Add(name, values);
                }
                values.Enqueue(i < renderer.BlendShapeWeights.Count ? renderer.BlendShapeWeights[i] : 0f);
            }

            await default(ToBackground);
            object readLock = new();
            await renderer.Mesh.Asset.RequestReadLock(readLock).ConfigureAwait(false);
            try
            {
                mesh = new MeshX(renderer.Mesh.Asset.Data);
            }
            finally
            {
                renderer.Mesh.Asset.ReleaseReadLock(readLock);
            }

            int inserted = InsertMissingBlendShapes(mesh, expected, requiredCount);
            if (inserted < 0)
            {
                LogSequenceMismatch(renderer.Slot.Name, mesh, expected.Take(requiredCount).ToList());
                await default(ToWorld);
                continue;
            }
            if (inserted == 0)
            {
                await default(ToWorld);
                continue;
            }
            List<string> repairedNames = mesh.BlendShapes.Select(shape => shape.Name).ToList();

            Uri uri = await renderer.Engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false);
            await default(ToWorld);
            if (uri == null || renderer.Mesh.Target is not StaticMesh staticMesh)
            {
                continue;
            }

            staticMesh.URL.Value = uri;
            while (renderer.BlendShapeWeights.Count > 0)
            {
                renderer.BlendShapeWeights.RemoveAt(renderer.BlendShapeWeights.Count - 1);
            }
            foreach (string name in repairedNames)
            {
                float value = oldWeights.TryGetValue(name, out Queue<float> values) && values.Count > 0
                    ? values.Dequeue()
                    : 0f;
                renderer.BlendShapeWeights.Add();
                renderer.BlendShapeWeights.GetElement(renderer.BlendShapeWeights.Count - 1).Value = value;
            }

            repaired++;
            UniLog.Log($"Restored {inserted} stripped blendshape(s) on {renderer.Slot.Name}.");
        }
        return repaired;
    }

    private static async Task<int> NormalizeRepeatedBlendShapeNames(Slot root)
    {
        int repaired = 0;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer.Mesh.Target == null ||
                renderer.Mesh.Asset?.Data == null ||
                renderer.MeshBlendshapeCount == 0)
            {
                continue;
            }

            MeshX mesh;
            await default(ToBackground);
            object readLock = new();
            await renderer.Mesh.Asset.RequestReadLock(readLock).ConfigureAwait(false);
            try
            {
                mesh = new MeshX(renderer.Mesh.Asset.Data);
            }
            finally
            {
                renderer.Mesh.Asset.ReleaseReadLock(readLock);
            }

            int renamed = NormalizeRepeatedBlendShapeNames(mesh);
            if (renamed == 0)
            {
                await default(ToWorld);
                continue;
            }

            Uri uri = await renderer.Engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false);
            await default(ToWorld);
            if (uri == null || renderer.Mesh.Target is not StaticMesh staticMesh)
            {
                continue;
            }

            staticMesh.URL.Value = uri;
            repaired++;
            UniLog.Log($"Normalized {renamed} repeated blendshape name(s) on {renderer.Slot.Name}.");
        }
        return repaired;
    }

    private static int NormalizeRepeatedBlendShapeNames(MeshX mesh)
    {
        var snapshots = new List<BlendShapeSnapshot>(mesh.BlendShapeCount);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int renamed = 0;
        for (int i = 0; i < mesh.BlendShapeCount; i++)
        {
            BlendShape shape = mesh.GetBlendShape(i);
            string normalized = BlendShapeNameNormalizer.CollapseRepeatedName(shape.Name);
            while (!used.Add(normalized))
            {
                normalized += "_";
            }
            if (!string.Equals(shape.Name, normalized, StringComparison.Ordinal))
            {
                renamed++;
            }
            snapshots.Add(BlendShapeSnapshot.Capture(shape, normalized));
        }

        if (renamed == 0)
        {
            return 0;
        }

        while (mesh.BlendShapeCount > 0)
        {
            mesh.RemoveBlendShape(mesh.BlendShapeCount - 1);
        }
        foreach (BlendShapeSnapshot snapshot in snapshots)
        {
            snapshot.Restore(mesh);
        }
        return renamed;
    }

    private static int MaxReferencedBlendShapeIndex(VrchatAvatar avatar, string rendererName)
    {
        int max = avatar.Blink?.MeshGameObjectName == rendererName
            ? avatar.Blink.BlendShapeIndex
            : -1;
        foreach (VrchatRendererMaterials renderer in avatar.RendererMaterials)
        {
            if (!string.Equals(renderer.RendererGameObjectName, rendererName, StringComparison.Ordinal))
            {
                continue;
            }
            foreach ((int index, _) in renderer.InitialBlendShapes)
            {
                max = Math.Max(max, index);
            }
        }
        return max;
    }

    private static void LogSequenceMismatch(string rendererName, MeshX mesh, IReadOnlyList<string> expected)
    {
        var current = Enumerable.Range(0, mesh.BlendShapeCount)
            .Select(i => mesh.GetBlendShape(i).Name)
            .ToList();
        int importedIndex = 0;
        for (int expectedIndex = 0; expectedIndex < expected.Count && importedIndex < current.Count; expectedIndex++)
        {
            if (string.Equals(expected[expectedIndex], current[importedIndex], StringComparison.Ordinal))
            {
                importedIndex++;
                continue;
            }
            if (current.Skip(importedIndex).Contains(expected[expectedIndex], StringComparer.Ordinal))
            {
                UniLog.Warning($"Blendshape order mismatch on {rendererName}: expected[{expectedIndex}]=" +
                               $"'{expected[expectedIndex]}', imported[{importedIndex}]='{current[importedIndex]}'.");
                return;
            }
        }
        UniLog.Warning($"Could not restore stripped blendshapes on {rendererName}: " +
                       $"FBX={expected.Count}, imported={current.Count}, matched={importedIndex}.");
    }

    private static int InsertMissingBlendShapes(
        MeshX mesh, IReadOnlyList<string> expected, int requiredCount)
    {
        int inserted = 0;
        int current = 0;
        for (int expectedIndex = 0; expectedIndex < requiredCount; expectedIndex++)
        {
            string expectedName = expected[expectedIndex];
            if (current < mesh.BlendShapeCount &&
                string.Equals(mesh.GetBlendShape(current).Name, expectedName, StringComparison.Ordinal))
            {
                current++;
                continue;
            }

            int later = -1;
            for (int i = current + 1; i < mesh.BlendShapeCount; i++)
            {
                if (string.Equals(mesh.GetBlendShape(i).Name, expectedName, StringComparison.Ordinal))
                {
                    later = i;
                    break;
                }
            }
            if (later >= 0)
            {
                // Current imported entries are not a subsequence of the FBX order. Avoid corrupting
                // an unfamiliar mesh layout.
                return -1;
            }

            if (mesh.HasBlendShape(expectedName))
            {
                return -1;
            }
            mesh.InsertBlendshapeAt(expectedName, expectedIndex).AddFrame(1f);
            inserted++;
            current++;
        }
        return inserted;
    }

    private sealed class BlendShapeSnapshot
    {
        private readonly string _name;
        private readonly bool _hasNormals;
        private readonly bool _hasTangents;
        private readonly List<FrameSnapshot> _frames = new();

        private BlendShapeSnapshot(string name, bool hasNormals, bool hasTangents)
        {
            _name = name;
            _hasNormals = hasNormals;
            _hasTangents = hasTangents;
        }

        public static BlendShapeSnapshot Capture(BlendShape shape, string name)
        {
            var snapshot = new BlendShapeSnapshot(name, shape.HasNormals, shape.HasTangents);
            foreach (BlendShapeFrame frame in shape.Frames)
            {
                snapshot._frames.Add(FrameSnapshot.Capture(frame, shape.HasNormals, shape.HasTangents));
            }
            return snapshot;
        }

        public void Restore(MeshX mesh)
        {
            BlendShape shape = mesh.AddBlendShape(_name);
            shape.HasNormals = _hasNormals;
            shape.HasTangents = _hasTangents;
            foreach (FrameSnapshot frame in _frames)
            {
                frame.Restore(shape);
            }
        }
    }

    private sealed class FrameSnapshot
    {
        private readonly float _weight;
        private readonly Elements.Core.float3[] _positions;
        private readonly Elements.Core.float3[] _normals;
        private readonly Elements.Core.float3[] _tangents;

        private FrameSnapshot(
            float weight,
            Elements.Core.float3[] positions,
            Elements.Core.float3[] normals,
            Elements.Core.float3[] tangents)
        {
            _weight = weight;
            _positions = positions;
            _normals = normals;
            _tangents = tangents;
        }

        public static FrameSnapshot Capture(BlendShapeFrame frame, bool hasNormals, bool hasTangents)
        {
            return new FrameSnapshot(
                frame.Weight,
                Copy(frame.RawPositions),
                hasNormals ? Copy(frame.RawNormals) : null,
                hasTangents ? Copy(frame.RawTangents) : null);
        }

        public void Restore(BlendShape shape)
        {
            BlendShapeFrame frame = shape.AddFrame(_weight);
            Array.Copy(_positions, frame.RawPositions, _positions.Length);
            if (_normals != null)
            {
                Array.Copy(_normals, frame.RawNormals, _normals.Length);
            }
            if (_tangents != null)
            {
                Array.Copy(_tangents, frame.RawTangents, _tangents.Length);
            }
        }

        private static Elements.Core.float3[] Copy(Elements.Core.float3[] source)
        {
            if (source == null)
            {
                return null;
            }
            var copy = new Elements.Core.float3[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }
}
