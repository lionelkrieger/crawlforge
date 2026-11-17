using System.Runtime.InteropServices;

namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Native Windows crash-dump interop (dbghelp). Local capture only — the dump
/// is written to the local diagnostics directory and never transmitted
/// (FS-CRAWL-001 §10.5, exclusion row 6).
/// </summary>
internal static class NativeMethods
{
    /// <summary>MiniDumpNormal: standard minidump with thread stacks and module list.</summary>
    internal const uint MiniDumpNormal = 0;

    /// <summary>
    /// Writes a minidump of the given process to an open file handle.
    /// Windows-only; callers must guard with <see cref="OperatingSystem.IsWindows"/>.
    /// </summary>
    /// <param name="hProcess">Handle of the process to capture.</param>
    /// <param name="processId">Identifier of the process to capture.</param>
    /// <param name="hFile">Open writable file handle receiving the dump.</param>
    /// <param name="dumpType">MINIDUMP_TYPE flags (MiniDumpNormal).</param>
    /// <param name="exceptionParam">Optional exception information (unused).</param>
    /// <param name="userStreamParam">Optional user stream (unused).</param>
    /// <param name="callbackParam">Optional callback (unused).</param>
    /// <returns>True when the dump was written successfully.</returns>
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}