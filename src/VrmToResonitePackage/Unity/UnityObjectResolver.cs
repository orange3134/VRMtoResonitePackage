namespace VrmToResonitePackage.Unity;

/// <summary>One fileID/stripped-alias resolver shared by discovery and composed conversion.</summary>
internal sealed class UnityObjectResolver(UnityPackage package, Func<string, UnityScene> sceneFor)
{
    private readonly Dictionary<UnityObjectId, UnityObjectId> _cache = new();

    public UnityObjectId Resolve(string guid, long fileId)
    {
        var key = new UnityObjectId(guid, fileId);
        if (key.IsNull) return default;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        return _cache[key] = Visit(key, new());
    }

    private UnityObjectId Visit(UnityObjectId key, HashSet<UnityObjectId> ancestors)
    {
        if (key.IsNull || !ancestors.Add(key)) return default;
        if (package.ByGuid(key.Occurrence)?.Extension == ".fbx")
            return package.ModelFileIds(key.Occurrence).ResolveName(key.FileId) != null ? key : default;
        var scene = sceneFor(key.Occurrence);
        if (scene == null) return default;
        var document = scene.Doc(key.FileId);
        var source = document?.Root?["m_CorrespondingSourceObject"];
        if ((source?.FileID ?? 0) != 0 && source.Guid != null)
            return Visit(new(source.Guid, source.FileID.Value), ancestors);
        if (document != null) return key;
        var candidates = new HashSet<UnityObjectId>();
        foreach (var instance in scene.Documents.Values.Where(d => d.ClassId == 1001))
            foreach (long child in SourceFileIds(key.FileId, instance.FileId))
            {
                var candidate = Visit(new(instance.Root?["m_SourcePrefab"]?.Guid, child), new(ancestors));
                if (!candidate.IsNull) candidates.Add(candidate);
            }
        return candidates.Count == 1 ? candidates.Single() : default;
    }

    private static IEnumerable<long> SourceFileIds(long local, long instance)
    {
        long value = (local ^ instance) & long.MaxValue;
        yield return value;
        yield return value | long.MinValue;
    }
}
