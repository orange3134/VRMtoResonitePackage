using System.Runtime.CompilerServices;

namespace VrmToResonitePackage;

/// <summary>
/// Entry point. Keeps all FrooxEngine types out of Main so the assembly resolver
/// can be installed before anything touches Resonite's DLLs.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (ShouldLaunchGui(args))
        {
            return GuiApp.Run(args.Where(File.Exists).Select(Path.GetFullPath).ToArray());
        }

        Console.WriteLine("ResoPon");
        Console.WriteLine($"バージョン: {AppVersion.Display}");
        Console.WriteLine();

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"引数エラー: {ex.Message}");
            CliOptions.PrintUsage();
            PauseIfInteractive();
            return 2;
        }

        if (options.ShowHelp || options.InputFiles.Count == 0)
        {
            CliOptions.PrintUsage();
            PauseIfInteractive();
            return options.ShowHelp ? 0 : 2;
        }

        string resonitePath;
        try
        {
            resonitePath = ResoniteLocator.Locate(options.ResonitePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Resoniteのインストールフォルダが見つかりませんでした。");
            Console.Error.WriteLine("  --resonite-path \"C:\\path\\to\\Resonite\" で指定するか、");
            Console.Error.WriteLine("  環境変数 RESONITE_PATH を設定してください。");
            PauseIfInteractive();
            return 3;
        }

        Console.WriteLine($"Resonite: {resonitePath}");
        ResoniteLocator.InstallAssemblyResolver(resonitePath);

        // Child processes (LocalDB probe) reuse the resolved installation.
        Environment.SetEnvironmentVariable("RESONITE_PATH", resonitePath);

        // FrooxEngine's initializer locates ProtoFlux assemblies relative to the
        // current directory, exactly like the official client launched from its folder.
        Environment.CurrentDirectory = resonitePath;

        try
        {
            int result = RunConverter(options, resonitePath);
            PauseIfInteractive();
            // The engine's update loop runs on a foreground thread; make sure the
            // process actually terminates even if engine shutdown timed out.
            Environment.Exit(result);
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"変換中にエラーが発生しました: {ex}");
            PauseIfInteractive();
            Environment.Exit(1);
            return 1;
        }
    }

    private static bool ShouldLaunchGui(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }
        // Drag & drop of supported files (VRM / VRChat .unitypackage) onto the exe opens the GUI;
        // anything with a flag argument stays on the CLI.
        return args.All(arg => File.Exists(arg) && GuiApp.IsSupportedInput(arg));
    }

    // NoInlining keeps FrooxEngine types from being JIT-resolved before the resolver is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunConverter(CliOptions options, string resonitePath)
    {
        if (options.InspectMode)
        {
            int result = 0;
            foreach (string file in options.InputFiles)
            {
                Console.WriteLine();
                Console.WriteLine($"### {file}");
                result |= PackageInspector.Inspect(file, options.InspectVerbose);
            }
            return result;
        }
        if (options.AssimpDump)
        {
            foreach (string file in options.InputFiles)
            {
                AssimpDump.Dump(file);
            }
            return 0;
        }
        if (options.VrchatDump)
        {
            int result = 0;
            foreach (string file in options.InputFiles)
            {
                Console.WriteLine();
                result |= Vrchat.VrchatDump.Dump(file, options.AvatarName);
            }
            return result;
        }
        return Converter.Run(options, resonitePath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// When launched by drag &amp; drop (no console parent that survives), keep the window
    /// open so the user can read the result.
    /// </summary>
    private static void PauseIfInteractive()
    {
        if (Environment.GetEnvironmentVariable("RESOPON_NOPAUSE") == "1")
        {
            return;
        }
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("何かキーを押すと終了します...");
            try
            {
                Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // No interactive console available.
            }
        }
    }
}

internal sealed class CliOptions
{
    public List<string> InputFiles { get; } = new();
    public string OutputDirectory { get; set; }
    public string ResonitePath { get; set; }
    public bool ShowHelp { get; set; }
    public bool NoAvatar { get; set; }
    public bool FaceTracking { get; set; }
    public bool NoProtection { get; set; }
    public bool NoExpressionMenu { get; set; }
    public bool DefaultUserScale { get; set; }
    public bool KeepWorkingFiles { get; set; }
    public float? ViewForward { get; set; }
    public float? ViewUp { get; set; }
    public float? NearClip { get; set; }
    public int ImportTimeoutSeconds { get; set; } = 300;
    public bool InspectMode { get; set; }
    public bool InspectVerbose { get; set; }
    public bool AssimpDump { get; set; }
    public bool VrchatDump { get; set; }

