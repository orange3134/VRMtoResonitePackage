using System.Runtime.CompilerServices;

namespace VrmToResonitePackage;

/// <summary>
/// Entry point. Keeps all FrooxEngine types out of Main so the assembly resolver
/// can be installed before anything touches Resonite's DLLs.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("VRM -> ResonitePackage converter");
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
        return Converter.Run(options, resonitePath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// When launched by drag &amp; drop (no console parent that survives), keep the window
    /// open so the user can read the result.
    /// </summary>
    private static void PauseIfInteractive()
    {
        if (Environment.GetEnvironmentVariable("VRM2RESPKG_NOPAUSE") == "1")
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
    public bool KeepWorkingFiles { get; set; }
    public float? TargetHeight { get; set; }
    public bool InspectMode { get; set; }
    public bool InspectVerbose { get; set; }

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
                case "--inspect":
                    options.InspectMode = true;
                    break;
                case "--inspect-verbose":
                    options.InspectMode = true;
                    options.InspectVerbose = true;
                    break;
                case "--keep-working-files":
                    options.KeepWorkingFiles = true;
                    break;
                case "--height":
                    string heightText = RequireValue(args, ref i, arg);
                    if (!float.TryParse(heightText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float height) || height <= 0f)
                    {
                        throw new ArgumentException($"--height の値が不正です: {heightText}");
                    }
                    options.TargetHeight = height;
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

    public static void PrintUsage()
    {
        Console.WriteLine("使い方:");
        Console.WriteLine("  VrmToResonitePackage.exe <model.vrm> [model2.vrm ...] [オプション]");
        Console.WriteLine();
        Console.WriteLine("  VRMファイルをこのexeにドラッグ&ドロップするだけでも変換できます。");
        Console.WriteLine("  出力は入力ファイルと同じ場所に <名前>.resonitepackage として保存されます。");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  -o, --output <dir>       出力先フォルダ");
        Console.WriteLine("  --resonite-path <dir>    Resoniteのインストールフォルダ");
        Console.WriteLine("  --no-avatar              アバターセットアップを行わずモデルのみ変換");
        Console.WriteLine("  --face-tracking          フェイストラッキング用AvatarExpressionDriverを生成");
        Console.WriteLine("  --no-protection          SimpleAvatarProtection（アバター保護）を付けない");
        Console.WriteLine("  --height <m>             アバターの身長をメートル指定でリスケール");
        Console.WriteLine("  --keep-working-files     作業用一時ファイルを残す（デバッグ用）");
        Console.WriteLine("  -h, --help               このヘルプ");
        Console.WriteLine();
        Console.WriteLine("前提: Resonite（Steam版等）がインストールされていること。");
        Console.WriteLine("      ResoniteのDLLは実行時に読み込まれ、再配布はしません。");
    }
}
