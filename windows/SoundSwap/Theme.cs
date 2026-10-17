using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SoundSwap;

/// <summary>Tracks Windows' light/dark app theme and updates the color resources to match.</summary>
static class Theme
{
    static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["BgBrush"] = ("#F3F4F6", "#141518"),
        ["CardBrush"] = ("#FFFFFF", "#1C1D22"),
        ["LineBrush"] = ("#E5E7EB", "#2B2D33"),
        ["FieldBrush"] = ("#FFFFFF", "#22242A"),
        ["HoverBrush"] = ("#F3F4F6", "#2A2C33"),
        ["TextBrush"] = ("#111827", "#F3F4F6"),
        ["SubTextBrush"] = ("#6B7280", "#9CA3AF"),
        ["AccentBrush"] = ("#2563EB", "#3B82F6"),
        ["AccentSoftBrush"] = ("#EFF4FF", "#1E2A44"),
        ["GoodBrush"] = ("#16A34A", "#22C55E"),
        ["WarnBrush"] = ("#D97706", "#F59E0B"),
        ["BadBrush"] = ("#DC2626", "#EF4444"),
        ["TrackBrush"] = ("#E5E7EB", "#2B2D33"),
        ["SwitchOffBrush"] = ("#6B7280", "#9CA3AF"),
    };

    /// <summary>Theme forced from the command line (for testing); null = Windows setting.</summary>
    public static bool? Forced { get; set; }

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    static void Apply()
    {
        IsDark = Forced ?? ReadSystemDark();
        foreach (var (key, (light, dark)) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        Changed?.Invoke();
    }

    static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color Get(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    /// <summary>Matches the window title bar to the theme (colored caption on Windows 11, dark mode on 10).</summary>
    public static void StyleTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var bg = Get("BgBrush");
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Win11)
        var fg = Get("TextBrush");
        int text = fg.R | (fg.G << 8) | (fg.B << 16);
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR (Win11)
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
