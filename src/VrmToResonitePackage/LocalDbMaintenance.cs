using System.Diagnostics;
using System.Security.Cryptography;
using Elements.Core;

namespace VrmToResonitePackage;

/// <summary>
/// FrooxEngine's LocalDB is an encrypted LiteDB; a force-killed process can corrupt it,
/// after which engine initialization dies with "LiteDB.LiteException: Invalid password"
/// and there is no in-engine recovery for that failure mode. The database is just a
/// local asset cache for this converter, so an unreadable one gets wiped.
///
/// The probe runs in a short-lived child process: a failed LiteDB open leaks its file
/// handle, and deleting through that leak leaves the file in a delete-pending state
/// that makes the engine's fresh database creation fail too. With the probe isolated,
/// the handles die with the child and the parent (which never opened the file) can
/// delete cleanly.
/// </summary>
internal static class LocalDbMaintenance
{
    public const string CheckArgument = "--check-localdb";
    private const int HealthyExitCode = 0;
    private const int BrokenExitCode = 2;

    /// <summary>The only data root this tool is allowed to manage (and delete from).</summary>
    public static string ManagedDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrmToResonitePackage");

    // ---------------------------------------------------------------- parent side

    /// <summary>Called before engine initialization; never opens the database itself.</summary>
    public static void EnsureHealthy(string dataDirectory)
    {
        if (!IsManagedDirectory(dataDirectory))
        {
            UniLog.Warning($"LocalDBの検証をスキップ: 管理外のデータフォルダです ({dataDirectory})");
            return;
        }
        string databaseFile = Path.Combine(dataDirectory, "Data.litedb");
        string logFile = Path.Combine(dataDirectory, "Data-log.litedb");
        if (!File.Exists(databaseFile))
        {
            // Clean up orphans from a previously interrupted reset.
            DeleteWithRetry(logFile);
            return;
        }

        int verdict = ProbeInChildProcess(dataDirectory);
        if (verdict == HealthyExitCode)
        {
            return;
        }
        Console.WriteLine("ローカルデータベースが開けないため初期化します。");
        UniLog.Warning("LocalDB validation failed, resetting database.");
        DeleteWithRetry(Path.Combine(dataDirectory, "Data.version"));
        DeleteWithRetry(logFile);
        DeleteWithRetry(databaseFile);
        // LocalKey.bin is kept so the machine identity stays stable.
    }

    /// <summary>Returns the child's verdict, or Broken when the probe cannot run/finish.</summary>
    private static int ProbeInChildProcess(string dataDirectory)
    {
        try
        {
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return CheckOnly(dataDirectory);
            }
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(CheckArgument);
            startInfo.ArgumentList.Add(dataDirectory);
            using Process child = Process.Start(startInfo);
            if (child == null)
            {
                return CheckOnly(dataDirectory);
            }
            if (!child.WaitForExit(60_000))
            {
                child.Kill(entireProcessTree: true);
                return BrokenExitCode;
            }
            return child.ExitCode == HealthyExitCode ? HealthyExitCode : BrokenExitCode;
        }
        catch (Exception ex)
        {
            UniLog.Warning("LocalDB検証プロセスの起動に失敗、プロセス内で検証します: " + ex.Message);
            return CheckOnly(dataDirectory);
        }
    }

    // ---------------------------------------------------------------- child side

    /// <summary>
    /// Opens the database the same way LocalDB does and reports its health.
    /// May leak file handles on failure — that is fine in the probe child process,
    /// and tolerated in the in-process fallback.
    /// </summary>
    public static int CheckOnly(string dataDirectory)
    {
        if (!IsManagedDirectory(dataDirectory))
        {
            return HealthyExitCode;
        }
        string databaseFile = Path.Combine(dataDirectory, "Data.litedb");
        string keyFile = Path.Combine(dataDirectory, "LocalKey.bin");
        if (!File.Exists(databaseFile))
        {
            return HealthyExitCode;
        }
        try
        {
            RSAParameters key = ReadLocalKey(keyFile);
            string machineId = FrooxEngine.Store.LocalDB.GenerateMachineID(key);
            string connection = FrooxEngine.Store.LocalDB.ProcessConnection(
                "filename=" + databaseFile + ";", machineId);
            using var database = new LiteDB.LiteDatabase(connection);
            _ = database.GetCollectionNames().Count();
            return HealthyExitCode;
        }
        catch
        {
            return BrokenExitCode;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsManagedDirectory(string dataDirectory)
    {
        string fullPath = Path.GetFullPath(dataDirectory);
        return fullPath.StartsWith(ManagedDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath, ManagedDataRoot, StringComparison.OrdinalIgnoreCase);
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
