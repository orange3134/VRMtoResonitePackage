using System.Reflection;

namespace VrmToResonitePackage;

/// <summary>
/// The application version, derived at build time from the build date/time (see the csproj
/// &lt;Version&gt;). Shown in the console header, the conversion log and the GUI title so that
/// user bug reports can be tied to a specific build.
/// </summary>
internal static class AppVersion
{
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // Strip any build-metadata suffix (e.g. "+<commit>") to keep just the date/time version.
            int plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
