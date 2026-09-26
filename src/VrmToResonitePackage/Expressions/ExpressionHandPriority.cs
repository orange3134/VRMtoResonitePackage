namespace VrmToResonitePackage.Expressions;

// Infer the convenient keyboard hand from the exported poses, independent of
// importer, state names and clip IDs. This never changes the gesture table.
internal static class ExpressionHandPriority
{
    internal static int PreferredHand(IReadOnlyList<ExpressionClip> pairs, ExpressionClip baseline)
    {
        if (pairs.Count != 64) throw new ArgumentException("Expected 64 gesture pairs.", nameof(pairs));
        var baseValues = baseline.Curves.ToDictionary(c => c.Binding, c => c.Keys[^1].Value);
        var bindings = pairs.Where(c => c != null).SelectMany(c => c.Curves).Select(c => c.Binding)
            .Concat(baseValues.Keys).Distinct().OrderBy(b => b.Key, StringComparer.Ordinal).ToArray();
        float[] Pose(ExpressionClip clip)
        {
            if (clip == null) return null;
            var values = clip.Curves.ToDictionary(c => c.Binding, c => c.Keys[^1].Value);
            return bindings.Select(b => values.GetValueOrDefault(b, baseValues.GetValueOrDefault(b))).ToArray();
        }
        var poses = pairs.Select(Pose).ToArray();
        var neutral = poses[0] ?? Pose(baseline);
        bool Same(float[] a, float[] b) => a != null && b != null && a.SequenceEqual(b);
        bool Active(float[] pose) => pose != null && !Same(pose, neutral);
        int leftWins = 0, rightWins = 0;
        for (int left = 1; left < 8; left++) for (int right = 1; right < 8; right++)
        {
            var l = poses[left * 8]; var r = poses[right]; var both = poses[left * 8 + right];
            if (!Active(l) || !Active(r) || Same(l, r)) continue;
            if (Same(both, l)) leftWins++;
            else if (Same(both, r)) rightWins++;
        }
        if (leftWins != rightWins) return rightWins > leftWins ? 1 : 0;
        // With only one expressive hand there is no conflict to resolve. Prefer
        // the hand with more distinct solo poses; a genuine tie keeps Left.
        int Count(bool right) => Enumerable.Range(1, 7).Select(i => poses[right ? i : i * 8])
            .Where(Active).Select(p => string.Join(",", p.Select(v => BitConverter.SingleToInt32Bits(v == 0 ? 0 : v))))
            .Distinct(StringComparer.Ordinal).Count();
        return Count(true) > Count(false) ? 1 : 0;
    }
}
