using System.Security.Cryptography;
using System.Text;

namespace VrmToResonitePackage.Unity;

/// <summary>
/// Gives each occurrence of a prefab/model its own parsing identity. The first occurrence
/// retains its asset GUID; repeated occurrences alias the same read-only file and importer meta.
/// References are rewritten per PrefabInstance, including its stripped documents and overrides.
/// This lets the downstream model-keyed collectors and importer reproduce every occurrence.
/// </summary>
internal static class UnityPrefabInstances
{
    public static UnityPackage CreateView(UnityPackage source, string sourceGuid, HashSet<long> subtree)
    {
        var view = source.CreateView(sourceGuid);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        Visit(sourceGuid, sourceGuid, new(StringComparer.OrdinalIgnoreCase), subtree);
        return view;

        Dictionary<string, string> Visit(string guid, string path, HashSet<string> ancestors,
            HashSet<long> included = null)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            UnityAsset asset = source.ByGuid(guid);
            if (asset?.Extension is not (".prefab" or ".unity" or ".fbx")) return mapping;
            if (!ancestors.Add(guid) || ++count > 10000)
                throw new InvalidDataException("Prefab instance graph is cyclic or too large.");
            string identity = claimed.Add(guid) ? guid :
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32].ToLowerInvariant();
            if (identity != guid && (source.ByGuid(identity) != null || !claimed.Add(identity)))
                throw new InvalidDataException("Prefab instance identity collision.");
            mapping.Add(guid, identity);
            var alias = new UnityAsset { Guid = identity, LogicalPath = asset.LogicalPath,
                DiskPath = asset.DiskPath, MetaPath = asset.MetaPath };
            if (asset.Extension == ".fbx")
            {
                view.SetViewAsset(alias);
                return mapping;
            }

            UnityScene scene = source.ReadScene(asset);
            if (scene == null) return mapping;
            var instances = scene.Documents.Values.Where(d => d.ClassId == 1001 &&
                (included == null || included.Contains(scene.Doc(
                    d.Root?["m_Modification"]?["m_TransformParent"]?.FileID ?? 0)?
                    .Root?["m_GameObject"]?.FileID ?? 0))).ToList();
            var instanceMaps = new Dictionary<long, Dictionary<string, string>>();
            foreach (var instance in instances)
            {
                var child = Visit(instance.Root?["m_SourcePrefab"]?.Guid, path + "/" + instance.FileId,
                    new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase));
                instanceMaps.Add(instance.FileId, child);
                // Unqualified references in this scene retain the first occurrence. References
                // owned by an instance use its full mapping below, never a sibling's mapping.
                foreach (var entry in child) mapping.TryAdd(entry.Key, entry.Value);
            }
            var documents = scene.Documents.Values.Where(d => included == null ||
                (d.ClassId == 1001 ? instanceMaps.ContainsKey(d.FileId) :
                 included.Contains(d.ClassId == 1 ? d.FileId : d.Root?["m_GameObject"]?.FileID ?? 0) ||
                 instanceMaps.ContainsKey(d.Root?["m_PrefabInstance"]?.FileID ?? 0))).ToList();
            // Unpacked geometry has no model PrefabInstance. Claim one source model per scene,
            // shared by its local renderers, Animator and bone references.
            foreach (string model in documents.SelectMany(d => References(d.Root))
                         .Where(g => source.ByGuid(g)?.Extension == ".fbx").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (mapping.ContainsKey(model)) continue;
                foreach (var entry in Visit(model, path + "/mesh/" + model,
                             new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase)))
                    mapping.TryAdd(entry.Key, entry.Value);
            }
            view.SetViewAsset(alias, UnityScene.FromDocuments(documents.Select(d =>
            {
                long owner = d.ClassId == 1001 ? d.FileId : d.Root?["m_PrefabInstance"]?.FileID ?? 0;
                var references = new Dictionary<string, string>(mapping, StringComparer.OrdinalIgnoreCase);
                if (instanceMaps.TryGetValue(owner, out var owned))
                    foreach (var entry in owned) references[entry.Key] = entry.Value;
                return new YamlDocument { ClassId = d.ClassId, FileId = d.FileId,
                    TypeName = d.TypeName, Stripped = d.Stripped, Root = Rewrite(d.Root, references) };
            })));
            return mapping;
        }
    }

    private static IEnumerable<string> References(YamlNode node)
    {
        if (node == null) yield break;
        if (node.Guid != null) yield return node.Guid;
        foreach (var child in node.Map?.Values.AsEnumerable() ?? node.Seq ?? Enumerable.Empty<YamlNode>())
            foreach (string guid in References(child)) yield return guid;
    }

    private static YamlNode Rewrite(YamlNode node, Dictionary<string, string> mapping)
    {
        if (node == null) return null;
        if (node.Map != null)
            return new YamlNode { Map = node.Map.ToDictionary(entry => entry.Key, entry =>
                entry.Key == "guid" && mapping.TryGetValue(entry.Value.AsString() ?? "", out string guid)
                    ? new YamlNode { ScalarValue = guid } : Rewrite(entry.Value, mapping)) };
        if (node.Seq != null) return new YamlNode { Seq = node.Seq.Select(child => Rewrite(child, mapping)).ToList() };
        return new YamlNode { ScalarValue = node.ScalarValue };
    }
}
