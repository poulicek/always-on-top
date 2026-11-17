using System.Runtime.InteropServices;

namespace AlwaysOnTop;

// Commands a second process instance can send to the running app (registered window message to the host window).
internal enum IpcCommand { None = 0, Menu = 1, Exit = 2, Status = 3, SplitOn = 4, SplitOff = 5, Pin = 6, Unpin = 7, PinCorner = 8 }

internal static class Ipc
{
    public static readonly uint Message = RegisterWindowMessage("AlwaysOnTop.Command.v1");
    public const string HostTitle = "AlwaysOnTop.Host.v1";

    public static IpcCommand Parse(string? arg) => arg switch
    {
        "--menu" => IpcCommand.Menu,
        "--exit" => IpcCommand.Exit,
        "--status" => IpcCommand.Status,
        "--split-on" => IpcCommand.SplitOn,      // separate taskbar buttons on/off (same as the tray menu item)
        "--split-off" => IpcCommand.SplitOff,
        "--pin" => IpcCommand.Pin,               // dev/test: --pin <hwnd> / --unpin <hwnd> (decimal window handle)
        "--unpin" => IpcCommand.Unpin,
        "--pin-corner" => IpcCommand.PinCorner,  // dev/test: pin as a corner dock would (auto pin)
        _ => IpcCommand.None
    };

    public static void Send(IpcCommand command, IntPtr argument = default)
    {
        if (command == IpcCommand.None) return;
        var target = FindWindow(null, HostTitle);
        if (target != IntPtr.Zero) PostMessage(target, Message, (IntPtr)(int)command, argument);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
