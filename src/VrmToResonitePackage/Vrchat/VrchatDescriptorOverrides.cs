using System.Text.RegularExpressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Folds face settings along the selected descriptor's prefab inheritance path.</summary>
internal static class VrchatDescriptorOverrides
{
    private static readonly Regex LayerPath = new(@"^baseAnimationLayers\.Array\.data\[(\d+)\]\.(animatorController|isDefault|type)$");

    public static YamlDocument Resolve(UnityPackage package, string selectedGuid, YamlDocument descriptor)
    {
        var resolved = Visit(selectedGuid, new(StringComparer.OrdinalIgnoreCase));
        if (resolved == null) throw new InvalidDataException("選択したアバターのDescriptor継承元を解決できません。");
        return new YamlDocument { ClassId = descriptor.ClassId, FileId = descriptor.FileId,
            TypeName = descriptor.TypeName, Stripped = descriptor.Stripped, Root = resolved.Value.Root };

        (YamlNode Root, HashSet<(string Guid, long Id)> Aliases)? Visit(string guid, HashSet<string> visited)
        {
            if (guid == null || !visited.Add(guid)) return null;
            var asset = package.ByGuid(guid);
            if (asset?.Extension is not (".prefab" or ".unity")) return null;
            var scene = package.ReadScene(asset);
            if (ReferenceEquals(scene.Doc(descriptor.FileId), descriptor))
                return (Clone(descriptor.Root), new() { (guid, descriptor.FileId) });

            (YamlNode Root, HashSet<(string Guid, long Id)> Aliases)? result = null;
            foreach (var instance in scene.Documents.Values.Where(d => d.ClassId == 1001))
            {
                string childGuid = instance.Root?["m_SourcePrefab"]?.Guid;
                var child = Visit(childGuid, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
                if (child == null) continue;
                if (result != null) throw new InvalidDataException("同じDescriptorを含むPrefabインスタンスが複数あり、表情設定を特定できません。");
                var (root, aliases) = child.Value;
                foreach (var modification in instance.Root?["m_Modification"]?["m_Modifications"]?.Seq ?? new())
                {
                    var target = modification["target"];
                    if (aliases.Contains((target?.Guid, target?.FileID ?? 0))) Apply(root, modification);
                }
                // Outer variants can target either an explicit stripped document or an
                // omitted document whose local ID is derived from the PrefabInstance ID.
                foreach (var alias in aliases.Where(a => a.Guid == childGuid).ToArray())
                    aliases.Add((guid, (alias.Id ^ instance.FileId) & long.MaxValue));
                foreach (var document in scene.Documents.Values)
                {
                    var source = document.Root?["m_CorrespondingSourceObject"];
                    if (aliases.Contains((source?.Guid, source?.FileID ?? 0)) &&
                        (document.Root?["m_PrefabInstance"]?.FileID ?? instance.FileId) == instance.FileId)
                        aliases.Add((guid, document.FileId));
                }
                result = (root, aliases);
            }
            return result;
        }
    }

    private static void Apply(YamlNode root, YamlNode modification)
    {
        string path = modification["propertyPath"]?.AsString();
        if (path == "lipSync")
        {
            root.Map[path] = Clone(modification["value"]);
            return;
        }
        var match = LayerPath.Match(path ?? "");
        if (path != "baseAnimationLayers.Array.size" && !match.Success) return;
        if (root["baseAnimationLayers"]?.Seq == null)
            root.Map["baseAnimationLayers"] = new YamlNode { Seq = new() };
        var layers = root["baseAnimationLayers"].Seq;
        int size = path == "baseAnimationLayers.Array.size" ? modification["value"]?.AsInt(-1) ?? -1
            : int.Parse(match.Groups[1].Value) + 1;
        if (size < 0 || size > 1024) throw new InvalidDataException("Animatorレイヤーの上書きサイズが不正です。");
        while (layers.Count < size) layers.Add(new YamlNode { Map = new() });
        if (path == "baseAnimationLayers.Array.size")
        {
            if (layers.Count > size) layers.RemoveRange(size, layers.Count - size);
            return;
        }
        string property = match.Groups[2].Value;
        if (layers[size - 1].Map == null) layers[size - 1] = new YamlNode { Map = new() };
        layers[size - 1].Map[property] = Clone(modification[property == "animatorController" ? "objectReference" : "value"]);
    }

    private static YamlNode Clone(YamlNode node) => node == null ? YamlNode.Empty : new YamlNode
    {
        ScalarValue = node.ScalarValue,
        Map = node.Map?.ToDictionary(entry => entry.Key, entry => Clone(entry.Value)),
        Seq = node.Seq?.Select(Clone).ToList(),
    };
}
