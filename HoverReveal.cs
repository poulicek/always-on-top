using System.Runtime.InteropServices;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Hover reveal: moving the cursor onto the title bar of a window that a pinned window covers brings that window above
// the pinned one while the cursor stays on that title bar (user request 2026-10-07: only the heading, not the whole
// window); nothing gets activated and no other window changes its place.
// Done by lowering the pinned window (Pinner.Lower: out of the topmost band, directly below the hovered window) and
// raising it back afterwards – the only window that ever moves in the z-order is the pinned one.
//
// Cursor tracking is a 50 ms poll (GetCursorPos + WindowFromPoint) that runs only while something is pinned. Not a
// low-level mouse hook (every mouse event of the system would pass through this process and wait for it) and not a
// global LOCATIONCHANGE WinEvent (also fires for carets, progress bars, every moving window). The title-bar test
// (WM_NCHITTEST, 30 ms timeout) is only sent when the cursor moved onto a new spot of another window.
//
// Timing: reveal once the cursor has been on the same covered title bar for ~300 ms (moving along it is fine; an
// immediate reveal, tried 2026-10-20, was distracting - the user asked for the delay back); keep it up ~500 ms after
// the cursor left the title bar, so a brief move off it does not drop the window again. Another covered title bar
// during that grace period takes over after its own 300 ms. Nothing changes while a mouse button is down (drags,
// resizes, clicks in progress).
internal sealed class HoverReveal : IDisposable
{
    private const int DwellTicks = 6;          // × 50 ms = 0.3 s on a title bar before it is revealed
    private const int LeaveTicks = 10;         // × 50 ms = 0.5 s grace after leaving the title bar
    private const int HTCAPTION = 2, HTSYSMENU = 3, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20, HTHELP = 21;
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;

