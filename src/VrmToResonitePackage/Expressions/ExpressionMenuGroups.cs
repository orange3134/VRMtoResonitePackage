namespace VrmToResonitePackage.Expressions;

/// <summary>Partitions the selectable catalog without duplicating gesture or menu aliases.</summary>
internal static class ExpressionMenuGroups
{
    internal static IReadOnlyList<string[]> Build(IEnumerable<string> clips, IReadOnlyList<string> pairs, int gestureCount = 8)
    {
        var left = Enumerable.Range(0, gestureCount).Select(i => pairs[i * gestureCount]).ToHashSet();
        var right = Enumerable.Range(0, gestureCount).Select(i => pairs[i]).ToHashSet();
        var mapped = pairs.ToHashSet();
        int Category(string id) => (left.Contains(id), right.Contains(id)) switch
        {
            (true, _) => 0,
            (false, true) => 1,
            _ => mapped.Contains(id) ? 2 : 3
        };
        // Include shared poses with the left hand; keep right-only, combined and unmapped separate.
        // Preserve catalog order within each category, including duplicate display names.
        return clips.Distinct().GroupBy(Category).OrderBy(group => group.Key)
            .SelectMany(group => group.Chunk(8)).ToArray();
    }
}
