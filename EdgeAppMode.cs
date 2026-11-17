using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Settings.EdgeAppWindows: an Edge browser window pinned by DOCKING IT INTO A RIGHT-HAND CORNER (auto pin) is replaced
// by an Edge APP window of the same page (thin title bar only - no tabs, no address bar), Edge's closest thing to IE's
// old minimal mode. Pinning from the title-bar menu keeps the normal browser window. Edge cannot be switched into that mode from outside, so:
//   1. the current tab's address is read from the address bar with UI Automation (read-only, off the UI thread;
//      the walk skips web content so it stays fast),
//   2. msedge --profile-directory=<the window's profile> --app=<url> is started (profile from the window title
//      "... - <profile name> - Microsoft Edge" mapped through Edge's "Local State"; --user-data-dir / --guest of the
//      original Edge process are carried over so the app window opens in the SAME Edge instance),
//   3. the new window gets a fixed compact size in device-independent pixels - 412 x 512 at 100 % scaling
//      (618 x 768 px at 150 %) - times the monitor's scale, so on a large screen with low scaling it is relatively
//      smaller (a size relative to the screen height was too small and grew with the screen); only shrunk to fit a
//      smaller work area. Placed in the corner the browser
//      window was docked in (exact padded corner) or else at the top-right of where the browser window was, and
//      pinned instead (same manual/corner kind),
//   4. the original window is only MINIMIZED - never closed - so nothing is lost and it can always be restored.
// When the app window leaves the pinned set (unpin, corner drag-out, closed by the user, Exit) it is closed and the
// original is restored to exactly its previous state (SW_RESTORE of the minimized window: same rect, maximized if it
// was maximized). Windows without an address bar (installed Edge apps, our own app windows) are left alone.
// Known trade-off: the app window loads the page again (logins are kept - same profile - but video position or
// unsaved input of the original tab is not carried over; the original tab keeps running minimized).
internal sealed class EdgeAppMode : IDisposable
{
    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const int WidthAt96 = 412, HeightAt96 = 512;              // window rect at 100 % scaling
    private const uint WM_CLOSE = 0x0010;

    private readonly Pinner _pinner;
    private readonly Control _ui;
    private readonly Dictionary<IntPtr, IntPtr> _pairs = new();     // app window -> original Edge window
    private readonly HashSet<IntPtr> _busy = new();                 // originals being converted
    private bool _converting;

    public bool Enabled { get; set; }

    public EdgeAppMode(Pinner pinner, Control ui, bool enabled)
    {
        _pinner = pinner;
        _ui = ui;
        Enabled = enabled;
        _pinner.Pinned += OnPinned;
        _pinner.Released += OnReleased;
    }

    private void OnPinned(IntPtr hwnd, bool auto)
    {
        if (!auto) return;                                          // only corner pins turn into app windows
        if (!Enabled || _converting || _pairs.ContainsKey(hwnd) || _pairs.ContainsValue(hwnd) || !_busy.Add(hwnd)) return;
        if (!IsEdgeWindow(hwnd)) { _busy.Remove(hwnd); return; }
        Task.Run(() => ReadAddress(hwnd)).ContinueWith(t =>
            _ui.BeginInvoke(() =>
            {
                _busy.Remove(hwnd);
                var url = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (url == null) { Log.Write($"edge app: no address bar in '{WindowTitle(hwnd)}' - left as it is"); return; }
                Convert(hwnd, auto, url);
            }));
    }

    private void Convert(IntPtr original, bool auto, string url)
    {
        if (!Enabled || !IsWindow(original) || !_pinner.IsPinned(original) || !GetWindowRect(original, out var rect)) return;
        var corner = DockedCorner(original);                       // read before the browser window is minimized
        var exe = ImagePath(original);
        if (exe == null) return;
        var carried = CarriedArguments(original);
        var profile = ProfileDirectory(original, carried.UserDataDir);
        var before = EdgeWindows();
        var args = carried.Text + (profile != null ? $"--profile-directory=\"{profile}\" " : "") + $"--app=\"{url}\"";
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write("edge app: start failed: " + ex.Message); return; }
        Log.Write($"edge app: opening {url} (profile {profile ?? "?"})");

