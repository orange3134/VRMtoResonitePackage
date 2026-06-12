using Elements.Core;
using FrooxEngine;
using SkyFrost.Base;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Boots a headless FrooxEngine using the user's Resonite installation, imports each VRM
/// through Resonite's own model importer, performs the avatar setup and writes the result
/// out as a .resonitepackage.
/// </summary>
internal static class Converter
{
    public static async Task<int> Run(CliOptions options, string resonitePath)
    {
        ConversionRunResult result = await RunDetailed(options, resonitePath).ConfigureAwait(false);
        return result.ExitCode;
    }

    public static async Task<ConversionRunResult> RunDetailed(CliOptions options, string resonitePath)
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrmToResonitePackage");
        string logPath = Path.Combine(AppContext.BaseDirectory, "Logs", $"convert_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
        using var logWriter = new StreamWriter(logPath) { AutoFlush = true };
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var teeOut = new TeeTextWriter(originalOut, logWriter);
        using var teeError = new TeeTextWriter(originalError, logWriter);
        Console.SetOut(teeOut);
        Console.SetError(teeError);
        Action unhookLogging = HookLogging(logWriter);
        try
        {

        Console.WriteLine($"ログ: {logPath}");
        Console.WriteLine("FrooxEngineをヘッドレスで起動しています（初回はしばらくかかります）...");

        var runner = new StandaloneFrooxEngineRunner();
        // Per-run data directory: the engine's LocalDB is single-process, and parallel
        // converter instances (GUI children, CLI) sharing one directory corrupt it.
        string dataDirectory = LocalDbMaintenance.CreateRunDataDirectory();
        var launchOptions = new LaunchOptions
        {
            DataDirectory = dataDirectory,
            CacheDirectory = Path.Combine(appData, "Cache"),
            LogsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs"),
            DoNotAutoLoadHome = true,
            StartInvisible = true,
            NeverSaveSettings = true,
            NeverSaveDash = true,
            DisablePlatformInterfaces = true,
        };

        await runner.Initialize(launchOptions).ConfigureAwait(false);
        Console.WriteLine("エンジン起動完了。変換を開始します。");

        // One shared world for all conversions. Userspace.ExitWorld is intentionally
        // avoided: with no home world to hand focus back to, it never completes headless.
        World world = await Userspace.OpenWorld(new WorldStartSettings
        {
            AutoFocus = true,
            CreateLoadIndicator = false,
            InitWorld = delegate
            {
            },
        }).ConfigureAwait(false);
        if (world == null)
        {
            throw new InvalidOperationException("変換用ワールドの作成に失敗しました。");
        }

        int failures = 0;
        var outputs = new List<string>();
        try
        {
            foreach (string inputFile in options.InputFiles)
            {
                Console.WriteLine();
                Console.WriteLine($"=== 変換中: {Path.GetFileName(inputFile)} ===");
                try
                {
                    string output = await ConvertOne(world, inputFile, options).ConfigureAwait(false);
                    outputs.Add(output);
                    Console.WriteLine($"=== 完了: {output} ===");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"=== 失敗: {Path.GetFileName(inputFile)} ===");
                    Console.Error.WriteLine(ex.ToString());
                }
            }
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("エンジンを終了しています...");
            try
            {
                Task shutdown = runner.Shutdown();
                if (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != shutdown)
                {
                    UniLog.Warning("エンジンの終了がタイムアウトしました。プロセスを終了します。");
                }
            }
            catch (Exception ex)
            {
                UniLog.Warning("Engine shutdown failed: " + ex.Message);
            }
            LocalDbMaintenance.ReleaseRunDataDirectory(dataDirectory);
        }
        return new ConversionRunResult(failures == 0 ? 0 : 1, outputs, logPath, failures);
        }
        finally
        {
            unhookLogging();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static Action HookLogging(StreamWriter writer)
    {
        object gate = new();
        void WriteLine(string prefix, object message, bool toConsole)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {prefix}{message}";
            lock (gate)
            {
                writer.WriteLine(line);
                if (toConsole)
                {
                    Console.WriteLine(line);
                }
            }
        }
        Action<object> log = message => WriteLine("", message, toConsole: false);
        Action<object> warning = message => WriteLine("WARN: ", message, toConsole: true);
        Action<object> error = message => WriteLine("ERROR: ", message, toConsole: true);
        UniLog.OnLog += log;
        UniLog.OnWarning += warning;
        UniLog.OnError += error;
        return () =>
        {
            UniLog.OnLog -= log;
            UniLog.OnWarning -= warning;
            UniLog.OnError -= error;
        };
    }

    private static async Task<string> ConvertOne(World world, string vrmPath, CliOptions options)
    {
        VrmModel vrm = VrmParser.Parse(vrmPath);
        string displayName = !string.IsNullOrWhiteSpace(vrm.Title)
            ? vrm.Title
            : Path.GetFileNameWithoutExtension(vrmPath);
        Console.WriteLine($"VRM {(vrm.SpecVersionMajor == 0 ? "0.x" : "1.0")} | タイトル: {displayName}" +
                          (string.IsNullOrWhiteSpace(vrm.Author) ? "" : $" | 作者: {vrm.Author}"));
        Console.WriteLine($"ヒューマノイドボーン: {vrm.HumanBones.Count}, 表情: {vrm.Expressions.Count}, " +
                          $"揺れもの: {vrm.SpringChains.Count}チェーン");

        string outputDirectory = options.OutputDirectory ?? Path.GetDirectoryName(vrmPath);
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory,
            SanitizeFileName(Path.GetFileNameWithoutExtension(vrmPath)) + ".resonitepackage");

