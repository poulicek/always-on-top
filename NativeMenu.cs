using System.Runtime.InteropServices;

namespace AlwaysOnTop;

// Plain Win32 popup menu (CreatePopupMenu / TrackPopupMenuEx), the same component as in Next Up. Built fresh for
// every click, so it always has the DPI, theme and position of the monitor it opens on. Dark mode comes from
// Theme.EnableDarkMenus().
internal sealed class NativeMenu : IDisposable
{
    private readonly IntPtr _menu = CreatePopupMenu();
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public void Add(string text, Action action, bool enabled = true, bool isChecked = false)
    {
        var id = _nextId++;
        _actions[id] = action;
        var flags = MF_STRING | (enabled ? 0 : MF_GRAYED) | (isChecked ? MF_CHECKED : 0);
        AppendMenuW(_menu, flags, (IntPtr)id, text);
    }

    public void AddSeparator() => AppendMenuW(_menu, MF_SEPARATOR, IntPtr.Zero, null);

    // Shows the menu at a screen point and runs the chosen action. The owner window must be brought to the
    // foreground first, otherwise the menu does not close when the user clicks elsewhere (classic tray-menu rule);
    // the WM_NULL afterwards makes the menu go away in the remaining edge cases.
    public void Show(IntPtr owner, Point screen)
    {
        SetForegroundWindow(owner);
        var id = TrackPopupMenuEx(_menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_TOPALIGN, screen.X, screen.Y, owner, IntPtr.Zero);
        PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        if (id > 0 && _actions.TryGetValue(id, out var action)) action();
    }

    public void Dispose() => DestroyMenu(_menu);

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    private const uint TPM_LEFTALIGN = 0x0000, TPM_TOPALIGN = 0x0000, TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint flags, IntPtr idOrSubmenu, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpmParams);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
