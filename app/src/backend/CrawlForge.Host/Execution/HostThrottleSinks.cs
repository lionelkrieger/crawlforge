using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Constants;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Host.Execution;

/// <summary>
/// Per-audit composite adjustment sink (WP-06-01 reopen, criterion #36):
/// fans every typed AIMD adjustment out to the bounded-history recorder and
/// the coalesced hub adapter. Emission stays fast and non-blocking — the
/// controller emits inside its synchronization gate.
/// </summary>
public sealed class CompositeHostThrottleSink : IHostThrottleAdjustmentSink
{
    private readonly IHostThrottleAdjustmentSink[] _sinks;

    /// <summary>Creates the composite over the ordered inner sinks.</summary>
    /// <param name="sinks">Sinks receiving every adjustment.</param>
    public CompositeHostThrottleSink(params IHostThrottleAdjustmentSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    /// <inheritdoc />
    public void Emit(HostThrottleAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        foreach (var sink in _sinks)
        {
            sink.Emit(adjustment);
        }
    }
}

/// <summary>
/// Hub adapter for typed host-throttle adjustments (FS-CRAWL-002 §3.7):
/// publishes the locked <c>host-throttle</c> hub event with the full typed
/// payload. The realtime hub enforces the ≤1/s/host coalescing window; the
/// composition additionally wraps this adapter in
/// <see cref="CoalescingHostThrottleSink"/> for deterministic local pacing.
/// Emission is fire-and-forget: a local hub hiccup must never stall or fail
/// the crawl (the bounded history recorder is the durable evidence path).
/// </summary>
public sealed class HubHostThrottleSink : IHostThrottleAdjustmentSink
{
    private static readonly JsonSerializerOptions PayloadJson = ApiJsonOptions.Create();
    private readonly IRealtimeHub _hub;

    /// <summary>Creates the adapter over the locked realtime hub.</summary>
    /// <param name="hub">Loopback realtime hub.</param>
    public HubHostThrottleSink(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <inheritdoc />
    public void Emit(HostThrottleAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        // Wire names are locked by the frontend hub contract
        // (contracts/hub/events.ts HostThrottlePayload and contracts/api/hosts.ts):
        // rateBefore/After, concurrencyBefore/After, ttfbSampleMs,
        // rollingBaselineRate. The previous names matched no consumer and left
        // ttfbEwmaMs undefined in the run view (HOTFIX-01).
        var payload = JsonSerializer.Serialize(new
        {
            auditId = adjustment.AuditId,
            host = adjustment.Host,
            trigger = adjustment.Trigger,
            rateBefore = adjustment.PreviousRate,
            rateAfter = adjustment.NewRate,
            concurrencyBefore = adjustment.PreviousConcurrency,
            concurrencyAfter = adjustment.NewConcurrency,
            ttfbSampleMs = adjustment.TtfbSampleMilliseconds,
            rollingBaselineRate = adjustment.RollingBaselineMilliseconds ?? 0d,
            timestamp = adjustment.Timestamp
        }, PayloadJson);
        _ = PublishAsync(payload);
    }

    private async Task PublishAsync(string payload)
    {
        try
        {
            await _hub.PublishAsync(new HubEvent(HubEventNames.HostThrottle, payload)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Local hub only; emission must never break the crawl. The
            // recorder's persisted history remains the durable evidence.
        }
    }
}
