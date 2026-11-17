using System.Collections.Concurrent;
using CrawlForge.Contracts.Entities;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Records typed host-throttle adjustments into the bounded
/// <c>host_throttle_events</c> history (FS-CRAWL-002 §3.6). Emission is
/// non-blocking (the controller emits inside its gate): adjustments buffer in
/// memory and <see cref="FlushAsync"/> persists them, pruning each (audit,
/// host) history to the newest N rows (default 500, configurable). The trigger
/// code rides the locked DTO's <c>Reason</c> field; the richer telemetry
/// payload stays on the typed emission surface for the Phase 05 hub adapter.
/// </summary>
public sealed class HostThrottleEventRecorder : IHostThrottleAdjustmentSink
{
    private readonly IHostThrottleEventStore _store;
    private readonly int _keepLastPerHost;
    private readonly ConcurrentQueue<HostThrottleAdjustment> _pending = new();

    /// <summary>Creates the recorder over the local history store.</summary>
    /// <param name="store">Bounded history store.</param>
    /// <param name="keepLastPerHost">Newest rows retained per (audit, host); default 500 (§3.6).</param>
    public HostThrottleEventRecorder(IHostThrottleEventStore store, int keepLastPerHost = 500)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (keepLastPerHost < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLastPerHost), keepLastPerHost, "The retained history size must be at least 1.");
        }

        _store = store;
        _keepLastPerHost = keepLastPerHost;
    }

    /// <summary>Number of adjustments buffered but not yet persisted.</summary>
    public int PendingCount => _pending.Count;

    /// <inheritdoc/>
    public void Emit(HostThrottleAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        _pending.Enqueue(adjustment);
    }

    /// <summary>
    /// Persists every buffered adjustment and prunes each affected (audit,
    /// host) history to the configured bound.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the buffer is drained.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (_pending.TryDequeue(out var adjustment))
        {
            var row = new HostThrottleEventDto(
                Id: 0,
                AuditId: adjustment.AuditId,
                Host: adjustment.Host,
                PreviousRate: adjustment.PreviousRate,
                NewRate: adjustment.NewRate,
                Reason: adjustment.Trigger,
                Timestamp: adjustment.Timestamp);
            await _store.AppendAsync(row, cancellationToken).ConfigureAwait(false);
            await _store.TrimAsync(adjustment.AuditId, adjustment.Host, _keepLastPerHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
