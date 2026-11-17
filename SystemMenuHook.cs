using System.Runtime.InteropServices;
using static AlwaysOnTop.Native;

namespace AlwaysOnTop;

// Adds the "Always on Top" item to the system menu (title-bar right-click / Alt+Space) of every top-level window and
// reports when the user picks it. No DLL injection:
//  * Out-of-context WinEvent hooks insert the item when a window becomes foreground (plus a 400 ms retry: some
//    windows are activated before they are visible) or starts a menu loop. Right-clicking a title bar activates the
//    window first, so foreground is enough; a global EVENT_OBJECT_SHOW hook was dropped because it is delivered for
//    every caret, tooltip and popup in the system (performance pass 2026-10-03).
//  * When the system menu pops up, the check mark is synced with WS_EX_TOPMOST.
//  * WM_SYSCOMMAND with our id goes to the target window's own wndproc, which we cannot see. Instead low-level mouse /
//    keyboard hooks – installed only while such a menu is open, so normal input never passes through this process –
//    catch the choice: click on the item (MenuItemFromPoint), Enter/Space on
//    the highlighted item, or the "T" mnemonic. The target ignores the unknown WM_SYSCOMMAND id.
internal sealed class SystemMenuHook : IDisposable
{
    private const uint ID_TOPMOST = 0x7A10;     // < 0xF000, low 4 bits zero (WM_SYSCOMMAND rule)
    private const uint ID_SEPARATOR = 0x7A20;
    private const string MenuText = "Always on &Top";

    public event Action<IntPtr>? ToggleRequested;   // top-level window whose item was chosen
    public event Action<IntPtr>? Foreground;

    private readonly Control _ui;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    // Delegates kept in fields so the GC never collects them while hooked.
    private readonly WinEventProc _winEventProc;
    private readonly HookProc _mouseProc, _keyboardProc;
    private IntPtr _winEventHook, _mouseHook, _keyboardHook;

    private readonly Dictionary<IntPtr, IntPtr> _patched = new();   // hwnd -> its system menu

    // The currently open system menu that contains our item.
    private IntPtr _activeMenu, _activePopup, _activeOwner;
    private int _childPopups;                                        // submenus open on top of it

    public SystemMenuHook(Control ui)
    {
        _ui = ui;
        _winEventProc = OnWinEvent;
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MENUPOPUPEND, IntPtr.Zero, _winEventProc, 0, 0, flags);
        if (_winEventHook == IntPtr.Zero) Log.Write("WinEvent hook install failed");

