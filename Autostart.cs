using Microsoft.Win32;

namespace AlwaysOnTop;

// "Start with Windows" via HKCU\...\Run. Written by the app itself (on start and on toggle) so the path is always current.
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(Program.AppName, $"\"{ExePath}\"");
        else key.DeleteValue(Program.AppName, throwOnMissingValue: false);
    }
}