    private readonly Pinner _pinner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private POINT _lastPt = new() { X = int.MinValue };
    private IntPtr _revealed;                  // window currently shown above the pinned ones
    private readonly List<IntPtr> _lowered = new();
    private int _outside;
    private bool _onCaption;                   // cursor on the revealed window's title bar (re-tested when it moves)
    private IntPtr _pending;                   // covered window whose title bar the cursor is on, not revealed yet
    private int _pendingTicks;

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Sync(); }
    }

    public HoverReveal(Pinner pinner, bool enabled)
    {
        _pinner = pinner;
        _timer.Tick += (_, _) => Tick();
        _pinner.Changed += Sync;
        Enabled = enabled;
    }

    // Poll only while it can matter.
    private void Sync()
    {
        if (_enabled && _pinner.Count > 0) { if (!_timer.Enabled) _timer.Start(); }
        else { _timer.Stop(); Restore(); }
    }

    private static bool ButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    private void Tick()
    {
        if (!GetCursorPos(out var pt)) return;
        var moved = pt.X != _lastPt.X || pt.Y != _lastPt.Y;
        _lastPt = pt;
        if (ButtonDown()) { _pending = IntPtr.Zero; return; }

        if (moved)
        {
            // Re-test only when the cursor moved: on the revealed title bar, or on another covered one (pending)?
            var under = WindowFromPoint(pt);
            var root = GetAncestor(under, GA_ROOT);
            _onCaption = _revealed != IntPtr.Zero && root == _revealed && IsWindow(_revealed) && !IsIconic(_revealed)
                         && IsCaption(_revealed, under, pt);
            if (_onCaption || !(IsCandidate(root) && IsCaption(root, under, pt))) _pending = IntPtr.Zero;
            else if (root != _pending) { _pending = root; _pendingTicks = 0; }
        }

        if (_revealed != IntPtr.Zero)
        {
            if (_onCaption) _outside = 0;
            else if (++_outside >= LeaveTicks) Restore();
        }
        if (_pending != IntPtr.Zero && ++_pendingTicks >= DwellTicks)
        {
            var next = _pending;
            _pending = IntPtr.Zero;
            Restore();                                 // another covered window takes over
            TryReveal(next);
        }
    }

    // Lower the pinned windows that cover part of `window` directly below it.
    private void TryReveal(IntPtr window)
    {
        if (!Edges.Frame(window, out var hovered)) return;
        foreach (var hwnd in _pinner.Handles.ToList())
        {
            if (Edges.Frame(hwnd, out var pinned) && Intersects(hovered, pinned) && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                _lowered.Add(hwnd);
        }
        if (_lowered.Count == 0) return;
        _revealed = window;
        _outside = 0;
        _onCaption = true;
        foreach (var hwnd in _lowered) _pinner.Lower(hwnd, _revealed);
        Log.Write($"hover reveal '{WindowTitle(_revealed)}' over {_lowered.Count} pinned");
    }

    private void Restore()
    {
        if (_revealed == IntPtr.Zero) return;
        foreach (var hwnd in _lowered) _pinner.Raise(hwnd);
        _lowered.Clear();
        _revealed = IntPtr.Zero;
        _outside = 0;
    }

    // An ordinary window of another app: visible, not topmost (topmost ones are above the pinned window anyway or are
    // pinned themselves), not ours, not the desktop/taskbar.
    private bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsTopmost(hwnd) || _pinner.IsPinned(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;
        return ClassName(hwnd) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    // Title bar at `pt`? Asked first of the window directly under the cursor: apps that draw their own title bar in a
    // web view (new Teams, other WebView2/Electron/Chromium apps) answer HTCLIENT on the top-level window but HTCAPTION
    // on the child that covers the drag strip (measured on Teams 2026-10-12: Chrome_WidgetWin_0 child, top 51 px).
    // Classic windows have no such child (under == root), so they still cost one message.
    // Checked first: the title-bar STRIP (2026-10-20, user: extra buttons / search boxes / tabs in a title bar answer
    // HTCLIENT and dropped the peek). The strip is geometry only – no message to the app, so it is also cheaper.
    private bool IsCaption(IntPtr root, IntPtr under, POINT pt)
    {
        if (InTitleStrip(root, pt)) return true;
        var hit = (under != IntPtr.Zero && under != root && IsCaptionHit(NcHitTest(under, pt, 30)))
                  || IsCaptionHit(NcHitTest(root, pt, 30));
        if (hit) LearnDepth(root, pt);
        return hit;
    }

    // Caption-button bounds can be lower than the whole app-drawn header (Teams' search box, Edge's tab row): every
    // confirmed title-bar hit deepens that window's strip, so buttons at that height count afterwards.
    private readonly Dictionary<IntPtr, int> _learnedDepth = new();

    private void LearnDepth(IntPtr hwnd, POINT pt)
    {
        if (!Edges.Frame(hwnd, out var f)) return;
        var depth = pt.Y - f.Top + 1;
        if (depth > (int)Math.Round(96 * Edges.Scale(hwnd))) return;
        if (_learnedDepth.Count > 64) _learnedDepth.Clear();
        if (!_learnedDepth.TryGetValue(hwnd, out var d) || depth > d) _learnedDepth[hwnd] = depth;
    }

    // Inside the band from the visible top of the window down to the bottom of its title bar: for a system-drawn title
    // bar that is where the client area starts, for an app-drawn one the bottom of DWMWA_CAPTION_BUTTON_BOUNDS (Teams
    // 51 px, Edge 30 px at 100 %). Sanity-limited so a window without a real title bar never gets a huge "strip".
    private bool InTitleStrip(IntPtr hwnd, POINT pt)
    {
        if (!Edges.Frame(hwnd, out var f) || pt.X < f.Left || pt.X >= f.Right || pt.Y < f.Top) return false;
        if (!GetWindowRect(hwnd, out var w)) return false;
        var scale = Edges.Scale(hwnd);
        int height;
        var client = new POINT();
        if (ClientToScreen(hwnd, ref client) && client.Y - f.Top >= (int)Math.Round(16 * scale))
            height = client.Y - f.Top;                                                   // system-drawn title bar
        else if (DwmGetWindowAttribute(hwnd, 5 /* DWMWA_CAPTION_BUTTON_BOUNDS */, out RECT cb, Marshal.SizeOf<RECT>()) == 0)
            height = cb.Bottom - (f.Top - w.Top);                                        // app-drawn title bar
        else return false;
        if (_learnedDepth.TryGetValue(hwnd, out var learned) && learned > height) height = learned;
        return height >= 16 * scale && height <= 96 * scale && pt.Y < f.Top + height;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    private static bool IsCaptionHit(int? hit) => hit is HTCAPTION or HTSYSMENU or HTMINBUTTON or HTMAXBUTTON or HTCLOSE or HTHELP;

    private static bool Intersects(RECT a, RECT b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public void Dispose()
    {
        _pinner.Changed -= Sync;
        _timer.Dispose();
        Restore();
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
}
