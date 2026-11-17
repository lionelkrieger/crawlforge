namespace CrawlForge.Contracts.Api;

/// <summary>
/// Real-time hub abstraction (FS-CRAWL-001 §10.3): publishes events to the
/// shell over the loopback hub with throttling and a 2-second system-health
/// heartbeat. Event names are open strings; new events arrive additively.
/// No event carries identity, machine, or account dimensions (privacy invariant).
/// </summary>
public interface IRealtimeHub
{
    /// <summary>Publishes an event to all connected shell clients.</summary>
    /// <param name="hubEvent">Event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// One hub event.
/// </summary>
/// <param name="Name">Event name (see Constants.HubEventNames).</param>
/// <param name="PayloadJson">JSON payload.</param>
/// <param name="ThrottleKey">Optional throttle key coalescing rapid events.</param>
/// <param name="ThrottleInterval">Optional minimum interval between throttled publishes.</param>
public sealed record HubEvent(
    string Name,
    string PayloadJson,
    string? ThrottleKey = null,
    TimeSpan? ThrottleInterval = null);

/// <summary>
/// System health snapshot published every 2 seconds and served on demand
/// (REST: GET /api/system/health; FS-CRAWL-002 §7). Local technical data only.
/// </summary>
/// <param name="WorkingSetBytes">Process working set in bytes.</param>
/// <param name="GcHeapBytes">Managed GC heap size in bytes.</param>
/// <param name="WalSizeBytes">Write-ahead log size in bytes, when applicable.</param>
/// <param name="Timestamp">Snapshot timestamp (UTC).</param>
public sealed record SystemHealthSnapshot(
    long WorkingSetBytes,
    long GcHeapBytes,
    long? WalSizeBytes,
    DateTimeOffset Timestamp);
