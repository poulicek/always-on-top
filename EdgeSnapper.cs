using System.Runtime.InteropServices;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Magnetic screen edges while a window is dragged: when a visible edge of the window comes within the threshold of the
// padded work-area edge (screen minus taskbar, inset by Edges.Padding) it snaps there; pushing on by more than the
// threshold breaks it free, so the window can still be moved past the edge. Shift held = no sticking for that moment.
//
// The drag loop runs inside the other process (WM_MOVING is not ours to change without injection), and the loop
// places the window at "cursor + fixed offset". So the sticking is done on the cursor: between
// EVENT_SYSTEM_MOVESIZESTART and END a low-level mouse hook (installed only for the drag) keeps a virtual "raw" cursor
// (where the pointer would be without sticking, accumulated from the real mouse deltas), works out where the window
// would land, snaps that rectangle and, if it had to be adjusted, swallows the mouse event and puts the cursor at the
// snapped position instead. Released, the cursor rejoins the raw position – the usual sticky feel.
//
// Accuracy: the cursor-to-window offset is first measured at the first hook event. Because the WinEvent is
// asynchronous the window may still lag the cursor at that point, which made the landing spot random by a few pixels
// (no visible gap at all with small paddings). While an axis is stuck the cursor stands still on it, so the window
// settles and its real position gives the exact offset: two equal readings in a row correct it on the spot. Users
// stop moving the mouse as soon as the window snaps (especially into the corner), so there may be no next event:
// a settle check 60 ms after the last move reads the settled window and nudges the cursor to the exact spot.
internal sealed class EdgeSnapper : IDisposable
{
    private const int ThresholdAt96 = 24;
    private const int HTLEFT = 10, HTBOTTOMRIGHT = 17;
    private const int VK_SHIFT = 0x10, VK_LBUTTON = 0x01;
    private const int WM_MOUSEMOVE = 0x0200;

    public event Action<IntPtr>? DragEnded;     // any move/size loop ended (top-level window)
    public event Action<IntPtr, bool>? CornerChanged;   // during a drag: snapped into / out of a right-corner dock spot

    private readonly Control _ui;
    private bool _inCorner;
    private readonly WinEventProc _winEventProc;
    private readonly HookProc _mouseProc;
    private readonly IntPtr _winEventHook;
    private IntPtr _mouseHook;

