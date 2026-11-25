using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Restores blendshapes removed by Resonite ModelImporter.StripEmptyBlendshapes so Unity-authored
/// numeric indices and named expression bindings remain usable after FBX import.
/// </summary>
internal static class VrchatBlendShapeRepair
{
    public static async Task<int> Apply(Slot root, VrchatAvatar avatar,
        IReadOnlyDictionary<Slot, string> sources, IReadOnlyDictionary<string, Slot> authoredObjects,
        IReadOnlyDictionary<Slot, string> importedPaths = null, Slot expressionRoot = null,
        IReadOnlyDictionary<string, Slot> prefabSlots = null)
    {
        using var profile = ConversionProfile.Start("blendshape-repair");
        int repaired = await NormalizeRepeatedBlendShapeNames(root);
        profile?.Mark("normalize-names");
        if (repaired > 0)
        {
            // Changing StaticMesh.URL starts an asynchronous asset reload. Indexed-shape repair
            // needs the normalized mesh data, so do not let the second pass observe Asset.Data as
            // null and silently skip the renderer. The converter's outer asset wait happens only
            // after this method returns and is therefore too late for this dependency.
            await WaitForRendererMeshes(root);
        }
        if (avatar.FbxBlendShapeNames.Count == 0)
        {
            return repaired;
        }

        // Resolve authored animation paths before later hierarchy changes; ambiguous paths stay unresolved.
        var expressionShapes = avatar.Expressions.Clips.SelectMany(c => c.Curves).GroupBy(c => c.Binding.Path)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Binding.Shape).ToHashSet(StringComparer.Ordinal));
        bool allowSuffix = expressionRoot == null;
        expressionRoot ??= root;
        string RelativePath(Slot slot)
        {
            var parts = new Stack<string>();
            for (; slot != null && slot != expressionRoot; slot = slot.Parent) parts.Push(slot.Name);
            return slot == expressionRoot ? string.Join("/", parts) : null;
        }
        var rendererPaths = expressionRoot.GetComponentsInChildren<SkinnedMeshRenderer>()
            .ToDictionary(r => r, r => RelativePath(r.Slot));
        var requestedByRenderer = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
        foreach (var (path, names) in expressionShapes)
        {
            var matches = rendererPaths.Where(pair => pair.Value == path || allowSuffix && path.Length > 0 &&
                pair.Value?.EndsWith("/" + path, StringComparison.Ordinal) == true).Select(pair => pair.Key).ToArray();
            if (matches.Length != 1) continue;
            if (!requestedByRenderer.TryGetValue(matches[0], out var requested))
                requestedByRenderer[matches[0]] = requested = new(StringComparer.Ordinal);
            requested.UnionWith(names);
        }
        // The FBX importer can discard a neutral viseme with no vertex deltas. Its
        // named channel is still part of the descriptor/FX contract.
        foreach (var viseme in avatar.Visemes)
        {
            var slot = viseme.MeshTarget == null || importedPaths == null ? null :
                VrchatSceneSetup.ResolveImportedTarget(viseme.MeshTarget, sources, importedPaths, prefabSlots);
            var matches = rendererPaths.Where(p => slot != null ? p.Key.Slot == slot :
                viseme.MeshGameObjectPath != null ? p.Value == viseme.MeshGameObjectPath :
                p.Key.Slot.Name == viseme.MeshGameObjectName).Select(p => p.Key).ToArray();
            if (matches.Length != 1) continue;
            if (!requestedByRenderer.TryGetValue(matches[0], out var requested))
                requestedByRenderer[matches[0]] = requested = new(StringComparer.Ordinal);
            requested.Add(viseme.BlendShapeName);
        }
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            string objectKey = authoredObjects.FirstOrDefault(entry => entry.Value == renderer.Slot).Key;
            string guid = VrchatSceneSetup.FbxGuidForSlot(root, renderer.Slot, avatar, sources);
            var expected = avatar.BlendShapeNamesFor(guid, renderer.Slot.Name, objectKey,
                importedPaths?.GetValueOrDefault(renderer.Slot));
            if (expected == null || expected.Count == 0 || renderer.Mesh.Target == null || renderer.Mesh.Asset?.Data == null)
            {
                continue;
            }
            var copy = objectKey == null ? null : avatar.MeshCopies.FirstOrDefault(c => c.Transform?.GameObjectKey == objectKey);
            var sourcePath = copy?.SourcePath ?? importedPaths?.GetValueOrDefault(renderer.Slot);
            var aliases = sourcePath == null ? null : avatar.ModelBlendShapeAliasesByPath.GetValueOrDefault(new(copy?.FbxGuid ?? guid, sourcePath));
            int maxReferencedIndex = MaxReferencedBlendShapeIndex(avatar, renderer.Slot.Name);
            var requestedNames = requestedByRenderer.GetValueOrDefault(renderer) ?? new HashSet<string>();
            int requiredCount = Math.Min(expected.Count, maxReferencedIndex + 1);
            if (await RepairMesh(root, renderer, expected, aliases, requiredCount, requestedNames,
                knownSource: sourcePath != null || copy != null)) repaired++;
        }
        return repaired;
    }

    internal static async Task<int> ApplyVrm(Slot root, Vrm.VrmModel model)
    {
        int repaired = 0;
        var slots = root.GetAllChildren().ToLookup(slot => slot.Name);
        var nodeNames = model.NodeNames.ToLookup(name => name);
        foreach (var (meshIndex, nodes) in model.MeshToNodes)
        {
            if (meshIndex < 0 || meshIndex >= model.MeshTargetNames.Count) continue;
            var expected = model.MeshTargetNames[meshIndex];
            if (expected.Count == 0) continue;
            foreach (int node in nodes)
            {
                string name = model.GetNodeName(node);
                if (string.IsNullOrEmpty(name) || nodeNames[name].Count() != 1 || slots[name].Count() != 1) continue;
                var skins = slots[name].Single().GetComponents<SkinnedMeshRenderer>().ToArray();
                if (skins.Length == 1 && await RepairMesh(root, skins[0], expected, null, 0, null, true)) repaired++;
            }
        }
        return repaired;
    }

    private static async Task<bool> RepairMesh(Slot root, SkinnedMeshRenderer renderer, IReadOnlyList<string> expected,
        IReadOnlyDictionary<string, string> aliases, int requiredCount, IReadOnlySet<string> requestedNames, bool knownSource)
    {
        if (renderer.Mesh.Target is not StaticMesh staticMesh || renderer.Mesh.Asset?.Data is not MeshX source) return false;
        var imported = source.BlendShapes.Select(s => s.Name).ToArray();
        if (imported.SequenceEqual(expected) && (aliases == null || !imported.Any(aliases.ContainsKey))) return false;
        // Only a source-identified mesh with a unique channel table can supply the entire order.
        // A name-only lookup can describe a different renderer elsewhere in the hierarchy.
        var order = knownSource && expected.All(n => !string.IsNullOrEmpty(n)) &&
            expected.Distinct(StringComparer.Ordinal).Count() == expected.Count
            ? expected.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal) : null;
        if (order != null && imported.Any(name => !order.ContainsKey(aliases?.GetValueOrDefault(name, name) ?? name))) order = null;
        if (order == null && requiredCount == 0 && (aliases == null || !imported.Any(aliases.ContainsKey)) &&
            (requestedNames == null || !expected.Any(name => requestedNames.Contains(name) && !source.HasBlendShape(name)))) return false;

        using var meshProfile = ConversionProfile.Start($"repair-mesh:{renderer.Slot.Name}");

        MeshX mesh;
        var oldWeights = new Dictionary<string, Queue<float>>(StringComparer.Ordinal);
        for (int i = 0; i < renderer.MeshBlendshapeCount; i++)
        {
            string name = renderer.BlendShapeName(i);
            name = aliases?.GetValueOrDefault(name, name) ?? name;
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
        meshProfile?.Mark("clone");

        int renamed = RestoreChannelNames(mesh, aliases);
        if (renamed < 0)
        {
            UniLog.Warning($"Ambiguous FBX channel names on {renderer.Slot.Name}; mesh left unchanged.");
            await default(ToWorld);
            return false;
        }
        int inserted;
        if (order != null)
        {
            inserted = 0;
            foreach (string name in expected)
                if (!mesh.HasBlendShape(name))
                {
                    mesh.AddBlendShape(name).AddFrame(1f);
                    inserted++;
                }
            mesh.SortBlendshapes((a, b) => order[a.Name].CompareTo(order[b.Name]));
        }
        else
        {
            inserted = InsertMissingBlendShapes(mesh, expected, requiredCount);
        }
        if (inserted < 0)
        {
            LogSequenceMismatch(renderer.Slot.Name, mesh, expected.Take(requiredCount).ToList());
            await default(ToWorld);
            return false;
        }
        if (order == null && requestedNames != null) inserted += InsertMissingNamedBlendShapes(mesh, expected, requestedNames);
        if (inserted == 0 && renamed == 0 && order == null)
        {
            await default(ToWorld);
            return false;
        }
        List<string> repairedNames = mesh.BlendShapes.Select(shape => shape.Name).ToList();
        meshProfile?.Mark("edit-shapes");

        Uri uri = await MeshAssetWriter.SaveAsync(renderer.Engine.LocalDB, mesh).ConfigureAwait(false);
        meshProfile?.Mark("save-asset");
        await default(ToWorld);
        if (uri == null)
        {
            return false;
        }

        if (root.GetComponentsInChildren<MeshRenderer>().Any(r => r != renderer && r.Mesh.Target == staticMesh))
        {
            var provider = staticMesh.Slot.AddSlot("Restored blendshapes").AttachComponent<StaticMesh>();
            provider.URL.Value = uri;
            renderer.Mesh.Target = provider;
        }
        else staticMesh.URL.Value = uri;
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

        UniLog.Log($"Restored blendshape order on {renderer.Slot.Name} ({inserted} inserted, {renamed} renamed).");
        return true;
    }

    private static async Task WaitForRendererMeshes(Slot root)
    {
        const int timeoutFrames = 60 * 120; // ~2 minutes at 60 ticks
        for (int frame = 0; frame < timeoutFrames; frame++)
        {
            bool allReady = root.GetComponentsInChildren<SkinnedMeshRenderer>()
                .All(renderer => renderer.Mesh.Target == null ||
                                 renderer.Mesh.Target.IsAssetAvailable);
            if (allReady)
            {
                return;
            }
            await default(NextUpdate);
        }
        UniLog.Warning("Timed out waiting for normalized blendshape meshes to reload; " +
                       "indexed blendshape repair may be incomplete.");
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
                var source = renderer.Mesh.Asset.Data;
                var names = NormalizedNames(source);
                mesh = source.BlendShapes.Select(s => s.Name).SequenceEqual(names)
                    ? null : new MeshX(source);
            }
            finally
            {
                renderer.Mesh.Asset.ReleaseReadLock(readLock);
            }

            int renamed = mesh == null ? 0 : NormalizeRepeatedBlendShapeNames(mesh);
            if (renamed == 0)
            {
                await default(ToWorld);
                continue;
            }

            Uri uri = await MeshAssetWriter.SaveAsync(renderer.Engine.LocalDB, mesh).ConfigureAwait(false);
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
        var names = NormalizedNames(mesh);
        int renamed = mesh.BlendShapes.Where((shape, index) => shape.Name != names[index]).Count();
        if (renamed == 0) return 0;
        var snapshots = mesh.BlendShapes.Select((shape, index) =>
            BlendShapeSnapshot.Capture(shape, names[index])).ToList();
        while (mesh.BlendShapeCount > 0)
            mesh.RemoveBlendShape(mesh.BlendShapeCount - 1);
        foreach (BlendShapeSnapshot snapshot in snapshots) snapshot.Restore(mesh);
        return renamed;
    }

    private static List<string> NormalizedNames(MeshX mesh)
    {
        var names = new List<string>(mesh.BlendShapeCount);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < mesh.BlendShapeCount; i++)
        {
            BlendShape shape = mesh.GetBlendShape(i);
            string normalized = BlendShapeNameNormalizer.CollapseRepeatedName(shape.Name);
            while (!used.Add(normalized))
            {
                normalized += "_";
            }
            names.Add(normalized);
        }

        return names;
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

    internal static int RestoreChannelNames(MeshX mesh, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases == null || aliases.Count == 0) return 0;
        var snapshots = new List<BlendShapeSnapshot>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int renamed = 0;
        foreach (var shape in mesh.BlendShapes)
        {
            string name = aliases.GetValueOrDefault(shape.Name, shape.Name);
            if (!names.Add(name)) return -1;
            if (name != shape.Name) renamed++;
            snapshots.Add(BlendShapeSnapshot.Capture(shape, name));
        }
        if (renamed == 0) return 0;
        while (mesh.BlendShapeCount > 0) mesh.RemoveBlendShape(mesh.BlendShapeCount - 1);
        foreach (var snapshot in snapshots) snapshot.Restore(mesh);
        return renamed;
    }

    internal static int InsertMissingNamedBlendShapes(MeshX mesh, IReadOnlyList<string> expected, IReadOnlySet<string> requested)
    {
        int inserted = 0;
        foreach (string name in expected.Distinct(StringComparer.Ordinal))
        {
            if (!requested.Contains(name) || mesh.HasBlendShape(name)) continue;
            mesh.AddBlendShape(name).AddFrame(1f);
            inserted++;
        }
        return inserted;
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
