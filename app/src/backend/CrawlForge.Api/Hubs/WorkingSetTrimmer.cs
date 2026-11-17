using System.Runtime.InteropServices;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// OS working-set trim for the manual compact action (FS-CRAWL-002 §7.2:
/// "an OS working-set trim where the platform supports it"). Windows only;
/// other platforms report the action as not performed. Memory management
/// only — no identity, registry, or instrumentation surface is touched.
/// </summary>
public static class WorkingSetTrimmer
{
    /// <summary>
    /// Attempts to trim the current process working set. Returns false on
    /// unsupported platforms or when the OS call fails (never fatal).
    /// </summary>
    /// <returns>True when the trim was performed.</returns>
    public static bool TryTrim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // Pseudo-handle (-1) targets the current process; (-1, -1) sizes
            // request the OS to empty the working set as far as possible.
            return SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
}
