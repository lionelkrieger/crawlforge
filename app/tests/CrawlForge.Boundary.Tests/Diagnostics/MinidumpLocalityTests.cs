using System.Net;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Contracts.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Diagnostics;

/// <summary>
/// Acceptance criterion #21: crash minidumps are produced locally, listed and
/// purged by the user, and NEVER transmitted. A real minidump of the running
/// backend process is captured while the connection monitor watches for any
/// outbound transfer; the artifact must stay inside the local data directory.
/// </summary>
public sealed class MinidumpLocalityTests
{
    [Fact]
    public async Task Minidump_CapturedLocally_Listed_Purged_NeverTransmitted()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        monitor.SetPhase("minidump");
        await using var fixture = await BackendFixture.StartAsync();

        var handler = fixture.Backend.App.Services.GetRequiredService<ICrashDumpHandler>();
        var artifact = await handler.CaptureMinidumpAsync();
        Assert.NotNull(artifact);
        Assert.True(File.Exists(artifact.FilePath), "The minidump file must exist locally.");

        // Locality: the artifact lives under the backend's local data directory.
        var fullDataDir = Path.GetFullPath(fixture.DataDirectory);
        var fullArtifact = Path.GetFullPath(artifact.FilePath);
        Assert.StartsWith(fullDataDir, fullArtifact, StringComparison.OrdinalIgnoreCase);
        Assert.True(artifact.SizeBytes > 0);

        // User-viewable listing through the REST surface.
        using (var list = await fixture.Http.GetAsync("/api/diagnostics/artifacts?kind=minidump"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var items = body.RootElement.GetProperty("data").EnumerateArray().ToList();
            Assert.Contains(items, i => i.GetProperty("kind").GetString() == "minidump");
        }

        // No transmission occurred during capture/listing.
        await Task.Delay(500);
        Assert.Empty(monitor.NonLoopbackObservations);

        // User-initiated purge removes the file and the registry entry.
        using (var purge = await fixture.Http.DeleteAsync("/api/diagnostics/artifacts?kind=minidump"))
        {
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
            using var body = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("data").GetProperty("purgedCount").GetInt32() >= 1);
        }

        Assert.False(File.Exists(artifact.FilePath), "Purge must remove the local minidump file.");
        using (var after = await fixture.Http.GetAsync("/api/diagnostics/artifacts?kind=minidump"))
        {
            using var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());
        }

        EvidenceWriter.WriteMarkdown(
            "minidump-locality.md",
            $"""
            # Criterion #21 — minidump locality, listing, purge

            - Captured artifact: {Path.GetFileName(artifact.FilePath)} ({artifact.SizeBytes} bytes)
            - Locality: under the local data directory ({fullDataDir})
            - REST listing: GET /api/diagnostics/artifacts?kind=minidump → present
            - Non-loopback connections during capture/list/purge: **{monitor.NonLoopbackObservations.Count}** (required: 0 — never transmitted)
            - Purge: DELETE /api/diagnostics/artifacts?kind=minidump → purgedCount ≥ 1; file removed; listing empty
            """);
    }
}
