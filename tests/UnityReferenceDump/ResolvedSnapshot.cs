using System.Globalization;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

// Test-only projection of the production prefab graph. Expectations supply only the
// selected GUID and schema; no expected hierarchy, values or asset IDs are consulted.
internal sealed class ResolvedSnapshot
{
    public const string Projection = "unity-resolved-prefab";
    private const string Bone = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
    private const string Collider = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
    private readonly UnityPackage view;
    private readonly string guid;
    private readonly Dictionary<UnityObjectId, YamlDocument> documents = new();
    private readonly Dictionary<UnityObjectId, Node> nodes = new();
    private readonly Dictionary<UnityObjectId, Node> owners = new();
    private readonly Dictionary<UnityObjectId, string> ids = new();
    private readonly Dictionary<string, (string Parent, YamlDocument Instance)> occurrences = new();
    private readonly Dictionary<string, ModelSnapshot> models = new();
    private readonly List<Node> ordered = new();
    private sealed class Node
    {
        public UnityObjectId Key, Owner;
        public YamlDocument Document, GameObject;
        public Node Parent;
        public List<Node> Children = new();
        public List<UnityObjectId> Components = new();
        public string Id, Path;
        public bool Active;
        public string Name => GameObject.Root["m_Name"]?.AsString() ?? "";
        public bool ActiveSelf => GameObject.Root["m_IsActive"]?.AsBool(true) ?? true;
    }

    private ResolvedSnapshot(UnityPackage view, string guid) { this.view = view; this.guid = guid; }
    public static object Capture(UnityPackage view, string guid) => new ResolvedSnapshot(view, guid).Capture();

