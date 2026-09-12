namespace VrmToResonitePackage.Unity;

/// <summary>An object in one occurrence of a prefab or model, independent of its display name.</summary>
internal readonly record struct UnityObjectId
{
    public string Occurrence { get; }
    public long FileId { get; }
    public UnityObjectId(string occurrence, long fileId)
    {
        Occurrence = occurrence?.ToLowerInvariant();
        FileId = fileId;
    }
    public bool IsNull => Occurrence == null || FileId == 0;
}

/// <summary>
/// One selected prefab graph. Resolves aliases and folds properties once, base before variant.
/// Source documents remain available for import provenance and deleted-model subtree filtering;
/// conversion enumerates only the effective scenes. No Unity Editor or component scripts run.
/// </summary>
internal sealed class UnityPrefabGraph
{
    internal sealed record SceneEntry(string Guid, UnityScene Scene);
    internal sealed record Modification(string DeclaringGuid, UnityObjectId Target, YamlNode Value);
    private readonly UnityPackage _package;
    private readonly Dictionary<string, UnityScene> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityScene> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityScene> _unfiltered = new(StringComparer.OrdinalIgnoreCase);
    private readonly UnityObjectResolver _identities;
    private readonly HashSet<UnityObjectId> _removed = new();
    private readonly HashSet<string> _removedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _removedModelPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly List<Modification> _modifications = new();
    public IReadOnlyList<Modification> Modifications => _modifications;
    public IEnumerable<SceneEntry> Scenes => _order.Select(guid => new SceneEntry(guid, _resolved[guid]));
    // A removed renderer can still supply the mesh template used to preserve its live
    // GameObject/children in the current importer. It must never supply a component.
    public IEnumerable<SceneEntry> RendererTemplateScenes => _order.Select(guid => new SceneEntry(guid,
        UnityScene.FromDocuments(_unfiltered[guid].Documents.Values.Where(document =>
            !IsRemoved(guid, document.FileId) ||
            (document.ClassId is 23 or 33 or 137 &&
             !IsRemoved(guid, document.Root?["m_GameObject"]?.FileID ?? 0))))));
    public IReadOnlySet<UnityObjectId> Removed => _removed;
    public IReadOnlySet<string> RemovedModels => _removedModels;
    public IReadOnlyList<UnityObjectId> RemovedGameObjects => _removedRoots;
    private readonly List<UnityObjectId> _removedRoots = new();

    private UnityPrefabGraph(UnityPackage package)
    {
        _package = package;
        _identities = new(package, guid => _sources.GetValueOrDefault(guid));
    }

    public static UnityPrefabGraph Resolve(UnityPackage package, string rootGuid)
    {
        var graph = new UnityPrefabGraph(package);
        graph.Visit(rootGuid, new(StringComparer.OrdinalIgnoreCase));
        graph.Compose();
        return graph;
    }

    public UnityScene Scene(string guid) => guid == null ? null : _resolved.GetValueOrDefault(guid);
    public UnityScene UnfilteredScene(string guid) => guid == null ? null : _unfiltered.GetValueOrDefault(guid);
    public bool IsRemoved(string guid, long id)
    {
        var identity = Identity(guid, id);
        if (identity.IsNull) return false;
        return _removed.Contains(identity) || _removedModels.Contains(identity.Occurrence) ||
            (_removedModelPaths.TryGetValue(identity.Occurrence, out var paths) &&
             paths.Contains(_package.ModelFileIds(identity.Occurrence).ResolveNodePath(identity.FileId) ?? ""));
    }

    public UnityObjectId Identity(string guid, long id) => _identities.Resolve(guid, id);

    private void Visit(string guid, HashSet<string> ancestors)
    {
        if (_package.ByGuid(guid)?.Extension is not (".prefab" or ".unity")) return;
        if (!ancestors.Add(guid)) throw new InvalidDataException("Cyclic prefab inheritance.");
        if (_sources.ContainsKey(guid)) throw new InvalidDataException("Prefab occurrences must have distinct identities.");
        var scene = _package.ReadScene(_package.ByGuid(guid));
        _sources.Add(guid, scene);
        foreach (var instance in scene.Documents.Values.Where(d => d.ClassId == 1001))
            Visit(instance.Root?["m_SourcePrefab"]?.Guid, ancestors);
        _order.Add(guid);
        ancestors.Remove(guid);
    }

