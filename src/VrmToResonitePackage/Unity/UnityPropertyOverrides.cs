using System.Globalization;

namespace VrmToResonitePackage.Unity;

/// <summary>Applies Unity serialized property paths without depending on a component type.</summary>
internal static class UnityPropertyOverrides
{
    private const int MaxArrayLength = 1_000_000;

    public static YamlNode Clone(YamlNode node) => node == null ? null : new YamlNode
    {
        ScalarValue = node.ScalarValue,
        Map = node.Map?.ToDictionary(pair => pair.Key, pair => Clone(pair.Value)),
        Seq = node.Seq?.Select(Clone).ToList(),
    };

    public static YamlDocument Clone(YamlDocument document) => new()
    {
        ClassId = document.ClassId, FileId = document.FileId, TypeName = document.TypeName,
        Stripped = document.Stripped, Root = Clone(document.Root),
    };

    public static void Apply(YamlNode root, YamlNode modification, string declaringGuid)
    {
        string path = modification["propertyPath"]?.AsString();
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Empty prefab property path.");
        string[] parts = path.Split('.');
        Set(root, 0);

        void Set(YamlNode parent, int offset)
        {
            if (parent?.Map == null)
                throw new InvalidDataException($"Cannot traverse prefab property: {path}");
            string key = parts[offset];
            parent.Map.TryGetValue(key, out var current);
            if (offset == parts.Length - 1)
            {
                parent.Map[key] = Value(current);
                return;
            }
            if (parts[offset + 1] == "Array")
            {
                if (offset + 2 >= parts.Length) throw new InvalidDataException($"Invalid array property: {path}");
                // Unity serializes some int arrays as a little-endian hexadecimal scalar.
                bool packed = current?.ScalarValue is { Length: > 0 } scalar &&
                    scalar.Length % 8 == 0 && scalar.All(Uri.IsHexDigit);
                var items = current?.Seq ?? (packed ? Decode(current.ScalarValue) : new List<YamlNode>());
                string element = parts[offset + 2];
                bool resize = element == "size";
                int size;
                if (resize)
                    size = int.TryParse(modification["value"]?.AsString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int length) ? length : -1;
                else if (element.StartsWith("data[", StringComparison.Ordinal) && element.EndsWith(']') &&
                    int.TryParse(element[5..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < MaxArrayLength)
                    size = index + 1;
                else throw new InvalidDataException($"Invalid array property: {path}");
                if (size < 0 || size > MaxArrayLength) throw new InvalidDataException($"Invalid prefab array size: {path}");
                YamlNode template = items.FirstOrDefault();
                while (items.Count < size) items.Add(Default(template, packed));
                if (resize)
                {
                    if (items.Count > size) items.RemoveRange(size, items.Count - size);
                }
                else if (offset + 3 == parts.Length) items[size - 1] = Value(items[size - 1]);
                else
                {
                    if (items[size - 1]?.Map == null) items[size - 1] = new YamlNode { Map = new() };
                    Set(items[size - 1], offset + 3);
                }
                parent.Map[key] = packed ? new YamlNode { ScalarValue = Convert.ToHexString(items
                    .SelectMany(item => BitConverter.GetBytes(item.AsInt())).ToArray()).ToLowerInvariant() }
                    : new YamlNode { Seq = items };
                return;
            }
            if (current?.Map == null) parent.Map[key] = current = new YamlNode { Map = new() };
            Set(current, offset + 1);
        }

        YamlNode Value(YamlNode current)
        {
            var reference = modification["objectReference"];
            bool isReference = current?.FileID != null || reference?.Guid != null ||
                (reference?.FileID ?? 0) != 0 ||
                ((current == null || (current.Map == null && current.Seq == null && current.ScalarValue == null)) &&
                 reference?.FileID != null && string.IsNullOrEmpty(modification["value"]?.AsString()));
            if (!isReference) return Clone(modification["value"]) ?? new YamlNode { ScalarValue = "" };
            var result = Clone(reference) ?? new YamlNode { Map = new() { ["fileID"] = new() { ScalarValue = "0" } } };
            // References introduced by an outer variant belong to that variant, not the base document.
            if ((result.FileID ?? 0) != 0 && string.IsNullOrEmpty(result.Guid))
                result.Map["guid"] = new YamlNode { ScalarValue = declaringGuid };
            return result;
        }
    }

    private static List<YamlNode> Decode(string hex)
    {
        byte[] data = Convert.FromHexString(hex);
        return Enumerable.Range(0, data.Length / 4).Select(i => new YamlNode
            { ScalarValue = BitConverter.ToInt32(data, i * 4).ToString(CultureInfo.InvariantCulture) }).ToList();
    }

    private static YamlNode Default(YamlNode template, bool packed)
        => packed ? new() { ScalarValue = "0" } : template?.FileID != null
            ? new() { Map = new() { ["fileID"] = new() { ScalarValue = "0" } } }
            : template?.Map != null ? new() { Map = new() } : template?.Seq != null
                ? new() { Seq = new() } : new();
}
