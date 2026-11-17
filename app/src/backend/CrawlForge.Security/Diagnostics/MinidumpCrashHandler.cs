using System.Diagnostics;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Native minidump crash handler (FS-CRAWL-001 §2.3/§10.5, acceptance criterion
/// #21): on abnormal backend termination a native diagnostic minidump is
/// written to the local diagnostics directory. Artifacts are local-only,
/// user-viewable and purgeable through the shared
/// <see cref="DiagnosticArtifactRegistry"/>, bounded with oldest-first
/// reclamation, and NEVER transmitted automatically (exclusion row 6).
/// </summary>
/// <remarks>
/// <para>
/// Capture uses the Windows <c>dbghelp!MiniDumpWriteDump</c> API against the
/// current process. On non-Windows platforms capture is not possible and
/// <see cref="CaptureMinidumpAsync"/> returns null (Windows primary, macOS and
/// Linux secondary per spec §13 portability).
/// </para>
/// <para>
/// <see cref="InstallCrashHooks"/> subscribes to
/// <see cref="AppDomain.UnhandledException"/> so an abnormal managed termination
/// captures a dump synchronously before the process dies; the host composition
/// root wires this at startup in a later phase. Purely native faults (for
/// example access violations) terminate before managed hooks run; capturing
/// those requires the supervisor-side recovery flow (spec §2.3), which restarts
/// the backend and surfaces the local crash artifacts.
/// </para>
/// <para>
/// This type contains no network code path of any kind: dumps are written to
/// the local filesystem only, and no transmission API is referenced.
/// </para>
/// </remarks>
public sealed class MinidumpCrashHandler : ICrashDumpHandler
{
    private readonly DiagnosticArtifactRegistry _registry;
    private readonly object _hookGate = new();
    private bool _hooksInstalled;

    /// <summary>Creates a crash handler storing dumps in the given local registry.</summary>
    /// <param name="registry">Shared local diagnostic artifact registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public MinidumpCrashHandler(DiagnosticArtifactRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>True while the abnormal-termination capture hooks are installed.</summary>
    public bool IsCrashHooksInstalled
    {
        get
        {
            lock (_hookGate)
            {
                return _hooksInstalled;
            }
        }
    }

    /// <summary>
    /// Installs the abnormal-termination capture hook (idempotent). The hook
    /// captures a local minidump synchronously when a managed unhandled
    /// exception terminates the process; it never throws and never transmits.
    /// </summary>
    public void InstallCrashHooks()
    {
        lock (_hookGate)
        {
            if (!_hooksInstalled)
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                _hooksInstalled = true;
            }
        }
    }

    /// <summary>Removes the abnormal-termination capture hook (idempotent).</summary>
    public void UninstallCrashHooks()
    {
        lock (_hookGate)
        {
            if (_hooksInstalled)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                _hooksInstalled = false;
            }
        }
    }

    /// <summary>
    /// Abnormal-termination hook target: captures a local minidump synchronously
    /// because the process is dying. Never throws; capture failure is silent so
    /// termination is never masked or delayed by diagnostics.
    /// </summary>
    /// <param name="sender">Event sender (unused; may be null).</param>
    /// <param name="e">Unhandled-exception event arguments.</param>
    public void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            CaptureCore();
        }
        catch (Exception)
        {
            // Diagnostics must never interfere with process termination.
        }
    }

    /// <inheritdoc />
    public Task<DiagnosticArtifactDto?> CaptureMinidumpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CaptureCore());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiagnosticArtifactDto>> ListArtifactsAsync(
        DiagnosticArtifactKind? kind,
        CancellationToken cancellationToken = default) =>
        _registry.ListAsync(kind, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeArtifactsAsync(
        DiagnosticArtifactKind? kind,
        CancellationToken cancellationToken = default) =>
        _registry.PurgeAsync(kind, cancellationToken);

    private DiagnosticArtifactDto? CaptureCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Native minidump capture is a Windows capability (spec §13 portability).
            return null;
        }

        string fileName = FormattableString.Invariant(
            $"minidump-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetRandomFileName()}.dmp");
        string path = Path.Combine(_registry.MinidumpDirectory, fileName);

        try
        {
            Directory.CreateDirectory(_registry.MinidumpDirectory);

            using (Process process = Process.GetCurrentProcess())
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                bool written = NativeMethods.MiniDumpWriteDump(
                    process.Handle,
                    (uint)Environment.ProcessId,
                    file.SafeFileHandle.DangerousGetHandle(),
                    NativeMethods.MiniDumpNormal,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!written)
                {
                    TryDelete(path);
                    return null;
                }
            }

            DiagnosticArtifactDto dto = _registry
                .RegisterAsync(path, DiagnosticArtifactKind.Minidump)
                .GetAwaiter()
                .GetResult();

            // Retention may have reclaimed the fresh dump when bounds are smaller
            // than the artifact; a vanished file means "capture not retained".
            return File.Exists(dto.FilePath) ? dto : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(path);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a partial dump.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a partial dump.
        }
    }
}