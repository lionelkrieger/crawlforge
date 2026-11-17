using System.Collections.Concurrent;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 criteria #20/#41 surface — real-time hub emission rules
/// (FS-CRAWL-001 §10.3, FS-CRAWL-002 §7.2 + Appendix C) exercised through the
/// COMPOSED backend: the product <see cref="RealtimeHubService"/> runs over a
/// recording <see cref="IHubClientChannel"/> substituted at the locked transport
/// seam. Proven: live-audit stage sequencing ending in exactly one completion,
/// per-audit channel routing, latest-wins ≤1/s coalescing (crawl-progress and
/// per-host host-throttle), and the 2-second system-health heartbeat gated on
/// shell connectivity.
/// </summary>
public sealed class HubSequencingE2ETests
{
    [Fact]
    public async Task LiveAudit_EmitsStageSequence_ThenExactlyOneCompletion_OnAuditChannel()
    {
        var channel = new RecordingHubChannel();
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Hub index", "<p>Hub sequencing fixture.</p>" + FixturePages.Links(["/p/1"]))));
            routes.Map("/p/1", OriginResponse.Html(FixturePages.Page(
                "Hub page", "<p>Hub page content.</p>")));
        });
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var groupName = RealtimeHubService.GroupName(auditId);
        var auditEvents = channel.Snapshot()
            .Where(e => e.Group == groupName)
            .ToList();

        // Lifecycle stages are emitted on the audit channel, in order, and the
        // single completion event comes after every stage change.
        var stages = auditEvents.Where(e => e.EventName == HubEventCatalog.StageChanged).ToList();
        Assert.NotEmpty(stages);
        var completions = auditEvents.Where(e => e.EventName == HubEventCatalog.AuditCompleted).ToList();
        Assert.Single(completions);
        Assert.All(stages, s => Assert.True(s.Seq < completions[0].Seq,
            "stage-changed emitted after audit-completed."));
        Assert.DoesNotContain(auditEvents, e => e.EventName == HubEventCatalog.AuditFailed);

        // Audit-scoped events carrying this auditId never broadcast.
        var broadcastAuditScoped = channel.Snapshot()
            .Where(e => e.Kind == "all" && IsAuditScoped(e.EventName) && PayloadAuditId(e.PayloadJson) == auditId)
            .ToList();
        Assert.Empty(broadcastAuditScoped);

        // Completion payload references the audit.
        Assert.Equal(auditId, PayloadAuditId(completions[0].PayloadJson));
    }

    [Fact]
    public async Task CrawlProgress_BurstCoalescesLatestWins_AtMostOnePerSecond()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var hub = backend.Service<IRealtimeHub>();

        // 25 progress events inside one throttle window.
        for (var i = 0; i < 25; i++)
        {
            await hub.PublishAsync(new HubEvent(
                HubEventCatalog.CrawlProgress,
                $$"""{"auditId":999,"fetched":{{i}}}"""));
        }

        var immediate = channel.Snapshot().Where(e => e.EventName == HubEventCatalog.CrawlProgress).ToList();
        Assert.Single(immediate);
        Assert.Equal(0, PayloadInt(immediate[0].PayloadJson, "fetched"));

        // The flush timer emits exactly one coalesced event: the LATEST payload.
        await Task.Delay(TimeSpan.FromMilliseconds(1600));
        var delivered = channel.Snapshot().Where(e => e.EventName == HubEventCatalog.CrawlProgress).ToList();
        Assert.Equal(2, delivered.Count);
        Assert.Equal(24, PayloadInt(delivered[^1].PayloadJson, "fetched"));
        Assert.True(delivered[1].At - delivered[0].At >= TimeSpan.FromSeconds(0.9),
            "Coalesced emission violated the ≤1/s cadence.");
    }

    [Fact]
    public async Task HostThrottle_ThrottlesIndependentlyPerHost()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var hub = backend.Service<IRealtimeHub>();

        for (var i = 0; i < 6; i++)
        {
            await hub.PublishAsync(new HubEvent(
                HubEventCatalog.HostThrottle,
                $$"""{"auditId":999,"host":"host-a","rate":{{i}}}"""));
            await hub.PublishAsync(new HubEvent(
                HubEventCatalog.HostThrottle,
                $$"""{"auditId":999,"host":"host-b","rate":{{i}}}"""));
        }

        // One immediate emission per host key — the keys do not share a window.
        var immediate = channel.Snapshot().Where(e => e.EventName == HubEventCatalog.HostThrottle).ToList();
        Assert.Equal(2, immediate.Count);
        Assert.Contains(immediate, e => PayloadString(e.PayloadJson, "host") == "host-a");
        Assert.Contains(immediate, e => PayloadString(e.PayloadJson, "host") == "host-b");

        await Task.Delay(TimeSpan.FromMilliseconds(1600));
        var delivered = channel.Snapshot().Where(e => e.EventName == HubEventCatalog.HostThrottle).ToList();
        Assert.Equal(4, delivered.Count);
        Assert.Equal(5, PayloadInt(delivered.Last(e => PayloadString(e.PayloadJson, "host") == "host-a").PayloadJson, "rate"));
        Assert.Equal(5, PayloadInt(delivered.Last(e => PayloadString(e.PayloadJson, "host") == "host-b").PayloadJson, "rate"));
    }

    [Fact]
    public async Task SystemHealth_HeartbeatEvery2Seconds_OnlyWhileShellConnected()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var hub = backend.Service<RealtimeHubService>();

        // Idle backend, no shell client: no heartbeat.
        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        Assert.DoesNotContain(channel.Snapshot(), e => e.EventName == HubEventCatalog.SystemHealth);

        // Connected shell: ~2s cadence.
        await hub.ConnectAsync("shell-1");
        await Task.Delay(TimeSpan.FromMilliseconds(4600));
        var beats = channel.Snapshot().Where(e => e.EventName == HubEventCatalog.SystemHealth).ToList();
        Assert.True(beats.Count >= 2, $"Expected ≥2 heartbeats in 4.6s, got {beats.Count}.");
        for (var i = 1; i < beats.Count; i++)
        {
            Assert.True(beats[i].At - beats[i - 1].At >= TimeSpan.FromSeconds(1.5),
                "Heartbeat cadence faster than the 2s interval.");
        }

        // Disconnected shell: heartbeat stops.
        await hub.DisconnectAsync("shell-1");
        var countAfterDisconnect = channel.Snapshot().Count(e => e.EventName == HubEventCatalog.SystemHealth);
        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        Assert.Equal(countAfterDisconnect, channel.Snapshot().Count(e => e.EventName == HubEventCatalog.SystemHealth));
    }

    [Fact]
    public async Task Routing_AuditScopedEventsGoToGroup_GlobalEventsBroadcast()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var hub = backend.Service<IRealtimeHub>();

        await hub.PublishAsync(new HubEvent(HubEventCatalog.StageChanged, """{"auditId":777,"state":"running"}"""));
        await hub.PublishAsync(new HubEvent(HubEventCatalog.BackendLifecycle, """{"state":"ready"}"""));
        // schedule-fired carries an auditId but is a global lifecycle event.
        await hub.PublishAsync(new HubEvent(HubEventCatalog.ScheduleFired, """{"auditId":777,"scheduleId":1}"""));

        var events = channel.Snapshot();
        Assert.Contains(events, e => e.Kind == "group" && e.Group == "audit-777" && e.EventName == HubEventCatalog.StageChanged);
        Assert.Contains(events, e => e.Kind == "all" && e.EventName == HubEventCatalog.BackendLifecycle);
        Assert.Contains(events, e => e.Kind == "all" && e.EventName == HubEventCatalog.ScheduleFired);
        Assert.DoesNotContain(events, e => e.Kind == "all" && e.EventName == HubEventCatalog.StageChanged);
    }

    [Fact]
    public async Task SubscribeAndUnsubscribe_ManageAuditGroupMembership()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var hub = backend.Service<RealtimeHubService>();

        await hub.SubscribeAsync("shell-1", [1, 2, 2]);
        var joins = channel.Snapshot().Where(e => e.Kind == "join").ToList();
        Assert.Equal(2, joins.Count);
        Assert.Contains(joins, e => e.Group == "audit-1");
        Assert.Contains(joins, e => e.Group == "audit-2");

        await hub.UnsubscribeAsync("shell-1", [1]);
        Assert.Contains(channel.Snapshot(), e => e.Kind == "leave" && e.Group == "audit-1");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static bool IsAuditScoped(string eventName) => eventName is
        HubEventCatalog.CrawlProgress or HubEventCatalog.UrlBatch or HubEventCatalog.StageChanged or
        HubEventCatalog.FindingsUpdated or HubEventCatalog.AuditCompleted or HubEventCatalog.AuditFailed or
        HubEventCatalog.WafBlocks or HubEventCatalog.HostThrottle or HubEventCatalog.MatrixRowValidated or
        HubEventCatalog.EmbeddingProgress or HubEventCatalog.EmbeddingCompleted;

    private static long? PayloadAuditId(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("auditId", out var value) && value.TryGetInt64(out var id)
            ? id
            : null;
    }

    private static int PayloadInt(string payloadJson, string property)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty(property).GetInt32();
    }

    private static string? PayloadString(string payloadJson, string property)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }

    private sealed record ChannelEvent(int Seq, string Kind, string? Group, string EventName, string PayloadJson, DateTimeOffset At);

    /// <summary>Ordered recording transport at the locked IHubClientChannel seam.</summary>
    private sealed class RecordingHubChannel : IHubClientChannel
    {
        private readonly ConcurrentQueue<ChannelEvent> _events = new();
        private int _sequence;

        public IReadOnlyList<ChannelEvent> Snapshot() => _events.ToArray();

        public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(new ChannelEvent(Interlocked.Increment(ref _sequence), "all", null, eventName, payloadJson, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(new ChannelEvent(Interlocked.Increment(ref _sequence), "group", groupName, eventName, payloadJson, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(new ChannelEvent(Interlocked.Increment(ref _sequence), "join", groupName, connectionId, "{}", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(new ChannelEvent(Interlocked.Increment(ref _sequence), "leave", groupName, connectionId, "{}", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }
}
