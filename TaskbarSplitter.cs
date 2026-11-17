using System.Runtime.InteropServices;
using System.Text;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Separate taskbar buttons (optional, Settings.SeparateTaskbarButtons, off by default; 2026-10-13): with "combine
// taskbar buttons and hide labels" the taskbar stacks all windows of one app on one icon. The taskbar groups by the
// window's AppUserModelID, which another process can set per window (SHGetPropertyStoreForWindow +
// PKEY_AppUserModel_ID, no injection; verified on Character Map: "2 running windows" became two buttons, clearing
// the property merged them back instantly).
//
// Per app (key = the window's own AppUserModelID if it has one, else the process image path) one window keeps its
// original identity – it stays on the app's pinned icon, with jump list – and every further window gets a unique id
// ("AlwaysOnTop.Separate.<hwnd>") and with it its own button. Known limits: such buttons are appended at the end of
// the taskbar and have no jump list; UWP frame windows (ApplicationFrameWindow) are left alone; elevated windows
// cannot be changed from a non-elevated process.
//
// Cost: windows are learnt from RegisterShellHookWindow (HSHELL_WINDOWCREATED / DESTROYED, i.e. only windows the
// taskbar itself cares about) – no polling, no global hooks. Disabling or Exit clears every id this class set.
internal sealed class TaskbarSplitter : IDisposable
{
    private const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2;
    private const string IdPrefix = "AlwaysOnTop.Separate.";

    private readonly HostForm _host;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<IntPtr, string> _keys = new();       // taskbar windows seen → group key
    private readonly HashSet<IntPtr> _separated = new();            // windows that got our id
    private bool _enabled;

    public TaskbarSplitter(HostForm host, bool enabled)
    {
        _host = host;
        _host.ShellMessage += OnShellMessage;
        Enabled = enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (value)
            {
                RegisterShellHookWindow(_host.Handle);
                EnumWindows((h, _) => { Add(h); return true; }, IntPtr.Zero);
                Log.Write($"separate taskbar buttons on: {_separated.Count} of {_keys.Count} windows separated");
            }
            else
            {
                DeregisterShellHookWindow(_host.Handle);
                RestoreAll();
            }
        }
    }

    private void OnShellMessage(int code, IntPtr hwnd)
    {
        if (!_enabled) return;
        if (code == HSHELL_WINDOWCREATED) Add(hwnd);
        else if (code == HSHELL_WINDOWDESTROYED) { _keys.Remove(hwnd); _separated.Remove(hwnd); }
    }

    // A window the taskbar shows: if its app already has a window with the original identity, give it its own id.
    private void Add(IntPtr hwnd)
    {
        if (_keys.ContainsKey(hwnd) || !IsTaskbarWindow(hwnd)) return;
        var key = GroupKey(hwnd);
        if (key == null) return;
        PruneDead();
        var groupHasOriginal = _keys.Any(p => p.Value == key && !_separated.Contains(p.Key));
        _keys[hwnd] = key;
        if (!groupHasOriginal) return;                              // first window of the app keeps its identity
        if (SetAppId(hwnd, IdPrefix + hwnd.ToInt64().ToString("X")) == 0) _separated.Add(hwnd);
        else Log.Write($"separate failed {hwnd} '{WindowTitle(hwnd)}'");
    }

    private void RestoreAll()
    {
        foreach (var hwnd in _separated)
            if (IsWindow(hwnd)) SetAppId(hwnd, null);
        Log.Write($"separate taskbar buttons off: {_separated.Count} windows restored");
        _separated.Clear();
        _keys.Clear();
    }

    private void PruneDead()
    {
        foreach (var dead in _keys.Keys.Where(h => !IsWindow(h)).ToList())
        {
            _keys.Remove(dead);
            _separated.Remove(dead);
        }
    }

    // The windows the taskbar lists: visible, unowned (or WS_EX_APPWINDOW), not tool windows.
    private bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;
        return ClassName(hwnd) is not ("ApplicationFrameWindow" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow");
    }

    private static string? GroupKey(IntPtr hwnd)
    {
        var id = GetAppId(hwnd);
        if (!string.IsNullOrEmpty(id)) return id.StartsWith(IdPrefix) ? null : "id:" + id;
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? "exe:" + sb.ToString().ToLowerInvariant() : null;
        }
        finally { CloseHandle(process); }
    }

    // ---- per-window AppUserModelID ----

    private static PROPERTYKEY AppIdKey = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    private static int SetAppId(IntPtr hwnd, string? id)
    {
        var iid = typeof(IPropertyStore).GUID;
        var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var store);
        if (hr != 0) return hr;
        var value = new PROPVARIANT();
        try
        {
            if (id != null) { value.vt = 31; value.p = Marshal.StringToCoTaskMemUni(id); }   // VT_LPWSTR; VT_EMPTY clears
            hr = store.SetValue(ref AppIdKey, value);
            if (hr == 0) hr = store.Commit();
            return hr;
        }
        finally
        {
            if (value.p != IntPtr.Zero) Marshal.FreeCoTaskMem(value.p);
            Marshal.ReleaseComObject(store);
        }
    }

    private static string? GetAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return null;
        var value = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref AppIdKey, value) != 0 || value.vt != 31) return null;
            return Marshal.PtrToStringUni(value.p);
        }
        finally
        {
            PropVariantClear(value);
            Marshal.ReleaseComObject(store);
        }
    }

    public void Dispose()
    {
        _host.ShellMessage -= OnShellMessage;
        Enabled = false;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, [Out] PROPVARIANT value);
        int SetValue(ref PROPERTYKEY key, [In] PROPVARIANT value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class PROPVARIANT { public ushort vt; private ushort r1, r2, r3; public IntPtr p; private int pad; }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint GW_OWNER = 4;

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear([In, Out] PROPVARIANT value);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