    /// <summary>Selects a specific avatar by name when a .unitypackage contains several.</summary>
    public string AvatarName { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    options.ShowHelp = true;
                    break;
                case "-o":
                case "--output":
                    options.OutputDirectory = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;
                case "--resonite-path":
                    options.ResonitePath = RequireValue(args, ref i, arg);
                    break;
                case "--no-avatar":
                    options.NoAvatar = true;
                    break;
                case "--face-tracking":
                    options.FaceTracking = true;
                    break;
                case "--no-protection":
                    options.NoProtection = true;
                    break;
                case "--no-expression-menu":
                    options.NoExpressionMenu = true;
                    break;
                case "--default-user-scale":
                    options.DefaultUserScale = true;
                    break;
                case "--inspect":
                    options.InspectMode = true;
                    break;
                case "--inspect-verbose":
                    options.InspectMode = true;
                    options.InspectVerbose = true;
                    break;
                case "--assimp-dump":
                    options.AssimpDump = true;
                    break;
                case "--vrchat-dump":
                    options.VrchatDump = true;
                    break;
                case "--avatar":
                    options.AvatarName = RequireValue(args, ref i, arg);
                    break;
                case "--keep-working-files":
                    options.KeepWorkingFiles = true;
                    break;
                case "--view-forward":
                    options.ViewForward = RequireFloat(args, ref i, arg, mustBePositive: false);
                    break;
                case "--view-up":
                    options.ViewUp = RequireFloat(args, ref i, arg, mustBePositive: false);
                    break;
                case "--near-clip":
                    options.NearClip = RequireFloat(args, ref i, arg, mustBePositive: false);
                    break;
                case "--import-timeout":
                    options.ImportTimeoutSeconds = (int)RequireFloat(args, ref i, arg, mustBePositive: true);
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"不明なオプション: {arg}");
                    }
                    if (!File.Exists(arg))
                    {
                        throw new ArgumentException($"ファイルが見つかりません: {arg}");
                    }
                    options.InputFiles.Add(Path.GetFullPath(arg));
                    break;
            }
        }
        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} には値が必要です");
        }
        index++;
        return args[index];
    }

    private static float RequireFloat(string[] args, ref int index, string name, bool mustBePositive)
    {
        string text = RequireValue(args, ref index, name);
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ||
            (mustBePositive && value <= 0f))
        {
            throw new ArgumentException($"{name} の値が不正です: {text}");
        }
        return value;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("使い方:");
        Console.WriteLine("  ResoPon.exe <model.vrm> [...] [オプション]");
        Console.WriteLine();
        Console.WriteLine("  VRM をこのexeにドラッグ&ドロップするだけでも変換できます。");
        Console.WriteLine("  出力は入力ファイルと同じ場所に <名前>.resonitepackage として保存されます。");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  -o, --output <dir>       出力先フォルダ");
        Console.WriteLine("  --resonite-path <dir>    Resoniteのインストールフォルダ");
        Console.WriteLine("  --no-avatar              アバターセットアップを行わずモデルのみ変換");
        Console.WriteLine("  --face-tracking          フェイストラッキング用AvatarExpressionDriverを生成");
        Console.WriteLine("  --no-protection          SimpleAvatarProtection（アバター保護）を付けない");
        Console.WriteLine("  --no-expression-menu     プリセット表情のコンテキストメニューを生成しない");
        Console.WriteLine("  --default-user-scale     DefaultUserScaleを付与し着用者を原寸サイズに縮小（低等身アバターの巨大化を防ぐ）");
        Console.WriteLine("  --view-forward <m>       視点の前方オフセット（既定: 目間距離から自動）");
        Console.WriteLine("  --view-up <m>            視点の上方オフセット（既定: 目間距離から自動)");
        Console.WriteLine("  --near-clip <m>          AvatarRenderSettingsのNearClip（既定: 0.075、0で無効）");
        Console.WriteLine("  --import-timeout <sec>   モデルインポートのタイムアウト秒数（既定: 300）");
        Console.WriteLine("  --keep-working-files     作業用一時ファイルを残す（デバッグ用）");
        Console.WriteLine("  -h, --help               このヘルプ");
        Console.WriteLine();
        Console.WriteLine("前提: Resonite（Steam版等）がインストールされていること。");
        Console.WriteLine("      ResoniteのDLLは実行時に読み込まれ、再配布はしません。");
    }
}
