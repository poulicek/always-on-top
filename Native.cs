using System.Runtime.InteropServices;
using System.Text;

namespace AlwaysOnTop;

internal static class Native
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MENUSTART = 0x0004;
    public const uint EVENT_SYSTEM_MENUEND = 0x0005;
    public const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    public const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    public const int VK_RETURN = 0x0D, VK_SPACE = 0x20;
    public const uint MN_GETHMENU = 0x01E1;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const uint MF_BYCOMMAND = 0x0000, MF_BYPOSITION = 0x0400;
    public const uint MF_UNCHECKED = 0x0000, MF_CHECKED = 0x0008;
    public const uint MF_HILITE = 0x0080, MF_SEPARATOR = 0x0800;
    public const uint MIIM_ID = 0x0002, MIIM_STRING = 0x0040, MIIM_FTYPE = 0x0100;
    public const uint MFT_STRING = 0x0000, MFT_SEPARATOR = 0x0800;
    public const uint SC_CLOSE = 0xF060;

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_SYSMENU = 0x00080000L, WS_EX_TOPMOST = 0x00000008L;
    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010,
        SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080, SWP_NOOWNERZORDER = 0x0200;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int CX, CY; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public string? dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // hooks
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    // windows
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // menus
    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hmenu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr hmenu, int pos);
    [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr hmenu, uint id, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool InsertMenuItem(IntPtr hmenu, uint item, bool byPosition, ref MENUITEMINFO mii);
    [DllImport("user32.dll")] public static extern bool DeleteMenu(IntPtr hmenu, uint id, uint flags);
    [DllImport("user32.dll")] public static extern uint CheckMenuItem(IntPtr hmenu, uint id, uint flags);
    [DllImport("user32.dll")] public static extern int MenuItemFromPoint(IntPtr hwnd, IntPtr hmenu, POINT pt);

    // layered window painting
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")] private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd, ref POINT pt);

    // WM_NCHITTEST of another app's window at a screen point (null if it does not answer within `timeoutMs`).
    // The point must be converted to the target's coordinate space first: this process is per-monitor DPI aware
    // (physical pixels), and a sent message is NOT virtualized for DPI-unaware / system-aware windows (Character Map
    // and other classic apps would test the wrong spot at 150 %).
    public static int? NcHitTest(IntPtr hwnd, POINT pt, uint timeoutMs)
    {
        PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref pt);
        var lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
        return SendMessageTimeout(hwnd, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, timeoutMs, out var hit) == IntPtr.Zero
            ? null : hit.ToInt32();
    }

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    public static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
