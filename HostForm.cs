namespace AlwaysOnTop;

// Invisible window: owns the UI thread (BeginInvoke), receives IPC commands, owns the tray menu. Never shown.
internal sealed class HostForm : Form
{
    public event Action<IpcCommand>? CommandReceived;
    public event Action<int, IntPtr>? ShellMessage;                 // RegisterShellHookWindow notifications (code, hwnd)
    private static readonly uint ShellHookMessage = RegisterWindowMessage("SHELLHOOK");

    public HostForm()
    {
        Text = Ipc.HostTitle;                             // second instances find this window by title
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        Opacity = 0;
        _ = Handle;                                       // create the HWND now (needed for BeginInvoke before any Show)
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;                           // WS_EX_TOOLWINDOW: never in Alt+Tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Ipc.Message)
        {
            CommandReceived?.Invoke((IpcCommand)(int)m.WParam);
            return;
        }
        if (m.Msg == ShellHookMessage)
        {
            ShellMessage?.Invoke((int)(m.WParam.ToInt64() & 0x7FFF), m.LParam);
            return;
        }
        base.WndProc(ref m);
    }
}
