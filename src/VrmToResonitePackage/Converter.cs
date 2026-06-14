using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
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

        // Written straight to the log file (not the console) so every log starts with the build
        // version for bug reports, without duplicating the console header printed by Program.Main.
        logWriter.WriteLine($"バージョン: {AppVersion.Display}");
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
                    bool isUnityPackage = string.Equals(Path.GetExtension(inputFile), ".unitypackage",
                        StringComparison.OrdinalIgnoreCase);
                    string output = isUnityPackage
                        ? await ConvertVrchat(world, inputFile, options).ConfigureAwait(false)
                        : await ConvertOne(world, inputFile, options).ConfigureAwait(false);
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
        // Records whether the VRM0 orientation was baked to +Z (or a proper-handed VRM1 was
        // X-mirrored) so collider offset conversion can pick the matching coordinate transform.
        vrm.OrientationBaked = GlbPreprocessor.CreateImportableGlb(vrmPath, glbPath, out bool mirroredX);
        vrm.OrientationMirroredX = mirroredX;

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
                        FaceTracking = options.FaceTracking,
                        Protect = !options.NoProtection,
                        ExpressionMenu = !options.NoExpressionMenu,
                        DefaultUserScale = options.DefaultUserScale,
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

                await SetupThumbnail(root, assetsSlot, vrm, vrmPath);

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
    /// Converts a VRChat avatar packaged as a .unitypackage: extracts it, parses the primary
    /// avatar (VRCAvatarDescriptor + PhysBones + liltoon material assignments), imports the FBX
    /// through Resonite's importer, then reuses the same rig/viseme/blink/spring setup as VRM via
    /// an adapter, and builds liltoon-derived XiexeToon materials.
    /// </summary>
    private static async Task<string> ConvertVrchat(World world, string packagePath, CliOptions options)
    {
        using Unity.UnityPackage package = Unity.UnityPackage.Extract(packagePath);
        Vrchat.VrchatAvatar avatar = Vrchat.VrchatAvatarParser.Parse(package, options.AvatarName);
        VrmModel model = Vrchat.VrchatModelAdapter.ToVrmModel(avatar);

        string displayName = !string.IsNullOrWhiteSpace(avatar.Name)
            ? avatar.Name
            : Path.GetFileNameWithoutExtension(packagePath);
        Console.WriteLine($"VRChatアバター: {displayName}");
        Console.WriteLine($"ヒューマノイドボーン: {avatar.HumanBones.Count}, ビセーム: {avatar.Visemes.Count}, " +
                          $"瞬き: {(avatar.Blink != null ? "あり" : "なし")}, PhysBone: {avatar.PhysBones.Count}");

        string outputDirectory = options.OutputDirectory ?? Path.GetDirectoryName(packagePath);
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory,
            SanitizeFileName(Path.GetFileNameWithoutExtension(packagePath)) + ".resonitepackage");

        // The extracted FBX content file is named "asset"; Resonite's importer keys off the
        // extension, so copy it to a real .fbx path.
        string workDirectory = Path.Combine(Path.GetTempPath(), "VrmToResonitePackage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string fbxPath = Path.Combine(workDirectory, SanitizeFileName(displayName) + ".fbx");
        File.Copy(avatar.FbxPath, fbxPath, overwrite: true);

        try
        {
            await world.Coroutines.StartTask(async () =>
            {
                await default(ToWorld);
                Slot root = world.AddSlot(displayName);
                Slot assetsSlot = root.AddSlot("Assets");

                ModelImportSettings settings = ModelImportSettings.XiexeToon(
                    generateColliders: false,
                    importBones: true,
                    importAnimations: false);
                settings.SetupIK = false;
                settings.ForceTpose = false;
                settings.CalculateTextureAlpha = true;
                settings.ImportVertexColors = false;
                settings.GenerateSkeletonBones = true;

                Console.WriteLine("FBXをインポート中...");
                Task importTask = ModelImporter.ImportModelAsync(fbxPath, root, settings, assetsSlot);
                Task winner = await Task.WhenAny(importTask, Task.Delay(TimeSpan.FromSeconds(options.ImportTimeoutSeconds)));
                await default(ToWorld);
                if (winner != importTask)
                {
                    throw new TimeoutException(
                        $"FBXのインポートが {options.ImportTimeoutSeconds} 秒以内に完了しませんでした。");
                }
                await importTask;

                Console.WriteLine("アセットの読み込みを待機中...");
                await WaitForAssets(assetsSlot);

                // Drop meshes the selected prefab deleted from the shared FBX, before any setup runs.
                Vrchat.VrchatSceneSetup.RemoveDeletedMeshes(root, avatar);

                if (options.NoAvatar)
                {
                    await Vrchat.VrchatMaterialBuilder.Apply(root, assetsSlot, avatar, package);
                    SpringBoneSetup.Apply(root, model);
                }
                else
                {
                    Console.WriteLine("アバターをセットアップ中...");
                    var setupOptions = new AvatarSetupOptions
                    {
                        FaceTracking = options.FaceTracking,
                        Protect = !options.NoProtection,
                        // VRChat expressions live in Animator/menu layers, out of scope: visemes + blink only.
                        ExpressionMenu = false,
                        DefaultUserScale = options.DefaultUserScale,
                        ViewForward = options.ViewForward,
                        ViewUp = options.ViewUp,
                    };
                    if (options.NearClip.HasValue)
                    {
                        setupOptions.NearClip = options.NearClip.Value;
                    }
                    AvatarSetup.Build(root, model, setupOptions);
                    await Vrchat.VrchatMaterialBuilder.Apply(root, assetsSlot, avatar, package);
                    SpringBoneSetup.Apply(root, model);
                }

                // Reflect prefab-authored scene state (inactive GameObjects, initial blendshape weights).
                Vrchat.VrchatSceneSetup.Apply(root, avatar);

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
    /// Extracts the VRM's embedded thumbnail image, imports it as a Resonite texture asset and
    /// attaches an <see cref="ItemTextureThumbnailSource"/> to the package root so it shows up as
    /// the inventory/item thumbnail. A missing or unextractable thumbnail is non-fatal.
    /// </summary>
    private static async Task SetupThumbnail(Slot root, Slot assetsSlot, VrmModel vrm, string vrmPath)
    {
        if (!vrm.ThumbnailImageIndex.HasValue)
        {
            UniLog.Log("サムネイル: VRMにサムネイル画像が含まれていません。スキップします。");
            return;
        }

        int imageIndex = vrm.ThumbnailImageIndex.Value;
        Engine engine = root.Engine;
        Uri uri = null;
        string extension = null;
        try
        {
            await default(ToBackground);
            (byte[] data, string ext) = VrmParser.ExtractImage(vrmPath, imageIndex);
            extension = ext;
            string tempFile = engine.LocalDB.GetTempFilePath(ext);
            await File.WriteAllBytesAsync(tempFile, data);
            uri = await engine.LocalDB.ImportLocalAssetAsync(tempFile, LocalDB.ImportLocation.Move);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"サムネイル: 画像 {imageIndex} の抽出/取り込みに失敗しました: {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null)
        {
            return;
        }

        Slot thumbnailSlot = assetsSlot.AddSlot("Thumbnail");
        StaticTexture2D texture = thumbnailSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;

        ItemTextureThumbnailSource source = root.AttachComponent<ItemTextureThumbnailSource>();
        source.Texture.Target = texture;

        UniLog.Log($"サムネイル: 画像 {imageIndex} ({extension}) をアイテムサムネイルに設定しました。");
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
