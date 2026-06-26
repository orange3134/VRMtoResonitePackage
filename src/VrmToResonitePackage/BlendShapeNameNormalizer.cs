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
