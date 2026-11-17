using CrawlForge.Contracts.Enums;

namespace CrawlForge.Mcp;

/// <summary>
/// Audit-scoped lifecycle port for the MCP tool surface (FS-CRAWL-001 §11.3).
/// The locked Contracts expose engine-level services (<c>ICrawlEngine</c>,
/// <c>IAuditExecutionService</c>, <c>IScheduledAuditService</c>) but no
/// audit-scoped facade; the Host binds an adapter over those services at
/// composition time (phase-06), the same pattern as SemanticPersistence.
/// Every operation maps one-to-one onto the corresponding local API
/// operation with identical validation, limits, and lifecycle rules.
/// </summary>
public interface IAuditLifecyclePort
{
    /// <summary>Checkpoints and pauses a running audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new lifecycle state.</returns>
    Task<AuditLifecycleSnapshot> PauseAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Rehydrates and resumes a paused audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new lifecycle state.</returns>
    Task<AuditLifecycleSnapshot> ResumeAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Drains workers and finalizes partial data.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new lifecycle state.</returns>
    Task<AuditLifecycleSnapshot> CancelAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Triggers a follow-up analysis pass over the same dataset.</summary>
    /// <param name="auditId">Audit identifier to re-analyze.</param>
    /// <param name="refresh">Whether to refresh stored content first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new audit identifier.</returns>
    Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken cancellationToken = default);

    /// <summary>Reads live progress for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Progress snapshot.</returns>
    Task<AuditProgressSnapshot> GetProgressAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lifecycle state returned by pause/resume/cancel.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="Status">New run status.</param>
/// <param name="Phase">New coarse phase.</param>
public sealed record AuditLifecycleSnapshot(long AuditId, AuditStatus Status, AuditPhase Phase);

/// <summary>
/// Live progress snapshot for get-audit-progress.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="Status">Run status.</param>
/// <param name="Phase">Coarse phase.</param>
/// <param name="Discovered">URLs discovered so far.</param>
/// <param name="Fetched">URLs fetched so far.</param>
/// <param name="Analyzed">URLs analyzed so far.</param>
/// <param name="Queued">URLs still queued.</param>
/// <param name="PercentComplete">Completion percentage, when known.</param>
public sealed record AuditProgressSnapshot(
    long AuditId,
    AuditStatus Status,
    AuditPhase Phase,
    long Discovered,
    long Fetched,
    long Analyzed,
    long Queued,
    double? PercentComplete);
