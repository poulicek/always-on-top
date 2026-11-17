using System.Text.Json;

namespace AlwaysOnTop;

internal static class Paths
{
    public static readonly string DataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.AppName);
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogFile => Path.Combine(DataRoot, "log.txt");
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataRoot);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 500_000)
                    fi.MoveTo(Path.Combine(Paths.DataRoot, "log.old.txt"), overwrite: true);
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

internal sealed class Settings
{
    public bool AutoStart { get; set; } = true;
    public bool ShowBorder { get; set; } = true;
    public int BorderThickness { get; set; } = 2;             // at 100 % scaling; scaled with the window's monitor DPI
    public bool StickToEdges { get; set; } = true;            // magnetic screen edges while dragging any window
    public bool AutoPinCorner { get; set; } = true;           // a window docked in the top- or bottom-right corner is pinned while it stays there
    public bool HoverReveal { get; set; } = true;             // hovering a covered window's title bar shows it above the pinned window
    public bool SeparateTaskbarButtons { get; set; }          // every window of an app gets its own taskbar button (off by default)
    public bool AutoUpdate { get; set; } = true;              // install promoted GitHub releases automatically

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}
