using System.Formats.Tar;
using System.IO.Compression;

namespace VrmToResonitePackage.Unity;

/// <summary>One asset inside a .unitypackage, keyed by its Unity GUID.</summary>
public sealed class UnityAsset
{
    public string Guid { get; init; }

    /// <summary>Logical project-relative path, e.g. "Assets/Foo/Bar.prefab" (from the entry's "pathname").</summary>
    public string LogicalPath { get; init; }

    /// <summary>Path on disk where the asset's binary content ("asset" entry) was extracted, or null for folders.</summary>
    public string DiskPath { get; set; }

    /// <summary>Path on disk of the asset's ".meta" importer settings ("asset.meta" entry), or null.</summary>
    public string MetaPath { get; set; }

    public string Extension => Path.GetExtension(LogicalPath ?? "").ToLowerInvariant();

    public bool HasContent => DiskPath != null && File.Exists(DiskPath);
}

/// <summary>
/// Extracts a .unitypackage (a gzip-compressed tar of <c>&lt;guid&gt;/{asset, asset.meta, pathname}</c>
/// entries) to a temporary directory and exposes the contained assets by GUID, extension and path.
/// Disposing removes the temporary directory.
/// </summary>
public sealed class UnityPackage : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, UnityAsset> _byGuid;
    private readonly Dictionary<string, string> _textByGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityScene> _sceneByGuid = new(StringComparer.OrdinalIgnoreCase);

    private UnityPackage(string root, Dictionary<string, UnityAsset> byGuid)
    {
        _root = root;
        _byGuid = byGuid;
    }

    public IReadOnlyDictionary<string, UnityAsset> Assets => _byGuid;

    /// <summary>When reading a project, only this prefab is an avatar input.</summary>
    public UnityAsset InputPrefab { get; private set; }

    public IEnumerable<UnityAsset> AvatarSources => InputPrefab != null
        ? new[] { InputPrefab }
        : ByExtension(".prefab").Concat(ByExtension(".unity"));

    public static UnityPackage Open(string path) =>
        string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase)
            ? OpenProjectPrefab(path) : Extract(path);

    private static UnityPackage OpenProjectPrefab(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Prefabが見つかりません。", path);
        DirectoryInfo project = new FileInfo(path).Directory;
        while (project != null && !(Directory.Exists(Path.Combine(project.FullName, "Assets")) &&
                                   Directory.Exists(Path.Combine(project.FullName, "ProjectSettings"))))
            project = project.Parent;
        if (project == null)
            throw new InvalidDataException("Unityプロジェクト内のPrefabを指定してください（AssetsとProjectSettingsが必要です）。");

        var assets = new Dictionary<string, UnityAsset>(StringComparer.OrdinalIgnoreCase);
        var cacheDirectories = UnityPackageCache.Select(project.FullName);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        // Assets plus embedded and resolved registry packages. Project files are read-only;
        // _root stays null so Dispose never deletes any of them.
        foreach (string folder in new[] { "Assets", "Packages", "Library/PackageCache" })
        {
            string directory = Path.Combine(project.FullName, folder);
            if (!Directory.Exists(directory)) continue;
            var directories = folder == "Library/PackageCache"
                ? cacheDirectories.Select(name => Path.Combine(directory, name)) : new[] { directory };
            foreach (string meta in directories.SelectMany(d => Directory.EnumerateFiles(d, "*.meta", enumeration)))
            {
                string diskPath = meta[..^5];
                if (!File.Exists(diskPath)) continue;
                string guid = File.ReadLines(meta).FirstOrDefault(line => line.StartsWith("guid: ", StringComparison.Ordinal))?[6..].Trim();
                if (guid == null || guid.Length != 32 || !guid.All(Uri.IsHexDigit)) continue;
                string logicalPath = Path.GetRelativePath(project.FullName, diskPath).Replace('\\', '/');
                if (folder == "Library/PackageCache")
                {
                    string relative = Path.GetRelativePath(directory, diskPath).Replace('\\', '/');
                    int slash = relative.IndexOf('/');
                    string packageName = slash < 0 ? relative : relative[..slash];
                    int version = packageName.IndexOf('@');
                    if (version >= 0) packageName = packageName[..version];
                    logicalPath = "Packages/" + packageName + (slash < 0 ? "" : relative[slash..]);
                }
                var asset = new UnityAsset { Guid = guid, LogicalPath = logicalPath, DiskPath = diskPath, MetaPath = meta };
                if (!assets.TryAdd(guid, asset))
                    throw new InvalidDataException($"GUIDが重複しています: {assets[guid].LogicalPath}, {logicalPath}");
            }
        }
        UnityAsset input = assets.Values.FirstOrDefault(asset =>
            string.Equals(asset.DiskPath, path, StringComparison.OrdinalIgnoreCase));
        if (input == null)
            throw new InvalidDataException("Prefabの.metaが存在しないか、有効なGUIDがありません。");
        return new UnityPackage(null, assets) { InputPrefab = input };
    }

    public UnityAsset ByGuid(string guid)
        => guid != null && _byGuid.TryGetValue(guid, out UnityAsset a) ? a : null;

    public IEnumerable<UnityAsset> ByExtension(string extensionWithDot)
        => _byGuid.Values.Where(a => a.Extension == extensionWithDot.ToLowerInvariant());

    public string ReadText(UnityAsset asset)
    {
        if (asset?.HasContent != true)
        {
            return null;
        }
        if (!_textByGuid.TryGetValue(asset.Guid, out string text))
        {
            text = File.ReadAllText(asset.DiskPath);
            _textByGuid.Add(asset.Guid, text);
        }
        return text;
    }

    /// <summary>
    /// Parses and caches a prefab/scene for the lifetime of this extracted package. Prefab variants
    /// repeatedly reference the same bases, so reparsing by traversal path can otherwise become
    /// prohibitively expensive for packages with many color/costume variants.
    /// </summary>
    public UnityScene ReadScene(UnityAsset asset)
    {
        if (asset?.HasContent != true)
        {
            return null;
        }
        if (!_sceneByGuid.TryGetValue(asset.Guid, out UnityScene scene))
        {
            scene = UnityScene.Parse(ReadText(asset));
            _sceneByGuid.Add(asset.Guid, scene);
        }
        return scene;
    }

    public static UnityPackage Extract(string packagePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "ResoPon", "upkg_" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);

        // Pass 1: extract the raw <guid>/{asset,pathname,...} tree.
        using (FileStream file = File.OpenRead(packagePath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                string name = entry.Name.Replace('\\', '/').TrimStart('.', '/');
                if (string.IsNullOrEmpty(name) || entry.EntryType is TarEntryType.Directory)
                {
                    continue;
                }
                string destination = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        // Pass 2: map each guid folder to its logical path + content file.
        var byGuid = new Dictionary<string, UnityAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (string guidDir in Directory.EnumerateDirectories(staging))
        {
            string guid = Path.GetFileName(guidDir);
            string pathnameFile = Path.Combine(guidDir, "pathname");
            if (!File.Exists(pathnameFile))
            {
                continue;
            }
            // "pathname" may contain a trailing "00" marker line; the first line is the path.
            string logicalPath = File.ReadAllLines(pathnameFile).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(logicalPath))
            {
                continue;
            }
            string assetFile = Path.Combine(guidDir, "asset");
            string metaFile = Path.Combine(guidDir, "asset.meta");
            byGuid[guid] = new UnityAsset
            {
                Guid = guid,
                LogicalPath = logicalPath,
                DiskPath = File.Exists(assetFile) ? assetFile : null,
                MetaPath = File.Exists(metaFile) ? metaFile : null,
            };
        }
        return new UnityPackage(root, byGuid);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
