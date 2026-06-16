using Elements.Core;

namespace VrmToResonitePackage;

/// <summary>
/// FrooxEngine's LocalDB (an encrypted LiteDB in the data directory) is strictly
/// single-process; concurrent converter instances (GUI children, CLI runs) sharing
/// one directory corrupt it, which surfaces as "LiteDB.LiteException: Invalid
/// password" on the next start. Every engine run therefore gets its own throwaway
/// data directory, deleted afterwards; leftovers from crashed runs are swept on the
/// next start. Only LocalKey.bin (the machine identity) is shared between runs.
/// </summary>
internal static class LocalDbMaintenance
{
    /// <summary>The only data root this tool is allowed to manage (and delete from).</summary>
    public static string ManagedDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResoPon");

    private static string SharedLocalKeyFile => Path.Combine(ManagedDataRoot, "LocalKey.bin");

    /// <summary>Creates a fresh per-run data directory (and sweeps orphaned ones).</summary>
    public static string CreateRunDataDirectory()
    {
        CleanupOrphans();
        string directory = Path.Combine(ManagedDataRoot,
            "Data-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(directory);
        // Reuse the machine identity so records stay attributed consistently.
        if (File.Exists(SharedLocalKeyFile))
        {
            File.Copy(SharedLocalKeyFile, Path.Combine(directory, "LocalKey.bin"), overwrite: true);
        }
        return directory;
    }

    /// <summary>Best-effort removal after engine shutdown; orphan sweep covers failures.</summary>
    public static void ReleaseRunDataDirectory(string directory)
    {
        PersistLocalKey(directory);
        TryDeleteDirectory(directory);
    }

    private static void CleanupOrphans()
    {
        if (!Directory.Exists(ManagedDataRoot))
        {
            return;
        }
        foreach (string directory in Directory.GetDirectories(ManagedDataRoot, "Data-*"))
        {
            TryDeleteDirectory(directory);
        }
        // The shared data directory used by older versions is no longer needed.
        string legacy = Path.Combine(ManagedDataRoot, "Data");
        if (Directory.Exists(legacy))
        {
            PersistLocalKey(legacy);
            TryDeleteDirectory(legacy);
        }
    }

    private static void PersistLocalKey(string directory)
    {
        try
        {
            string keyFile = Path.Combine(directory, "LocalKey.bin");
            if (File.Exists(keyFile) && !File.Exists(SharedLocalKeyFile))
            {
                File.Copy(keyFile, SharedLocalKeyFile);
            }
        }
        catch (Exception ex)
        {
            UniLog.Warning("LocalKeyの保存に失敗しました: " + ex.Message);
        }
    }

    /// <summary>
    /// Deletes a per-run data directory unless another (live) converter process still
    /// holds it — detected via the engine's exclusive Instance.lock.
    /// </summary>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            // Never operate outside the managed root.
            string fullPath = Path.GetFullPath(directory);
            if (!fullPath.StartsWith(ManagedDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string instanceLock = Path.Combine(fullPath, "Instance.lock");
            if (File.Exists(instanceLock))
            {
                // Engine holds this open with FileShare.None while running.
                using FileStream probe = File.Open(instanceLock, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // In use by a live run (or transiently locked) — the next start retries.
        }
    }
}
