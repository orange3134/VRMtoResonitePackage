using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace VrmToResonitePackage;

internal enum ResoniteSetupAction
{
    Later,
    Settings,
    Retry
}

/// <summary>Offers setup help without assuming an unsuccessful search means no installation exists.</summary>
internal sealed class ResoniteSetupWindow : Window
{
    private const string SteamStoreUrl = "https://store.steampowered.com/app/2519830/Resonite/";

    public ResoniteSetupAction Action { get; private set; } = ResoniteSetupAction.Later;

    public ResoniteSetupWindow(string configuredPath)
    {
        Title = AppLocalization.Get("ResoniteSetupTitle");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(configuredPath)
                ? AppLocalization.Get("ResoniteSetupNotFound")
                : AppLocalization.Format("ResoniteSetupInvalidPath", configuredPath),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var link = new Hyperlink(new Run(AppLocalization.Get("ResoniteSetupSteamLink")))
        {
            NavigateUri = new Uri(SteamStoreUrl)
        };
        // Open only in response to the user's click; never launch an installer automatically.
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(SteamStoreUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    AppLocalization.Format("ResoniteSetupLinkFailed", ex.Message),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var linkText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        linkText.Inlines.Add(link);
        panel.Children.Add(linkText);

        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        AddButton(buttons, "Settings", ResoniteSetupAction.Settings);
        AddButton(buttons, "ResoniteSetupRetry", ResoniteSetupAction.Retry);
        AddButton(buttons, "ResoniteSetupLater", ResoniteSetupAction.Later, isCancel: true);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void AddButton(Panel panel, string label, ResoniteSetupAction action, bool isCancel = false)
    {
        var button = new Button
        {
            Content = AppLocalization.Get(label),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = isCancel
        };
        button.Click += (_, _) =>
        {
            Action = action;
            DialogResult = action != ResoniteSetupAction.Later;
        };
        panel.Children.Add(button);
    }
}
