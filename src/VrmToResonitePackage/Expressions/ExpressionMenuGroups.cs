namespace VrmToResonitePackage.Expressions;

/// <summary>Partitions the selectable catalog without duplicating gesture or menu aliases.</summary>
internal static class ExpressionMenuGroups
{
    internal static IReadOnlyList<string[]> Build(IEnumerable<string> clips, IReadOnlyList<string> pairs)
    {
        var left = Enumerable.Range(0, 8).Select(i => pairs[i * 8]).ToHashSet();
        var right = Enumerable.Range(0, 8).Select(i => pairs[i]).ToHashSet();
        var mapped = pairs.ToHashSet();
        int Category(string id) => (left.Contains(id), right.Contains(id)) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => 2,
            _ => mapped.Contains(id) ? 3 : 4
        };
        // Keep shared, left-only, right-only, combined and unmapped poses separate.
        // Preserve catalog order within each category, including duplicate display names.
        return clips.Distinct().GroupBy(Category).OrderBy(group => group.Key)
            .SelectMany(group => group.Chunk(8)).ToArray();
    }
}
