using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VrmToResonitePackage;

internal sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        Title = AppLocalization.Get("Help");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        TitleBarTheme.Apply(this);
        GuiTheme.Apply(this);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("HelpDescription"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Published destinations from GitHub issue #51. Keep URLs out of translations.
        AddLink(panel, "HelpGuideJapanese", "https://lapis-bell-123.notion.site/ResoPon-VRM-Resonite-3816b3a1e27f80738667f9c661105173?source=copy_link");
        AddLink(panel, "HelpGuideEnglish", "https://lapis-bell-123.notion.site/ResoPon-VRM-Upload-Your-Avatar-to-Resonite-in-One-Shot-39f6b3a1e27f809381e9d15456e46b00?source=copy_link");
        AddLink(panel, "HelpDownload", "https://orange3134.booth.pm/items/8515694");
        AddLink(panel, "HelpResonite", "https://store.steampowered.com/app/2519830/Resonite/");
        AddLink(panel, "HelpBugReport", "https://discord.com/channels/1155744905604366489/1514226522813239346");
        AddLink(panel, "HelpThirdPartyNotices", "https://github.com/orange3134/VRMtoResonitePackage/blob/main/THIRD-PARTY-NOTICES.md");

        var close = new Button
        {
            Content = AppLocalization.Get("Close"),
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }

    private void AddLink(Panel panel, string labelKey, string url)
    {
        var link = new Hyperlink(new Run(AppLocalization.Get(labelKey)))
        {
            NavigateUri = new Uri(url),
            ToolTip = url
        };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    AppLocalization.Format("HelpOpenLinkFailed", url, ex.Message),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        };
        text.Inlines.Add(link);
        panel.Children.Add(text);
    }
}
