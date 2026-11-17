using CrawlForge.Contracts.Entities;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Local persistence abstraction for the bounded host-throttle adjustment
/// history (FS-CRAWL-002 §3.6). The default implementation rides the locked
/// generic repositories; tests may substitute fakes.
/// </summary>
public interface IHostThrottleEventStore
{
    /// <summary>Appends one adjustment event.</summary>
    /// <param name="throttleEvent">The event row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the row is persisted.</returns>
    Task AppendAsync(HostThrottleEventDto throttleEvent, CancellationToken cancellationToken = default);

    /// <summary>Reads the newest events for one (audit, host), newest first.</summary>
    /// <param name="auditId">Audit reference.</param>
    /// <param name="host">Target host.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bounded history slice.</returns>
    Task<IReadOnlyList<HostThrottleEventDto>> GetHistoryAsync(long auditId, string host, int limit, CancellationToken cancellationToken = default);

    /// <summary>Prunes the history of one (audit, host) to the newest N rows.</summary>
    /// <param name="auditId">Audit reference.</param>
    /// <param name="host">Target host.</param>
    /// <param name="keepLast">Number of newest rows to keep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when pruning is done.</returns>
    Task TrimAsync(long auditId, string host, int keepLast, CancellationToken cancellationToken = default);
}
