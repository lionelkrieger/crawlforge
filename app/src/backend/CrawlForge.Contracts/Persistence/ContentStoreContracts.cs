using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Persistence;

/// <summary>
/// Compressed content-artifact store (FS-CRAWL-001 §9.6): bodies, screenshots,
/// and rendered DOM snapshots addressed by storage key, indexed by
/// content_artifacts. Streaming keeps memory bounded at scale.
/// </summary>
public interface IContentStore
{
    /// <summary>Stores an artifact and returns its index row.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="kind">Artifact kind.</param>
    /// <param name="content">Artifact content stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted artifact index row.</returns>
    Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens an artifact for reading.</summary>
    /// <param name="storageKey">Storage key from the index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable stream, or null when absent. The caller owns the stream.</returns>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes an artifact.</summary>
    /// <param name="storageKey">Storage key from the index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the artifact was deleted.</returns>
    Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Retention pruner (FS-CRAWL-001 §9.5): transactionally prunes audit-scoped
/// data; configuration tables are never pruned.
/// </summary>
public interface IRetentionPruner
{
    /// <summary>Prunes data according to a retention policy.</summary>
    /// <param name="policy">Retention policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Pruning counters.</returns>
    Task<RetentionPruneResult> PruneAsync(RetentionPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>
/// A retention policy. Null fields are unconstrained.
/// </summary>
/// <param name="AuditId">Restrict pruning to one audit, when set.</param>
/// <param name="OlderThan">Prune audit-scoped data older than this timestamp.</param>
/// <param name="KeepLastAudits">Keep at least this many recent audits per project.</param>
public sealed record RetentionPolicy(
    long? AuditId = null,
    DateTimeOffset? OlderThan = null,
    int? KeepLastAudits = null);

/// <summary>
/// Retention pruning result.
/// </summary>
/// <param name="DeletedRowCount">Total rows pruned.</param>
/// <param name="PrunedTables">Names of the tables pruned.</param>
public sealed record RetentionPruneResult(int DeletedRowCount, IReadOnlyList<string> PrunedTables);
