namespace VrmToResonitePackage;

internal static class BlendShapeNameNormalizer
{
    public static string CollapseRepeatedName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        string current = name;
        while (TryCollapseOnce(current, out string collapsed))
        {
            current = collapsed;
        }
        return current;
    }

    // Multi-material FBX imports can use shape-geometry names instead of channel names.
    // Use the actual FBX connection, never a dotted-name suffix guess.
    internal static Dictionary<string, string> ChannelAliases(IEnumerable<string> importedNames,
        IEnumerable<(string Channel, string Shape)> channels)
    {
        var imported = importedNames.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var source = channels.Where(c => !string.IsNullOrEmpty(c.Channel)).Select(c =>
            (Channel: CollapseRepeatedName(c.Channel), Shape: CollapseRepeatedName(c.Shape))).ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, count) in imported)
        {
            if (count != 1 || source.Any(c => c.Channel == name)) continue;
            var candidates = source.Where(c => c.Shape == name).ToArray();
            if (candidates.Length != 1) continue;
            string target = candidates[0].Channel;
            if (imported.ContainsKey(target) || source.Count(c => c.Channel == target) != 1) continue;
            result[name] = target;
        }
        return result;
    }

    private static bool TryCollapseOnce(string name, out string collapsed)
    {
        collapsed = null;
        int separator = name.Length / 2;
        if (name.Length < 3 ||
            name.Length % 2 == 0 ||
            name[separator] != '.')
        {
            return false;
        }

        ReadOnlySpan<char> left = name.AsSpan(0, separator);
        ReadOnlySpan<char> right = name.AsSpan(separator + 1);
        if (!left.SequenceEqual(right))
        {
            return false;
        }

        collapsed = name[..separator];
        return true;
    }
}
