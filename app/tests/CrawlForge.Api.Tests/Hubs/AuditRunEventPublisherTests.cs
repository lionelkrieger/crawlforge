using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// WP-09-06 G05: the Api-side url-batch / findings-updated publication surface.
/// Locks the frozen payload shapes (events.ts UrlBatchPayload /
/// FindingsUpdatedPayload, guards.ts), string EntityIds (G-G), per-audit group
/// routing through the real hub service, and LOSSLESS delivery (explicit zero
/// throttle interval — cadence is the engine producer's responsibility).
/// </summary>
public sealed class AuditRunEventPublisherTests
{
    private readonly FakeHubChannel _channel = new();
    private readonly TestHealthSampler _sampler = new();
    private readonly MutableTimeProvider _time = new();

    private RealtimeHubService CreateService() => new(_channel, _sampler, _time, metrics: null);

    [Fact]
    public async Task UrlBatch_PublishesFrozenShape_WithStringEntityIds()
    {
        var hub = new CapturingHub();
        var publisher = new AuditRunEventPublisher(hub);

        await publisher.PublishUrlBatchAsync(7, new[]
        {
            new UrlBatchEntry("11", "https://example.com/a", "2xx"),
            new UrlBatchEntry("12", "https://example.com/b", "4xx", "robots-disallow", "cloudflare"),
        });

        var hubEvent = Assert.Single(hub.Events);
        Assert.Equal(HubEventCatalog.UrlBatch, hubEvent.Name);
        Assert.Equal(TimeSpan.Zero, hubEvent.ThrottleInterval); // lossless: no latest-wins coalescing
        using var payload = JsonDocument.Parse(hubEvent.PayloadJson);
        Assert.Equal("7", payload.RootElement.GetProperty("auditId").GetString());
        var urls = payload.RootElement.GetProperty("urls");
        Assert.Equal(2, urls.GetArrayLength());
        Assert.Equal("11", urls[0].GetProperty("urlId").GetString());
        Assert.Equal("https://example.com/a", urls[0].GetProperty("canonicalUrl").GetString());
        Assert.Equal("2xx", urls[0].GetProperty("statusClass").GetString());
        Assert.False(urls[0].TryGetProperty("blockReason", out _)); // null annotations omitted
        Assert.Equal("robots-disallow", urls[1].GetProperty("blockReason").GetString());
        Assert.Equal("cloudflare", urls[1].GetProperty("wafProvider").GetString());
    }

    [Fact]
    public async Task UrlBatch_UnknownStatusClass_Throws()
    {
        var publisher = new AuditRunEventPublisher(new CapturingHub());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishUrlBatchAsync(7, new[] { new UrlBatchEntry("11", "https://example.com/a", "blocked") }));
    }

    [Fact]
    public async Task FindingsUpdated_PublishesDeltas_WithStringAuditId()
    {
        var hub = new CapturingHub();
        var publisher = new AuditRunEventPublisher(hub);

        await publisher.PublishFindingsUpdatedAsync(
            7,
            new Dictionary<string, int> { ["high"] = 2, ["low"] = 1 },
            new Dictionary<string, int> { ["Content"] = 3 });

        var hubEvent = Assert.Single(hub.Events);
        Assert.Equal(HubEventCatalog.FindingsUpdated, hubEvent.Name);
        Assert.Equal(TimeSpan.Zero, hubEvent.ThrottleInterval);
        using var payload = JsonDocument.Parse(hubEvent.PayloadJson);
        Assert.Equal("7", payload.RootElement.GetProperty("auditId").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("severityDeltas").GetProperty("high").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("severityDeltas").GetProperty("low").GetInt32());
        Assert.Equal(3, payload.RootElement.GetProperty("categoryDeltas").GetProperty("Content").GetInt32());
    }

    [Fact]
    public async Task UrlBatch_ThroughHubService_RoutesToPerAuditGroup_Lossless()
    {
        await using var service = CreateService();
        var publisher = new AuditRunEventPublisher(service);

        // Two flushes inside one second: BOTH must be delivered (coalescing
        // would drop a whole batch of URL rows).
        await publisher.PublishUrlBatchAsync(7, new[] { new UrlBatchEntry("11", "https://example.com/a", "2xx") });
        await publisher.PublishUrlBatchAsync(7, new[] { new UrlBatchEntry("12", "https://example.com/b", "3xx") });

        var sends = _channel.Snapshot();
        Assert.Equal(2, sends.Count);
        Assert.All(sends, send =>
        {
            Assert.Equal("audit-7", send.Target);
            Assert.Equal(HubEventCatalog.UrlBatch, send.EventName);
        });
        Assert.Contains("\"urlId\":\"11\"", sends[0].PayloadJson);
        Assert.Contains("\"urlId\":\"12\"", sends[1].PayloadJson);
    }

    [Fact]
    public async Task FindingsUpdated_ThroughHubService_RoutesToPerAuditGroup_Lossless()
    {
        await using var service = CreateService();
        var publisher = new AuditRunEventPublisher(service);

        await publisher.PublishFindingsUpdatedAsync(7, new Dictionary<string, int> { ["high"] = 1 }, new Dictionary<string, int>());
        await publisher.PublishFindingsUpdatedAsync(7, new Dictionary<string, int> { ["high"] = 2 }, new Dictionary<string, int>());

        var sends = _channel.Snapshot();
        Assert.Equal(2, sends.Count); // incremental deltas must not be coalesced away
        Assert.All(sends, send =>
        {
            Assert.Equal("audit-7", send.Target);
            Assert.Equal(HubEventCatalog.FindingsUpdated, send.EventName);
        });
        Assert.Contains("\"auditId\":\"7\"", sends[0].PayloadJson);
    }

    [Fact]
    public async Task PublisherPayloads_PassThroughNormalizerUnchanged()
    {
        // The hub boundary normalizer must not alter already-wire-shaped
        // publisher payloads (string ids stay strings; no counters present).
        var hub = new CapturingHub();
        var publisher = new AuditRunEventPublisher(hub);
        await publisher.PublishUrlBatchAsync(7, new[] { new UrlBatchEntry("11", "https://example.com/a", "2xx") });
        var original = Assert.Single(hub.Events).PayloadJson;

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.UrlBatch, original);

        Assert.Equal(original, normalized);
    }

    /// <summary>Capturing hub double (records raw published events).</summary>
    private sealed class CapturingHub : IRealtimeHub
    {
        public List<HubEvent> Events { get; } = [];

        public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(hubEvent);
            return Task.CompletedTask;
        }
    }
}
