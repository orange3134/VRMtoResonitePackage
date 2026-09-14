namespace VrmToResonitePackage.Expressions;

/// <summary>Serializable, engine-independent subset shared by the importer and scene compiler.</summary>
public sealed class ExpressionModel
{
    public Dictionary<string, ExpressionParameter> Parameters { get; } = new(StringComparer.Ordinal);
    public List<ExpressionClip> Clips { get; } = new();
    public List<ExpressionLayer> Layers { get; } = new();
    public List<ExpressionMenuControl> Menu { get; } = new();
    public List<string> Diagnostics { get; } = new();
}

public sealed record ExpressionParameter(string Name, int Type, float Default, bool Saved = false);
public sealed record ExpressionBinding(string Path, string Shape)
{
    public string Key => Path + "\n" + Shape;
}
public sealed record ExpressionKey(float Time, float Value, float InSlope, float OutSlope);
public sealed class ExpressionCurve
{
    public ExpressionBinding Binding { get; init; }
    public List<ExpressionKey> Keys { get; } = new();

    // Unity's unweighted Hermite interpolation. Stepped segments use infinite slopes.
    public float Sample(float time)
    {
        if (time <= Keys[0].Time) return Keys[0].Value;
        for (int i = 1; i < Keys.Count; i++)
        {
            var b = Keys[i];
            if (time >= b.Time) continue;
            var a = Keys[i - 1];
            if (float.IsInfinity(a.OutSlope) || float.IsInfinity(b.InSlope)) return a.Value;
            float duration = b.Time - a.Time, t = (time - a.Time) / duration;
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Value + (t3 - 2 * t2 + t) * duration * a.OutSlope
                + (-2 * t3 + 3 * t2) * b.Value + (t3 - t2) * duration * b.InSlope;
        }
        return Keys[^1].Value;
    }
}

public sealed class ExpressionClip
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Source { get; init; }
    public float Duration { get; set; }
    public bool Loop { get; init; }
    public List<ExpressionCurve> Curves { get; } = new();
}

public sealed class ExpressionLayer
{
    public string Id { get; init; }
    public string Name { get; init; }
    public float Weight { get; init; } = 1;
    public int DefaultState { get; set; }
    public List<ExpressionState> States { get; } = new();
    public List<ExpressionTransition> Entry { get; } = new();
    public List<ExpressionTransition> Transitions { get; } = new();
}

public sealed record ExpressionState(string Name, string ClipId, float Speed, bool WriteDefaults, string TimeParameter = null);
public sealed class ExpressionTransition
{
    public int Source { get; init; } = -1;
    public int Destination { get; init; }
    public bool CanTransitionToSelf { get; init; }
    public bool HasExitTime { get; init; }
    public float ExitTime { get; init; }
    public float Duration { get; init; }
    public bool FixedDuration { get; init; }
    public float Offset { get; init; }
    public List<ExpressionCondition> Conditions { get; } = new();
}

public sealed record ExpressionCondition(string Parameter, int Mode, float Threshold)
{
    public bool Matches(float value) => Mode switch
    {
        1 => value != 0, 2 => value == 0, 3 => value > Threshold, 4 => value < Threshold,
        6 => value == Threshold, 7 => value != Threshold, _ => false,
    };
}

public sealed class ExpressionMenuControl
{
    public string Name { get; init; }
    public string Parameter { get; init; }
    public float Value { get; init; }
    public int Type { get; init; }
    public List<ExpressionMenuControl> Children { get; } = new();
}
