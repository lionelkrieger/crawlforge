using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Api.Metrics;
using CrawlForge.Contracts.Api;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// Hub core unit tests (FS-CRAWL-001 §10.3; FS-CRAWL-002 §7.2 + Appendix C):
/// spec cadences (≤1/s families, per-host coalescing), latest-wins batching,
/// per-audit routing, subscribe/unsubscribe, connection metrics, and the
/// 2-second system-health heartbeat (idle or running).
/// </summary>
public sealed class RealtimeHubServiceTests
{
    private readonly FakeHubChannel _channel = new();
    private readonly TestHealthSampler _sampler = new();
    private readonly MutableTimeProvider _time = new();

    private RealtimeHubService CreateService(IMetricsExposition? metrics = null) =>
        new(_channel, _sampler, _time, metrics);

    private static HubEvent Event(string name, string payload, string? throttleKey = null, TimeSpan? interval = null) =>
        new(name, payload, throttleKey, interval);

    private static string Progress(long auditId, long fetched) =>
        JsonSerializer.Serialize(new { auditId, counters = new { fetched }, phase = "crawling" });

    // ── Cadence: ≤1/s families ────────────────────────────────────────────

    [Fact]
    public async Task CrawlProgress_AtMostOncePerSecond_LatestWins()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 1)));
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 2)));
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 3)));
        Assert.Single(_channel.Snapshot());

        _time.Advance(TimeSpan.FromMilliseconds(500));
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 4)));
        Assert.Single(_channel.Snapshot()); // Still inside the window.

        _time.Advance(TimeSpan.FromMilliseconds(500)); // t=1s: flush fires.
        var sends = _channel.Snapshot();
        Assert.Equal(2, sends.Count);
        Assert.Contains("\"fetched\":4", sends[1].PayloadJson); // Latest payload won.
    }

    [Fact]
    public async Task CrawlProgress_DifferentAudits_ThrottleIndependently()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 1)));
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(200, 1)));
        Assert.Equal(2, _channel.Snapshot().Count);
    }

    [Theory]
    [InlineData(HubEventCatalog.WafBlocks)]
    [InlineData(HubEventCatalog.MatrixRowValidated)]
    [InlineData(HubEventCatalog.EmbeddingProgress)]
    [InlineData(HubEventCatalog.UrlBatch)]
    [InlineData(HubEventCatalog.FindingsUpdated)]
    public async Task AdditiveProgressFamilies_AtMostOncePerSecond(string eventName)
    {
        await using var service = CreateService();
        var payload = JsonSerializer.Serialize(new { auditId = 100 });
        await service.PublishAsync(Event(eventName, payload));
        await service.PublishAsync(Event(eventName, payload));
        Assert.Single(_channel.Snapshot());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _channel.Snapshot().Count);
    }

    [Fact]
    public async Task LogImportProgress_ThrottledPerImport()
    {
        await using var service = CreateService();
        var payload1 = JsonSerializer.Serialize(new { importId = 7, linesParsed = 10, phase = "parsing" });
        var payload2 = JsonSerializer.Serialize(new { importId = 8, linesParsed = 20, phase = "parsing" });
        await service.PublishAsync(Event(HubEventCatalog.LogImportProgress, payload1));
        await service.PublishAsync(Event(HubEventCatalog.LogImportProgress, payload1));
        await service.PublishAsync(Event(HubEventCatalog.LogImportProgress, payload2));
        Assert.Equal(2, _channel.Snapshot().Count); // Per-import keys.
    }

    [Fact]
    public async Task HostThrottle_CoalescedPerHost_NotAcrossHosts()
    {
        await using var service = CreateService();
        string Payload(string host, double rate) =>
            JsonSerializer.Serialize(new { auditId = 100, host, trigger = "ttfb", rateAfter = rate });

        await service.PublishAsync(Event(HubEventCatalog.HostThrottle, Payload("a.test", 1)));
        await service.PublishAsync(Event(HubEventCatalog.HostThrottle, Payload("b.test", 1)));
        Assert.Equal(2, _channel.Snapshot().Count);

        await service.PublishAsync(Event(HubEventCatalog.HostThrottle, Payload("a.test", 2)));
        await service.PublishAsync(Event(HubEventCatalog.HostThrottle, Payload("a.test", 3)));
        Assert.Equal(2, _channel.Snapshot().Count); // Coalesced for a.test.

        _time.Advance(TimeSpan.FromSeconds(1));
        var sends = _channel.Snapshot();
        Assert.Equal(3, sends.Count);
        Assert.Contains("\"rateAfter\":3", sends[2].PayloadJson);
    }

    [Fact]
    public async Task ExplicitThrottleMetadata_OverridesDefaults()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 1), "shared-key", TimeSpan.FromSeconds(5)));
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(200, 1), "shared-key", TimeSpan.FromSeconds(5)));
        Assert.Single(_channel.Snapshot()); // Same explicit key across audits.

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _channel.Snapshot().Count);
    }

    [Theory]
    [InlineData(HubEventCatalog.StageChanged)]
    [InlineData(HubEventCatalog.AuditCompleted)]
    [InlineData(HubEventCatalog.AuditFailed)]
    [InlineData(HubEventCatalog.BackendLifecycle)]
    [InlineData(HubEventCatalog.ScheduleFired)]
    [InlineData(HubEventCatalog.ScheduleRunCompleted)]
    [InlineData(HubEventCatalog.ExportDelivery)]
    [InlineData(HubEventCatalog.LogImportCompleted)]
    [InlineData(HubEventCatalog.EmbeddingCompleted)]
    [InlineData(HubEventCatalog.LocalNotification)]
    public async Task LifecycleEvents_EmitImmediately_EveryTime(string eventName)
    {
        await using var service = CreateService();
        var payload = JsonSerializer.Serialize(new { auditId = 100 });
        await service.PublishAsync(Event(eventName, payload));
        await service.PublishAsync(Event(eventName, payload));
        await service.PublishAsync(Event(eventName, payload));
        Assert.Equal(3, _channel.Snapshot().Count);
    }

    // ── Routing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditScopedEvents_RouteToPerAuditGroup()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, Progress(100, 1)));
        var send = Assert.Single(_channel.Snapshot());
        Assert.Equal("audit-100", send.Target);
    }

    [Theory]
    [InlineData(HubEventCatalog.BackendLifecycle)]
    [InlineData(HubEventCatalog.ScheduleFired)]
    [InlineData(HubEventCatalog.ScheduleRunCompleted)]
    [InlineData(HubEventCatalog.ExportDelivery)]
    [InlineData(HubEventCatalog.LogImportCompleted)]
    public async Task NonAuditChannelEvents_Broadcast(string eventName)
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(eventName, JsonSerializer.Serialize(new { auditId = 100 })));
        var send = Assert.Single(_channel.Snapshot());
        Assert.Null(send.Target);
    }

    [Fact]
    public async Task SystemHealth_WithAuditId_StillBroadcasts()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.SystemHealth, JsonSerializer.Serialize(new { auditId = 100 })));
        Assert.Null(Assert.Single(_channel.Snapshot()).Target);
    }

    [Fact]
    public async Task MalformedPayload_BroadcastsWithoutCrash()
    {
        await using var service = CreateService();
        await service.PublishAsync(Event(HubEventCatalog.CrawlProgress, "{not json"));
        Assert.Null(Assert.Single(_channel.Snapshot()).Target);
    }

    // ── Subscribe / unsubscribe ───────────────────────────────────────────

    [Fact]
    public async Task SubscribeUnsubscribe_ManageGroupMembership()
    {
        await using var service = CreateService();
        await service.SubscribeAsync("conn-1", [100, 200, 100]);
        Assert.Equal(new HashSet<string> { "audit-100", "audit-200" }, _channel.Groups["conn-1"]);

        await service.UnsubscribeAsync("conn-1", [100]);
        Assert.Equal(new HashSet<string> { "audit-200" }, _channel.Groups["conn-1"]);
    }

    // ── Connections + heartbeat ───────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_Every2Seconds_WhileConnected_Idle()
    {
        await using var service = CreateService();
        await service.ConnectAsync("conn-1");

        _time.Advance(TimeSpan.FromSeconds(2));
        var sends = _channel.Snapshot();
        var heartbeat = Assert.Single(sends);
        Assert.Equal(HubEventCatalog.SystemHealth, heartbeat.EventName);
        Assert.Null(heartbeat.Target); // Independent of per-audit subscriptions.

        using var payload = JsonDocument.Parse(heartbeat.PayloadJson);
        Assert.Equal(1_000_000, payload.RootElement.GetProperty("backendWorkingSetBytes").GetInt64());
        Assert.Equal(500_000, payload.RootElement.GetProperty("managedHeapBytes").GetInt64());
        Assert.Equal(3, payload.RootElement.GetProperty("gcGen0Count").GetInt32());
        Assert.Equal(2, payload.RootElement.GetProperty("gcGen1Count").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("gcGen2Count").GetInt32());
        Assert.Equal(4096, payload.RootElement.GetProperty("sqliteWalBytes").GetInt64());
        Assert.False(payload.RootElement.TryGetProperty("auditId", out var auditId) && auditId.ValueKind != JsonValueKind.Null);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, _channel.Snapshot().Count);
    }

    [Fact]
    public async Task Heartbeat_IncludesRunningAuditId()
    {
        _sampler.Payload = _sampler.Payload with { AuditId = 77 };
        await using var service = CreateService();
        await service.ConnectAsync("conn-1");
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains("\"auditId\":77", Assert.Single(_channel.Snapshot()).PayloadJson);
    }

    [Fact]
    public async Task Heartbeat_NullWal_WhenExternalProvider()
    {
        _sampler.Payload = _sampler.Payload with { SqliteWalBytes = null };
        await using var service = CreateService();
        await service.ConnectAsync("conn-1");
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains("\"sqliteWalBytes\":null", Assert.Single(_channel.Snapshot()).PayloadJson);
    }

    [Fact]
    public async Task Heartbeat_NotEmitted_WithoutConnections()
    {
        await using var service = CreateService();
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(_channel.Snapshot());
    }

    [Fact]
    public async Task Heartbeat_StopsAfterLastDisconnect()
    {
        await using var service = CreateService();
        await service.ConnectAsync("conn-1");
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(_channel.Snapshot());

        await service.DisconnectAsync("conn-1");
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(_channel.Snapshot());
        Assert.Equal(0, service.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionGauge_RecordedIntoMetrics()
    {
        var metrics = new PrometheusMetricsExposition();
        await using var service = CreateService(metrics);
        await service.ConnectAsync("conn-1");
        await service.ConnectAsync("conn-2");
        Assert.Equal(2, service.ConnectionCount);
        Assert.Contains("hub_connections 2", metrics.Render());

        await service.DisconnectAsync("conn-1");
        Assert.Contains("hub_connections 1", metrics.Render());
    }

    [Fact]
    public async Task PublishAfterDispose_Throws()
    {
        var service = CreateService();
        await service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.PublishAsync(Event(HubEventCatalog.StageChanged, "{}")));
    }
}
