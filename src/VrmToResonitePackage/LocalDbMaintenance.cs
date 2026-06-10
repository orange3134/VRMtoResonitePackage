using System.Security.Cryptography;
using Elements.Core;

namespace VrmToResonitePackage;

/// <summary>
/// FrooxEngine's LocalDB is an encrypted LiteDB; a force-killed process can corrupt it,
/// after which engine initialization dies with "LiteDB.LiteException: Invalid password"
/// and there is no in-engine recovery for that failure mode. The database is just a
/// local asset cache for this converter, so before booting the engine we open it the
/// same way LocalDB does and wipe it if it is unreadable.
/// </summary>
internal static class LocalDbMaintenance
{
    /// <summary>The only data root this tool is allowed to manage (and delete from).</summary>
    public static string ManagedDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrmToResonitePackage");

    public static void ValidateOrReset(string dataDirectory)
    {
        // Hard safety guard: never touch a database outside this tool's own data
        // folder (e.g. the user's actual Resonite installation data).
        string fullPath = Path.GetFullPath(dataDirectory);
        if (!fullPath.StartsWith(ManagedDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, ManagedDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            UniLog.Warning($"LocalDBの検証をスキップ: 管理外のデータフォルダです ({dataDirectory})");
            return;
        }

        string databaseFile = Path.Combine(dataDirectory, "Data.litedb");
        string logFile = Path.Combine(dataDirectory, "Data-log.litedb");
        string keyFile = Path.Combine(dataDirectory, "LocalKey.bin");
        if (!File.Exists(databaseFile))
        {
            // Clean up orphans from a previously interrupted reset.
            if (File.Exists(logFile))
            {
                DeleteWithRetry(logFile);
            }
            return;
        }
        try
        {
            RSAParameters key = ReadLocalKey(keyFile);
            string machineId = FrooxEngine.Store.LocalDB.GenerateMachineID(key);
            string connection = FrooxEngine.Store.LocalDB.ProcessConnection(
                "filename=" + databaseFile + ";", machineId);
            using var database = new LiteDB.LiteDatabase(connection);
            _ = database.GetCollectionNames().Count();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ローカルデータベースが開けないため初期化します ({ex.GetType().Name}: {ex.Message})");
            UniLog.Warning("LocalDB validation failed, resetting database: " + ex.Message);
            // A failed LiteDatabase constructor leaks its file handle (its dispose path
            // throws too); finalizers close the abandoned streams.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // The main database file goes last: as long as it exists, the next run
            // re-validates and retries the cleanup.
            DeleteWithRetry(Path.Combine(dataDirectory, "Data.version"));
            DeleteWithRetry(logFile);
            DeleteWithRetry(databaseFile);
            // LocalKey.bin is kept so the machine identity stays stable.
        }
    }

    private static void DeleteWithRetry(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"破損したデータベースファイルを削除できません: {path}\n" +
                    "手動で削除してから再実行してください。", ex);
            }
        }
    }

    /// <summary>Reads LocalKey.bin: eight 7-bit-length-prefixed RSA parameter arrays.</summary>
    private static RSAParameters ReadLocalKey(string keyFile)
    {
        using var reader = new BinaryReader(File.OpenRead(keyFile));
        return new RSAParameters
        {
            Exponent = ReadArray(reader),
            Modulus = ReadArray(reader),
            P = ReadArray(reader),
            Q = ReadArray(reader),
            DP = ReadArray(reader),
            DQ = ReadArray(reader),
            InverseQ = ReadArray(reader),
            D = ReadArray(reader),
        };
    }

    private static byte[] ReadArray(BinaryReader reader)
    {
        ulong length = 0;
        int shift = 0;
        byte b;
        do
        {
            b = reader.ReadByte();
            length |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        if (length > 4096)
        {
            throw new InvalidDataException("Local key is corrupted");
        }
        return reader.ReadBytes((int)length);
    }
}
