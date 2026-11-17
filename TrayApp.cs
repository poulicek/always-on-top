using System.Diagnostics;
using Microsoft.Win32;

namespace AlwaysOnTop;

internal sealed class TrayApp : ApplicationContext
{
    private readonly HostForm _host = new();
    private readonly NotifyIcon _tray = new();
    private readonly Settings _settings = Settings.Load();
    private readonly SystemMenuHook _menuHook;
    private readonly Pinner _pinner;
    private readonly EdgeSnapper _snapper;
    private readonly HoverReveal _hover;
    private readonly TaskbarSplitter _splitter;
    private readonly Updater _updater;
    private Icon? _trayIcon;
    private string? _trayKey;
    private bool _exiting;

    public TrayApp(bool afterUpdate = false)
    {
        Log.Write("---- start " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
        Theme.EnableDarkMenus();

        _pinner = new Pinner(_settings.ShowBorder, _settings.BorderThickness);
        _pinner.Changed += UpdateTray;
        _pinner.Failed += msg => _tray.ShowBalloonTip(5000, Program.AppName, msg, ToolTipIcon.Warning);

        _menuHook = new SystemMenuHook(_host);
        _menuHook.ToggleRequested += hwnd => { _pinner.Toggle(hwnd); _menuHook.SyncCheck(hwnd); };
        _menuHook.Foreground += _pinner.OnForeground;

        _snapper = new EdgeSnapper(_host, _settings.StickToEdges);
        _hover = new HoverReveal(_pinner, _settings.HoverReveal);
        _splitter = new TaskbarSplitter(_host, _settings.SeparateTaskbarButtons);
        _updater = new Updater(_settings.AutoUpdate);
        _updater.Notify += (msg, icon) => _tray.ShowBalloonTip(5000, Program.AppName, msg, icon);
        _updater.RestartRequested += Restart;
        _snapper.DragEnded += OnDragEnded;
        _snapper.CornerChanged += (hwnd, inCorner) => { if (_settings.AutoPinCorner) _pinner.CornerPreview(hwnd, inCorner); };

        // MouseUp rather than MouseClick: the synthesised click events can be swallowed on the Windows 11 taskbar.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right) ShowMenu(Cursor.Position);
        };
        UpdateTray();
        _tray.Visible = true;

        _host.CommandReceived += OnCommand;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        if (_settings.AutoStart) TrySetAutostart(true);
        if (afterUpdate) _tray.ShowBalloonTip(5000, Program.AppName, $"Updated to version {Updater.Current}.", ToolTipIcon.Info);
        _host.BeginInvoke(Memory.Trim);
    }

    // ---- tray ----

    private void UpdateTray()
    {
        var size = Theme.TrayIconSize();
        var fore = Theme.TaskbarForeground();
        var pinned = _pinner.Count > 0;
        var key = $"{size}|{fore.ToArgb()}|{pinned}";
        if (key != _trayKey)
        {
            using var bmp = TrayIcons.Draw(pinned, fore, size);
            var icon = TrayIcons.Create(bmp);
            _tray.Icon = icon;
            _trayIcon?.Dispose();
            _trayIcon = icon;
            _trayKey = key;
        }
        _tray.Text = _pinner.Count switch
        {
            0 => $"{Program.AppName}\nRight-click a title bar to pin a window",
            1 => $"{Program.AppName}\n1 window pinned",
            var n => $"{Program.AppName}\n{n} windows pinned",
        };
    }

    // Built fresh on every click (native Win32 menu: correct DPI/position on any monitor, themed by Windows).
    private void ShowMenu(Point at)
    {
        using var menu = new NativeMenu();
        var windows = _pinner.Windows;
        if (windows.Count == 0)
        {
            menu.Add("No pinned windows", () => { }, enabled: false);
        }
        else
        {
            foreach (var (hwnd, title, auto) in windows)
            {
                var h = hwnd;
                menu.Add(Shorten(title) + (auto ? "  (corner)" : ""), () => _pinner.Unpin(h), isChecked: true);
            }
            if (windows.Count > 1) menu.Add("Unpin all", _pinner.UnpinAll);
        }
        menu.AddSeparator();
        menu.Add("Show border around pinned windows", ToggleBorder, isChecked: _settings.ShowBorder);
        menu.Add("Show hovered windows above pinned ones", ToggleHover, isChecked: _settings.HoverReveal);
        menu.Add("Stick windows to screen edges", ToggleStick, isChecked: _settings.StickToEdges);
        menu.Add("Pin windows docked in a right-hand corner", ToggleAutoPin, isChecked: _settings.AutoPinCorner, enabled: _settings.StickToEdges);
        menu.Add("Separate taskbar button for every window", ToggleSplit, isChecked: _settings.SeparateTaskbarButtons);
        menu.Add("Start with Windows", ToggleAutostart, isChecked: _settings.AutoStart);
        menu.AddSeparator();
        menu.Add("Install updates automatically", ToggleAutoUpdate, isChecked: _settings.AutoUpdate);
        menu.Add($"Check for updates (version {Updater.Current})", () => _ = _updater.CheckAsync(manual: true));
        menu.AddSeparator();
        menu.Add("Exit", Exit);
        menu.Show(_host.Handle, at);
        Memory.Trim();
    }

