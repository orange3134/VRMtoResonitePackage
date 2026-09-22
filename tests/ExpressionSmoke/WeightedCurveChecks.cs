using System.Globalization;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class WeightedCurveChecks
{
    public static void Run()
    {
        foreach (var (outWeight, inWeight) in new[] { (0.76f, 0.76f), (0f, 0f), (0.9f, 0.9f), (0.05f, 0.9f), (1f / 3, 1f / 3) })
        {
            var curve = new ExpressionCurve { Binding = new("Face", "Smile") };
            curve.Keys.Add(new(0, 0.2f, 0, 1.7f)); curve.Keys.Add(new(2, 0.8f, -0.3f, 0));
            var yaml = Keys(outWeight, inWeight);
            Check(UnityWeightedExpressionCurve.TryBake(curve, yaml), $"valid weighted segment accepted ({outWeight}, {inWeight})");
            // Independent parametric Bezier oracle: no inverse-time solver or baker used here.
            for (int i = 0; i <= 10000; i++)
            {
                double u = i / 10000d, v = 1 - u;
                float time = (float)(3 * v * v * u * (2 * outWeight) + 3 * v * u * u * (2 - 2 * inWeight) + 2 * u * u * u);
                float value = (float)(v * v * v * 0.2 + 3 * v * v * u * (0.2 + 2 * outWeight * 1.7) +
                    3 * v * u * u * (0.8 + 2 * inWeight * 0.3) + u * u * u * 0.8);
                Check(Math.Abs(curve.Sample(time) - value) < 0.00005f, "weighted Bezier error bound");
            }
        }
        var singular = new ExpressionCurve { Binding = new("Face", "Smile") };
        singular.Keys.Add(new(0, 0.2f, 0, 1.7f)); singular.Keys.Add(new(2, 0.8f, -0.3f, 0));
        Check(!UnityWeightedExpressionCurve.TryBake(singular, Keys(1, 1)) && singular.Keys.Count == 2,
            "singular time curve that exceeds float precision is rejected atomically");
        var invalid = new ExpressionCurve { Binding = new("Face", "Smile") };
        invalid.Keys.Add(new(0, 0, 0, 0)); invalid.Keys.Add(new(1, 1, 0, 0));
        Check(!UnityWeightedExpressionCurve.TryBake(invalid, Keys(-0.1f, 0.5f)), "negative weight rejected");
        Check(!UnityWeightedExpressionCurve.TryBake(invalid, Keys(1.1f, 0.5f)), "oversized weight rejected");
        Check(!UnityWeightedExpressionCurve.TryBake(invalid, Keys(float.NaN, 0.5f)), "nonfinite weight rejected");
        invalid.Keys[0] = invalid.Keys[0] with { OutSlope = float.PositiveInfinity };
        Check(UnityWeightedExpressionCurve.TryBake(invalid, Keys(0.76f, 0.76f)) && invalid.Sample(0.9f) == 0,
            "weighted metadata does not change stepped segments");
        Console.WriteLine("PASS: weighted Unity curves match parametric Bezier; invalid weights and holds checked");
    }
    private static List<YamlNode> Keys(float output, float input) => UnityScene.Parse(
        "--- !u!74 &1\nAnimationClip:\n  keys:\n  - weightedMode: 2\n    outWeight: " + output.ToString(CultureInfo.InvariantCulture) +
        "\n  - weightedMode: 1\n    inWeight: " + input.ToString(CultureInfo.InvariantCulture) + "\n").Doc(1).Root["keys"].Seq;
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
