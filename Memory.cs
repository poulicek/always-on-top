using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace AlwaysOnTop;

// The app idles almost all the time; after start-up and after the menu closes, give the start-up / menu garbage back
// and let Windows page out what is not needed (working set goes from ~45 MB to a few MB; it grows back only on use).
internal static class Memory
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
