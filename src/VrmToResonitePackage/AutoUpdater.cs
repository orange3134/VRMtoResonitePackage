using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace VrmToResonitePackage;

/// <summary>Checks GitHub Releases and replaces the published single-file executable.</summary>
internal static class AutoUpdater
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/orange3134/VRMtoResonitePackage/releases/latest";
    private const string ReleaseAssetName = "ResoPon.exe";
    private const string ApplyUpdateArgument = "--resopon-apply-update";
    private const long MaximumDownloadSize = 512L * 1024 * 1024;

    public static bool CanSelfUpdate =>
        OperatingSystem.IsWindows() &&
        TryGetExecutablePath(out string executablePath) &&
        !File.Exists(Path.ChangeExtension(executablePath, ".dll"));

    public static bool TryRunUpdateHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], ApplyUpdateArgument, StringComparison.Ordinal))
        {
            return false;
        }

        if (args.Length < 4 || !int.TryParse(args[2], out int processId))
        {
            exitCode = 2;
            return true;
        }

        string targetPath = Path.GetFullPath(args[1]);
        string downloadedPath = Path.GetFullPath(args[3]);
        string[] restartArguments = args.Skip(4).ToArray();
        exitCode = ApplyUpdate(targetPath, downloadedPath, processId, restartArguments);
        return true;
    }

    public static async Task<UpdateRelease> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSelfUpdate || !Version.TryParse(AppVersion.Display, out Version currentVersion))
        {
            return null;
        }

        using var client = CreateHttpClient(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString();
        if (!TryParseReleaseVersion(tag, out Version latestVersion) || latestVersion <= currentVersion)
        {
            return null;
        }

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), ReleaseAssetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string downloadUrl = asset.GetProperty("browser_download_url").GetString();
            if (!IsTrustedDownloadUrl(downloadUrl))
            {
                throw new InvalidDataException("The release contains an unexpected download URL.");
            }

            long size = asset.TryGetProperty("size", out JsonElement sizeElement)
                ? sizeElement.GetInt64()
                : 0;
            string digest = asset.TryGetProperty("digest", out JsonElement digestElement) &&
                            digestElement.ValueKind == JsonValueKind.String
                ? digestElement.GetString()
                : null;
            ValidateDigest(digest);
            return new UpdateRelease(tag, tag.Trim().TrimStart('v', 'V'), downloadUrl, size, digest);
        }

        throw new InvalidDataException($"The latest release does not contain {ReleaseAssetName}.");
    }

    public static async Task<string> DownloadAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        if (release.Size < 0 || release.Size > MaximumDownloadSize)
        {
            throw new InvalidDataException("The update file has an unexpected size.");
        }

        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResoPon",
            "Updates",
            release.Version);
        Directory.CreateDirectory(updateDirectory);
        string destination = Path.Combine(updateDirectory, ReleaseAssetName);
        string partial = destination + ".download";

        try
        {
            using var client = CreateHttpClient(TimeSpan.FromMinutes(5));
            using HttpResponseMessage response = await client.GetAsync(
                release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                (contentLength > MaximumDownloadSize || release.Size > 0 && contentLength != release.Size))
            {
                throw new InvalidDataException("The update download size does not match the release metadata.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                             partial,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > MaximumDownloadSize)
                    {
                        throw new InvalidDataException("The update download is too large.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);

                if (release.Size > 0 && total != release.Size)
                {
                    throw new InvalidDataException("The downloaded update size does not match the release metadata.");
                }
            }

            VerifyDigest(partial, release.Digest);
            File.Move(partial, destination, true);
            return destination;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public static void StartInstallAndRestart(string downloadedPath, IReadOnlyList<string> restartArguments)
    {
        if (!TryGetExecutablePath(out string targetPath))
        {
            throw new InvalidOperationException(AppLocalization.Get("ExecutablePathUnavailable"));
        }

        // Fail while the current process can still show an error if its directory is not writable.
        string writeProbe = targetPath + $".update-{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(writeProbe))
            {
            }
        }
        finally
        {
            TryDelete(writeProbe);
        }

        var startInfo = new ProcessStartInfo(downloadedPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetPath)
        };
        startInfo.ArgumentList.Add(ApplyUpdateArgument);
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(downloadedPath);
        foreach (string argument in restartArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process.Start(startInfo);
    }

    internal static bool TryParseReleaseVersion(string tag, out Version version) =>
        Version.TryParse(tag?.Trim().TrimStart('v', 'V'), out version);

    private static int ApplyUpdate(
        string targetPath,
        string downloadedPath,
        int processId,
        IReadOnlyList<string> restartArguments)
    {
        if (!TryGetExecutablePath(out string helperPath) ||
            !File.Exists(downloadedPath) ||
            !string.Equals(helperPath, downloadedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetPath, downloadedPath, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        try
        {
            try
            {
                using Process oldProcess = Process.GetProcessById(processId);
                if (!oldProcess.WaitForExit(120_000))
                {
                    return 3;
                }
            }
            catch (ArgumentException)
            {
                // The old process has already exited.
            }

            string replacement = targetPath + ".new";
            string backup = targetPath + ".old";
            TryDelete(replacement);
            File.Copy(downloadedPath, replacement, true);
            TryDelete(backup);
            File.Move(targetPath, backup);
            try
            {
                File.Move(replacement, targetPath);
            }
            catch
            {
                File.Move(backup, targetPath, true);
                throw;
            }

            var startInfo = new ProcessStartInfo(targetPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath)
            };
            foreach (string argument in restartArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            Process.Start(startInfo);
            return 0;
        }
        catch
        {
            TryStart(targetPath, restartArguments);
            return 1;
        }
    }

    private static void TryStart(string targetPath, IReadOnlyList<string> arguments)
    {
        try
        {
            if (!File.Exists(targetPath))
            {
                return;
            }
            var startInfo = new ProcessStartInfo(targetPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath)
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ResoPon/{AppVersion.Display}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static bool TryGetExecutablePath(out string path)
    {
        path = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(path) &&
               File.Exists(path) &&
               string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedDownloadUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith(
                   "/orange3134/VRMtoResonitePackage/releases/download/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyDigest(string path, string digest)
    {
        ValidateDigest(digest);
        const string prefix = "sha256:";
        string expected = digest[prefix.Length..];
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update failed its SHA-256 check.");
        }
    }

    private static void ValidateDigest(string digest)
    {
        const string prefix = "sha256:";
        string hash = digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? digest[prefix.Length..]
            : null;
        if (hash?.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The release does not contain a valid SHA-256 digest.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed record UpdateRelease(
    string Tag,
    string Version,
    string DownloadUrl,
    long Size,
    string Digest);