    private void Compose()
    {
        var documents = _sources.ToDictionary(entry => entry.Key,
            entry => entry.Value.Documents.Values.ToDictionary(d => d.FileId, UnityPropertyOverrides.Clone),
            StringComparer.OrdinalIgnoreCase);
        foreach (string guid in _order)
            foreach (var instance in _sources[guid].Documents.Values.Where(d => d.ClassId == 1001))
            {
                string childGuid = instance.Root?["m_SourcePrefab"]?.Guid;
                var modifications = instance.Root?["m_Modification"];
                foreach (var modification in modifications?["m_Modifications"]?.Seq ?? new())
                {
                    var target = modification["target"];
                    var identity = Identity(target?.Guid ?? childGuid, target?.FileID ?? 0);
                    if (identity.IsNull) continue;
                    _modifications.Add(new(guid, identity, modification));
                    if (documents.TryGetValue(identity.Occurrence, out var owned) && owned.TryGetValue(identity.FileId, out var document))
                        UnityPropertyOverrides.Apply(document.Root, modification, guid);
                }
                foreach (var removed in modifications?["m_RemovedComponents"]?.Seq ?? new())
                {
                    var identity = Identity(removed.Guid ?? childGuid, removed.FileID ?? 0);
                    if (!identity.IsNull) _removed.Add(identity);
                }
                foreach (var removed in modifications?["m_RemovedGameObjects"]?.Seq ?? new())
                {
                    var target = removed["asset"] ?? removed;
                    var identity = Identity(target.Guid ?? childGuid, target.FileID ?? 0);
                    if (!identity.IsNull) _removedRoots.Add(identity);
                }
            }
        foreach (var entry in documents) _unfiltered.Add(entry.Key, UnityScene.FromDocuments(entry.Value.Values));
        foreach (var root in _removedRoots) RemoveSubtree(root, new());
        // Added components and children may be serialized in an outer scene while their
        // owner/parent belongs to a nested occurrence. Propagate deletion through canonical
        // references across all scenes, not just through one file's child list.
        bool changed;
        do
        {
            int before = _removed.Count + _removedModels.Count;
            foreach (var entry in _unfiltered)
                foreach (var document in entry.Value.Documents.Values)
                {
                    var owner = document.Root?["m_GameObject"];
                    var parent = document.ClassId == 1001 ? document.Root?["m_Modification"]?["m_TransformParent"]
                        : document.ClassId == 4 || document.TypeName == "RectTransform" ? document.Root?["m_Father"] : null;
                    if (!IsRemoved(entry.Key, document.FileId) &&
                        !IsRemoved(owner?.Guid ?? entry.Key, owner?.FileID ?? 0) &&
                        !IsRemoved(parent?.Guid ?? entry.Key, parent?.FileID ?? 0)) continue;
                    _removed.Add(Identity(entry.Key, document.FileId));
                    if (document.ClassId == 4 || document.TypeName == "RectTransform")
                        _removed.Add(Identity(owner?.Guid ?? entry.Key, owner?.FileID ?? 0));
                    if (document.ClassId == 1001) RemoveOccurrence(document.Root?["m_SourcePrefab"]?.Guid, new());
                }
            changed = before != _removed.Count + _removedModels.Count;
        } while (changed);
        foreach (var entry in documents)
            _resolved.Add(entry.Key, UnityScene.FromDocuments(entry.Value.Values.Where(document =>
                !IsRemoved(entry.Key, document.FileId))));
    }

    private void RemoveSubtree(UnityObjectId root, HashSet<UnityObjectId> visited)
    {
        if (root.IsNull || !visited.Add(root)) return;
        _removed.Add(root);
        // Imported model nodes have no YAML documents. Their removal is consumed by
        // the model importer using RemovedGameObjects and its fileID/path resolver.
        if (!_unfiltered.TryGetValue(root.Occurrence, out var scene))
        {
            if (_package.ByGuid(root.Occurrence)?.Extension == ".fbx")
            {
                var model = _package.ModelFileIds(root.Occurrence);
                if (model.IsRootFileId(root.FileId)) _removedModels.Add(root.Occurrence);
                if (!_removedModelPaths.TryGetValue(root.Occurrence, out var paths))
                    _removedModelPaths[root.Occurrence] = paths = new(StringComparer.Ordinal);
                paths.UnionWith(model.NodePathsUnder(root.FileId));
            }
            return;
        }
        var document = scene.Doc(root.FileId);
        long owner = document?.ClassId == 1 ? root.FileId : document?.Root?["m_GameObject"]?.FileID ?? 0;
        var objects = owner == 0 ? new HashSet<long>() : new HashSet<long> { owner };
        var transforms = new HashSet<UnityObjectId>();
        bool changed;
        do
        {
            changed = false;
            foreach (var transform in scene.Documents.Values.Where(d => d.ClassId == 4 || d.TypeName == "RectTransform"))
            {
                var parent = transform.Root?["m_Father"];
                long go = transform.Root?["m_GameObject"]?.FileID ?? 0;
                if (objects.Contains(go) || transforms.Contains(Identity(parent?.Guid ?? root.Occurrence, parent?.FileID ?? 0)))
                {
                    changed |= objects.Add(go);
                    changed |= transforms.Add(Identity(root.Occurrence, transform.FileId));
                }
            }
        } while (changed);
        foreach (var item in scene.Documents.Values.Where(d => objects.Contains(d.ClassId == 1 ? d.FileId : d.Root?["m_GameObject"]?.FileID ?? 0)))
            _removed.Add(Identity(root.Occurrence, item.FileId));
        foreach (var instance in scene.Documents.Values.Where(d => d.ClassId == 1001))
        {
            var parent = instance.Root?["m_Modification"]?["m_TransformParent"];
            if (!transforms.Contains(Identity(parent?.Guid ?? root.Occurrence, parent?.FileID ?? 0))) continue;
            RemoveOccurrence(instance.Root?["m_SourcePrefab"]?.Guid, new());
            _removed.Add(new(root.Occurrence, instance.FileId));
        }
    }

    private void RemoveOccurrence(string guid, HashSet<string> visited)
    {
        if (guid == null || !visited.Add(guid)) return;
        if (_package.ByGuid(guid)?.Extension == ".fbx")
        {
            _removedModels.Add(guid);
            return;
        }
        if (!_unfiltered.TryGetValue(guid, out var scene)) return;
        foreach (var document in scene.Documents.Values) _removed.Add(Identity(guid, document.FileId));
        foreach (var instance in scene.Documents.Values.Where(d => d.ClassId == 1001))
            RemoveOccurrence(instance.Root?["m_SourcePrefab"]?.Guid, visited);
    }
}
