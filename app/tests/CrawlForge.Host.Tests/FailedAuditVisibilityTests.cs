using System.Collections.Concurrent;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Host;
using CrawlForge.Host.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// HOTFIX-01: a failed audit must persist its reason and publish the
/// contract-shaped hub event, so REST and the shell never show RUNNING.
/// </summary>
public sealed class FailedAuditVisibilityTests
{
    [Fact]
    public async Task FailedAudit_PersistsMessage_AndPublishesAuditFailedEvent()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"))}", "--CrawlForge:Port=0"],
            builder => builder.Services.AddSingleton<IHubClientChannel>(channel));

        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Failure visibility", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { "http://127.0.0.1:47311/" }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        // An invalid concurrency ceiling is accepted by the start port but
        // rejected by AIMD validation - a genuine, real-path audit failure.
        var auditId = await execution.StartAuditAsync(
            project.Id,
            configuration.Id,
            ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(
                MaxUrls: 5,
                MaxDepth: null,
                MaxDurationSeconds: 30,
                MaxConcurrency: 0,
                RequestsPerSecond: null,
                FetchTimeoutSeconds: null));

        var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        AuditDto? audit = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            audit = await audits.GetByIdAsync(auditId);
            if (audit?.Status is AuditStatus.Failed or AuditStatus.Completed or AuditStatus.Cancelled)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(audit);
        Assert.Equal(AuditStatus.Failed, audit.Status);
        using var counters = JsonDocument.Parse(audit.CountersJson);
        Assert.Contains("WorkerConcurrencyCeiling", counters.RootElement.GetProperty("errorMessage").GetString());

        var failedEvent = Assert.Single(channel.Events, e => e.Name == "audit-failed");
        using var payload = JsonDocument.Parse(failedEvent.PayloadJson);
        Assert.Equal("audit-failed", payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("WorkerConcurrencyCeiling", payload.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain(channel.Events, e => e.Name == "audit-completed");
    }

    private sealed class RecordingHubChannel : IHubClientChannel
    {
        public ConcurrentQueue<(string Name, string PayloadJson)> Events { get; } = new();

        public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