    private object Capture()
    {
        foreach (var entry in view.PrefabGraph.Scenes)
        {
            foreach (var d in entry.Scene.Documents.Values.Where(d => !d.Stripped))
                documents.Add(new(entry.Guid, d.FileId), d);
            foreach (var instance in entry.Scene.Documents.Values.Where(d => d.ClassId == 1001))
            {
                string child = instance.Root["m_SourcePrefab"]?.Guid;
                var asset = view.ByGuid(child);
                if (asset == null) throw new InvalidDataException("MISSING_PREFAB_SOURCE: " + child);
                occurrences.Add(child, (entry.Guid, instance));
            }
        }
        foreach (string occurrence in occurrences.Keys.Where(g => view.ByGuid(g).Extension == ".fbx"))
        {
            var references = new HashSet<long>();
            foreach (var entry in view.PrefabGraph.Scenes)
                foreach (var document in view.ReadScene(view.ByGuid(entry.Guid)).Documents.Values)
                    Collect(document.Root);
            foreach (var modification in view.PrefabGraph.Modifications.Where(m => m.Target.Occurrence == occurrence))
                references.Add(modification.Target.FileId);
            var model = new ModelSnapshot(view.ByGuid(occurrence), view.ModelFileIds(occurrence), references);
            models.Add(occurrence, model);
            foreach (var modification in view.PrefabGraph.Modifications.Where(m => m.Target.Occurrence == occurrence))
            {
                if (!model.Documents.TryGetValue(model.Canonical(modification.Target.FileId), out var document)) continue;
                string property = modification.Value["propertyPath"]?.AsString()?.Split('.')[0];
                if (property is "m_Name" or "m_IsActive" or "m_LocalPosition" or "m_LocalRotation" or "m_LocalScale" or
                    "m_Children" or "m_Father" or "m_RootOrder" or "m_Materials" or "m_Enabled" or "m_Mesh" or
                    "m_Bones" or "m_RootBone" or "m_BlendShapeWeights")
                    UnityPropertyOverrides.Apply(document.Root, modification.Value, modification.DeclaringGuid);
            }
            foreach (var document in model.Documents.Values)
                if (!view.PrefabGraph.IsRemoved(occurrence, document.FileId) &&
                    !view.PrefabGraph.IsRemoved(occurrence, document.Root["m_GameObject"]?.FileID ?? 0))
                    documents.Add(new(occurrence, document.FileId), document);

            void Collect(YamlNode node)
            {
                if (node == null) return;
                if (node.Guid == occurrence && node.FileID is long id && id != 0) references.Add(id);
                foreach (var child in node.Map?.Values ?? Enumerable.Empty<YamlNode>()) Collect(child);
                foreach (var child in node.Items) Collect(child);
            }
        }
        foreach (var (key, doc) in documents.Where(p => p.Value.ClassId is 4 or 224))
        {
            var owner = Ref(key, doc.Root["m_GameObject"]);
            if (!documents.TryGetValue(owner, out var go) || go.ClassId != 1)
                throw new InvalidDataException("Unresolved Transform owner: " + key);
            var node = new Node { Key = key, Owner = owner, Document = doc, GameObject = go };
            nodes.Add(key, node);
            owners.Add(owner, node);
        }
        foreach (var node in nodes.Values)
        {
            var parent = Ref(node.Key, node.Document.Root["m_Father"]);
            string occurrence = node.Key.Occurrence;
            var seen = new HashSet<string>();
            while (parent.IsNull && occurrences.TryGetValue(occurrence, out var link))
            {
                if (!seen.Add(occurrence)) throw new InvalidDataException("Cyclic occurrence hierarchy.");
                parent = Ref(new(link.Parent, link.Instance.FileId), link.Instance.Root["m_Modification"]?["m_TransformParent"]);
                occurrence = link.Parent;
            }
            if (!parent.IsNull)
            {
                if (!nodes.TryGetValue(parent, out node.Parent)) throw new InvalidDataException("Unresolved parent: " + parent);
                node.Parent.Children.Add(node);
            }
        }
        foreach (var parent in nodes.Values)
        {
            var explicitOrder = (parent.Document.Root["m_Children"]?.Items ?? Array.Empty<YamlNode>())
                .Select(n => Ref(parent.Key, n)).ToList();
            // Start with authored children, omit removals, then apply additions at their
            // serialized insertion indices. File order is never a hierarchy identity.
            var pending = parent.Children.ToList();
            parent.Children.Clear();
            foreach (var key in explicitOrder)
                if (pending.FirstOrDefault(n => n.Key == key) is Node child)
                { parent.Children.Add(child); pending.Remove(child); }
            foreach (var child in pending.OrderBy(n => AddedPlacement(n.Key, "m_AddedGameObjects").Order == int.MaxValue
                             ? n.Document.Root["m_RootOrder"]?.AsInt() ?? int.MaxValue : int.MaxValue)
                         .ThenBy(n => AddedPlacement(n.Key, "m_AddedGameObjects").Order))
            {
                var placement = AddedPlacement(child.Key, "m_AddedGameObjects");
                int index = placement.Order == int.MaxValue
                    ? child.Document.Root["m_RootOrder"]?.AsInt(-1) ?? -1 : placement.Index;
                parent.Children.Insert(index < 0 ? parent.Children.Count : Math.Min(index, parent.Children.Count), child);
            }
        }
        var roots = nodes.Values.Where(n => n.Parent == null).ToArray();
        if (roots.Length != 1) throw new InvalidDataException($"Expected one resolved root, found {roots.Length}.");
        Index(roots[0], "r", "", true);
        if (ordered.Count != nodes.Count) throw new InvalidDataException("Disconnected or cyclic hierarchy.");
        foreach (var node in ordered)
        {
            var components = documents.Where(p => p.Value.ClassId is not (1 or 4 or 224 or 1001) &&
                Ref(p.Key, p.Value.Root["m_GameObject"]) == node.Owner).Select(p => p.Key).ToList();
            foreach (var entry in node.GameObject.Root["m_Component"]?.Items ?? Array.Empty<YamlNode>())
            {
                var key = Ref(node.Owner, entry["component"]);
                if (components.Remove(key)) node.Components.Add(key);
            }
            foreach (var key in components.OrderBy(k => AddedPlacement(k, "m_AddedComponents").Order))
            {
                int index = AddedComponentIndex(key);
                node.Components.Insert(index < 0 ? node.Components.Count : Math.Min(Math.Max(0, index - 1), node.Components.Count), key);
            }
            var ordinals = new Dictionary<string, int>();
            foreach (var key in node.Components)
            {
                string type = Type(documents[key]);
                if (type == null) continue;
                int ordinal = ordinals.GetValueOrDefault(type);
                ordinals[type] = ordinal + 1;
                ids.Add(key, node.Id + "/c:" + type + ":" + ordinal.ToString(CultureInfo.InvariantCulture));
            }
        }
        var transforms = new List<object>();
        var renderers = new List<object>();
        var bones = new List<object>();
        var colliders = new List<object>();
        foreach (var node in ordered)
        {
            var r = Identity(node.Key, node, node.Document.ClassId == 224 ? "UnityEngine.RectTransform" : "UnityEngine.Transform");
            r["parent"] = node.Parent?.Id ?? "";
            // Unity's Prefab importer names the asset root after its file even when
            // the serialized root name is stale (for example after a file rename).
            r["name"] = node.Parent == null ? Path.GetFileNameWithoutExtension(view.ByGuid(guid).LogicalPath) : node.Name;
            r["siblingIndex"] = node.Parent?.Children.IndexOf(node) ?? 0;
            r["activeSelf"] = node.ActiveSelf; r["activeInHierarchy"] = node.Active;
            foreach (string property in new[] { "localPosition", "localRotation", "localScale" })
            {
                var value = node.Document.Root["m_" + char.ToUpperInvariant(property[0]) + property[1..]]
                    ?? throw new InvalidDataException("Missing transform property: " + property);
                r[property] = (property == "localRotation" ? "xyzw" : "xyz")
                    .ToDictionary(axis => axis.ToString(), axis => Number(value[axis.ToString()]));
            }
            transforms.Add(r);
            foreach (var key in node.Components.Where(ids.ContainsKey))
            {
                var doc = documents[key];
                string type = Type(doc);
                var record = Identity(key, node, type);
                record["transform"] = node.Id;
                record["enabled"] = doc.Root["m_Enabled"]?.AsBool(true) ?? true;
                record["activeInHierarchy"] = node.Active;
                if (doc.ClassId is 23 or 137)
                {
                    bool skinned = doc.ClassId == 137;
                    YamlNode mesh = skinned ? doc.Root["m_Mesh"] : node.Components
                        .Where(k => documents[k].ClassId == 33).Select(k => documents[k].Root["m_Mesh"]).SingleOrDefault();
                    record["activeSelf"] = node.ActiveSelf;
                    record["mesh"] = Asset(key, mesh);
                    record["materials"] = Items(doc.Root["m_Materials"]).Select(n => Asset(key, n)).ToArray();
                    record["rootBone"] = skinned ? Address(key, doc.Root["m_RootBone"]) : "";
                    record["bones"] = skinned ? Items(doc.Root["m_Bones"]).Select(n => Address(key, n)).ToArray() : Array.Empty<string>();
                    record["blendShapes"] = skinned ? Shapes(key, mesh, doc.Root["m_BlendShapeWeights"]) : Array.Empty<object>();
                    renderers.Add(record);
                }
                else
                {
                    record["root"] = (doc.Root["rootTransform"]?.FileID ?? 0) == 0 ? node.Id : Address(key, doc.Root["rootTransform"]);
                    record["colliders"] = type == Bone ? Items(doc.Root["colliders"]).Select(n => Address(key, n)).ToArray() : Array.Empty<string>();
                    record["parameters"] = type == Bone ? new[] { "pull", "spring", "stiffness", "gravity", "gravityFalloff", "immobile", "radius" }
                        .Select(name => (object)new { name, value = Number(doc.Root[name]) }).ToArray() : Array.Empty<object>();
                    (type == Bone ? bones : colliders).Add(record);
                }
            }
        }
        if (renderers.Count + bones.Count + colliders.Count == 0) throw new InvalidDataException("Empty resolved capture.");
        return new { format = ReferenceSnapshot.Format, schemaVersion = 2, projection = Projection, prefabGuid = guid,
            sections = new { transforms, renderers, physBones = bones, colliders } };
    }

