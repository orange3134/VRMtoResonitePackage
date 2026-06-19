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
        if (avatar.FbxBlendShapeNames.Count == 0)
        {
            return 0;
        }

        int repaired = 0;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (!avatar.FbxBlendShapeNames.TryGetValue(renderer.Slot.Name, out IReadOnlyList<string> expected) ||
                expected.Count == 0 || renderer.Mesh.Target == null || renderer.Mesh.Asset?.Data == null)
            {
                continue;
            }
            if (expected.Count == renderer.MeshBlendshapeCount)
            {
                continue;
            }
            UniLog.Log($"Checking stripped blendshapes on {renderer.Slot.Name}: " +
                       $"FBX={expected.Count}, imported={renderer.MeshBlendshapeCount}.");

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

            int inserted = InsertMissingBlendShapes(mesh, expected);
            if (inserted == 0)
            {
                if (expected.Count != mesh.BlendShapeCount)
                {
                    LogSequenceMismatch(renderer.Slot.Name, mesh, expected);
                }
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
            while (renderer.BlendShapeWeights.Count > 0)
            {
                renderer.BlendShapeWeights.RemoveAt(renderer.BlendShapeWeights.Count - 1);
            }
            foreach (string name in expected)
            {
                float value = oldWeights.TryGetValue(name, out Queue<float> values) && values.Count > 0
                    ? values.Dequeue()
                    : 0f;
                renderer.BlendShapeWeights.Add();
                renderer.BlendShapeWeights[renderer.BlendShapeWeights.Count - 1] = value;
            }

            repaired++;
            UniLog.Log($"Restored {inserted} stripped blendshape(s) on {renderer.Slot.Name}.");
        }
        return repaired;
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

    private static int InsertMissingBlendShapes(MeshX mesh, IReadOnlyList<string> expected)
    {
        int inserted = 0;
        int current = 0;
        for (int expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
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
                return 0;
            }

            if (mesh.HasBlendShape(expectedName))
            {
                return 0;
            }
            mesh.InsertBlendshapeAt(expectedName, expectedIndex).AddFrame(1f);
            inserted++;
            current++;
        }
        return current == mesh.BlendShapeCount ? inserted : 0;
    }
}
