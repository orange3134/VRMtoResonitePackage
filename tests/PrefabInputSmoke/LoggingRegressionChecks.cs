using System.Reflection;
using VrmToResonitePackage.Vrchat;

internal static class LoggingRegressionChecks
{
    public static void Run()
    {
        var assembly = typeof(VrchatAvatar).Assembly;
        var teeType = assembly.GetType("VrmToResonitePackage.TeeTextWriter")!;
        using var primary = new StringWriter();
        var log = new StreamWriter(new MemoryStream());
        var tee = (TextWriter)Activator.CreateInstance(teeType, primary, log)!;
        tee.WriteLine("before");
        tee.Dispose();
        log.Dispose();
        Parallel.For(0, 100, _ => tee.WriteLine("after"));
        if (!primary.ToString().Contains("after")) throw new Exception("Closed tee lost the original console");
        Console.WriteLine("PASS: Captured console tee cannot write to a closed log");

        var logType = Type.GetType("Elements.Core.UniLog, Elements.Core", throwOnError: true)!;
        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        var detach = (Action)assembly.GetType("VrmToResonitePackage.Converter")!
            .GetMethod("HookLogging", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { writer })!;
        Delegate callback;
        try
        {
            callback = ((Delegate)logType.GetField("OnLog", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!).GetInvocationList().Last();
            callback.DynamicInvoke("before");
        }
        finally { detach(); }
        writer.Dispose();
        Parallel.For(0, 100, _ => callback.DynamicInvoke("after"));
        Console.WriteLine("PASS: Detached engine log callback cannot write to a closed log");
    }
}
