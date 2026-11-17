using System.Net;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Traffic;

/// <summary>
/// Acceptance criterion #3 (audit half): during an audit, connections go ONLY
/// to crawl targets (or the configured proxy). A real audit runs against a
/// loopback target while the process monitor classifies every connection:
/// all must be loopback, and every remote port must be a known product port
/// (crawl target, backend, MCP) — no unknown local service, no external host.
/// </summary>
public sealed class AuditTrafficTests
{
    [Fact]
    public async Task Audit_TrafficGoesOnlyToTheCrawlTarget()
    {
        await using var target = LoopbackTargetSite.Start();
        var targetPort = new Uri(target.BaseUrl).Port;

        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        var mcpPort = ReadMcpPort(fixture.DataDirectory);
        var knownPorts = new HashSet<int> { targetPort, fixture.Backend.Port, mcpPort };

        var (projectId, configurationId) = await SeedProjectAsync(fixture, target.BaseUrl);
        monitor.SetPhase("audit");

        var execution = fixture.Backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 50));

        var audit = await WaitForStatusAsync(fixture, auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));
        Assert.NotNull(audit.FinishedAt);

        // Give any trailing connection a poll cycle to surface.
        await Task.Delay(1000);

        var observations = monitor.Observations;
        var nonLoopback = monitor.NonLoopbackObservations;
        // Baseline connections pre-exist product activity (testhost <-> runner
        // plumbing); product traffic must only touch known product ports.
        var unknownPort = observations
            .Where(o => !o.IsBaseline && !knownPorts.Contains(o.RemotePort))
            .ToList();

        EvidenceWriter.WriteCsv(
            "audit-traffic-connections.csv",
            "at,remoteAddress,remotePort,localAddress,localPort,state,phase,isLoopback,isBaseline,knownProductPort",
            observations.Select(o =>
                $"{o.At:O},{o.RemoteAddress},{o.RemotePort},{o.LocalAddress},{o.LocalPort},{o.State},{o.Phase},{o.IsLoopback},{o.IsBaseline},{knownPorts.Contains(o.RemotePort)}"));
        EvidenceWriter.WriteMarkdown(
            "audit-traffic.md",
            $"""
            # Criterion #3 — audit-time traffic classification

            - Crawl target: {target.BaseUrl} (loopback), requests served: {target.Requests.Count}
            - Known product ports: target={targetPort}, backend={fixture.Backend.Port}, mcp={mcpPort}
            - Audit {auditId}: status={audit.Status}, phase={audit.Phase}
            - Observed connections during run: {observations.Count}
            - Non-loopback observations: **{nonLoopback.Count}** (required: 0)
            - Connections to unknown ports: **{unknownPort.Count}** (required: 0 — no tunneling of local services)

            Raw observations: audit-traffic-connections.csv
            """);

        Assert.True(target.Requests.Count >= 3, "The crawl target must actually have been crawled.");
        Assert.Empty(nonLoopback);
        Assert.Empty(unknownPort);
        Assert.NotEmpty(observations);
    }

    private static int ReadMcpPort(string dataDirectory)
    {
        var descriptorPath = Path.Combine(dataDirectory, "mcp-endpoint.json");
        if (!File.Exists(descriptorPath))
        {
            return -1;
        }

        using var descriptor = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        var endpoint = descriptor.RootElement.GetProperty("endpoint").GetString()!;
        return new Uri(endpoint).Port;
    }

    private static async Task<(long ProjectId, long ConfigurationId)> SeedProjectAsync(BackendFixture fixture, string seedUrl)
    {
        var projects = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Boundary Audit", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

        var configurations = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return (project.Id, configuration.Id);
    }

    private static async Task<AuditDto> WaitForStatusAsync(BackendFixture fixture, long auditId, AuditStatus status, TimeSpan timeout)
    {
        var reads = fixture.Backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && audit.Status == status)
            {
                return audit;
            }

            if (audit?.Status == AuditStatus.Failed)
            {
                throw new InvalidOperationException($"Audit {auditId} failed during boundary verification.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach {status} within {timeout}.");
    }
}
