using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VrmToResonitePackage;

/// <summary>
/// Tints the OS window chrome (title bar, caption text, border) to match the app theme via DWM,
/// dimming to a lighter shade while the window is deactivated.
/// Requires Windows 11 (build 22000+); on older Windows the attributes are no-ops and we ignore them.
/// </summary>
internal static class TitleBarTheme
{
    // Theme: base color #caa4ec with white caption text when focused; a lighter lavender with muted
    // text when the window loses focus, mirroring how Windows dims inactive title bars.
    private static readonly Color ActiveCaption = Color.FromRgb(0xca, 0xa4, 0xec);
    private static readonly Color InactiveCaption = Color.FromRgb(0xe4, 0xd1, 0xf5);
    private static readonly Color BaseCaption = Color.FromRgb(248, 244, 252);

    // DWMWINDOWATTRIBUTE values (dwmapi.h).
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Applies the themed chrome to <paramref name="window"/> and keeps it in sync with focus.</summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) => SetColors(window, window.IsActive);
        window.Activated += (_, _) => SetColors(window, true);
        window.Deactivated += (_, _) => SetColors(window, false);
    }

    private static void SetColors(Window window, bool active)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            int caption = ToColorRef(active ? ActiveCaption : InactiveCaption);
            int baseCaption = ToColorRef(BaseCaption);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref baseCaption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref caption, sizeof(int));
        }
        catch
        {
            // Pre-Windows 11 or DWM unavailable: keep the default chrome.
        }
    }

    // DWM expects a COLORREF (0x00BBGGRR).
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
