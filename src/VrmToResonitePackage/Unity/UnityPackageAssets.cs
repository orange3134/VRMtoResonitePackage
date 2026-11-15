namespace VrmToResonitePackage.Unity;

public sealed partial class UnityPackage
{
    private readonly Dictionary<string, UnitySerializedAssets> _serializedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<YamlDocument>> _nativeYamlDocuments = new(StringComparer.OrdinalIgnoreCase);
    private string _nativeAssetDirectory;

    private UnitySerializedAssets SerializedFile(UnityAsset asset)
    {
        if (asset?.HasContent != true || asset.Extension is not (".asset" or ".mat" or ".anim" or ".controller")) return null;
        if (_serializedFiles.TryGetValue(asset.DiskPath, out var file)) return file;
        using var stream = File.OpenRead(asset.DiskPath);
        // SerializedFile headers begin with a big endian metadata size (zero in v22).
        file = stream.ReadByte() == 0 ? new UnitySerializedAssets(asset) : null;
        _serializedFiles.Add(asset.DiskPath, file);
        return file;
    }

    internal YamlDocument ReadAssetDocument(string guid, long id)
    {
        var asset = ByGuid(guid);
        var binary = SerializedFile(asset);
        return binary != null ? binary.Read(id) : asset?.HasContent == true ? RawDocuments(asset).FirstOrDefault(d => d.FileId == id) : null;
    }

    internal UnityScene ReadController(YamlNode reference)
    {
        var asset = ByGuid(reference?.Guid);
        if (asset?.HasContent != true || asset.Extension is not (".controller" or ".asset")) return null;
        var scene = ReadScene(asset);
        long id = reference.FileID ?? (asset.Extension == ".controller" && scene.Documents.Values.Count(d => d.ClassId == 91) == 1
            ? scene.Documents.Values.Single(d => d.ClassId == 91).FileId : 0);
        if (scene.Doc(id)?.ClassId != 91) return null;
        return UnityScene.FromDocuments(scene.Documents.Values.Where(d => d.ClassId != 91 || d.FileId == id));
    }

    private UnityScene ReadAssetScene(UnityAsset asset)
    {
        var binary = SerializedFile(asset);
        var documents = asset.SubAssetId is long id ? new List<YamlDocument> { ReadAssetDocument(asset.SourceGuid, id) } :
            binary == null ? RawDocuments(asset) : binary.SceneIds.Select(binary.Read).ToList();
        foreach (var document in documents) NormalizeAssetReferences(document.Root, asset.SourceGuid ?? asset.Guid);
        return UnityScene.FromDocuments(documents);
    }

    private List<YamlDocument> RawDocuments(UnityAsset asset)
    {
        if (!_nativeYamlDocuments.TryGetValue(asset.DiskPath, out var documents))
            _nativeYamlDocuments.Add(asset.DiskPath, documents = UnityYaml.ParseDocuments(ReadText(asset)));
        return documents;
    }

    // Existing material/texture caches use string keys. Include fileID for native subassets;
    // keep scene/animation references in their containing file's identity space.
    private void NormalizeAssetReferences(YamlNode node, string owner)
    {
        if (node?.Map != null)
        {
            if (node.FileID is long id && id != 0)
            {
                var source = ByGuid(node.Guid ?? owner);
                if (source?.Extension == ".asset")
                {
                    int type = SerializedFile(source)?.ClassId(id) ??
                        RawDocuments(source).FirstOrDefault(d => d.FileId == id)?.ClassId ?? 0;
                    if (type is 21 or 28)
                        node.Map["guid"] = new() { ScalarValue = $"{source.Guid}@{id}" };
                    else if (type == 74 && node.Guid == null)
                        // YAML stores same-file clips as local PPtrs; clip readers need the
                        // containing asset just like binary references. Graph nodes stay local.
                        node.Map["guid"] = new() { ScalarValue = source.Guid };
                }
                return;
            }
            foreach (var child in node.Map.Values) NormalizeAssetReferences(child, owner);
        }
        if (node?.Seq != null)
            foreach (var child in node.Seq) NormalizeAssetReferences(child, owner);
    }

    private UnityAsset ResolveSubAsset(string key)
    {
        if (key == null) return null;
        int split = key.LastIndexOf('@');
        if (split < 0 || !long.TryParse(key.AsSpan(split + 1), out long id)) return null;
        var source = ByGuid(key[..split]);
        if (source == null) return null;
        var doc = ReadAssetDocument(source.Guid, id);
        if (doc == null) throw new InvalidDataException($"Unity subasset missing: {key}");
        var alias = new UnityAsset { Guid = key, SourceGuid = source.Guid, SubAssetId = id,
            DiskPath = source.DiskPath, MetaPath = source.MetaPath,
            LogicalPath = source.LogicalPath + "/" + id + (doc.ClassId == 21 ? ".mat" : doc.ClassId == 28 ? ".png" : ".asset") };
        if (doc.ClassId == 28)
        {
            _nativeAssetDirectory ??= Path.Combine(Path.GetTempPath(), "ResoPon", "native_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_nativeAssetDirectory);
            alias.DiskPath = Path.Combine(_nativeAssetDirectory, source.Guid + "_" + id + ".png");
            alias.MetaPath = alias.DiskPath + ".meta";
            UnityNativeTexture.WritePng(doc.Root, alias.DiskPath);
            var settings = doc.Root["m_TextureSettings"];
            File.WriteAllText(alias.MetaPath, $"TextureImporter:\n  mipmaps:\n    sRGBTexture: {doc.Root["m_ColorSpace"]?.AsInt(1) ?? 1}\n  textureSettings:\n    filterMode: {settings?["m_FilterMode"]?.AsInt(1) ?? 1}\n    wrapU: {settings?["m_WrapU"]?.AsInt() ?? 0}\n    wrapV: {settings?["m_WrapV"]?.AsInt() ?? 0}\n");
        }
        _byGuid.Add(key, alias);
        return alias;
    }
}
