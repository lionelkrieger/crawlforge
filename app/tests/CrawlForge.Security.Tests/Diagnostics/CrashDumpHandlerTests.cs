using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;
using CrawlForge.Security.Diagnostics;
using CrawlForge.Security.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Security.Tests.Diagnostics;

/// <summary>
/// Acceptance criterion #21: native minidump written locally on (simulated)
/// abnormal termination, user-viewable/purgeable artifact registry, local-only
/// storage. Zero automatic transmission is proven structurally by
/// <c>SecurityGuardrailTests</c> (no network code path exists).
/// </summary>
public sealed class CrashDumpHandlerTests
{
    [WindowsOnlyFact]
    public async Task CaptureMinidump_WritesLocalNativeDump_AndRegistersIt()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);

        DiagnosticArtifactDto? dto = await handler.CaptureMinidumpAsync();

        Assert.NotNull(dto);
        Assert.Equal(DiagnosticArtifactKind.Minidump, dto.Kind);
        Assert.True(File.Exists(dto.FilePath));
        Assert.True(dto.SizeBytes > 0);
        Assert.Equal(dto.SizeBytes, new FileInfo(dto.FilePath).Length);
        Assert.StartsWith(
            Path.Combine(temp.RootPath, "minidumps"),
            dto.FilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(dto.Id > 0);
        Assert.True((DateTimeOffset.UtcNow - dto.CreatedAt).Duration() < TimeSpan.FromMinutes(1));
        AssertNativeMinidumpSignature(dto.FilePath);

        IReadOnlyList<DiagnosticArtifactDto> listed = await handler.ListArtifactsAsync(DiagnosticArtifactKind.Minidump);
        DiagnosticArtifactDto row = Assert.Single(listed);
        Assert.Equal(dto.Id, row.Id);
        Assert.Equal(dto.FilePath, row.FilePath);
    }

    [WindowsOnlyFact]
    public async Task SimulatedAbnormalTermination_CapturesLocalDump()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);

        // Simulate the abnormal-termination path: the unhandled-exception hook
        // target runs synchronously, exactly as it would while the process dies.
        handler.OnUnhandledException(
            null,
            new UnhandledExceptionEventArgs(new InvalidOperationException("simulated abnormal termination"), isTerminating: true));

        IReadOnlyList<DiagnosticArtifactDto> artifacts = await handler.ListArtifactsAsync(DiagnosticArtifactKind.Minidump);
        DiagnosticArtifactDto dto = Assert.Single(artifacts);
        Assert.True(File.Exists(dto.FilePath));
        Assert.True(dto.SizeBytes > 0);
        AssertNativeMinidumpSignature(dto.FilePath);
    }

    [WindowsOnlyFact]
    public void InstallCrashHooks_IsIdempotent_AndUninstallRemovesThem()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);

        try
        {
            Assert.False(handler.IsCrashHooksInstalled);

            handler.InstallCrashHooks();
            Assert.True(handler.IsCrashHooksInstalled);

            handler.InstallCrashHooks();
            Assert.True(handler.IsCrashHooksInstalled);
        }
        finally
        {
            handler.UninstallCrashHooks();
        }

        Assert.False(handler.IsCrashHooksInstalled);
        handler.UninstallCrashHooks();
        Assert.False(handler.IsCrashHooksInstalled);
    }

    [WindowsOnlyFact]
    public async Task ListArtifacts_FiltersByKind_AcrossDumpsAndLogs()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Information, "diagnostics filter test");
        DiagnosticArtifactDto? dump = await handler.CaptureMinidumpAsync();
        Assert.NotNull(dump);

        IReadOnlyList<DiagnosticArtifactDto> all = await handler.ListArtifactsAsync(null);
        Assert.Equal(2, all.Count);

        IReadOnlyList<DiagnosticArtifactDto> dumps = await handler.ListArtifactsAsync(DiagnosticArtifactKind.Minidump);
        Assert.Single(dumps);
        Assert.Equal(DiagnosticArtifactKind.Minidump, dumps[0].Kind);

        IReadOnlyList<DiagnosticArtifactDto> logs = await handler.ListArtifactsAsync(DiagnosticArtifactKind.Log);
        Assert.Single(logs);
        Assert.Equal(DiagnosticArtifactKind.Log, logs[0].Kind);
    }

    [WindowsOnlyFact]
    public async Task PurgeArtifacts_ByKind_RemovesOnlyThatKind_FromDiskAndRegistry()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Information, "purge kind test");
        DiagnosticArtifactDto? dump = await handler.CaptureMinidumpAsync();
        Assert.NotNull(dump);

        int purged = await handler.PurgeArtifactsAsync(DiagnosticArtifactKind.Minidump);

        Assert.Equal(1, purged);
        Assert.False(File.Exists(dump.FilePath));
        Assert.Single(await handler.ListArtifactsAsync(DiagnosticArtifactKind.Log));
        Assert.Empty(await handler.ListArtifactsAsync(DiagnosticArtifactKind.Minidump));
    }

    [WindowsOnlyFact]
    public async Task PurgeArtifacts_All_RemovesEverything_AndIsIdempotent()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var handler = new MinidumpCrashHandler(registry);
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Warning, "purge all test");
        DiagnosticArtifactDto? dump = await handler.CaptureMinidumpAsync();
        Assert.NotNull(dump);
        IReadOnlyList<DiagnosticArtifactDto> before = await handler.ListArtifactsAsync(null);
        Assert.Equal(2, before.Count);

        int purged = await handler.PurgeArtifactsAsync(null);

        Assert.Equal(2, purged);
        foreach (DiagnosticArtifactDto artifact in before)
        {
            Assert.False(File.Exists(artifact.FilePath));
        }

        Assert.Empty(await handler.ListArtifactsAsync(null));
        Assert.Equal(0, await handler.PurgeArtifactsAsync(null));
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MinidumpCrashHandler(null!));
    }

    private static void AssertNativeMinidumpSignature(string path)
    {
        byte[] header = new byte[4];
        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(4, stream.Read(header, 0, 4));
        }

        // "MDMP" — the native Windows minidump magic (MINIDUMP_HEADER.Signature
        // 0x504D444D, little-endian on disk).
        Assert.Equal(new byte[] { 0x4D, 0x44, 0x4D, 0x50 }, header);
    }
}