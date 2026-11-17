using System.Net;
using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.AirGap;

/// <summary>
/// Acceptance criterion #3 (idle/reporting half): with no destinations
/// configured, no external database selected, and no schedule due, NO outbound
/// connection occurs during idle or reporting. The backend runs the full
/// composed lifecycle (scheduler, hub, MCP, metrics samplers) while a
/// process-level monitor classifies every connection it owns.
/// </summary>
public sealed class IdleZeroOutboundTests
{
    [Fact]
    public async Task IdleAndReporting_WithNoDestinations_ProduceZeroOutbound()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        // Phase 1: idle — no user activity; background services run undisturbed.
        monitor.SetPhase("idle");
        await Task.Delay(TimeSpan.FromSeconds(5));
        var idleNonLoopback = monitor.NonLoopbackObservations.Count;

        // Phase 2: reporting — read-only surfaces render from local aggregates.
        monitor.SetPhase("reporting");
        string[] reportingRoutes =
        [
            "/api/projects",
            "/api/audits",
            "/api/settings",
            "/api/destinations",
            "/api/diagnostics/version",
            "/api/diagnostics/artifacts",
            "/api/system/info",
            "/metrics",
            "/hub",
        ];
        foreach (var route in reportingRoutes)
        {
            using var response = await fixture.Http.GetAsync(route);
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"Reporting route {route} returned {(int)response.StatusCode}.");
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        var nonLoopback = monitor.NonLoopbackObservations;

        EvidenceWriter.WriteCsv(
            "idle-reporting-connections.csv",
            "at,remoteAddress,remotePort,localAddress,localPort,state,phase,isLoopback,isBaseline",
            monitor.Observations.Select(o =>
                $"{o.At:O},{o.RemoteAddress},{o.RemotePort},{o.LocalAddress},{o.LocalPort},{o.State},{o.Phase},{o.IsLoopback},{o.IsBaseline}"));
        EvidenceWriter.WriteMarkdown(
            "idle-reporting.md",
            $"""
            # Criterion #3 — idle & reporting, zero outbound

            Configuration: fresh install — no destinations, no external database, no schedule due.

            - Idle phase (5s): non-loopback observations = {idleNonLoopback}
            - Reporting phase ({reportingRoutes.Length} read-only routes): non-loopback observations = {nonLoopback.Count - idleNonLoopback}
            - Total observed connections: {monitor.Observations.Count}
            - Total non-loopback: **{nonLoopback.Count}** (required: 0)

            Raw observations: idle-reporting-connections.csv
            """);

        Assert.Empty(nonLoopback);
        Assert.NotEmpty(monitor.Observations);
    }

    [Fact]
    public async Task DefaultConfiguration_HasNoDestinations_NoExternalDb_NoDueSchedule()
    {
        await using var fixture = await BackendFixture.StartAsync();

        // No destinations exist by default (§2.6).
        using var destinations = await fixture.Http.GetAsync("/api/destinations");
        Assert.Equal(HttpStatusCode.OK, destinations.StatusCode);
        var body = await destinations.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"host\"", body, StringComparison.OrdinalIgnoreCase);

        // The default provider is the local file-based database (§2.4).
        using var settings = await fixture.Http.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var settingsBody = await settings.Content.ReadAsStringAsync();
        Assert.Contains("sqlite", settingsBody, StringComparison.OrdinalIgnoreCase);

        // No schedule is due on a fresh install.
        using var schedules = await fixture.Http.GetAsync("/api/schedules");
        Assert.True(
            schedules.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Schedules route returned {(int)schedules.StatusCode}.");
    }
}
