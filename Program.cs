using System.Diagnostics;

namespace AlwaysOnTop;

internal static class Program
{
    public const string AppName = "Always on Top";

    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostics that need no running instance.
        if (args.Length >= 2 && args[0] == "--render-tray") { TrayIcons.RenderPreview(args[1]); return 0; }

        var command = args.FirstOrDefault();
        var afterUpdate = command == "--after-update";
        if (afterUpdate) Updater.WaitForPrevious(args);              // the old instance is still exiting

        // Single instance. A second launch ("Always on Top.exe --exit") forwards its command to the running one.
        using var mutex = new Mutex(true, @"Local\AlwaysOnTop.SingleInstance", out var isFirst);
        var others = Process.GetProcessesByName(AppName).Count(p => p.Id != Environment.ProcessId);
        if (!isFirst || others > 0)
        {
            Ipc.Send(Ipc.Parse(command ?? "--menu"));
            return 0;
        }
        if (Ipc.Parse(command) == IpcCommand.Exit) return 0;
        Updater.CleanUp();

        Application.ThreadException += (_, e) => Log.Write("unhandled: " + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(afterUpdate));
        return 0;
    }
}
