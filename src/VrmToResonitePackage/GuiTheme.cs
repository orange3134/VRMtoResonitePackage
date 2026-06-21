using System.Windows;
using System.Windows.Markup;

namespace VrmToResonitePackage;

/// <summary>
/// Loads the shared control styles (rounded, app-themed buttons / fields / dropdowns / checkboxes)
/// from the embedded <c>Theme/ControlStyles.xaml</c> and merges them into a window's resources.
/// Parsed at runtime with <see cref="XamlReader"/> rather than WPF markup-compiled, so the build
/// doesn't have to resolve the full Resonite DLL reference closure.
/// </summary>
internal static class GuiTheme
{
    private static ResourceDictionary _controls;

    /// <summary>The accent button style key for emphasized/primary actions (Save, 変換).</summary>
    public const string AccentButtonKey = "AccentButton";

    private static ResourceDictionary Controls => _controls ??= Load();

    private static ResourceDictionary Load()
    {
        using Stream stream = typeof(GuiTheme).Assembly
            .GetManifestResourceStream("VrmToResonitePackage.Theme.ControlStyles.xaml");
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>Merges the themed control styles into <paramref name="element"/>'s resources.</summary>
    public static void Apply(FrameworkElement element)
    {
        element.Resources.MergedDictionaries.Add(Controls);
    }
}