        // Resonite's importer keys its behavior off the file extension; a VRM is a GLB container.
        // The preprocessor also fixes morph target naming quirks of older exporters.
        string workDirectory = Path.Combine(Path.GetTempPath(), "VrmToResonitePackage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string glbPath = Path.Combine(workDirectory, SanitizeFileName(Path.GetFileNameWithoutExtension(vrmPath)) + ".glb");
        // Records whether the VRM0 orientation was baked to +Z so collider offset conversion
        // can pick the matching coordinate transform.
        vrm.OrientationBaked = GlbPreprocessor.CreateImportableGlb(vrmPath, glbPath);

        try
        {
            await world.Coroutines.StartTask(async () =>
            {
                await default(ToWorld);
                Slot root = world.AddSlot(displayName);
                // Match Resonite's "Place Assets On Object" import option so meshes,
                // textures, and materials are packaged under the imported object.
                Slot assetsSlot = root.AddSlot("Assets");

                ModelImportSettings settings = ModelImportSettings.XiexeToon(
                    generateColliders: false,
                    importBones: true,
                    importAnimations: false);
                settings.SetupIK = false; // we build the rig from VRM's exact humanoid map instead
                settings.ForceTpose = false; // VRM rest pose is T-pose by spec
                settings.CalculateTextureAlpha = true;
                settings.ImportVertexColors = false;
                settings.GenerateSkeletonBones = true;

                Console.WriteLine("モデルをインポート中...");
                // A crashed import coroutine never completes its task (engine logs the
                // exception and goes silent), so guard with a timeout to keep batch
                // conversion moving.
                Task importTask = ModelImporter.ImportModelAsync(glbPath, root, settings, assetsSlot);
                Task winner = await Task.WhenAny(importTask, Task.Delay(TimeSpan.FromSeconds(options.ImportTimeoutSeconds)));
                await default(ToWorld);
                if (winner != importTask)
                {
                    throw new TimeoutException(
                        $"モデルのインポートが {options.ImportTimeoutSeconds} 秒以内に完了しませんでした。" +
                        "インポート中の例外が原因の可能性があります（ログを確認してください）。");
                }
                await importTask;

                Console.WriteLine("アセットの読み込みを待機中...");
                await WaitForAssets(assetsSlot);

                if (options.NoAvatar)
                {
                    await MaterialTuner.Apply(root, assetsSlot, vrm, vrmPath);
                    SpringBoneSetup.Apply(root, vrm);
                }
                else
                {
                    Console.WriteLine("アバターをセットアップ中...");
                    var setupOptions = new AvatarSetupOptions
                    {
                        TargetHeight = options.TargetHeight,
                        FaceTracking = options.FaceTracking,
                        Protect = !options.NoProtection,
                        ExpressionMenu = !options.NoExpressionMenu,
                        ViewForward = options.ViewForward,
                        ViewUp = options.ViewUp,
                    };
                    if (options.NearClip.HasValue)
                    {
                        setupOptions.NearClip = options.NearClip.Value;
                    }
                    AvatarSetup.Build(root, vrm, setupOptions);
                    await MaterialTuner.Apply(root, assetsSlot, vrm, vrmPath);
                    SpringBoneSetup.Apply(root, vrm);
                }

                // Let deferred import tasks (alpha detection, normal map detection, ...) settle.
                for (int i = 0; i < 30; i++)
                {
                    await default(NextUpdate);
                }

                root.Name = displayName;
                Console.WriteLine("パッケージを書き出し中...");
                await ExportPackage(world, root, outputPath);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!options.KeepWorkingFiles)
            {
                try
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
                catch
                {
                    // Temp cleanup is best-effort.
                }
            }
            else
            {
                Console.WriteLine($"作業ファイル: {workDirectory}");
            }
        }

        if (!File.Exists(outputPath))
        {
            throw new IOException($"パッケージが出力されませんでした: {outputPath}");
        }
        return outputPath;
    }

    /// <summary>
    /// Waits (on the world thread) until every asset provider created by the import has its
    /// asset loaded, so post-load detection logic has correct data to work with.
    /// </summary>
    private static async Task WaitForAssets(Slot assetsSlot)
    {
        const int timeoutFrames = 60 * 120; // ~2 minutes at 60 ticks
        for (int frame = 0; frame < timeoutFrames; frame++)
        {
            bool allReady = true;
            foreach (IAssetProvider provider in assetsSlot.GetComponentsInChildren<IAssetProvider>())
            {
                if (!provider.IsAssetAvailable)
                {
                    allReady = false;
                    break;
                }
            }
            if (allReady)
            {
                return;
            }
            await default(NextUpdate);
        }
        UniLog.Warning("一部のアセットの読み込みが完了しませんでした。そのまま続行します。");
    }

    private static async Task ExportPackage(World world, Slot root, string outputPath)
    {
        SavedGraph graph = root.SaveObject(DependencyHandling.CollectAssets);
        string ownerId = world.LocalUser.UserID ?? world.LocalUser.MachineID;
        SkyFrost.Base.Record record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(root.Name, ownerId, null);
        await default(ToBackground);
        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            await PackageCreator.BuildPackage(world.Engine, record, graph, stream, includeVariants: false);
        }
        await default(ToWorld);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}

internal sealed record ConversionRunResult(int ExitCode, IReadOnlyList<string> OutputFiles, string LogPath, int Failures);

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly TextWriter _secondary;

    public TeeTextWriter(TextWriter primary, TextWriter secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public override System.Text.Encoding Encoding => _primary.Encoding;

    public override void Write(char value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void Write(string value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void WriteLine(string value)
    {
        _primary.WriteLine(value);
        _secondary.WriteLine(value);
    }

    public override void Flush()
    {
        _primary.Flush();
        _secondary.Flush();
    }
}
