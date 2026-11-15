using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace VrmToResonitePackage.Unity;

/// <summary>Reads editor SerializedFiles using their embedded type trees, without Unity or a class database.</summary>
internal sealed class UnitySerializedAssets : IDisposable
{
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance file;
    private readonly string guid;
    private readonly Dictionary<long, YamlDocument> documents = new();

    public UnitySerializedAssets(UnityAsset asset)
    {
        guid = asset.SourceGuid ?? asset.Guid;
        file = manager.LoadAssetsFile(asset.DiskPath, false);
        if (!file.file.Metadata.TypeTreeEnabled)
        {
            manager.UnloadAll();
            throw new InvalidDataException($"Unity asset has no embedded type tree: {asset.LogicalPath}");
        }
    }

    public int ClassId(long id) => file.file.GetAssetInfo(id)?.TypeId ?? 0;
    public IEnumerable<long> SceneIds => file.file.AssetInfos.Where(a => a.TypeId is not (43 or 28)).Select(a => a.PathId);

    public YamlDocument Read(long id)
    {
        if (documents.TryGetValue(id, out var cached)) return cached;
        var info = file.file.GetAssetInfo(id);
        if (info == null) return null;
        var field = manager.GetBaseField(file, info);
        var doc = new YamlDocument { ClassId = info.TypeId, FileId = id, TypeName = field.TypeName, Root = ConvertField(field) };
        documents.Add(id, doc);
        return doc;
    }

    private YamlNode ConvertField(AssetTypeValueField field)
    {
        if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
        {
            long id = field["m_PathID"].AsLong;
            int external = field["m_FileID"].AsInt;
            var map = new Dictionary<string, YamlNode> { ["fileID"] = Scalar(id) };
            if (id != 0)
            {
                if (external < 0 || external > file.file.Metadata.Externals.Count)
                    throw new InvalidDataException("Invalid Unity external reference index.");
                if (external != 0 || ClassId(id) is 21 or 28 or 43 or 74 or 91)
                    map["guid"] = Scalar(external == 0 ? guid : file.file.Metadata.Externals[external - 1].Guid.ToString());
                map["type"] = Scalar(2);
            }
            return new() { Map = map };
        }
        if (field.Value?.ValueType == AssetValueType.ByteArray)
            return new() { Bytes = field.AsByteArray };
        if (field.Value?.ValueType == AssetValueType.String) return Scalar(field.AsString);
        if (field.TemplateField.IsArray)
            return new() { Seq = field.Children.Select(ConvertField).ToList() };
        if (field.Children.Count == 1 && field.Children[0].TemplateField.IsArray)
        {
            var array = field.Children[0];
            if (array.Value?.ValueType == AssetValueType.ByteArray) return new() { Bytes = array.AsByteArray };
            // Unity's serialized maps are arrays of first/second pairs; YAML uses one-key maps.
            if (field.TypeName == "map" && array.Children.All(pair => pair["first"].Value?.ValueType == AssetValueType.String))
                return new() { Seq = array.Children.Select(pair => new YamlNode { Map = new()
                    { [pair["first"].AsString] = ConvertField(pair["second"]) } }).ToList() };
            return new() { Seq = array.Children.Select(ConvertField).ToList() };
        }
        if (field.Children.Count > 0)
            return new() { Map = field.Children.ToDictionary(child => child.FieldName, ConvertField) };
        return Scalar(field.Value?.ValueType switch
        {
            AssetValueType.Bool => field.AsBool ? "1" : "0",
            AssetValueType.Float => field.AsFloat.ToString("R", CultureInfo.InvariantCulture),
            AssetValueType.Double => field.AsDouble.ToString("R", CultureInfo.InvariantCulture),
            AssetValueType.UInt64 => field.AsULong.ToString(CultureInfo.InvariantCulture),
            null or AssetValueType.None => "",
            _ => field.AsLong.ToString(CultureInfo.InvariantCulture),
        });
    }

    private static YamlNode Scalar(object value) => new() { ScalarValue = System.Convert.ToString(value, CultureInfo.InvariantCulture) };
    public void Dispose() => manager.UnloadAll();
}
