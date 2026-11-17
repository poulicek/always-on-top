using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Accent-coloured frame around a pinned window: a click-through, non-activating, topmost layered window drawn with
// per-pixel alpha (so the rounded Windows 11 corners are smooth) and kept just below the target in the z-order.
// DwmSetWindowAttribute(DWMWA_BORDER_COLOR) would be simpler but only works on the caller's own windows.
// It follows the target through out-of-context WinEvent hooks limited to the target's process and to the few events
// it needs (destroy/show/hide, location, cloak = other virtual desktop, minimize) – one hook over the whole
// 0x8001..0x8018 range would also deliver focus/name/value/selection changes, which browsers fire constantly.
//
// Animation (same for manual pins, corner pins and the corner preview while dragging): a visibility level v runs
// 0 → 1 on Appear (~350 ms) and back on Disappear (~250 ms); reversing mid-way continues from the current v.
// Appearing, the frame bursts in thick and bright (accent lightened towards white) and settles to the thin accent
// line; disappearing, it swells, brightens and fades. Drawing goes into one reused premultiplied DIB section sized to
// the window plus the peak thickness, so a frame of animation is a clear + one path fill, no allocation; outside
// animations only moves/resizes of the target cause work.
internal sealed class PinBorder : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint ULW_ALPHA = 2;
    private const float PeakFactor = 4f;               // thickness at the start of the burst, × normal thickness
    private const int AppearMs = 350, DisappearMs = 250;

    public IntPtr Target { get; }
    public event Action<PinBorder>? TargetGone;

    // Preview borders (corner dock while dragging) are shown for windows that are not topmost (yet).
    public bool Preview { get; set; }
    // Pinned but temporarily lowered below a hovered window (hover reveal): still shown although not topmost.
    public bool Held { get; set; }

    private readonly WinEventProc _proc;
    private readonly IntPtr[] _hooks;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private Color _color;
    private int _thicknessAt96;

    private float _v;                  // 0 = invisible, 1 = settled
    private int _direction;            // +1 appearing, -1 disappearing, 0 idle
    private long _lastTick;
    private Action? _hidden;           // called once a Disappear has finished

    // Reused drawing surface.
    private IntPtr _memDc, _dib, _oldBitmap;
    private Bitmap? _surface;
    private Size _surfaceSize;
    private string? _drawnKey;
    private bool _visible;

    public PinBorder(IntPtr target, Color color, int thicknessAt96, bool preview = false)
    {
        Target = target;
        _color = color;
        _thicknessAt96 = Math.Max(1, thicknessAt96);
        Preview = preview;

        CreateHandle(new CreateParams
        {
            Caption = "AlwaysOnTop.Border",
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        });

        _proc = OnWinEvent;
        GetWindowThreadProcessId(target, out var pid);
        _hooks = new[]
        {
            SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT),
            SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT),
            SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT),
            SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT),
        };
        _timer.Tick += (_, _) => Animate();
        Appear();
    }

    public void SetStyle(Color color, int thicknessAt96)
    {
        _color = color;
        _thicknessAt96 = Math.Max(1, thicknessAt96);
        Update();
    }

    public void Appear()
    {
        _hidden = null;
        if (_direction == 1 || (_direction == 0 && _v >= 1)) return;
        Start(+1);
    }

    // Plays the disappear animation, then hides; `hidden` runs afterwards (e.g. Dispose).
    public void Disappear(Action? hidden = null)
    {
        if (_v <= 0 || !_visible)
        {
            _v = 0;
            _direction = 0;
            _timer.Stop();
            Hide();
            hidden?.Invoke();
            return;
        }
        _hidden = hidden;
        if (_direction != -1) Start(-1);
    }

    private void Start(int direction)
    {
        _direction = direction;
        _lastTick = Environment.TickCount64;
        _timer.Start();
        Update();
    }

    private void Animate()
    {
        var now = Environment.TickCount64;
        var dt = now - _lastTick;
        _lastTick = now;
        _v = Math.Clamp(_v + _direction * dt / (float)(_direction > 0 ? AppearMs : DisappearMs), 0f, 1f);
        if ((_direction > 0 && _v >= 1) || (_direction < 0 && _v <= 0))
        {
            _timer.Stop();
            var wasDisappearing = _direction < 0;
            _direction = 0;
            if (wasDisappearing)
            {
                Hide();
                var hidden = _hidden;
                _hidden = null;
                hidden?.Invoke();
                return;
            }
        }
        Update();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != Target || idObject != OBJID_WINDOW || idChild != 0) return;
        if (ev == EVENT_OBJECT_DESTROY) { Hide(); TargetGone?.Invoke(this); return; }
        Update();
    }

    private bool TargetShown()
    {
        if (!IsWindow(Target) || !IsWindowVisible(Target) || IsIconic(Target)) return false;
        if (DwmGetWindowAttribute(Target, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return Preview || Held || _direction < 0 || IsTopmost(Target);
    }

    private static float EaseOut(float x) => 1 - (1 - x) * (1 - x) * (1 - x);

    // Current look from the visibility level and direction.
    private (float thicknessFactor, float brightness, byte alpha) Look()
    {
        if (_direction >= 0)
        {
            var e = EaseOut(_v);                                      // appear: burst in, settle
            return (1 + (PeakFactor - 1) * (1 - e), 0.65f * (1 - e), (byte)(255 * Math.Min(1f, _v * 4)));
        }
        var d = 1 - _v;                                               // disappear: swell, brighten, fade
        return (1 + (PeakFactor - 1) * 0.6f * EaseOut(d), 0.65f * EaseOut(d), (byte)(255 * (1 - d * d)));
    }

    // Recomputes position/size and repaints if needed; called on every relevant event of the target and per frame.
    public void Update()
    {
        if (Handle == IntPtr.Zero || (_v <= 0 && _direction <= 0)) return;
        if (!TargetShown()) { Hide(); return; }
        if (!Edges.Frame(Target, out var frame)) return;

        var scale = Edges.Scale(Target);
        var baseT = Math.Max(1f, (float)Math.Round(_thicknessAt96 * scale));
        var (factor, bright, alpha) = Look();
        var t = baseT * factor;
        var maximized = IsZoomed(Target);
        var f = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);

        // Normal windows: the frame hugs the window from outside (Windows 11 corner radius 8 px); the layered window
        // reserves room for the peak thickness so its size only changes with the target. Maximized: inside the
        // visible area, square corners.
        Rectangle outer;
        float radius;
        if (maximized)
        {
            outer = Rectangle.Intersect(f, Screen.FromHandle(Target).Bounds);
            radius = 0;
        }
        else
        {
            var margin = (int)Math.Ceiling(baseT * PeakFactor) + 1;
            outer = Rectangle.Inflate(f, margin, margin);
            radius = Theme.IsWindows11 ? 8 * scale : 0;
        }
        if (outer.Width <= 2 * t || outer.Height <= 2 * t) { Hide(); return; }

        var color = Lerp(_color, Color.White, bright);
        var inner = maximized ? new Rectangle(0, 0, outer.Width, outer.Height)
                              : new Rectangle(f.X - outer.X, f.Y - outer.Y, f.Width, f.Height);
        var key = $"{outer.Size}|{inner}|{t:F2}|{radius}|{maximized}|{color.ToArgb()}";
        if (key != _drawnKey) Draw(outer.Size, inner, t, radius, maximized, color, key);

        var pos = new POINT { X = outer.X, Y = outer.Y };
        var size = new SIZE { CX = outer.Width, CY = outer.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref pos, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);

        // Directly BELOW the target (the frame lies outside the window, so that looks the same, and other topmost windows
        // above the target cover the frame correctly). Not above it: while a topmost window sits directly above the
        // target, SetWindowPos(HWND_NOTOPMOST) on the target silently does nothing (verified 2026-10-03).
        var flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW;
        // Follow the target in and out of the topmost band (hover reveal lowers it temporarily) – inserting after a
        // window of the other band does not reliably move the border between bands by itself.
        var targetTop = IsTopmost(Target);
        if (IsTopmost(Handle) != targetTop)
            SetWindowPos(Handle, targetTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else if (_visible && GetWindow(Target, GW_HWNDNEXT) == Handle) flags |= SWP_NOZORDER;   // already in place
        SetWindowPos(Handle, Target, 0, 0, 0, 0, flags);
        _visible = true;
    }

    private static Color Lerp(Color a, Color b, float k) => Color.FromArgb(255,
        (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    public void Hide()
    {
        if (!_visible || Handle == IntPtr.Zero) return;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_HIDEWINDOW);
        _visible = false;
    }

    private void Draw(Size size, Rectangle inner, float t, float radius, bool maximized, Color color, string key)
    {
        EnsureSurface(size);
        using var g = Graphics.FromImage(_surface!);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var ring = new GraphicsPath(FillMode.Alternate);
        if (maximized)
        {
            AddRounded(ring, inner, 0);
            AddRounded(ring, RectangleF.Inflate(inner, -t, -t), 0);
        }
        else
        {
            AddRounded(ring, RectangleF.Inflate(inner, t, t), radius > 0 ? radius + t : 0);
            AddRounded(ring, inner, radius);
        }
        using var brush = new SolidBrush(color);
        g.FillPath(brush, ring);
        g.Flush(FlushIntention.Sync);
        _drawnKey = key;
    }

    // One top-down 32 bpp DIB section, wrapped by a premultiplied GDI+ bitmap over the same memory (what
    // UpdateLayeredWindow wants); re-created only when the size changes.
    private void EnsureSurface(Size size)
    {
        if (_surface != null && size == _surfaceSize) return;
        ReleaseSurface();
        var bmi = new BITMAPINFO
        {
            biSize = 40, biWidth = size.Width, biHeight = -size.Height, biPlanes = 1, biBitCount = 32,
        };
        var screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memDc, _dib);
        _surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        _surfaceSize = size;
        _drawnKey = null;
    }

    private void ReleaseSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        _drawnKey = null;
    }

    private static void AddRounded(GraphicsPath p, RectangleF r, float radius)
    {
        if (radius <= 0.5f) { p.AddRectangle(r); return; }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.StartFigure();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        ReleaseSurface();
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
