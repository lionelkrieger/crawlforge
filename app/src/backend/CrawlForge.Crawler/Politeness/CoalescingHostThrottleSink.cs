namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Coalesces typed host-throttle adjustments to at most one forwarded message
/// per second per host (FS-CRAWL-002 §3.7): the first adjustment in a window
/// forwards immediately; later adjustments within the window replace the
/// pending latest value, which <see cref="FlushPending"/> forwards when the
/// composition pump calls it. Timing uses the injected clock for determinism.
/// The Phase 05 hub adapter sits behind this sink; no hub wiring happens here.
/// </summary>
public sealed class CoalescingHostThrottleSink : IHostThrottleAdjustmentSink
{
    private readonly IHostThrottleAdjustmentSink _inner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastForwarded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostThrottleAdjustment> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the coalescing wrapper.</summary>
    /// <param name="inner">Sink that receives the coalesced adjustments.</param>
    /// <param name="timeProvider">Deterministic clock; null uses the system clock.</param>
    /// <param name="window">Coalescing window; null uses one second.</param>
    public CoalescingHostThrottleSink(IHostThrottleAdjustmentSink inner, TimeProvider? timeProvider = null, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _time = timeProvider ?? TimeProvider.System;
        _window = window ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc/>
    public void Emit(HostThrottleAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        HostThrottleAdjustment? forward = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_lastForwarded.TryGetValue(adjustment.Host, out var last) || now - last >= _window)
            {
                _lastForwarded[adjustment.Host] = now;
                _pending.Remove(adjustment.Host);
                forward = adjustment;
            }
            else
            {
                _pending[adjustment.Host] = adjustment;
            }
        }

        if (forward is not null)
        {
            _inner.Emit(forward);
        }
    }

    /// <summary>
    /// Forwards the latest pending adjustment of every host whose window has
    /// elapsed. Called by the composition pump; safe to call at any cadence.
    /// </summary>
    public void FlushPending()
    {
        List<HostThrottleAdjustment> due;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            due = [];
            foreach (var (host, adjustment) in _pending)
            {
                if (now - _lastForwarded[host] >= _window)
                {
                    due.Add(adjustment);
                }
            }

            foreach (var adjustment in due)
            {
                _pending.Remove(adjustment.Host);
                _lastForwarded[adjustment.Host] = now;
            }
        }

        foreach (var adjustment in due)
        {
            _inner.Emit(adjustment);
        }
    }
}
