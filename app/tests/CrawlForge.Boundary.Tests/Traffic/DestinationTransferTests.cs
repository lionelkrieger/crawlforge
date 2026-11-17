using System.Net;
using System.Text;
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
/// Acceptance criterion #14 / §2.6: destination transfers go ONLY to the
/// user-configured endpoint, with the user's credentials. The full user path
/// runs: audit → export → webhook destination (loopback stand-in for the user
/// endpoint) → explicit delivery. The monitor proves every connection during
/// the transfer phase is loopback and addressed to the user endpoint only.
/// </summary>
public sealed class DestinationTransferTests
{
    [Fact]
    public async Task Delivery_GoesOnlyToTheUserEndpoint_WithUserCredentials()
    {
        await using var target = LoopbackTargetSite.Start();
        await using var receiver = WebhookReceiver.Start();
        var receiverPort = new Uri(receiver.BaseUrl).Port;
        var targetPort = new Uri(target.BaseUrl).Port;
        const string userToken = "USER-WEBHOOK-TOKEN-boundary";

        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        // Audit the loopback target (crawl phase).
        var (projectId, configurationId) = await SeedProjectAsync(fixture, target.BaseUrl);
        monitor.SetPhase("crawl");
        var execution = fixture.Backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 50));
        await WaitForStatusAsync(fixture, auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));

        // User-configured webhook destination with the user's bearer token.
        long destinationId;
        var destinationPayload = JsonSerializer.Serialize(new
        {
            name = "user-webhook",
            kind = "webhook",
            target = receiver.BaseUrl + "ingest",
            secret = userToken,
        });
        using (var created = await fixture.Http.PostAsync("/api/destinations", new StringContent(destinationPayload, Encoding.UTF8, "application/json")))
        {
            Assert.True(created.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            destinationId = long.Parse(body.RootElement.GetProperty("data").GetProperty("id").GetString()!);
        }

        // Export the audit (local file write).
        long exportId;
        var exportPayload = JsonSerializer.Serialize(new
        {
            auditId = auditId.ToString(),
            format = "csv",
            datasets = new[] { "urls" },
        });
        using (var export = await fixture.Http.PostAsync("/api/exports", new StringContent(exportPayload, Encoding.UTF8, "application/json")))
        {
            Assert.True(export.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"Export create returned {(int)export.StatusCode}.");
            using var body = JsonDocument.Parse(await export.Content.ReadAsStringAsync());
            exportId = long.Parse(body.RootElement.GetProperty("data").GetProperty("exportId").GetString()!);
        }

        // Wait for the export job to complete (local file write).
        var exportDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < exportDeadline)
        {
            using var status = await fixture.Http.GetAsync($"/api/exports/{exportId}");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var state = body.RootElement.GetProperty("data").GetProperty("state").GetString();
            if (state == "completed")
            {
                break;
            }

            Assert.NotEqual("failed", state);
            await Task.Delay(100);
        }

        // Explicit user-initiated delivery.
        monitor.SetPhase("destination-transfer");
        var deliverPayload = JsonSerializer.Serialize(new { exportId = exportId.ToString() });
        using (var deliver = await fixture.Http.PostAsync($"/api/destinations/{destinationId}/deliver", new StringContent(deliverPayload, Encoding.UTF8, "application/json")))
        {
            Assert.True(deliver.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, $"Deliver returned {(int)deliver.StatusCode}.");
        }

        // Wait for the delivery to land.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (receiver.Deliveries.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Task.Delay(500);
        // Both sides of each loopback connection live in this process (the
        // receiver is in-process): client sockets have remote port = endpoint;
        // accepted server sockets have LOCAL port = endpoint. Both views are
        // the same on-endpoint connection.
        var knownPorts = new HashSet<int> { receiverPort, fixture.Backend.Port };
        var transferObservations = monitor.Observations.Where(o => o.Phase == "destination-transfer" && !o.IsBaseline).ToList();
        var nonLoopback = monitor.NonLoopbackObservations;
        var offEndpoint = transferObservations
            .Where(o => !knownPorts.Contains(o.RemotePort) && !knownPorts.Contains(o.LocalPort))
            .ToList();

        EvidenceWriter.WriteCsv(
            "destination-transfer-connections.csv",
            "at,remoteAddress,remotePort,localAddress,localPort,state,phase,isLoopback,isBaseline",
            monitor.Observations.Select(o =>
                $"{o.At:O},{o.RemoteAddress},{o.RemotePort},{o.LocalAddress},{o.LocalPort},{o.State},{o.Phase},{o.IsLoopback},{o.IsBaseline}"));
        EvidenceWriter.WriteMarkdown(
            "destination-transfer.md",
            $"""
            # Criterion #14 / §2.6 — destination transfer classification

            - User endpoint (loopback stand-in): {receiver.BaseUrl}ingest
            - Deliveries received: {receiver.Deliveries.Count}
            - Authorization carried: {(receiver.Deliveries.Count > 0 ? receiver.Deliveries[0].Authorization : "(none)")} (user's bearer token)
            - Transfer-phase connections: {transferObservations.Count}; addressed to user endpoint or backend: {transferObservations.Count - offEndpoint.Count}
            - Connections to any other destination during transfer: **{offEndpoint.Count}** (required: 0)
            - Non-loopback across the whole run (crawl + export + transfer): **{nonLoopback.Count}** (required: 0)

            Raw observations: destination-transfer-connections.csv
            """);

        Assert.NotEmpty(receiver.Deliveries);
        Assert.Equal("Bearer " + userToken, receiver.Deliveries[0].Authorization);
        Assert.Empty(nonLoopback);
        Assert.Empty(offEndpoint);
    }

    private static async Task<(long ProjectId, long ConfigurationId)> SeedProjectAsync(BackendFixture fixture, string seedUrl)
    {
        var projects = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Transfer Boundary", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return (project.Id, configuration.Id);
    }

    private static async Task WaitForStatusAsync(BackendFixture fixture, long auditId, AuditStatus status, TimeSpan timeout)
    {
        var reads = fixture.Backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && audit.Status == status)
            {
                return;
            }

            if (audit?.Status == AuditStatus.Failed)
            {
                throw new InvalidOperationException($"Audit {auditId} failed during transfer verification.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach {status} within {timeout}.");
    }
}
