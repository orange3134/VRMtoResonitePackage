using VrmToResonitePackage.Expressions;

internal static class HandPriorityChecks
{
    public static void Run()
    {
        ExpressionClip Clip(float value, string id = "pose")
        {
            var clip = new ExpressionClip { Id = id };
            var curve = new ExpressionCurve { Binding = new("Face", "Smile") };
            curve.Keys.Add(new(0, value, 0, 0)); clip.Curves.Add(curve); return clip;
        }
        var baseline = Clip(0.2f);
        ExpressionClip[] Table(Func<int, int, float?> value) => Enumerable.Range(0, 64)
            .Select(i => value(i / 8, i % 8) is float v ? Clip(v, "unique-" + i) : null).ToArray();
        void Check(ExpressionClip[] table, int hand, string name)
        {
            if (ExpressionHandPriority.PreferredHand(table, baseline) != hand) throw new Exception(name);
        }
        Check(Table((l, r) => r > 0 ? 0.8f : l > 0 ? 0.4f : 0.2f), 1, "right wins conflicts despite distinct clip IDs");
        Check(Table((l, r) => l > 0 ? 0.4f : r > 0 ? 0.8f : 0.2f), 0, "left wins conflicts");
        Check(Table((l, r) => r >= 2 ? r * 0.1f + 0.2f : 0.2f), 1, "right-only sparse set");
        Check(Table((l, r) => l >= 2 ? l * 0.1f + 0.2f : 0.2f), 0, "left-only sparse set");
        Check(Table((l, r) => l + r > 0 ? 0.4f : 0.2f), 0, "identical expressions do not imply a winner");
        Check(Table((l, r) => l > 0 && r > 0 ? 1 : l > 0 ? 0.4f : r > 0 ? 0.8f : 0.2f), 0, "combined expressions do not imply a winner");
        Check(Table((l, r) => l > 0 && r > 0 ? (l <= 3 ? 0.4f : 0.8f) : l > 0 ? 0.4f : r > 0 ? 0.8f : 0.2f), 1, "mixed priorities use majority");
        Check(Table((l, r) => l > 0 ? null : r > 0 ? 0.8f : 0.2f), 1, "unresolved pairs are not winning poses");
        Check(Table((l, r) => null), 0, "unassigned table keeps Left");
        var sparse = Table((l, r) => r == 1 ? 0.8f : 0.2f);
        sparse[0] = new ExpressionClip();
        Check(sparse, 1, "missing curve uses authored baseline");
        Console.WriteLine("PASS: keyboard hand priority, left/right winners, sparse/unknown tables, equal and combined poses, majority and ties");
    }
}
