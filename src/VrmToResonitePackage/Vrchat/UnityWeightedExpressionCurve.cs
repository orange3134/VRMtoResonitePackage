using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Bakes Unity weighted Bezier segments into bounded-error linear Hermite segments.</summary>
internal static class UnityWeightedExpressionCurve
{
    internal const double Tolerance = 0.00001; // normalized blendshape weight
    private readonly record struct Point(double X, double Y)
    {
        public static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    internal static bool TryBake(ExpressionCurve curve, IReadOnlyList<YamlNode> serialized)
    {
        var weights = new List<(double In, double Out)>();
        foreach (var k in serialized)
        {
            int mode = k["weightedMode"]?.AsInt() ?? 0;
            if (mode is < 0 or > 3) return false;
            double input = (mode & 1) == 0 ? 1d / 3 : k["inWeight"]?.AsFloat() ?? float.NaN;
            double output = (mode & 2) == 0 ? 1d / 3 : k["outWeight"]?.AsFloat() ?? float.NaN;
            if (!double.IsFinite(input) || !double.IsFinite(output) || input < 0 || input > 1 || output < 0 || output > 1) return false;
            weights.Add((input, output));
        }
        var baked = new List<ExpressionKey> { curve.Keys[0] };
        for (int i = 1; i < curve.Keys.Count; i++)
        {
            var a = curve.Keys[i - 1]; var b = curve.Keys[i];
            double w0 = weights[i - 1].Out, w1 = weights[i].In;
            if ((w0 == 1d / 3 && w1 == 1d / 3) || float.IsInfinity(a.OutSlope) || float.IsInfinity(b.InSlope))
            { baked.Add(b); continue; }
            double duration = b.Time - (double)a.Time;
            var points = new List<Point> { new(a.Time, a.Value) };
            if (!Split(points[0], new(a.Time + duration * w0, a.Value + duration * w0 * a.OutSlope),
                new(b.Time - duration * w1, b.Value - duration * w1 * b.InSlope), new(b.Time, b.Value), 0, points)) return false;
            for (int j = 1; j < points.Count; j++)
            {
                var previous = baked[^1];
                float time = (float)points[j].X, value = (float)points[j].Y;
                if (time <= previous.Time || !float.IsFinite(value)) return false;
                float slope = (value - previous.Value) / (time - previous.Time);
                if (!float.IsFinite(slope)) return false;
                baked[^1] = previous with { OutSlope = slope };
                baked.Add(new(time, value, slope, j == points.Count - 1 ? b.OutSlope : 0));
            }
            if (baked.Count > 65536) return false;
        }
        curve.Keys.Clear(); curve.Keys.AddRange(baked);
        return true;
    }

    private static bool Split(Point a, Point b, Point c, Point d, int depth, List<Point> output)
    {
        double slope = (d.Y - a.Y) / (d.X - a.X);
        double Error(Point p) => Math.Abs(p.Y - (a.Y + slope * (p.X - a.X)));
        // Bezier lies inside its control hull; these vertical errors bound the entire segment.
        if (Error(b) <= Tolerance && Error(c) <= Tolerance) { output.Add(d); return true; }
        if (depth >= 20 || output.Count > 65536) return false;
        var ab = Point.Mid(a, b); var bc = Point.Mid(b, c); var cd = Point.Mid(c, d);
        var abc = Point.Mid(ab, bc); var bcd = Point.Mid(bc, cd); var middle = Point.Mid(abc, bcd);
        return Split(a, ab, abc, middle, depth + 1, output) && Split(middle, bcd, cd, d, depth + 1, output);
    }
}