        // Wait (up to 6 s) for the new top-level Edge window.
        var started = Environment.TickCount64;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var app = EdgeWindows().FirstOrDefault(h => !before.Contains(h) && WindowTitle(h).Length > 0);
            if (app == IntPtr.Zero && Environment.TickCount64 - started < 6000) return;
            timer.Dispose();
            if (app == IntPtr.Zero) { Log.Write("edge app: app window did not appear - original kept pinned"); return; }
            if (!IsWindow(original) || !_pinner.IsPinned(original))
            {
                PostMessage(app, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);       // original was unpinned meanwhile
                return;
            }
            PlaceCompact(app, rect, corner);
            SwapIn(app, original, auto, retry: true);
        };
        timer.Start();
    }

    // Pin the app window FIRST, then release and minimize the browser window - only if the pin took. (The other order
    // failed when the window was docked in a corner: the browser window's border, topmost and still fading out, sat
    // next to the new window in the z-order and SetWindowPos(HWND_TOPMOST) was silently refused - the same Windows
    // quirk as with HWND_NOTOPMOST, see "Border z-order".) One retry after 300 ms; if that fails too, the app window is
    // closed and the browser window simply stays pinned.
    private void SwapIn(IntPtr app, IntPtr original, bool auto, bool retry)
    {
        if (!IsWindow(app)) return;
        if (!IsWindow(original) || !_pinner.IsPinned(original)) { PostMessage(app, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return; }
        _converting = true;
        try { _pinner.Pin(app, auto); }
        finally { _converting = false; }
        if (_pinner.IsPinned(app))
        {
            _pairs[app] = original;
            _pinner.Unpin(original);
            ShowWindow(original, SW_MINIMIZE);
            Log.Write($"edge app: '{WindowTitle(app)}' pinned instead of the browser window (minimized)");
            return;
        }
        if (retry)
        {
            var again = new System.Windows.Forms.Timer { Interval = 300 };
            again.Tick += (_, _) => { again.Dispose(); SwapIn(app, original, auto, retry: false); };
            again.Start();
            return;
        }
        PostMessage(app, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Log.Write("edge app: app window could not be pinned - closed, browser window stays pinned");
    }

    private void OnReleased(IntPtr hwnd)
    {
        if (!_pairs.Remove(hwnd, out var original)) return;
        if (IsWindow(hwnd)) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (IsWindow(original)) ShowWindow(original, SW_RESTORE);         // back to exactly its previous state
        Log.Write("edge app: app window released, browser window restored");
    }

    public void Dispose()
    {
        _pinner.Pinned -= OnPinned;
        _pinner.Released -= OnReleased;
        foreach (var (app, original) in _pairs)
        {
            if (IsWindow(app)) PostMessage(app, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (IsWindow(original)) ShowWindow(original, SW_RESTORE);
        }
        _pairs.Clear();
    }

    // ---- compact placement ----

    private enum Corner { None, TopRight, BottomRight }

    private static Corner DockedCorner(IntPtr hwnd)
    {
        if (!Edges.IsDockedRightCorner(hwnd) || !Edges.Frame(hwnd, out var f)) return Corner.None;
        var wa = Edges.WorkArea(f);
        var pad = Edges.Padding(Edges.Scale(hwnd));
        return Math.Abs(f.Top - (wa.Top + pad)) <= Math.Abs(f.Bottom - (wa.Bottom - pad)) ? Corner.TopRight : Corner.BottomRight;
    }

    // Fixed size in device-independent pixels times the scale of the monitor the browser window is on; shrunk only to
    // fit the work area. Edge may enforce a minimum size; the corner alignment below uses the size it actually took.
    private static void PlaceCompact(IntPtr app, RECT browser, Corner corner)
    {
        var wa = Edges.WorkArea(browser);
        var scale = Edges.Scale(app);
        var width = Math.Min((int)Math.Round(WidthAt96 * scale), wa.Width);
        var height = Math.Min((int)Math.Round(HeightAt96 * scale), wa.Height);
        SetWindowPos(app, IntPtr.Zero, browser.Right - width, browser.Top, width, height, SWP_NOZORDER);

        // Align the visible frame: into the padded corner it was docked in, else keep it inside the work area.
        if (!Edges.Frame(app, out var f) || !GetWindowRect(app, out var w)) return;
        var pad = Edges.Padding(Edges.Scale(app));
        int dx, dy;
        if (corner != Corner.None)
        {
            dx = wa.Right - pad - f.Right;
            dy = corner == Corner.TopRight ? wa.Top + pad - f.Top : wa.Bottom - pad - f.Bottom;
        }
        else
        {
            dx = f.Right > wa.Right ? wa.Right - f.Right : f.Left < wa.Left ? wa.Left - f.Left : 0;
            dy = f.Bottom > wa.Bottom ? wa.Bottom - f.Bottom : f.Top < wa.Top ? wa.Top - f.Top : 0;
        }
        if (dx != 0 || dy != 0) SetWindowPos(app, IntPtr.Zero, w.Left + dx, w.Top + dy, 0, 0, SWP_NOZORDER | SWP_NOSIZE);
    }

    // ---- Edge windows ----

    private static bool IsEdgeWindow(IntPtr hwnd) =>
        ClassName(hwnd) == "Chrome_WidgetWin_1" && string.Equals(Path.GetFileName(ImagePath(hwnd)), "msedge.exe", StringComparison.OrdinalIgnoreCase);

    private static HashSet<IntPtr> EdgeWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && ClassName(h) == "Chrome_WidgetWin_1" && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero
                && string.Equals(Path.GetFileName(ImagePath(h)), "msedge.exe", StringComparison.OrdinalIgnoreCase))
                set.Add(h);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    // The address bar: an Edit control in the browser UI (web content - Document subtrees - is skipped) whose value
    // looks like an address. Null when there is none (app windows, installed sites).
    private static string? ReadAddress(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited++ < 600)
        {
            var (el, depth) = queue.Dequeue();
            var type = el.Current.ControlType;
            if (type == ControlType.Document) continue;
            if (type == ControlType.Edit && el.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
            {
                var value = ((ValuePattern)p).Current.Value?.Trim() ?? "";
                if (LooksLikeAddress(value)) return value.Contains("://") ? value : "https://" + value;
            }
            if (depth >= 14) continue;
            for (var child = walker.GetFirstChild(el); child != null; child = walker.GetNextSibling(child))
                queue.Enqueue((child, depth + 1));
        }
        return null;
    }

    private static bool LooksLikeAddress(string v) =>
        v.Length > 3 && !v.Contains(' ') && (v.Contains("://") || v.Contains('.') || v.StartsWith("localhost"));

    // --user-data-dir and --guest of the Edge process that owns the window (read from its command line).
    private static (string Text, string? UserDataDir) CarriedArguments(IntPtr hwnd)
    {
        var cmd = CommandLine(hwnd) ?? "";
        var text = new StringBuilder();
        string? dir = null;
        var m = System.Text.RegularExpressions.Regex.Match(cmd, "--user-data-dir=(\"[^\"]+\"|\\S+)");
        if (m.Success)
        {
            dir = m.Groups[1].Value.Trim('"');
            text.Append($"--user-data-dir=\"{dir}\" ");
        }
        if (cmd.Contains("--guest")) text.Append("--guest ");
        return (text.ToString(), dir);
    }

    // "<page> - <profile name> - Microsoft Edge" -> profile directory via Edge's Local State (profile.info_cache).
    private static string? ProfileDirectory(IntPtr hwnd, string? userDataDir)
    {
        try
        {
            var parts = WindowTitle(hwnd).Split(" - ");
            if (parts.Length < 3) return null;
            var name = parts[^2].Trim();
            var localState = Path.Combine(userDataDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data"), "Local State");
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            // The title shows the profile's display name: "shortcut_name" (the custom profile name), "name" stays "Profile N".
            foreach (var field in new[] { "shortcut_name", "name", "enterprise_label" })
                foreach (var profile in doc.RootElement.GetProperty("profile").GetProperty("info_cache").EnumerateObject())
                    if (profile.Value.TryGetProperty(field, out var n) && n.GetString() == name) return profile.Name;
        }
        catch (Exception ex) { Log.Write("edge app: profile lookup failed: " + ex.Message); }
        return null;
    }

    private static string? ImagePath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    // Command line of the window's process (NtQueryInformationProcess, ProcessCommandLineInformation = 60).
    private static string? CommandLine(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        var buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;
            buffer = Marshal.AllocHGlobal(needed);
            if (NtQueryInformationProcess(process, 60, buffer, needed, out _) != 0) return null;
            var length = Marshal.ReadInt16(buffer);                         // UNICODE_STRING.Length (bytes)
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);              // UNICODE_STRING.Buffer
            return Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
