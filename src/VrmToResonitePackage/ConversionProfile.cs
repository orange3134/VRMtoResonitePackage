using System.Diagnostics;

namespace VrmToResonitePackage;

/// <summary>Opt-in wall-clock checkpoints; nested scopes include their children's time.</summary>
internal sealed class ConversionProfile : IDisposable
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("RESOPON_PROFILE") == "1";
    private readonly string _name;
    private readonly long _start;
    private long _previous;

    private ConversionProfile(string name)
    {
        _name = name;
        _start = _previous = Stopwatch.GetTimestamp();
    }

    public static ConversionProfile Start(string name) => Enabled ? new(name) : null;

    public void Mark(string step)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_previous, now).TotalSeconds;
        double total = Stopwatch.GetElapsedTime(_start, now).TotalSeconds;
        _previous = now;
        Console.WriteLine(FormattableString.Invariant(
            $"[Profile] {_name} | {step} | seconds={seconds:F3} | total={total:F3}"));
    }

    public void Dispose() => Mark("end");
}
