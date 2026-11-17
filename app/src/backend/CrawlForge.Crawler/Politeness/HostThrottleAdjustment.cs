namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// One host-throttle adjustment, emitted on every rate/concurrency change
/// (FS-CRAWL-002 §3.7): audit id, host, trigger code, rate and concurrency
/// before/after, the TTFB sample, and the rolling baseline at event time.
/// This is the typed emission surface only — the SignalR hub consumes it in
/// Phase 05 (coalesced to at most one message per second per host); no hub
/// wiring happens here.
/// </summary>
/// <param name="AuditId">Audit reference.</param>
/// <param name="Host">Target host.</param>
/// <param name="Trigger">Trigger code from <see cref="HostThrottleTriggerCodes"/>.</param>
/// <param name="PreviousRate">Rate before the adjustment (requests/second).</param>
/// <param name="NewRate">Rate after the adjustment (requests/second).</param>
/// <param name="PreviousConcurrency">Per-host concurrency before the adjustment.</param>
/// <param name="NewConcurrency">Per-host concurrency after the adjustment.</param>
/// <param name="TtfbSampleMilliseconds">TTFB sample that triggered the event, when applicable.</param>
/// <param name="RollingBaselineMilliseconds">Rolling EWMA TTFB baseline at event time.</param>
/// <param name="Timestamp">Event timestamp (UTC).</param>
public sealed record HostThrottleAdjustment(
    long AuditId,
    string Host,
    string Trigger,
    double PreviousRate,
    double NewRate,
    int PreviousConcurrency,
    int NewConcurrency,
    double? TtfbSampleMilliseconds,
    double? RollingBaselineMilliseconds,
    DateTimeOffset Timestamp);

/// <summary>
/// Sink for typed host-throttle adjustments. Implementations must be fast and
/// non-blocking: the controller emits inside its synchronization gate. The
/// bounded-history recorder buffers and flushes asynchronously; the Phase 05
/// hub adapter coalesces and broadcasts.
/// </summary>
public interface IHostThrottleAdjustmentSink
{
    /// <summary>Accepts one adjustment event.</summary>
    /// <param name="adjustment">The adjustment payload.</param>
    void Emit(HostThrottleAdjustment adjustment);
}
