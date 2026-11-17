using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 — full-stack AIMD host-throttle proof (FS-CRAWL-002 §3.5/§3.6/§3.7)
/// after the composite-sink fix: a live crawl under HTTP 429 pressure must
/// (1) persist bounded host_throttle_events history with the locked trigger
/// codes and multiplicative decrease, (2) emit coalesced host-throttle hub
/// events on the audit channel with the full typed payload, and (3) write the
/// audit-completion metric summary under the locked host-throttle key.
/// </summary>
public sealed class AimdHostThrottleE2ETests
{
    [Fact]
    public async Task LiveAudit_429Pressure_PersistsHistory_EmitsHubEvents_WritesMetricSummary()
    {
        // Origin: every content page answers 429 (Retry-After: 1) for its first
        // two requests, then recovers — deterministic AIMD pressure.
        var counters = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        OriginResponse Pressured(string path, string title)
        {
            var attempt = counters.AddOrUpdate(path, 1, (_, count) => count + 1);
            return attempt <= 2
                ? OriginResponse.Status(429, "<html><body>Slow down.</body></html>").WithHeader("Retry-After", "1")
                : OriginResponse.Html(FixturePages.Page(title, "<p>Recovered page content for analysis.</p>"));
        }

        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "AIMD index", "<p>AIMD pressure fixture.</p>" +
                FixturePages.Links(["/p/1", "/p/2", "/p/3", "/p/4", "/p/5", "/p/6"]))));
            for (var i = 1; i <= 6; i++)
            {
                var path = $"/p/{i}";
                routes.Map(path, _ => Pressured(path, $"AIMD page {i}"));
            }
        });

        var channel = new RecordingHubChannel();
        await using var backend = await E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(180));

        // (1) Durable history: host_throttle_events rows with locked triggers.
        var history = await backend.ListByAuditAsync<HostThrottleEventDto>(auditId);
        Assert.NotEmpty(history);
        Assert.All(history, e => Assert.Contains("127.0.0.1", e.Host));
        var decreases = history.Where(e => e.Reason == "http-429").ToList();
        Assert.NotEmpty(decreases);
        // Multiplicative decrease clamps at the rate floor (Math.Max(beta*rate,
        // MinRate)): rows above the floor strictly decrease; rows at the floor
        // stay pinned to it.
        Assert.All(decreases, e => Assert.True(e.NewRate <= e.PreviousRate,
            $"http-429 adjustment must not increase rate: {e.PreviousRate} -> {e.NewRate}"));
        Assert.Contains(decreases, e => e.NewRate < e.PreviousRate);
        var floor = decreases.Min(e => e.NewRate);
        Assert.All(decreases.Where(e => e.NewRate == e.PreviousRate),
            e => Assert.Equal(floor, e.NewRate));
        Assert.All(history, e => Assert.True(e.PreviousRate > 0 && e.NewRate > 0));
        // Any additive increases must actually increase.
        Assert.All(history.Where(e => e.Reason == "additive-increase"),
            e => Assert.True(e.NewRate > e.PreviousRate));

        // (2) Hub: coalesced host-throttle events on the audit channel with the
        // full typed payload; ≤1/s per host.
        var hubEvents = channel.Snapshot()
            .Where(e => e.EventName == "host-throttle" && e.Group == $"audit-{auditId}")
            .ToList();
        Assert.NotEmpty(hubEvents);
        using (var first = JsonDocument.Parse(hubEvents[0].PayloadJson))
        {
            var payload = first.RootElement;
            Assert.Equal(auditId, payload.GetProperty("auditId").GetInt64());
            Assert.Contains("127.0.0.1", payload.GetProperty("host").GetString());
            Assert.False(string.IsNullOrEmpty(payload.GetProperty("trigger").GetString()));
            Assert.True(payload.GetProperty("previousRate").GetDouble() > 0);
            Assert.True(payload.GetProperty("newRate").GetDouble() > 0);
            Assert.True(payload.TryGetProperty("previousConcurrency", out _));
            Assert.True(payload.TryGetProperty("newConcurrency", out _));
            Assert.True(payload.TryGetProperty("timestamp", out _));
        }
        Assert.Contains(hubEvents, e =>
        {
            using var document = JsonDocument.Parse(e.PayloadJson);
            return document.RootElement.GetProperty("trigger").GetString() == "http-429";
        });
        for (var i = 1; i < hubEvents.Count; i++)
        {
            Assert.True(hubEvents[i].At - hubEvents[i - 1].At >= TimeSpan.FromSeconds(0.9),
                "host-throttle hub events violated the ≤1/s/host coalescing window.");
        }

        // (3) Completion metric summary under the locked host-throttle key.
        var metrics = await backend.ListByAuditAsync<MetricSummaryDto>(auditId);
        var summary = Assert.Single(metrics, m => m.MetricKey == "host-throttle");
        Assert.True(summary.Value > 0, "host-throttle summary must count the adjustments.");
        using var dimensions = JsonDocument.Parse(summary.DimensionsJson);
        var root = dimensions.RootElement;
        Assert.True(root.GetProperty("hostCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("adjustedHostCount").GetInt32() >= 1);
        var hosts = root.GetProperty("hosts").EnumerateArray().ToList();
        Assert.NotEmpty(hosts);
        Assert.Contains(hosts, h =>
            h.GetProperty("adjustments").GetInt64() > 0 &&
            h.GetProperty("host").GetString()!.Contains("127.0.0.1"));

        // REST explorer surface conforms (history join over live state).
        var hostsResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/hosts?includeHistory=true");
        Assert.True(hostsResponse.IsSuccessStatusCode, await hostsResponse.Content.ReadAsStringAsync());
        var hostsEnvelope = await WireJson.EnvelopeAsync(hostsResponse);
        Assert.True(hostsEnvelope.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task HostOverride_ValidationAndNotFound_UseTheEnvelope()
    {
        await using var backend = await E2EBackend.StartAsync();

        var unknownAudit = await backend.Http.PostAsJsonAsync("/api/audits/999999/hosts/127.0.0.1/override",
            new { rate = 2.0 });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownAudit.StatusCode);
        var unknownEnvelope = await WireJson.EnvelopeAsync(unknownAudit);
        Assert.Equal("not-found", unknownEnvelope.GetProperty("error").GetProperty("code").GetString());

        var unknownHosts = await backend.Http.GetAsync("/api/audits/999999/hosts");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownHosts.StatusCode);
    }

    // ── recording transport (same seam as the hub sequencing suite) ────────

    private sealed record ChannelEvent(int Seq, string Kind, string? Group, string EventName, string PayloadJson, DateTimeOffset At);

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
