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
