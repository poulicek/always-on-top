using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlwaysOnTop;

internal static class Theme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Windows has two switches: "apps" (windows, menus) and "system" (taskbar, tray, start). Tray glyphs follow the latter.
    public static bool TaskbarDark() => ReadLight("SystemUsesLightTheme") != true;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    // Native Windows 11 tray glyphs are white on the dark taskbar and near-black on the light one.
    public static Color TaskbarForeground() => TaskbarDark() ? Color.White : Color.FromArgb(27, 27, 27);

    private static bool? ReadLight(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue(name) is int v ? v != 0 : null;
        }
        catch { return null; }
    }

    // The user's Windows accent colour (DWM stores it as 0xAABBGGRR).
    public static Color Accent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch { }
        return Color.FromArgb(0, 120, 212);
    }

    // Win32 popup menus follow the app theme only when the process opts in. These two uxtheme entry points (ordinals
    // 135/136, present since Windows 10 1809) are what Explorer and most tray apps use; failures are ignored.
    public static void EnableDarkMenus()
    {
        try
        {
            SetPreferredAppMode(1);            // AllowDark: follow "Choose your default app mode"
            FlushMenuThemes();
        }
        catch (Exception ex) { Log.Write("dark menus unavailable: " + ex.Message); }
    }

    public static void RefreshMenuTheme()
    {
        try { FlushMenuThemes(); } catch { }
    }

    // The size the shell wants for a tray icon: SM_CXSMICON at the taskbar's DPI (a hidden window's DPI goes stale
    // after a monitor switch, see Next Up).
    public static int TrayIconSize()
    {
        try
        {
            var tray = FindWindowW("Shell_TrayWnd", null);
            var dpi = tray != IntPtr.Zero ? GetDpiForWindow(tray) : GetDpiForSystem();
            if (dpi == 0) dpi = 96;
            var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
            return size > 0 ? size : (int)Math.Round(16 * dpi / 96.0);
        }
        catch { return 16; }
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
