using System.Globalization;
using VrmToResonitePackage.Unity;

// Projects source model metadata into the same serialized fields as authored YAML.
// Overrides/removals still come from the production prefab graph, never Unity JSON.
internal sealed class ModelSnapshot
{
    private readonly UnityAsset asset;
    private readonly UnityModelFileIdResolver resolver;
    private readonly YamlNode importer;
    private readonly Dictionary<string, string> materialGuids = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Class, string Name), List<long>> metaIds = new();
    private readonly Dictionary<(string Path, int Class), long> identities = new();
    public Dictionary<long, YamlDocument> Documents { get; } = new();
    private readonly Dictionary<long, List<string>> meshPaths = new();
    private readonly Dictionary<long, string> rendererPaths = new();
    private IReadOnlyList<UnityModelNode> hierarchy;

    public ModelSnapshot(UnityPackage package, UnityAsset asset, UnityModelFileIdResolver resolver, IReadOnlySet<long> references)
    {
        this.asset = asset; this.resolver = resolver;
        var meta = asset.MetaPath == null ? null : UnityYaml.ParseFlatDocument(File.ReadAllText(asset.MetaPath));
        importer = meta?["ModelImporter"];
        VrmToResonitePackage.Vrchat.VrchatAvatarParser.ParseFbxMaterialMappings(package, asset, meta, materialGuids);
        hierarchy = resolver.Hierarchy ?? throw new InvalidDataException("FBX source hierarchy could not be imported: " + asset.LogicalPath);
        if (hierarchy.Count == 0 || hierarchy.Select(n => n.Path).Distinct().Count() != hierarchy.Count)
            throw new InvalidDataException("Empty or ambiguous FBX hierarchy: " + asset.LogicalPath);
        foreach (var entry in importer?["internalIDToNameTable"]?.Items ?? Array.Empty<YamlNode>())
            foreach (var pair in entry["first"]?.Map ?? new())
                Remember(int.Parse(pair.Key, CultureInfo.InvariantCulture), entry["second"]?.AsString(), pair.Value.AsLong());
        foreach (var pair in importer?["fileIDToRecycleName"]?.Map ?? new())
        {
            long id = long.Parse(pair.Key, CultureInfo.InvariantCulture);
            Remember((int)(id / 100000), pair.Value.AsString(), id);
        }
        if (importer?["animations"]?["optimizeGameObjects"]?.AsBool() == true)
            throw new NotSupportedException("Optimized FBX GameObject hierarchy cannot be projected: " + asset.LogicalPath);
        ChooseLayout(references);
        Build();
    }

    private void Remember(int type, string name, long id)
    {
        if (name == null || id == 0) return;
        if (!metaIds.TryGetValue((type, name), out var ids)) metaIds.Add((type, name), ids = new());
        if (!ids.Contains(id)) ids.Add(id);
    }
    private void ChooseLayout(IReadOnlySet<long> references)
    {
        var candidates = new List<(int Score, IReadOnlyList<UnityModelNode> Nodes, Dictionary<(string, int), long> Ids)>();
        var layouts = new List<IReadOnlyList<UnityModelNode>> { hierarchy };
        var children = hierarchy.Where(n => n.ParentPath == hierarchy[0].Path).ToArray();
        if (children.Length == 1 && importer?["meshes"]?["preserveHierarchy"]?.AsBool() != true && !hierarchy[0].HasMesh)
            layouts.Insert(0, hierarchy.Skip(1).ToArray());
        foreach (var nodes in layouts)
            foreach (string root in new[] { "//RootNode/root", "//RootNode" })
            {
                var ids = new Dictionary<(string, int), long>();
                var duplicates = new Dictionary<(int, string), int>();
                foreach (var node in nodes)
                {
                    string path = root + node.Path[nodes[0].Path.Length..];
                    foreach (int type in node.HasMesh ? new[] { 1, 4, 43, node.Skinned ? 137 : 23, 33 } : new[] { 1, 4 })
                    {
                        string typeName = TypeName(type);
                        bool byName = type == 43 || (importer?["meshes"]?["fileIdsGeneration"]?.AsInt(2) ?? 2) == 1;
                        string address = byName ? node.Name : type == 1 ? path : path + "/" + typeName;
                        int duplicate = duplicates.GetValueOrDefault((type, address));
                        duplicates[(type, address)] = duplicate + 1;
                        long id = metaIds.TryGetValue((type, node.Name), out var explicitIds) && explicitIds.Count == 1
                            ? explicitIds[0] : UnityModelFileIdResolver.Compute(typeName, address, duplicate);
                        ids.Add((node.Path, type), id);
                    }
                }
                // Serialized model references prove the imported path convention. Mesh IDs
                // are name based and cannot distinguish hierarchy layouts.
                int score = ids.Where(p => p.Key.Item2 is 1 or 4 or 23 or 137).Count(p => references.Contains(p.Value));
                candidates.Add((score, nodes, ids));
            }
        var best = candidates.OrderByDescending(c => c.Score).First();
        if (best.Score == 0) throw new InvalidDataException("No serialized references anchor the FBX hierarchy: " + asset.LogicalPath);
        hierarchy = best.Nodes;
        foreach (var pair in best.Ids) identities.Add(pair.Key, pair.Value);
    }

    public long Canonical(long fileId)
    {
        if (Documents.ContainsKey(fileId) || meshPaths.ContainsKey(fileId)) return fileId;
        string path = resolver.ResolveNodePath(fileId);
        int type = resolver.ClassId(fileId);
        return path != null && identities.TryGetValue((path, type), out long id) ? id : fileId;
    }

    public string ResolveMeshPath(long meshId, long rendererId)
    {
        if (!meshPaths.TryGetValue(meshId, out var paths)) return null;
        // Prefer the actual renderer, but only while it still uses this mesh.
        // Prefab overrides can replace m_Mesh with a different subasset.
        if (rendererPaths.TryGetValue(rendererId, out string path) && paths.Contains(path)) return path;
        // An authored renderer outside the model has no source renderer context.
        // Shared paths are interchangeable only when their channel metadata agrees.
        var names = resolver.BlendShapeNamesByPath.GetValueOrDefault(paths[0]) ?? Array.Empty<string>();
        if (paths.Skip(1).Any(p => !names.SequenceEqual(
                resolver.BlendShapeNamesByPath.GetValueOrDefault(p) ?? Array.Empty<string>())))
            throw new InvalidDataException("Ambiguous shared FBX mesh/blendshape metadata: " + asset.LogicalPath);
        return paths[0];
    }

    private void Build()
    {
        var meshes = importer?["meshes"];
        bool useFileUnits = meshes?["useFileUnits"]?.AsBool(meshes?["useFileScale"]?.AsBool(true) ?? true) ?? true;
        float units = (meshes?["globalScale"]?.AsFloat(1) ?? 1) * (useFileUnits ? FbxUnits.MetersPerUnit(asset.DiskPath) : 1);
        var childrenByParent = UnityModelNode.ChildrenByParent(hierarchy, meshes?["sortHierarchyByName"]?.AsBool(true) ?? true);
        var nodesByName = hierarchy.ToLookup(n => n.Name, StringComparer.Ordinal);
        foreach (var node in hierarchy)
        {
            long go = Identity(node.Path, 1), transform = Identity(node.Path, 4);
            bool root = node == hierarchy[0];
            var children = childrenByParent.GetValueOrDefault(node.Path, Array.Empty<UnityModelNode>());
            var components = new List<YamlNode> { Map(("component", Ref(transform))) };
            if (node.HasMesh)
            {
                int type = node.Skinned ? 137 : 23;
                long renderer = Identity(node.Path, type), mesh = Identity(node.Path, 43);
                if (!node.Skinned) components.Add(Map(("component", Ref(Identity(node.Path, 33)))));
                components.Add(Map(("component", Ref(renderer))));
                if (!meshPaths.TryGetValue(mesh, out var paths)) meshPaths.Add(mesh, paths = new());
                paths.Add(node.Path);
                rendererPaths.Add(renderer, node.Path);
                var boneNames = resolver.MeshBoneNamesByPath.GetValueOrDefault(node.Path) ?? Array.Empty<string>();
                string[] connected = resolver.SourceSkinBonePaths?.GetValueOrDefault(node.Path);
                var bonePaths = connected != null && connected.Select(p => p.Split('/')[^1]).SequenceEqual(boneNames)
                    ? connected : boneNames.Select(name =>
                {
                    var matches = nodesByName[name];
                    return matches.Count() == 1 ? matches.First().Path : throw new InvalidDataException("Ambiguous FBX skin bone: " + name);
                }).ToArray();
                string rootBone = CommonAncestor(bonePaths);
                // Unity uses the last connected cluster when the shared ancestor is not
                // itself in this skin. ResoPonSkinRootSmoke checks reversed cluster order
                // and a skin that explicitly includes the common ancestor in Unity.
                if (rootBone != null && !bonePaths.Contains(rootBone)) rootBone = bonePaths[^1];
                var fields = Map(("m_GameObject", Ref(go)), ("m_Enabled", Scalar(1)),
                    ("m_Materials", Sequence(node.MaterialNames.Select(Material))), ("m_Mesh", Ref(mesh)),
                    ("m_RootBone", Ref(rootBone == null ? 0 : Identity(rootBone, 4))),
                    ("m_Bones", Sequence(bonePaths.Select(p => Ref(Identity(p, 4))))),
                    ("m_BlendShapeWeights", Sequence((resolver.BlendShapeDefaultWeightsByPath.GetValueOrDefault(node.Path)
                        ?? Enumerable.Repeat(0f, resolver.BlendShapeNamesByPath.GetValueOrDefault(node.Path)?.Count ?? 0).ToArray()).Select(Scalar))));
                Add(type, renderer, fields);
                if (!node.Skinned) Add(33, Identity(node.Path, 33), Map(("m_GameObject", Ref(go)), ("m_Mesh", Ref(mesh))));
            }
            Add(1, go, Map(("m_Name", Scalar(root ? System.IO.Path.GetFileNameWithoutExtension(asset.LogicalPath) : node.Name)),
                ("m_IsActive", Scalar(1)), ("m_Component", Sequence(components))));
            var p = node.Position * units; var q = node.Rotation; var s = node.Scale;
            Add(4, transform, Map(("m_GameObject", Ref(go)), ("m_Father", Ref(root ? 0 : Identity(node.ParentPath, 4))),
                ("m_Children", Sequence(children.Select(n => Ref(Identity(n.Path, 4))))),
                ("m_LocalPosition", Vector(p.X, p.Y, p.Z)), ("m_LocalScale", Vector(s.X, s.Y, s.Z)),
                ("m_LocalRotation", Map(("x", Scalar(q.X)), ("y", Scalar(q.Y)), ("z", Scalar(q.Z)), ("w", Scalar(q.W))))));
        }
    }

    private long Identity(string path, int type) => identities.TryGetValue((path, type), out long id) ? id
        : throw new InvalidDataException("Missing FBX object identity: " + path + ":" + type);
    private string CommonAncestor(string[] paths)
    {
        if (paths.Length == 0) return null;
        string path = paths[0];
        while (paths.Any(p => p != path && !p.StartsWith(path + "/", StringComparison.Ordinal)))
        {
            int slash = path.LastIndexOf('/');
            if (slash < 0) throw new InvalidDataException("Disconnected FBX skeleton.");
            path = path[..slash];
        }
        return path;
    }
    private YamlNode Material(string name)
    {
        var mappings = (importer?["externalObjects"]?.Items ?? Array.Empty<YamlNode>()).Where(e =>
            e["first"]?["name"]?.AsString() == name && e["first"]?["type"]?.AsString() == "UnityEngine:Material").ToArray();
        if (mappings.Length > 1) throw new InvalidDataException("Ambiguous FBX material remap.");
        if (mappings.Length == 1) return UnityPropertyOverrides.Clone(mappings[0]["second"]);
        if (materialGuids.TryGetValue(name, out string materialGuid))
            return Map(("fileID", Scalar(2100000L)), ("guid", Scalar(materialGuid)));
        long id = metaIds.TryGetValue((21, name), out var ids) && ids.Count == 1 ? ids[0]
            : UnityModelFileIdResolver.Compute("Material", name, 0);
        return Ref(id);
    }
    private void Add(int type, long id, YamlNode root) => Documents.Add(id,
        new() { ClassId = type, TypeName = TypeName(type), FileId = id, Root = root });
    private static string TypeName(int type) => type switch { 1 => "GameObject", 4 => "Transform", 23 => "MeshRenderer",
        33 => "MeshFilter", 43 => "Mesh", 137 => "SkinnedMeshRenderer", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private YamlNode Ref(long id) => Map(("fileID", Scalar(id)), ("guid", Scalar(asset.Guid)));
    private static YamlNode Vector(float x, float y, float z) => Map(("x", Scalar(x)), ("y", Scalar(y)), ("z", Scalar(z)));
    private static YamlNode Scalar(float value) => new() { ScalarValue = value.ToString("R", CultureInfo.InvariantCulture) };
    private static YamlNode Scalar(long value) => new() { ScalarValue = value.ToString(CultureInfo.InvariantCulture) };
    private static YamlNode Scalar(string value) => new() { ScalarValue = value };
    private static YamlNode Map(params (string Key, YamlNode Value)[] pairs) => new() { Map = pairs.ToDictionary(p => p.Key, p => p.Value) };
    private static YamlNode Sequence(IEnumerable<YamlNode> items) => new() { Seq = items.ToList() };
}
