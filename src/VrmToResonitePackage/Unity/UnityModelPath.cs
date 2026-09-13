namespace VrmToResonitePackage.Unity;

/// <summary>Matches Unity model identities across Assimp's transform helper hierarchy.</summary>
internal static class UnityModelPath
{
    public static bool IsTransformHelper(string name)
        => name?.Contains("_$AssimpFbx$_", StringComparison.Ordinal) == true;

    public static string Normalize(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Helpers carry real transforms but have no Unity object identity. Keep a terminal
        // helper distinct, so it cannot become an additional match for its authored parent.
        return string.Join("/", parts.Where((part, index) =>
            !(index == 0 && part == "RootNode") &&
            (index == parts.Length - 1 || !IsTransformHelper(part))));
    }
}
