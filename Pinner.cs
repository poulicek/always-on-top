using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Owns the set of windows this app pinned: toggles HWND_TOPMOST, keeps a border per pinned window (optional) and
// forgets windows that are closed or were un-pinned by someone else. Windows it pinned are un-pinned again on Exit,
// so the feature lives exactly as long as the app runs.
// Auto pins (window docked in the top-right or bottom-right corner) are remembered separately: they are released when the window
// leaves the corner, by a drag (TrayApp) or any other way (sweep). Manual pins are never released automatically.
// While a window is dragged, CornerPreview shows what dropping it would do: a preview border animates in when it
// snaps into the corner (and becomes the real border on drop), an auto pin's border animates out when it leaves.
// Lower/Raise implement the hover reveal (HoverReveal): a pinned window temporarily leaves the topmost band and sits
// directly below the hovered window; it stays "pinned" (entry, border, sweep-proof) until raised again.
//
// Taskbar guard: Windows inserts a newly activated ordinary window directly below the taskbar (Shell_TrayWnd, itself
// topmost). A pinned window that has slipped below the taskbar – which happens when it is pinned from its own system
// menu (the closing menu re-stacks it) and whenever the taskbar raises itself – therefore ends up UNDER the next
// window that gets activated, although it is still flagged WS_EX_TOPMOST (found 2026-10-07 while testing the hover
// reveal). So 250 ms after a pin and on every foreground change, pinned windows below the taskbar are re-raised.
internal sealed class Pinner : IDisposable
{
    private sealed class Entry
    {
        public PinBorder? Border;
        public bool Auto;
        public bool Lowered;
    }

    private readonly Dictionary<IntPtr, Entry> _pinned = new();
    private readonly Dictionary<IntPtr, PinBorder> _previews = new();
    private readonly System.Windows.Forms.Timer _sweep = new() { Interval = 2000 };   // runs only while something is pinned
    private bool _showBorder;
    private readonly int _thickness;

    public event Action? Changed;
    public event Action<string>? Failed;
    public event Action<IntPtr, bool>? Pinned;                  // (window, auto) after a successful pin
    public event Action<IntPtr>? Released;                      // window left the pinned set (unpin, closed, swept)

    public Pinner(bool showBorder, int thickness)
    {
        _showBorder = showBorder;
        _thickness = thickness;
        _sweep.Tick += (_, _) => Sweep();
        _restack.Tick += (_, _) => Restack();
        _guard.Tick += (_, _) => { _guard.Stop(); KeepAboveTaskbar(); };
    }

    public int Count => _pinned.Count;

    public IReadOnlyList<(IntPtr Hwnd, string Title, bool Auto)> Windows =>
        _pinned.Where(p => IsWindow(p.Key)).Select(p => (p.Key, WindowTitle(p.Key), p.Value.Auto)).ToList();

    public bool IsAutoPinned(IntPtr hwnd) => _pinned.TryGetValue(hwnd, out var pin) && pin.Auto;
    public bool IsPinned(IntPtr hwnd) => _pinned.ContainsKey(hwnd);
    public IReadOnlyCollection<IntPtr> Handles => _pinned.Keys;

    private const uint SWP_ASYNCWINDOWPOS = 0x4000;   // never block on a busy/hung target
    private readonly System.Windows.Forms.Timer _restack = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer _guard = new() { Interval = 250 };

    // Hover reveal: take the pinned window out of the topmost band and put it directly below `above`, without
    // activating anything or moving any other window.
    public void Lower(IntPtr hwnd, IntPtr above)
    {
        if (!_pinned.TryGetValue(hwnd, out var entry) || entry.Lowered) return;
        entry.Lowered = true;
        if (entry.Border != null) entry.Border.Held = true;
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(hwnd, above, 0, 0, 0, 0, flags);
        _restack.Start();
    }

    public void Raise(IntPtr hwnd)
    {
        if (!_pinned.TryGetValue(hwnd, out var entry) || !entry.Lowered) return;
        entry.Lowered = false;
        if (IsWindow(hwnd))
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        _restack.Start();
    }

    // The async z-order changes land a moment later; then put the borders back right below their windows.
    private void Restack()
    {
        _restack.Stop();
        foreach (var entry in _pinned.Values)
        {
            if (entry.Border == null) continue;
            entry.Border.Held = entry.Lowered;
            entry.Border.Update();
        }
    }

    public void Toggle(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        Log.Write($"toggle {hwnd} '{WindowTitle(hwnd)}' topmost={IsTopmost(hwnd)} pinned={IsPinned(hwnd)}");
        if (IsTopmost(hwnd) || IsPinned(hwnd)) Unpin(hwnd);
        else Pin(hwnd, auto: false);
    }