    // State of the current drag.
    private IntPtr _target;
    private POINT _shown, _raw;          // cursor as displayed / as it would be without sticking
    private POINT _offset;               // visible frame top-left minus cursor
    private POINT _inset;                // visible frame top-left minus window-rect top-left (invisible resize border)
    private bool _calibrate;             // measure _offset at the next mouse event
    private SIZE _frameSize, _windowSize;
    private int _threshold, _padding;
    private Axis _x, _y;                 // self-correction per axis
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 60 };
    private int _settleRounds;

    private struct Axis
    {
        public int LastShown, Candidate, Hits;
        public bool Stuck;
    }

    public bool Enabled { get; set; }

    public EdgeSnapper(Control ui, bool enabled)
    {
        _ui = ui;
        Enabled = enabled;
        _winEventProc = OnWinEvent;
        _mouseProc = OnMouse;
        _settle.Tick += (_, _) => Settle();
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW) return;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (ev == EVENT_SYSTEM_MOVESIZEEND)
        {
            Stop();
            if (root != IntPtr.Zero) DragEnded?.Invoke(root);
            return;
        }
        if (ev != EVENT_SYSTEM_MOVESIZESTART || !Enabled) return;
        Stop();
        if (root == IntPtr.Zero || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return;   // keyboard move: leave alone
        if (!GetCursorPos(out var cursor)) return;

        // Resizing also runs a move/size loop; only a drag by the caption (or app-defined drag area) is ours.
        if (NcHitTest(root, cursor, 100) is >= HTLEFT and <= HTBOTTOMRIGHT) return;

        _target = root;
        if (!Baseline(cursor)) { _target = IntPtr.Zero; return; }
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
    }

    // Offset between cursor and visible frame, frame/window size and the DPI-scaled distances, from the current state.
    private bool Baseline(POINT cursor)
    {
        if (!GetWindowRect(_target, out var win)) return false;
        if (!Edges.Frame(_target, out var frame)) frame = win;
        _shown = _raw = cursor;
        _offset = new POINT { X = frame.Left - cursor.X, Y = frame.Top - cursor.Y };
        _inset = new POINT { X = frame.Left - win.Left, Y = frame.Top - win.Top };
        _frameSize = new SIZE { CX = frame.Width, CY = frame.Height };
        _windowSize = new SIZE { CX = win.Width, CY = win.Height };
        var scale = Edges.Scale(_target);
        _threshold = (int)Math.Round(ThresholdAt96 * scale);
        _padding = Edges.Padding(scale);
        _x = _y = default;
        _calibrate = true;
        return true;
    }

    private void Stop()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _target = IntPtr.Zero;
        _inCorner = false;
        _settle.Stop();
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _target == IntPtr.Zero || wParam.ToInt32() != WM_MOUSEMOVE)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
        if (!GetWindowRect(_target, out var win)) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        // The window changed size (e.g. a maximized window restored on drag): start over from the new geometry.
        if (win.Width != _windowSize.CX || win.Height != _windowSize.CY)
        {
            Baseline(pt);
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // First event: the hook runs before the cursor moves, and by now the loop has caught up with the cursor.
        if (_calibrate && GetCursorPos(out var now))
        {
            _calibrate = false;
            _shown = _raw = now;
            _offset = new POINT { X = win.Left + _inset.X - now.X, Y = win.Top + _inset.Y - now.Y };
        }
        else
        {
            Correct(ref _x, ref _offset.X, win.Left + _inset.X, _shown.X);
            Correct(ref _y, ref _offset.Y, win.Top + _inset.Y, _shown.Y);
        }

        _raw.X += pt.X - _shown.X;
        _raw.Y += pt.Y - _shown.Y;

        var want = _raw;
        var corner = false;
        _x.Stuck = _y.Stuck = false;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0)
        {
            var frame = new RECT { Left = _raw.X + _offset.X, Top = _raw.Y + _offset.Y };
            frame.Right = frame.Left + _frameSize.CX;
            frame.Bottom = frame.Top + _frameSize.CY;
            var wa = Edges.WorkArea(frame);
            var dx = Nearest(frame.Left - (wa.Left + _padding), frame.Right - (wa.Right - _padding));
            var dy = Nearest(frame.Top - (wa.Top + _padding), frame.Bottom - (wa.Bottom - _padding));
            if (dx is { } ax) { want.X += ax; _x.Stuck = true; }
            if (dy is { } ay) { want.Y += ay; _y.Stuck = true; }
            corner = _x.Stuck && _y.Stuck && frame.Right + dx == wa.Right - _padding
                     && (frame.Top + dy == wa.Top + _padding || frame.Bottom + dy == wa.Bottom - _padding);
        }
        if (corner != _inCorner)
        {
            // Raised outside the hook: the hook must return quickly, and showing a border creates a window.
            _inCorner = corner;
            var target = _target;
            _ui.BeginInvoke(() => CornerChanged?.Invoke(target, corner));
        }
        _x.LastShown = _shown.X;
        _y.LastShown = _shown.Y;
        _settle.Stop();
        if (_x.Stuck || _y.Stuck) { _settleRounds = 0; _settle.Start(); }

        if (want.X == pt.X && want.Y == pt.Y)
        {
            _shown = pt;
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        _shown = want;
        SetCursorPos(want.X, want.Y);
        return (IntPtr)1;                                   // swallow the original move
    }

    // No mouse event for 60 ms while stuck: the window has settled, so frame - cursor on a stuck axis is the true
    // offset. If it differs, move the cursor (the drag loop follows it) so the window lands exactly; verify once more.
    private void Settle()
    {
        _settle.Stop();
        if (_target == IntPtr.Zero || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return;
        if (!GetWindowRect(_target, out var win) || win.Width != _windowSize.CX || win.Height != _windowSize.CY) return;
        var fix = new POINT();
        if (_x.Stuck) fix.X = win.Left + _inset.X - _shown.X - _offset.X;
        if (_y.Stuck) fix.Y = win.Top + _inset.Y - _shown.Y - _offset.Y;
        if (fix.X == 0 && fix.Y == 0) return;
        _offset.X += fix.X;
        _offset.Y += fix.Y;
        _shown.X -= fix.X;
        _shown.Y -= fix.Y;
        SetCursorPos(_shown.X, _shown.Y);
        if (++_settleRounds < 3) _settle.Start();
    }

    // While an axis is stuck and the cursor did not move on it since the previous event, the window has settled for
    // that cursor position: frame - cursor is the true offset. Accept it after two equal readings.
    private static void Correct(ref Axis a, ref int offset, int frameEdge, int shown)
    {
        if (!a.Stuck || shown != a.LastShown) { a.Hits = 0; return; }
        var measured = frameEdge - shown;
        if (a.Hits > 0 && measured == a.Candidate)
        {
            if (++a.Hits >= 2) offset = measured;
        }
        else
        {
            a.Candidate = measured;
            a.Hits = 1;
        }
    }

    // Adjustment that puts the nearer frame edge onto its padded target, if one is within the threshold.
    private int? Nearest(int toStart, int toEnd)
    {
        int? best = null;
        var bestDist = _threshold + 1;
        foreach (var d in new[] { toStart, toEnd })
            if (Math.Abs(d) < bestDist) { bestDist = Math.Abs(d); best = -d; }
        return best;
    }

    public void Dispose()
    {
        Stop();
        _settle.Dispose();
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
}

// Shared geometry: visible frame, monitor scale, padded work area, the right-corner "dock" positions.
internal static class Edges
{
    public const int PaddingAt96 = 8;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static int Padding(float scale) => (int)Math.Round(PaddingAt96 * scale);

    public static bool Frame(IntPtr hwnd, out RECT frame) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<RECT>()) == 0;

    public static float Scale(IntPtr hwnd)
    {
        if (GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), 0, out var dpi, out _) == 0 && dpi > 0)
            return dpi / 96f;
        return 1f;
    }

    public static RECT WorkArea(RECT frame)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromRect(ref frame, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcWork;
    }

    // Docked in the top-right or bottom-right corner = sitting exactly at the padded corner position the snapping
    // produces (not flush: maximized, Windows Snap halves and quadrants must not count). Bottom-right added 2026-10-07.
    public static bool IsDockedRightCorner(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd) || !Frame(hwnd, out var frame)) return false;
        var scale = Scale(hwnd);
        var pad = Padding(scale);
        var tolerance = Math.Max(2, (int)Math.Round(2 * scale));
        var wa = WorkArea(frame);
        return Math.Abs(frame.Right - (wa.Right - pad)) <= tolerance
               && (Math.Abs(frame.Top - (wa.Top + pad)) <= tolerance || Math.Abs(frame.Bottom - (wa.Bottom - pad)) <= tolerance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
}
