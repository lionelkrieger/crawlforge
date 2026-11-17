using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Priority frontier with per-host sub-queues (FS-CRAWL-001 §5.3). Entries are
/// dequeued by priority while respecting per-host politeness; the frontier is
/// serializable for checkpointing (§5.7).
/// </summary>
public interface IPriorityFrontier
{
    /// <summary>Number of entries currently queued.</summary>
    long Count { get; }

    /// <summary>Enqueues a discovered URL.</summary>
    /// <param name="entry">Frontier entry to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry is queued.</returns>
    ValueTask EnqueueAsync(FrontierEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Dequeues the next eligible entry, or null when empty.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next entry, or null.</returns>
    ValueTask<FrontierEntry?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a serializable snapshot of the frontier.</summary>
    /// <returns>The frontier snapshot.</returns>
    FrontierSnapshot CreateSnapshot();

    /// <summary>Restores the frontier from a snapshot.</summary>
    /// <param name="snapshot">Previously captured snapshot.</param>
    void Restore(FrontierSnapshot snapshot);
}

/// <summary>
/// One queued URL in the frontier.
/// </summary>
/// <param name="Url">Canonical URL.</param>
/// <param name="Host">Host used for per-host sub-queue routing.</param>
/// <param name="Depth">Link depth from the seeds.</param>
/// <param name="Priority">Scheduling priority (higher dequeues first).</param>
/// <param name="DiscoverySource">How the URL was discovered.</param>
public sealed record FrontierEntry(
    string Url,
    string Host,
    int Depth,
    double Priority,
    DiscoverySource DiscoverySource);

/// <summary>
/// Serializable frontier snapshot stored in crawl checkpoints.
/// </summary>
/// <param name="Entries">Queued entries at snapshot time.</param>
public sealed record FrontierSnapshot(IReadOnlyList<FrontierEntry> Entries);

/// <summary>
/// Persists and restores crawl resume state (schema catalog: crawl_checkpoints).
/// Checkpoints are written on pause and periodically during runs.
/// </summary>
public interface ICrawlCheckpointService
{
    /// <summary>Saves a checkpoint for an audit.</summary>
    /// <param name="checkpoint">Checkpoint payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the checkpoint is durable.</returns>
    Task SaveAsync(CrawlCheckpointDto checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Loads the latest checkpoint of an audit, if any.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest checkpoint, or null.</returns>
    Task<CrawlCheckpointDto?> LoadLatestAsync(long auditId, CancellationToken cancellationToken = default);
}
