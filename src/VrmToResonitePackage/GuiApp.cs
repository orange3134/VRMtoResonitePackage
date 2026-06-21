using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrm;
using VrmToResonitePackage.Vrchat;

namespace VrmToResonitePackage;

internal static class GuiApp
{
    public static int Run(IReadOnlyList<string> initialFiles)
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        var window = new MainWindow(initialFiles);
        app.Run(window);
        return 0;
    }

    /// <summary>True for the input file types the converter accepts (VRM or a VRChat .unitypackage).</summary>
    internal static bool IsSupportedInput(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".vrm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".unitypackage", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class MainWindow : Window
{
    private readonly Grid _root;
    private readonly Image _logo;
    private readonly Grid _dropZone;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly TextBlock _detail;
    private readonly Button _settingsButton;
    private readonly DispatcherTimer _spinnerTimer;
    private readonly Grid _loadingArea;
    private readonly RotateTransform _spinnerRotation = new();
    private readonly Border _packageIcon;
    private readonly GuiSettings _settings;
    private IReadOnlyList<string> _outputFiles = Array.Empty<string>();
    private string _lastLogPath;
    private bool _isConverting;
    private Point _dragStart;

    public MainWindow(IReadOnlyList<string> initialFiles)
    {
        _settings = GuiSettings.Load();

        Title = $"ResoPon  v{AppVersion.Display}";
        Width = 760;
        Height = 500;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        AllowDrop = true;
        Icon = LoadWindowIcon();
        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        _root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 244, 252))
        };
        Content = _root;

        // Themed icon button: shares the rounded white/accent-outline style; the accent glyph color
        // is kept as a local value and zero padding centers the gear in the fixed 44x44 box.
        _settingsButton = new Button
        {
            Content = "⚙",
            Width = 44,
            Height = 44,
            FontSize = 22,
            Padding = new Thickness(0),
            ToolTip = "設定",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 18, 0),
            Foreground = AccentBrush
        };
        _settingsButton.Click += (_, _) => OpenSettings();

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
            MaxWidth = 640
        };

        _logo = new Image
        {
            Source = SvgImage.Load("logo.svg", TextColor),
            Width = 380,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true
        };

        _loadingArea = BuildLoadingArea();

        _title = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            FontSize = 36,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        _message = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            TextAlignment = TextAlignment.Center,
            FontSize = 20,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        _detail = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
            TextWrapping = TextWrapping.Wrap
        };

        _dropZone = BuildDropZone();

        _packageIcon = BuildPackageIcon();
        _packageIcon.Visibility = Visibility.Collapsed;
        _packageIcon.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        _packageIcon.MouseMove += PackageIconOnMouseMove;

        center.Children.Add(_logo);
        center.Children.Add(_loadingArea);
        center.Children.Add(_packageIcon);
        center.Children.Add(_title);
        center.Children.Add(_dropZone);
        center.Children.Add(_message);
        center.Children.Add(_detail);
        _root.Children.Add(center);
        _root.Children.Add(_settingsButton);

        DragEnter += WindowOnDragEnter;
        Drop += WindowOnDrop;

        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick += (_, _) => _spinnerRotation.Angle = (_spinnerRotation.Angle + 7) % 360;

        ShowIdle();

        if (initialFiles.Count > 0)
        {
            Loaded += async (_, _) => await StartConversion(initialFiles);
        }
    }

    // Theme: base color #caa4ec, white text on the base color, #3e3e3e text on light backgrounds.
    private static Color AccentColor { get; } = Color.FromRgb(0xca, 0xa4, 0xec);
    private static Color TextColor { get; } = Color.FromRgb(0x3e, 0x3e, 0x3e);
    private static Brush AccentBrush { get; } = Frozen(AccentColor);
    private static Brush TextBrush { get; } = Frozen(TextColor);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ShowIdle()
    {
        _isConverting = false;
        _spinnerTimer.Stop();
        _logo.Visibility = Visibility.Visible;
        _dropZone.Visibility = Visibility.Visible;
        _loadingArea.Visibility = Visibility.Collapsed;
        _packageIcon.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Collapsed;
        _detail.Visibility = Visibility.Collapsed;
        _settingsButton.IsEnabled = true;
        _title.Text = "";
        _message.Text = "";
        _detail.Text = "";
    }

    private void ShowConverting(string fileName)
    {
        _isConverting = true;
        _logo.Visibility = Visibility.Collapsed;
        _dropZone.Visibility = Visibility.Collapsed;
        _loadingArea.Visibility = Visibility.Visible;
        _packageIcon.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
        _detail.Visibility = Visibility.Visible;
        _settingsButton.IsEnabled = false;
        _spinnerTimer.Start();
        _message.Text = fileName;
        _detail.Text = "ログは実行ファイル横の Logs フォルダに出力されます。";
    }

    private void ShowComplete(ConversionRunResult result)
    {
        _isConverting = false;
        _spinnerTimer.Stop();
        _logo.Visibility = Visibility.Collapsed;
        _dropZone.Visibility = Visibility.Collapsed;
        _loadingArea.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Visible;
        _detail.Visibility = Visibility.Visible;
        _settingsButton.IsEnabled = true;
        _outputFiles = result.OutputFiles.Where(File.Exists).ToArray();
        _packageIcon.Visibility = result.ExitCode == 0 && _outputFiles.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _lastLogPath = result.LogPath;
        _title.Text = result.ExitCode == 0 ? "変換完了！" : "変換に失敗しました";
        _message.Text = result.ExitCode == 0
            ? "このアイコンをResoniteの画面にドラッグしてください"
            : "ログを確認してください。";
        _detail.Text = result.ExitCode == 0
            ? Path.GetFileName(result.OutputFiles.FirstOrDefault() ?? "")
            : $"ログ: {result.LogPath}";
    }

    private async Task StartConversion(IReadOnlyList<string> files)
    {
        if (_isConverting)
        {
            return;
        }

        string[] inputFiles = files.Where(GuiApp.IsSupportedInput).ToArray();
        if (inputFiles.Length == 0)
        {
            _detail.Text = "VRM ファイルを指定してください。";
            return;
        }

        // A .unitypackage may hold several avatars; let the user pick which one (skipped for a
        // single avatar or a VRM). Done before conversion, engine-independent.
        List<ConversionJob> jobs = await BuildConversionJobs(inputFiles);
        if (jobs == null)
        {
            ShowIdle(); // user cancelled the avatar selection
            return;
        }

        try
        {
            string resonitePath = ResoniteLocator.Locate(_settings.ResonitePath);
            var outputs = new List<string>();
            int exitCode = 0;
            int failures = 0;
            string logPath = null;
            foreach (ConversionJob job in jobs)
            {
                ShowConverting(job.DisplayName);
                ConversionRunResult result = await RunConversionProcessAsync(job.Options, resonitePath);
                outputs.AddRange(result.OutputFiles);
                failures += result.Failures;
                if (!string.IsNullOrWhiteSpace(result.LogPath))
                {
                    logPath = result.LogPath;
                }
                if (result.ExitCode != 0)
                {
                    exitCode = result.ExitCode;
                }
            }
            ShowComplete(new ConversionRunResult(exitCode, outputs, logPath, failures));
        }
        catch (Exception ex)
        {
            _isConverting = false;
            _spinnerTimer.Stop();
            _logo.Visibility = Visibility.Collapsed;
            _dropZone.Visibility = Visibility.Collapsed;
            _loadingArea.Visibility = Visibility.Collapsed;
            _packageIcon.Visibility = Visibility.Collapsed;
            _title.Visibility = Visibility.Visible;
            _message.Visibility = Visibility.Visible;
            _detail.Visibility = Visibility.Visible;
            _settingsButton.IsEnabled = true;
            _title.Text = "変換に失敗しました";
            _message.Text = ex.Message;
            _detail.Text = string.IsNullOrWhiteSpace(_lastLogPath) ? "" : $"ログ: {_lastLogPath}";
        }
    }

    /// <summary>
    /// Builds the conversion jobs, prompting for avatar selection on any .unitypackage that holds
    /// more than one avatar. VRMs are grouped into one batch job; each .unitypackage becomes its own
    /// job carrying the selected prefab name for the progress display. Returns null if the user
    /// cancels a selection.
    /// </summary>
    private async Task<List<ConversionJob>> BuildConversionJobs(string[] inputFiles)
    {
        // Listing a package's avatars uses Elements.Core (UniLog); the GUI process must resolve
        // Resonite's assemblies for that to JIT. The CLI path installs this; the GUI doesn't, so
        // ensure it before parsing (best-effort — if it fails, listing falls back to no dialog).
        EnsureAssemblyResolver();

        var batchFiles = new List<string>();
        var avatarJobs = new List<ConversionJob>();
        foreach (string file in inputFiles)
        {
            string extension = Path.GetExtension(file);
            if (_settings.PromptMtoonTransparentBlendMode &&
                string.Equals(extension, ".vrm", StringComparison.OrdinalIgnoreCase))
            {
                ShowConverting(Path.GetFileName(file) + " を解析中...");
                IReadOnlyList<string> transparentMaterials = await Task.Run(() => ListMtoonTransparentMaterials(file));
                if (transparentMaterials.Count > 0)
                {
                    IReadOnlyList<string> cutoutMaterials = SelectMtoonTransparentModes(transparentMaterials);
                    if (cutoutMaterials == null)
                    {
                        return null;
                    }

                    CliOptions option = _settings.ToCliOptions(new[] { file });
                    foreach (string material in cutoutMaterials)
                    {
                        option.MtoonTransparentCutoutMaterials.Add(material);
                    }
                    avatarJobs.Add(new ConversionJob(option, Path.GetFileName(file)));
                    continue;
                }
            }

            if (string.Equals(extension, ".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                ShowConverting(Path.GetFileName(file) + " を解析中...");
                IReadOnlyList<VrchatAvatarChoice> avatars = await Task.Run(() => ListPackageAvatars(file));
                VrchatAvatarChoice selected = avatars.Count == 1 ? avatars[0] : null;
                if (avatars.Count > 1)
                {
                    string chosen = SelectAvatar(file, avatars);
                    if (chosen == null)
                    {
                        return null; // cancelled
                    }
                    selected = avatars.FirstOrDefault(avatar =>
                        string.Equals(avatar.Name, chosen, StringComparison.Ordinal));
                }

                CliOptions option = _settings.ToCliOptions(new[] { file });
                option.AvatarName = selected?.Name;
                string prefabName = Path.GetFileName(selected?.SourcePath);
                string displayName = string.IsNullOrWhiteSpace(prefabName)
                    ? Path.GetFileName(file)
                    : $"{Path.GetFileName(file)}\n{prefabName}";
                avatarJobs.Add(new ConversionJob(option, displayName));
                continue;
            }
            batchFiles.Add(file);
        }

        var jobs = new List<ConversionJob>();
        if (batchFiles.Count > 0)
        {
            jobs.Add(new ConversionJob(
                _settings.ToCliOptions(batchFiles.ToArray()),
                Path.GetFileName(batchFiles[0])));
        }
        jobs.AddRange(avatarJobs);
        return jobs;
    }

    private sealed record ConversionJob(CliOptions Options, string DisplayName);

    private bool _resolverInstalled;

    private void EnsureAssemblyResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }
        try
        {
            string resonitePath = ResoniteLocator.Locate(_settings.ResonitePath);
            ResoniteLocator.InstallAssemblyResolver(resonitePath);
            _resolverInstalled = true;
        }
        catch
        {
            // No Resonite found yet; avatar listing will fall back to a single auto-selected avatar.
        }
    }

    private static IReadOnlyList<VrchatAvatarChoice> ListPackageAvatars(string path)
    {
        try
        {
            using Unity.UnityPackage package = Unity.UnityPackage.Extract(path);
            return Vrchat.VrchatAvatarParser.ListAvatars(package);
        }
        catch
        {
            // If listing fails, fall through to a normal (auto-select) conversion.
            return Array.Empty<VrchatAvatarChoice>();
        }
    }

    private static IReadOnlyList<string> ListMtoonTransparentMaterials(string path)
    {
        VrmModel model = VrmParser.Parse(path);
        return model.Materials.Values
            .Where(material => material.IsMToon && material.AlphaMode == "blend")
            .Select(material => material.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string SelectAvatar(string packageFile, IReadOnlyList<VrchatAvatarChoice> avatars)
    {
        var dialog = new AvatarSelectionWindow(Path.GetFileName(packageFile), avatars) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.SelectedAvatar : null;
    }

    private IReadOnlyList<string> SelectMtoonTransparentModes(IReadOnlyList<string> materials)
    {
        var dialog = new MtoonTransparentModeWindow(materials) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.CutoutMaterials : null;
    }

    private static async Task<ConversionRunResult> RunConversionProcessAsync(CliOptions options, string resonitePath)
    {
        string logsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
        DateTime startTime = DateTime.Now;

        var startInfo = CreateConverterProcessStartInfo();
        startInfo.Environment["RESOPON_NOPAUSE"] = "1";
        AddCliArguments(startInfo.ArgumentList, options, resonitePath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await outputTask;
        string error = await errorTask;

        string logPath = FindLatestLogPath(logsDirectory, startTime);
        string[] reportedOutputFiles = output
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("RESOPON_OUTPUT:", StringComparison.Ordinal))
            .Select(line => line["RESOPON_OUTPUT:".Length..].Trim())
            .Where(File.Exists)
            .ToArray();
        string[] outputFiles = reportedOutputFiles.Length > 0
            ? reportedOutputFiles
            : GetExpectedOutputFiles(options).Where(File.Exists).ToArray();
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(logPath))
        {
            logPath = WriteGuiProcessFailureLog(logsDirectory, output, error);
        }
        return new ConversionRunResult(process.ExitCode, outputFiles, logPath, process.ExitCode == 0 ? 0 : 1);
    }

    private static ProcessStartInfo CreateConverterProcessStartInfo()
    {
        string processPath = Environment.ProcessPath;
        string commandPath = Environment.GetCommandLineArgs().FirstOrDefault();
        ProcessStartInfo startInfo;
        if (IsDotNetHost(processPath) && !string.IsNullOrWhiteSpace(commandPath) && File.Exists(commandPath))
        {
            startInfo = new ProcessStartInfo(processPath);
            startInfo.ArgumentList.Add(Path.GetFullPath(commandPath));
        }
        else if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            startInfo = new ProcessStartInfo(processPath);
        }
        else if (!string.IsNullOrWhiteSpace(commandPath) && File.Exists(commandPath))
        {
            startInfo = new ProcessStartInfo(Path.GetFullPath(commandPath));
        }
        else
        {
            throw new InvalidOperationException("実行ファイルのパスを取得できませんでした。");
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = AppContext.BaseDirectory;
        return startInfo;
    }

    private static bool IsDotNetHost(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCliArguments(Collection<string> arguments, CliOptions options, string resonitePath)
    {
        arguments.Add("--resonite-path");
        arguments.Add(resonitePath);
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            arguments.Add("--output");
            arguments.Add(options.OutputDirectory);
        }
        if (!string.IsNullOrWhiteSpace(options.AvatarName))
        {
            arguments.Add("--avatar");
            arguments.Add(options.AvatarName);
        }
        if (options.NoAvatar)
        {
            arguments.Add("--no-avatar");
        }
        if (options.FaceTracking)
        {
            arguments.Add("--face-tracking");
        }
        if (options.NoProtection)
        {
            arguments.Add("--no-protection");
        }
        if (options.NoExpressionMenu)
        {
            arguments.Add("--no-expression-menu");
        }
        if (options.DefaultUserScale)
        {
            arguments.Add("--default-user-scale");
        }
        if (options.KeepWorkingFiles)
        {
            arguments.Add("--keep-working-files");
        }
        AddNullableFloat(arguments, "--view-forward", options.ViewForward);
        AddNullableFloat(arguments, "--view-up", options.ViewUp);
        AddNullableFloat(arguments, "--near-clip", options.NearClip);
        arguments.Add("--import-timeout");
        arguments.Add(options.ImportTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        foreach (string material in options.MtoonTransparentCutoutMaterials)
        {
            arguments.Add("--mtoon-transparent-cutout");
            arguments.Add(material);
        }
        foreach (string file in options.InputFiles)
        {
            arguments.Add(file);
        }
    }

    private static void AddNullableFloat(Collection<string> arguments, string name, float? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        arguments.Add(name);
        arguments.Add(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string[] GetExpectedOutputFiles(CliOptions options)
    {
        return options.InputFiles
            .Select(file =>
            {
                string outputDirectory = options.OutputDirectory ?? Path.GetDirectoryName(file);
                string outputName = string.Equals(
                        Path.GetExtension(file), ".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(options.AvatarName)
                        ? options.AvatarName
                        : Path.GetFileNameWithoutExtension(file);
                return Path.Combine(
                    outputDirectory,
                    SanitizeFileName(outputName) + ".resonitepackage");
            })
            .ToArray();
    }

    private static string FindLatestLogPath(string logsDirectory, DateTime startTime)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return null;
        }

        return Directory.GetFiles(logsDirectory, "convert_*.log")
            .Select(file => new FileInfo(file))
            .Where(file => file.LastWriteTime >= startTime.AddSeconds(-2))
            .OrderByDescending(file => file.LastWriteTime)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string WriteGuiProcessFailureLog(string logsDirectory, string output, string error)
    {
        Directory.CreateDirectory(logsDirectory);
        string logPath = Path.Combine(logsDirectory, $"gui_process_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        File.WriteAllText(logPath, output + Environment.NewLine + error);
        return logPath;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private void WindowOnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_isConverting
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WindowOnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _isConverting)
        {
            return;
        }
        await StartConversion((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    private void PackageIconOnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _outputFiles.Count == 0)
        {
            return;
        }
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var files = new StringCollection();
        foreach (string file in _outputFiles.Where(File.Exists))
        {
            files.Add(file);
        }
        if (files.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetFileDropList(files);
        DragDrop.DoDragDrop(_packageIcon, data, DragDropEffects.Copy);
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.CopyFrom(dialog.Settings);
            _settings.Save();
        }
    }

    private static ImageSource LoadWindowIcon()
    {
        try
        {
            using Stream stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("VrmToResonitePackage.Resources.ResoPon.png");
            if (stream == null)
            {
                return null;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The rotating loading mark with the "変換中..." caption layered on top of it.</summary>
    private Grid BuildLoadingArea()
    {
        var area = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        area.Children.Add(new Image
        {
            Source = SvgImage.Load("loading.svg", AccentColor),
            Width = 150,
            Height = 150,
            Stretch = Stretch.Uniform,
            RenderTransform = _spinnerRotation,
            RenderTransformOrigin = new Point(0.5, 0.5)
        });
        area.Children.Add(new TextBlock
        {
            Text = "変換中...",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return area;
    }

    /// <summary>Large rounded dashed drop target that signals where to drag VRM files.</summary>
    private static Grid BuildDropZone()
    {
        var zone = new Grid
        {
            Margin = new Thickness(0, 30, 0, 0),
            MaxWidth = 540,
            Visibility = Visibility.Collapsed
        };
        zone.Children.Add(new System.Windows.Shapes.Rectangle
        {
            RadiusX = 20,
            RadiusY = 20,
            Stroke = AccentBrush,
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeDashCap = PenLineCap.Round,
            Fill = Frozen(Color.FromArgb(38, AccentColor.R, AccentColor.G, AccentColor.B))
        });
        var content = new StackPanel
        {
            Margin = new Thickness(44, 34, 44, 34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new Image
        {
            Source = SvgImage.Load("arrow.svg", AccentColor),
            Width = 46,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock
        {
            Text = "VRM ファイルをここにドラッグ＆ドロップ",
            TextAlignment = TextAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        zone.Children.Add(content);
        return zone;
    }

    private static Border BuildPackageIcon()
    {
        var icon = new Border
        {
            Width = 116,
            Height = 132,
            Margin = new Thickness(0, 0, 0, 22),
            Background = Brushes.White,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "R",
            FontSize = 52,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new TextBlock
        {
            Text = ".resonitepackage",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e)),
            Margin = new Thickness(6, 0, 6, 10),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(label, 1);
        panel.Children.Add(label);
        icon.Child = panel;
        return icon;
    }
}

internal sealed class SettingsWindow : Window
{
    private const string DefaultOutputDirectoryText = "入力ファイルと同じフォルダ";
    private const string AutoDetectText = "自動検出";
    private const string AutoValueText = "自動";

    private readonly TextBox _outputDirectory = new();
    private readonly TextBox _resonitePath = new();
    private readonly CheckBox _noAvatar = new() { Content = "アバターセットアップを行わない" };
    private readonly CheckBox _faceTracking = new() { Content = "フェイストラッキング用ドライバーを生成" };
    private readonly CheckBox _noProtection = new() { Content = "アバター保護を付けない" };
    private readonly CheckBox _noExpressionMenu = new() { Content = "表情メニューを生成しない" };
    private readonly CheckBox _defaultUserScale = new() { Content = "アバターの原寸サイズを維持" };
    private readonly CheckBox _keepWorkingFiles = new() { Content = "作業用一時ファイルを残す" };
    private readonly CheckBox _promptMtoonTransparentBlendMode = new() { Content = "半透明マテリアルの変換先を手動で選ぶ" };
    private readonly TextBox _viewForward = new();
    private readonly TextBox _viewUp = new();
    private readonly TextBox _nearClip = new();
    private readonly TextBox _importTimeout = new();

    public SettingsWindow(GuiSettings settings)
    {
        Settings = settings.Clone();
        Title = "設定";
        Width = 560;
        Height = 620;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 244, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        panel.Children.Add(Header("変換オプション"));
        panel.Children.Add(PathRow("出力先フォルダ", _outputDirectory));
        panel.Children.Add(PathRow("Resoniteフォルダ", _resonitePath));
        panel.Children.Add(_faceTracking);
        panel.Children.Add(_noProtection);
        panel.Children.Add(_noExpressionMenu);
        panel.Children.Add(_defaultUserScale);
        panel.Children.Add(_promptMtoonTransparentBlendMode);
        panel.Children.Add(_keepWorkingFiles);
        panel.Children.Add(Field("視点の前方オフセット(m)", _viewForward));
        panel.Children.Add(Field("視点の上方オフセット(m)", _viewUp));
        panel.Children.Add(Field("NearClip(m)", _nearClip));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 28, 0, 0)
        };
        var cancel = new Button { Content = "キャンセル", MinWidth = 104, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button
        {
            Content = "保存",
            MinWidth = 104,
            Style = (Style)FindResource(GuiTheme.AccentButtonKey)
        };
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => SaveAndClose();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        LoadValues();
    }

    public GuiSettings Settings { get; private set; }

    private void LoadValues()
    {
        _outputDirectory.Text = Settings.OutputDirectory ?? DefaultOutputDirectoryText;
        _resonitePath.Text = Settings.ResonitePath ?? AutoDetectText;
        _noAvatar.IsChecked = Settings.NoAvatar;
        _faceTracking.IsChecked = Settings.FaceTracking;
        _noProtection.IsChecked = Settings.NoProtection;
        _noExpressionMenu.IsChecked = Settings.NoExpressionMenu;
        _defaultUserScale.IsChecked = Settings.DefaultUserScale;
        _promptMtoonTransparentBlendMode.IsChecked = Settings.PromptMtoonTransparentBlendMode;
        _keepWorkingFiles.IsChecked = Settings.KeepWorkingFiles;
        _viewForward.Text = Settings.ViewForward?.ToString(CultureInfo.InvariantCulture) ?? AutoValueText;
        _viewUp.Text = Settings.ViewUp?.ToString(CultureInfo.InvariantCulture) ?? AutoValueText;
        _nearClip.Text = Settings.NearClip?.ToString(CultureInfo.InvariantCulture) ?? "0.075";
        _importTimeout.Text = Settings.ImportTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void SaveAndClose()
    {
        try
        {
            Settings.OutputDirectory = EmptyToNull(_outputDirectory.Text, DefaultOutputDirectoryText);
            Settings.ResonitePath = EmptyToNull(_resonitePath.Text, AutoDetectText);
            Settings.NoAvatar = _noAvatar.IsChecked == true;
            Settings.FaceTracking = _faceTracking.IsChecked == true;
            Settings.NoProtection = _noProtection.IsChecked == true;
            Settings.NoExpressionMenu = _noExpressionMenu.IsChecked == true;
            Settings.DefaultUserScale = _defaultUserScale.IsChecked == true;
            Settings.PromptMtoonTransparentBlendMode = _promptMtoonTransparentBlendMode.IsChecked == true;
            Settings.KeepWorkingFiles = _keepWorkingFiles.IsChecked == true;
            Settings.ViewForward = ParseNullableFloat(_viewForward.Text, "視点の前方オフセット", AutoValueText);
            Settings.ViewUp = ParseNullableFloat(_viewUp.Text, "視点の上方オフセット", AutoValueText);
            Settings.NearClip = ParseNullableFloat(_nearClip.Text, "NearClip");
            Settings.ImportTimeoutSeconds = (int)(ParseNullableFloat(_importTimeout.Text, "インポートタイムアウト") ?? 300);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e)),
        Margin = new Thickness(0, 0, 0, 20)
    };

    private static FrameworkElement Field(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e)) });
        box.Margin = new Thickness(0, 8, 0, 0);
        box.Height = 36;
        panel.Children.Add(box);
        return panel;
    }

    private FrameworkElement PathRow(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e)) });
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Height = 36;
        row.Children.Add(box);
        var browse = new Button { Content = "参照", MinWidth = 80, Height = 36, Margin = new Thickness(10, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(box.Text))
            {
                dialog.InitialDirectory = box.Text;
            }
            if (dialog.ShowDialog(this) == true)
            {
                box.Text = dialog.FolderName;
            }
        };
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        panel.Children.Add(row);
        return panel;
    }

    private static string EmptyToNull(string value, string defaultText = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string trimmed = value.Trim();
        return string.Equals(trimmed, defaultText, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static float? ParseNullableFloat(string value, string label, string defaultText = null)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), defaultText, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
        {
            throw new FormatException($"{label} は数値で入力してください。");
        }
        return result;
    }
}

/// <summary>Modal dialog that asks which avatar to convert when a package contains several.</summary>
internal sealed class AvatarSelectionWindow : Window
{
    private readonly ListBox _list = new();

    public string SelectedAvatar { get; private set; }

    public AvatarSelectionWindow(string packageName, IReadOnlyList<VrchatAvatarChoice> avatars)
    {
        Title = "アバターを選択";
        Width = 480;
        Height = 460;
        MinWidth = 400;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 244, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock
        {
            Text = "変換するアバターを選択",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e))
        });
        header.Children.Add(new TextBlock
        {
            Text = packageName,
            Margin = new Thickness(0, 4, 0, 14),
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        root.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "キャンセル", MinWidth = 104, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button
        {
            Content = "変換",
            MinWidth = 104,
            Style = (Style)FindResource(GuiTheme.AccentButtonKey)
        };
        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        for (int i = 0; i < avatars.Count; i++)
        {
            VrchatAvatarChoice avatar = avatars[i];
            var content = new StackPanel { Margin = new Thickness(4) };
            content.Children.Add(new TextBlock
            {
                Text = avatar.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e))
            });
            content.Children.Add(new TextBlock
            {
                Text = avatar.SourcePath,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            _list.Items.Add(new ListBoxItem { Content = content, Tag = avatar.Name });
        }
        _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => Confirm();
        root.Children.Add(_list);
    }

    private void Confirm()
    {
        if (_list.SelectedItem is ListBoxItem item && item.Tag is string name)
        {
            SelectedAvatar = name;
            DialogResult = true;
        }
    }
}

/// <summary>Modal dialog that lets users override MToon Transparent materials to Cutout.</summary>
internal sealed class MtoonTransparentModeWindow : Window
{
    private readonly Dictionary<string, ComboBox> _modeBoxes = new(StringComparer.Ordinal);

    public IReadOnlyList<string> CutoutMaterials { get; private set; } = Array.Empty<string>();

    public MtoonTransparentModeWindow(IReadOnlyList<string> materials)
    {
        Title = "半透明マテリアルの変換先設定";
        Width = 560;
        Height = 520;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 244, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock
        {
            Text = "半透明マテリアルの変換先設定",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e))
        });
        header.Children.Add(new TextBlock
        {
            Text = "Resoniteのシェーダーの仕様により、それぞれ以下のような表示になります。\n" +
                   "Alpha: 半透明処理が綺麗にできますが、特定のライティングで光って見えます。\n" +
                   "Cutout: 透明か不透明かのゼロイチになりますが、ライティングに馴染みます。",
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 13,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
            TextWrapping = TextWrapping.Wrap
        });
        header.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "キャンセル", MinWidth = 104, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button
        {
            Content = "変換",
            MinWidth = 104,
            Style = (Style)FindResource(GuiTheme.AccentButtonKey)
        };
        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        var list = new StackPanel();
        foreach (string material in materials)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = material,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x3e)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var mode = new ComboBox
            {
                Width = 120,
                Height = 34,
                Margin = new Thickness(14, 0, 0, 0)
            };
            mode.Items.Add("Alpha");
            mode.Items.Add("Cutout");
            mode.SelectedIndex = 0;
            Grid.SetColumn(mode, 1);
            row.Children.Add(mode);
            _modeBoxes[material] = mode;
            list.Children.Add(row);
        }

        root.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
    }

    private void Confirm()
    {
        CutoutMaterials = _modeBoxes
            .Where(pair => string.Equals(pair.Value.SelectedItem as string, "Cutout", StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        DialogResult = true;
    }
}

internal sealed class GuiSettings
{
    public string OutputDirectory { get; set; }
    public string ResonitePath { get; set; }
    public bool NoAvatar { get; set; }
    public bool FaceTracking { get; set; }
    public bool NoProtection { get; set; }
    public bool NoExpressionMenu { get; set; }
    public bool DefaultUserScale { get; set; }
    public bool KeepWorkingFiles { get; set; }
    public bool PromptMtoonTransparentBlendMode { get; set; }
    public float? ViewForward { get; set; }
    public float? ViewUp { get; set; }
    public float? NearClip { get; set; }
    public int ImportTimeoutSeconds { get; set; } = 300;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ResoPon",
        "settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new GuiSettings();
            }
        }
        catch
        {
        }
        return new GuiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public GuiSettings Clone() => new()
    {
        OutputDirectory = OutputDirectory,
        ResonitePath = ResonitePath,
        NoAvatar = NoAvatar,
        FaceTracking = FaceTracking,
        NoProtection = NoProtection,
        NoExpressionMenu = NoExpressionMenu,
        DefaultUserScale = DefaultUserScale,
        KeepWorkingFiles = KeepWorkingFiles,
        PromptMtoonTransparentBlendMode = PromptMtoonTransparentBlendMode,
        ViewForward = ViewForward,
        ViewUp = ViewUp,
        NearClip = NearClip,
        ImportTimeoutSeconds = ImportTimeoutSeconds
    };

    public void CopyFrom(GuiSettings other)
    {
        OutputDirectory = other.OutputDirectory;
        ResonitePath = other.ResonitePath;
        NoAvatar = other.NoAvatar;
        FaceTracking = other.FaceTracking;
        NoProtection = other.NoProtection;
        NoExpressionMenu = other.NoExpressionMenu;
        DefaultUserScale = other.DefaultUserScale;
        KeepWorkingFiles = other.KeepWorkingFiles;
        PromptMtoonTransparentBlendMode = other.PromptMtoonTransparentBlendMode;
        ViewForward = other.ViewForward;
        ViewUp = other.ViewUp;
        NearClip = other.NearClip;
        ImportTimeoutSeconds = other.ImportTimeoutSeconds;
    }

    public CliOptions ToCliOptions(IEnumerable<string> files)
    {
        var options = new CliOptions
        {
            OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : Path.GetFullPath(OutputDirectory),
            ResonitePath = ResonitePath,
            NoAvatar = NoAvatar,
            FaceTracking = FaceTracking,
            NoProtection = NoProtection,
            NoExpressionMenu = NoExpressionMenu,
            DefaultUserScale = DefaultUserScale,
            KeepWorkingFiles = KeepWorkingFiles,
            ViewForward = ViewForward,
            ViewUp = ViewUp,
            NearClip = NearClip,
            ImportTimeoutSeconds = ImportTimeoutSeconds <= 0 ? 300 : ImportTimeoutSeconds
        };
        foreach (string file in files)
        {
            options.InputFiles.Add(Path.GetFullPath(file));
        }
        return options;
    }
}