    // A drag (or resize) ended: a window that now sits at a padded right-corner position gets pinned, an auto-pinned
    // one that left it gets released. Windows that were already topmost (pinned by hand or by their app) are left alone.
    private void OnDragEnded(IntPtr hwnd)
    {
        if (!_settings.AutoPinCorner || !_settings.StickToEdges) { _pinner.EndPreview(hwnd); return; }
        var docked = Edges.IsDockedRightCorner(hwnd);
        if (docked && !Native.IsTopmost(hwnd)) _pinner.Pin(hwnd, auto: true);           // adopts the preview border
        else if (!docked && _pinner.IsAutoPinned(hwnd)) _pinner.Unpin(hwnd);
        _pinner.EndPreview(hwnd);
        _menuHook.SyncCheck(hwnd);
    }

    private void ToggleAutoPin()
    {
        _settings.AutoPinCorner = !_settings.AutoPinCorner;
        _settings.Save();
    }

    private static string Shorten(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "(untitled window)";
        title = title.Replace("&", "&&");
        return title.Length <= 60 ? title : title[..57] + "…";
    }

    private void ToggleBorder()
    {
        _settings.ShowBorder = !_settings.ShowBorder;
        _settings.Save();
        _pinner.ShowBorder = _settings.ShowBorder;
    }

    private void ToggleHover()
    {
        _settings.HoverReveal = !_settings.HoverReveal;
        _settings.Save();
        _hover.Enabled = _settings.HoverReveal;
    }

    private void ToggleSplit()
    {
        _settings.SeparateTaskbarButtons = !_settings.SeparateTaskbarButtons;
        _settings.Save();
        _splitter.Enabled = _settings.SeparateTaskbarButtons;
    }

    private void ToggleStick()
    {
        _settings.StickToEdges = !_settings.StickToEdges;
        _settings.Save();
        _snapper.Enabled = _settings.StickToEdges;
    }

    private void ToggleAutostart()
    {
        _settings.AutoStart = !_settings.AutoStart;
        _settings.Save();
        TrySetAutostart(_settings.AutoStart);
    }

    private void ToggleAutoUpdate()
    {
        _settings.AutoUpdate = !_settings.AutoUpdate;
        _settings.Save();
        _updater.Enabled = _settings.AutoUpdate;
    }

    // The updated exe is in place: release everything (Exit un-pins and removes the menu items) and start it. It waits
    // for this process to end before taking the single-instance mutex.
    private void Restart(string exe)
    {
        var pid = Environment.ProcessId;
        Exit();
        try { Process.Start(new ProcessStartInfo(exe, $"--after-update {pid}") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write("update: restart failed: " + ex.Message); }
    }

    private static void TrySetAutostart(bool enabled)
    {
        try { Autostart.Set(enabled); Log.Write("autostart " + (enabled ? "on" : "off")); }
        catch (Exception ex) { Log.Write("autostart failed: " + ex.Message); }
    }

    private void OnCommand(IpcCommand command)
    {
        Log.Write("command " + command);
        switch (command)
        {
            case IpcCommand.Menu: ShowMenu(Cursor.Position); break;
            case IpcCommand.Exit: Exit(); break;
            case IpcCommand.SplitOn or IpcCommand.SplitOff:
                if (_settings.SeparateTaskbarButtons != (command == IpcCommand.SplitOn)) ToggleSplit();
                break;
            case IpcCommand.Status:
                Log.Write($"status: patched={_menuHook.PatchedCount} pinned={_pinner.Count} " +
                          $"[{string.Join(", ", _pinner.Windows.Select(w => $"{w.Hwnd}:'{w.Title}'"))}] border={_settings.ShowBorder} stick={_settings.StickToEdges} corner={_settings.AutoPinCorner} hover={_settings.HoverReveal} split={_settings.SeparateTaskbarButtons} update={_settings.AutoUpdate} v{Updater.Current} " +
                          $"autostart={_settings.AutoStart} ws={Environment.WorkingSet / 1048576} MB");
                break;
        }
    }

    // ---- system events ----

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _host.BeginInvoke(() =>
            {
                Theme.RefreshMenuTheme();
                _trayKey = null;
                UpdateTray();
                _pinner.RefreshStyle();
            });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _host.BeginInvoke(() => { _trayKey = null; UpdateTray(); _pinner.RefreshStyle(); });

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _host.BeginInvoke(Exit);

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Write("exit");
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _menuHook.Dispose();
        _snapper.Dispose();
        _hover.Dispose();
        _splitter.Dispose();
        _updater.Dispose();
        _pinner.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        _host.Dispose();
        ExitThread();
    }
}
