using System.Text.Json;

namespace VrmToResonitePackage.Unity;

internal static class UnityPackageCache
{
    public static HashSet<string> Select(string project)
    {
        string cache = Path.Combine(project, "Library", "PackageCache");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(cache)) return result;
        string lockPath = Path.Combine(project, "Packages", "packages-lock.json");
        using var locked = File.Exists(lockPath) ? JsonDocument.Parse(File.ReadAllText(lockPath)) : null;
        var dependencies = locked?.RootElement.GetProperty("dependencies");
        var folders = Directory.EnumerateDirectories(cache).Where(p =>
            (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0);
        foreach (var group in folders.GroupBy(p => Path.GetFileName(p).Split('@')[0]))
        {
            if (Directory.Exists(Path.Combine(project, "Packages", group.Key))) continue;
            var candidates = group.ToList();
            if (dependencies.HasValue)
            {
                if (!dependencies.Value.TryGetProperty(group.Key, out var dependency)) continue;
                string source = dependency.GetProperty("source").GetString();
                if (source != "registry" && source != "git" && source != "builtin") continue;
                string version = dependency.GetProperty("version").GetString();
                string hash = dependency.TryGetProperty("hash", out var h) ? h.GetString() : null;
                candidates = candidates.Where(p => Matches(p, source, version, hash)).ToList();
                if (candidates.Count == 0)
                    throw new InvalidDataException($"使用中のパッケージキャッシュが見つかりません: {group.Key} ({version})。Unityでパッケージを解決してください。");
            }
            if (candidates.Count != 1)
                throw new InvalidDataException($"パッケージキャッシュのバージョンを特定できません: {group.Key}。Unityでパッケージを解決してください。");
            result.Add(Path.GetFileName(candidates[0]));
        }
        return result;
    }

    private static bool Matches(string path, string source, string version, string hash)
    {
        string suffix = Path.GetFileName(path).Split('@').Last();
        if (source == "git")
            return !string.IsNullOrEmpty(hash) && suffix.Length >= 7 && hash.StartsWith(suffix, StringComparison.OrdinalIgnoreCase);
        string manifest = Path.Combine(path, "package.json");
        if (!File.Exists(manifest)) return suffix == version;
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        return json.RootElement.TryGetProperty("version", out var v) && v.GetString() == version;
    }
}
