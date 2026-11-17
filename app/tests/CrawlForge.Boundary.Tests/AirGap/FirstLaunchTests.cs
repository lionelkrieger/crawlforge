using System.Net;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.AirGap;

/// <summary>
/// Acceptance criterion #1: first launch on a machine with no network completes
/// initialization and opens the main hub. Proven at runtime: a fresh data
/// directory initializes fully while a process-level monitor observes ZERO
/// non-loopback connections, and the hub route answers locally.
/// </summary>
public sealed class FirstLaunchTests
{
    [Fact]
    public async Task FirstLaunch_CompletesInitialization_WithZeroOutbound()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        monitor.SetPhase("first-launch");

        await using var fixture = await BackendFixture.StartAsync();

        // Initialization completed against fresh local storage.
        Assert.True(fixture.Backend.FirstLaunch.FirstRun);
        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, "settings.json")));
        Assert.True(fixture.Backend.Port > 0);

        // The main hub surface is open locally.
        var hub = await fixture.Http.GetAsync("/hub");
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        using var hubBody = JsonDocument.Parse(await hub.Content.ReadAsStringAsync());
        Assert.Equal("ready", hubBody.RootElement.GetProperty("status").GetString());

        // Readiness probe answers.
        var health = await fixture.Http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // Let any delayed background activity surface, then assert the boundary.
        await Task.Delay(1500);
        var nonLoopback = monitor.NonLoopbackObservations;

        EvidenceWriter.WriteCsv(
            "first-launch-connections.csv",
            "at,remoteAddress,remotePort,localAddress,localPort,state,phase,isLoopback,isBaseline",
            monitor.Observations.Select(o =>
                $"{o.At:O},{o.RemoteAddress},{o.RemotePort},{o.LocalAddress},{o.LocalPort},{o.State},{o.Phase},{o.IsLoopback},{o.IsBaseline}"));
        EvidenceWriter.WriteMarkdown(
            "first-launch.md",
            $"""
            # Criterion #1 — first launch, zero outbound

            - Fresh data directory: `{fixture.DataDirectory}`
            - FirstRun: {fixture.Backend.FirstLaunch.FirstRun}
            - Loopback port: {fixture.Backend.Port}
            - Hub route: HTTP 200 (status=ready); health: HTTP 200
            - Observed connections: {monitor.Observations.Count}
            - Non-loopback observations: **{nonLoopback.Count}** (required: 0)

            Raw observations: first-launch-connections.csv
            """);

        Assert.Empty(nonLoopback);
        Assert.NotEmpty(monitor.Observations); // the monitor actually observed the loopback traffic
    }

    [Fact]
    public async Task SecondLaunch_ReusesLocalState_WithZeroOutbound()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        monitor.SetPhase("second-launch");

        var dataDirectory = Path.Combine(Path.GetTempPath(), "cf-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] args = [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"];
            await using (var first = await CrawlForge.Host.CrawlForgeBackend.StartAsync(args))
            {
                Assert.True(first.FirstLaunch.FirstRun);
            }

            await using var second = await CrawlForge.Host.CrawlForgeBackend.StartAsync(args);
            Assert.False(second.FirstLaunch.FirstRun);

            await Task.Delay(1000);
            Assert.Empty(monitor.NonLoopbackObservations);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