        EnumWindows((h, _) => { Patch(h); return true; }, IntPtr.Zero);
        Log.Write($"patched {_patched.Count} existing windows");
    }

    public int PatchedCount => _patched.Count;

    // ---- patching ----

    private bool IsEligible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_SYSMENU) == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;
        var c = ClassName(hwnd);
        return c is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    private void Patch(IntPtr hwnd)
    {
        if (!IsEligible(hwnd)) return;
        var hmenu = GetSystemMenu(hwnd, false);
        if (hmenu == IntPtr.Zero) return;

        if (GetMenuState(hmenu, ID_TOPMOST, MF_BYCOMMAND) == uint.MaxValue)
        {
            var count = GetMenuItemCount(hmenu);
            if (count < 0) return;
            var closePos = -1;
            for (var i = 0; i < count; i++)
                if (GetMenuItemID(hmenu, i) == SC_CLOSE) { closePos = i; break; }

            if (closePos > 0 && IsSeparator(hmenu, closePos - 1))
            {
                // ... [new separator] Always on Top [existing separator] Close
                InsertItem(hmenu, closePos - 1, true);
                InsertItem(hmenu, closePos, false);
            }
            else if (closePos >= 0)
            {
                InsertItem(hmenu, closePos, false);
                InsertItem(hmenu, closePos + 1, true);
            }
            else
            {
                InsertItem(hmenu, count, true);
                InsertItem(hmenu, count + 1, false);
            }
        }
        if (!_patched.ContainsKey(hwnd) && _patched.Count % 200 == 199)
            foreach (var dead in _patched.Keys.Where(h => !IsWindow(h)).ToList()) _patched.Remove(dead);
        _patched[hwnd] = hmenu;
        SyncCheck(hmenu, hwnd);
    }

    private void PatchLater(IntPtr hwnd)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 400 };
        timer.Tick += (_, _) => { timer.Dispose(); Patch(hwnd); };
        timer.Start();
    }

    private static bool IsSeparator(IntPtr hmenu, int pos)
    {
        var state = GetMenuState(hmenu, (uint)pos, MF_BYPOSITION);
        return state != uint.MaxValue && (state & MF_SEPARATOR) != 0;
    }

    private static void InsertItem(IntPtr hmenu, int pos, bool separator)
    {
        var mii = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MIIM_FTYPE | MIIM_ID | (separator ? 0 : MIIM_STRING),
            fType = separator ? MFT_SEPARATOR : MFT_STRING,
            wID = separator ? ID_SEPARATOR : ID_TOPMOST,
        };
        if (!separator) { mii.dwTypeData = MenuText; mii.cch = (uint)MenuText.Length; }
        InsertMenuItem(hmenu, (uint)pos, true, ref mii);
    }

    private static void SyncCheck(IntPtr hmenu, IntPtr hwnd) =>
        CheckMenuItem(hmenu, ID_TOPMOST, MF_BYCOMMAND | (IsTopmost(hwnd) ? MF_CHECKED : MF_UNCHECKED));

    public void SyncCheck(IntPtr hwnd)
    {
        if (_patched.TryGetValue(hwnd, out var hmenu)) SyncCheck(hmenu, hwnd);
    }

    private IntPtr OwnerOf(IntPtr hmenu)
    {
        foreach (var (hwnd, menu) in _patched)
            if (menu == hmenu && IsWindow(hwnd)) return hwnd;
        return GetForegroundWindow();
    }

    private void QueueToggle()
    {
        var hwnd = _activeOwner;
        _ui.BeginInvoke(() => ToggleRequested?.Invoke(hwnd));
    }

    // ---- hooks ----

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND:
                Patch(hwnd);
                PatchLater(hwnd);
                Foreground?.Invoke(hwnd);
                break;

            case EVENT_SYSTEM_MENUSTART:
                if (hwnd != IntPtr.Zero) Patch(GetAncestor(hwnd, GA_ROOT));
                break;

            case EVENT_SYSTEM_MENUPOPUPSTART:
            {
                if (_activeMenu != IntPtr.Zero) { _childPopups++; break; }
                if (SendMessageTimeout(hwnd, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out var hmenu) == IntPtr.Zero
                    || hmenu == IntPtr.Zero)
                    break;
                if (GetMenuState(hmenu, ID_TOPMOST, MF_BYCOMMAND) == uint.MaxValue) break;
                _activeMenu = hmenu;
                _activePopup = hwnd;
                _activeOwner = OwnerOf(hmenu);
                _childPopups = 0;
                SyncCheck(hmenu, _activeOwner);
                InstallInputHooks();
                break;
            }

            case EVENT_SYSTEM_MENUPOPUPEND:
                if (hwnd == _activePopup) ClearActive();
                else if (_childPopups > 0) _childPopups--;
                break;

            case EVENT_SYSTEM_MENUEND:
                ClearActive();
                break;
        }
    }

    private void InstallInputHooks()
    {
        var hMod = GetModuleHandle(null);
        if (_mouseHook == IntPtr.Zero) _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_keyboardHook == IntPtr.Zero) _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    private void RemoveInputHooks()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
    }

    private void ClearActive()
    {
        RemoveInputHooks();
        _activeMenu = _activePopup = _activeOwner = IntPtr.Zero;
        _childPopups = 0;
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _activeMenu != IntPtr.Zero && _childPopups == 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_LBUTTONUP or WM_RBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var idx = MenuItemFromPoint(IntPtr.Zero, _activeMenu, info.pt);
                if (idx >= 0 && GetMenuItemID(_activeMenu, idx) == ID_TOPMOST) QueueToggle();
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _activeMenu != IntPtr.Zero && _childPopups == 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var vk = Marshal.ReadInt32(lParam);
                if (vk is VK_RETURN or VK_SPACE)
                {
                    var state = GetMenuState(_activeMenu, ID_TOPMOST, MF_BYCOMMAND);
                    if (state != uint.MaxValue && (state & MF_HILITE) != 0) QueueToggle();
                }
                else if (vk == 'T')
                {
                    QueueToggle();
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        RemoveInputHooks();
        _winEventHook = IntPtr.Zero;

        // Take our items out again, so nothing is left behind after Exit.
        foreach (var (hwnd, hmenu) in _patched)
        {
            if (!IsWindow(hwnd)) continue;
            DeleteMenu(hmenu, ID_TOPMOST, MF_BYCOMMAND);
            DeleteMenu(hmenu, ID_SEPARATOR, MF_BYCOMMAND);
        }
        _patched.Clear();
    }
}