    public void Pin(IntPtr hwnd, bool auto)
    {
        if (_pinned.TryGetValue(hwnd, out var existing)) { existing.Auto &= auto; return; }
        if (!SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) || !IsTopmost(hwnd))
        {
            Log.Write($"pin failed {hwnd} '{WindowTitle(hwnd)}'");
            if (!auto)
                Failed?.Invoke("This window could not be pinned. It probably runs as administrator – run Always on Top " +
                               "as administrator to control it.");
            return;
        }
        Log.Write($"pinned{(auto ? " (corner)" : "")} {hwnd} '{WindowTitle(hwnd)}'");
        PinBorder? border = null;
        if (_previews.Remove(hwnd, out var preview))
        {
            if (_showBorder) { preview.Preview = false; preview.Appear(); border = preview; }
            else preview.Dispose();
        }
        _pinned[hwnd] = new Entry { Auto = auto, Border = border ?? (_showBorder ? NewBorder(hwnd) : null) };
        _sweep.Start();
        _guard.Start();
        Changed?.Invoke();
        Pinned?.Invoke(hwnd, auto);
    }

    public void Unpin(IntPtr hwnd)
    {
        var ok = true;
        if (IsWindow(hwnd) && IsTopmost(hwnd))
            ok = SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Forget(hwnd, fade: true);
        Log.Write($"unpinned {hwnd} ok={ok} topmost={IsWindow(hwnd) && IsTopmost(hwnd)}");
    }

    public void UnpinAll()
    {
        foreach (var hwnd in _pinned.Keys.ToList()) Unpin(hwnd);
    }

    private void Forget(IntPtr hwnd, bool fade = false)
    {
        if (!_pinned.Remove(hwnd, out var pin)) return;
        if (pin.Border is { } border)
        {
            if (fade && IsWindow(hwnd)) border.Disappear(border.Dispose);
            else border.Dispose();
        }
        if (_pinned.Count == 0) _sweep.Stop();
        Changed?.Invoke();
        Released?.Invoke(hwnd);
    }

    private PinBorder NewBorder(IntPtr hwnd)
    {
        var border = new PinBorder(hwnd, Theme.Accent(), _thickness);
        border.TargetGone += b => Forget(b.Target);
        return border;
    }

    // During a drag: the window snapped into (true) or out of (false) a right-corner dock position.
    public void CornerPreview(IntPtr hwnd, bool inCorner)
    {
        if (!_showBorder || !IsWindow(hwnd)) return;
        if (_pinned.TryGetValue(hwnd, out var entry))
        {
            if (!entry.Auto) return;                       // manual pins do not react to the corner
            if (inCorner) entry.Border?.Appear(); else entry.Border?.Disappear();
            return;
        }
        if (IsTopmost(hwnd)) return;                       // topmost by its own app: corner pin will not take it
        if (inCorner)
        {
            if (!_previews.TryGetValue(hwnd, out var preview))
            {
                preview = new PinBorder(hwnd, Theme.Accent(), _thickness, preview: true);
                preview.TargetGone += b => EndPreview(b.Target);
                _previews[hwnd] = preview;
            }
            else preview.Appear();
        }
        else if (_previews.TryGetValue(hwnd, out var preview))
        {
            preview.Disappear(() => { if (_previews.Remove(hwnd, out var p)) p.Dispose(); });
        }
    }

    // Drag ended without the preview being adopted by Pin(): let it go. An auto pin that is still docked gets its
    // border back (it may have animated out while the drag left the corner and came back).
    public void EndPreview(IntPtr hwnd)
    {
        if (_previews.TryGetValue(hwnd, out var preview))
            preview.Disappear(() => { if (_previews.Remove(hwnd, out var p)) p.Dispose(); });
        if (_pinned.TryGetValue(hwnd, out var entry) && entry.Auto && IsTopmost(hwnd) && Edges.IsDockedRightCorner(hwnd))
            entry.Border?.Appear();
    }

    public bool ShowBorder
    {
        get => _showBorder;
        set
        {
            _showBorder = value;
            foreach (var (hwnd, pin) in _pinned)
            {
                pin.Border?.Dispose();
                pin.Border = value ? NewBorder(hwnd) : null;
            }
        }
    }

    // Accent colour or scaling changed.
    public void RefreshStyle()
    {
        var accent = Theme.Accent();
        foreach (var pin in _pinned.Values) pin.Border?.SetStyle(accent, _thickness);
    }

    // Some window became foreground: pinned windows must still be above the taskbar (see header), and a pinned
    // foreground window's border has to be re-stacked with it.
    public void OnForeground(IntPtr hwnd)
    {
        KeepAboveTaskbar();
        if (_pinned.TryGetValue(hwnd, out var pin)) pin.Border?.Update();
    }

    private void KeepAboveTaskbar()
    {
        if (_pinned.Count == 0) return;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;
        var raised = false;
        foreach (var (hwnd, entry) in _pinned)
        {
            if (entry.Lowered || !IsWindow(hwnd) || !IsTopmost(hwnd) || !IsBelow(hwnd, taskbar)) continue;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            raised = true;
        }
        if (raised) _restack.Start();
    }

    // Is `other` somewhere above `hwnd` in the z-order? Only walks the windows above `hwnd` (for a pinned window that
    // is the short topmost band).
    private static bool IsBelow(IntPtr hwnd, IntPtr other)
    {
        var h = hwnd;
        for (var i = 0; i < 256 && h != IntPtr.Zero; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);
            if (h == other) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    private void Sweep()
    {
        foreach (var (hwnd, pin) in _pinned.ToList())
        {
            if (pin.Lowered) continue;                                           // hover reveal in progress
            if (!IsWindow(hwnd) || !IsTopmost(hwnd)) Forget(hwnd);
            else if (pin.Auto && !Edges.IsDockedRightCorner(hwnd)) Unpin(hwnd);   // moved by keyboard, maximized, …
        }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        _restack.Dispose();
        _guard.Dispose();
        foreach (var preview in _previews.Values) preview.Dispose();
        _previews.Clear();
        foreach (var hwnd in _pinned.Keys.ToList())
        {
            if (IsWindow(hwnd) && IsTopmost(hwnd))
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Forget(hwnd);
        }
    }
}