    private int AddedComponentIndex(UnityObjectId key) => AddedPlacement(key, "m_AddedComponents").Index;
    private (int Index, int Order) AddedPlacement(UnityObjectId key, string property)
    {
        int order = 0;
        foreach (var (parent, instance) in occurrences.Values)
            foreach (var item in instance.Root["m_Modification"]?[property]?.Items ?? Array.Empty<YamlNode>())
            {
                if (Ref(new(parent, instance.FileId), item["addedObject"]) == key ||
                    (nodes.TryGetValue(key, out var node) && Ref(new(parent, instance.FileId), item["addedObject"]) == node.Owner))
                    return (item["insertIndex"]?.AsInt(-1) ?? -1, order);
                ++order;
            }
        return (-1, int.MaxValue);
    }
    private void Index(Node node, string id, string path, bool active)
    {
        if (!ids.TryAdd(node.Key, id)) throw new InvalidDataException("Cyclic hierarchy.");
        node.Id = id; node.Path = path; node.Active = active && node.ActiveSelf;
        ordered.Add(node);
        for (int i = 0; i < node.Children.Count; ++i)
        {
            var child = node.Children[i];
            Index(child, id + "/" + i.ToString(CultureInfo.InvariantCulture),
                path.Length == 0 ? child.Name : path + "/" + child.Name, node.Active);
        }
    }
    private Dictionary<string, object> Identity(UnityObjectId key, Node node, string type) => new()
    {
        ["id"] = ids[key], ["path"] = node.Path, ["type"] = type,
        ["source"] = Source(key), ["originalSource"] = new AssetReference(OriginalGuid(key.Occurrence), Id(key.FileId)),
    };
    private AssetReference Source(UnityObjectId key)
    {
        string occurrence = key.Occurrence;
        long fileId = key.FileId;
        while (occurrence != guid)
        {
            if (!occurrences.TryGetValue(occurrence, out var link)) throw new InvalidDataException("Missing source occurrence.");
            var explicitIds = view.ReadScene(view.ByGuid(link.Parent)).Documents.Values
                .Where(d => d.Stripped && Canonical(view.PrefabGraph.Identity(link.Parent, d.FileId)) == key).Select(d => d.FileId).ToArray();
            if (explicitIds.Length > 1) throw new InvalidDataException("Ambiguous source identity.");
            fileId = explicitIds.Length == 1 ? explicitIds[0] : (fileId ^ link.Instance.FileId) & long.MaxValue;
            occurrence = link.Parent;
        }
        return new(guid, Id(fileId));
    }
    private UnityObjectId Ref(UnityObjectId context, YamlNode reference)
    {
        if (reference == null || reference.FileID == 0) return default;
        if (reference.FileID == null) throw new InvalidDataException("Malformed object reference.");
        var key = view.PrefabGraph.Identity(reference.Guid ?? context.Occurrence, reference.FileID.Value);
        if (key.IsNull) throw new InvalidDataException($"Unresolved reference: {reference.Guid ?? context.Occurrence}:{reference.FileID}");
        return Canonical(key);
    }
    private UnityObjectId Canonical(UnityObjectId key) => !key.IsNull && models.TryGetValue(key.Occurrence, out var model)
        ? new(key.Occurrence, model.Canonical(key.FileId)) : key;
    private string Address(UnityObjectId context, YamlNode reference)
    {
        var key = Ref(context, reference);
        if (key.IsNull) return "";
        return ids.TryGetValue(key, out string id) ? id : throw new InvalidDataException("Reference outside resolved hierarchy: " + key);
    }
    private string OriginalGuid(string value) => view.ByGuid(value)?.SourceGuid ?? value;
    private AssetReference Asset(UnityObjectId context, YamlNode reference) => reference == null || reference.FileID == 0
        ? new("", "0") : new(OriginalGuid(reference.Guid ?? context.Occurrence),
            Id(reference.FileID ?? throw new InvalidDataException("Malformed asset reference.")));
    private object[] Shapes(UnityObjectId context, YamlNode mesh, YamlNode weights)
    {
        if (mesh == null || mesh.FileID == 0) return Array.Empty<object>();
        var asset = view.ByGuid(mesh.Guid ?? context.Occurrence);
        if (asset?.HasContent != true) throw new InvalidDataException("Missing mesh asset for blendshape metadata.");
        IReadOnlyList<string> names;
        if (asset.Extension == ".fbx")
        {
            var resolver = view.ModelFileIds(asset.Guid);
            string path = models.TryGetValue(asset.Guid, out var model)
                ? model.ResolveMeshPath(mesh.FileID ?? 0, context.Occurrence == asset.Guid ? context.FileId : 0) : null;
            path ??= resolver.ResolveNodePath(mesh.FileID ?? 0);
            if (resolver.PossibleBlendShapeNames == null || (path == null && resolver.PossibleBlendShapeNames.Count != 0))
                throw new InvalidDataException("Unresolved FBX mesh/blendshape metadata: " + asset.LogicalPath);
            // A successful scan proving that the entire FBX has no shapes does not
            // need an unambiguous node path (e.g. a folded, rigid accessory mesh).
            names = path == null ? Array.Empty<string>() : resolver.BlendShapeNamesByPath.GetValueOrDefault(path) ?? Array.Empty<string>();
        }
        else
        {
            // Mesh/Texture payloads are intentionally omitted from binary scene
            // enumeration. Resolve the exact subasset using the production reader.
            var document = view.ReadAssetDocument(asset.Guid, mesh.FileID ?? 0);
            if (document?.ClassId != 43) throw new NotSupportedException("Unsupported native mesh metadata: " + asset.LogicalPath);
            names = Items(document.Root["m_Shapes"]?["channels"]).Select(n => n["name"]?.AsString()
                ?? throw new InvalidDataException("Unnamed blendshape.")).ToArray();
        }
        var values = Items(weights);
        // Unity exposes weights only for channels on the current sharedMesh. Serialized
        // arrays can retain stale trailing entries after a mesh revision/replacement.
        return names.Select((name, index) => (object)new { index, name, weight = index < values.Count ? Number(values[index]) : 0f }).ToArray();
    }
    private static string Type(YamlDocument doc) => doc.ClassId switch
    {
        23 => "UnityEngine.MeshRenderer", 137 => "UnityEngine.SkinnedMeshRenderer",
        114 when doc.Root["m_Script"]?.Guid == VrchatConstants.PhysBoneDllGuid => doc.Root["m_Script"].FileID switch
        { VrchatConstants.PhysBoneScriptFileId => Bone, -1631200402 => Collider, _ => null },
        _ => null,
    };
    private static IReadOnlyList<YamlNode> Items(YamlNode node) => node == null ? Array.Empty<YamlNode>() : node.IsSeq
        ? node.Items : throw new InvalidDataException("Expected an array.");
    private static float Number(YamlNode node) => node?.AsString() is string value &&
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) && float.IsFinite(result)
        ? result : throw new InvalidDataException("Missing or non-finite numeric field.");
    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
