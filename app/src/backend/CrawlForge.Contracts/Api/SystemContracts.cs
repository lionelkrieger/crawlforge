// CrawlForge.Contracts — additive amendment WP-05-A1 (orchestrator-applied, phase-05).
// Requested by cf-wp-01-01 (WP-05-01) to bind the system REST surface to the
// WP-05-02 hub/metrics implementation without sibling coupling. Additive only:
// no locked member is changed. Host binds the implementations (phase-06).

namespace CrawlForge.Contracts.Api;

/// <summary>
/// Additive (WP-05-A1): on-demand system health sampling for the REST surface
/// (GET /api/system/health) and the 2-second hub heartbeat. Local technical
/// data only — no identity, no remote resolution.
/// </summary>
public interface ISystemHealthService
{
    /// <summary>
    /// Samples the current process health: working set, managed GC heap, and
    /// SQLite WAL size when applicable. Sampling is in-process and non-blocking.
    /// </summary>
    /// <returns>A fresh <see cref="SystemHealthSnapshot"/> with a UTC timestamp.</returns>
    SystemHealthSnapshot GetSnapshot();
}

/// <summary>
/// Additive (WP-05-A1): the compact action behind POST /api/system/compact
/// (criterion #41). Implementations perform GC compact/trim, a SQLite WAL
/// TRUNCATE checkpoint, and a working-set trim, and MUST enforce the spec
/// guards: a minimum interval of 10 seconds between compactions and refusal
/// while an audit is running. Guard violations throw
/// <see cref="InvalidOperationException"/> (the REST layer maps this to a
/// conflict response per the error contract).
/// </summary>
public interface ICompactService
{
    /// <summary>
    /// Runs one compact action and returns before/after health snapshots so
    /// the caller can present reclaimed memory and WAL bytes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Before/after snapshots and the UTC completion timestamp.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the rate limit or the audit-running guard refuses the action.
    /// </exception>
    Task<CompactResult> CompactAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive (WP-05-A1): outcome of one compact action. Reclaimed working-set
/// and WAL bytes are derivable from the two snapshots
/// (<c>Before.WorkingSetBytes - After.WorkingSetBytes</c>,
/// <c>Before.WalSizeBytes - After.WalSizeBytes</c>).
/// WP-06-A1 (additive): <paramref name="ActionsPerformed"/> added to satisfy the
/// frozen TS SystemCompactResponse wire shape {before, after, actionsPerformed};
/// CompletedAt remains as an additive extra. Null normalizes to an empty list
/// at the wire boundary.
/// </summary>
/// <param name="Before">Health snapshot taken immediately before compacting.</param>
/// <param name="After">Health snapshot taken immediately after compacting.</param>
/// <param name="CompletedAt">UTC completion timestamp (additive wire extra).</param>
/// <param name="ActionsPerformed">
/// Human-readable labels of the actions performed (e.g. "gc-compact",
/// "wal-checkpoint-truncate", "working-set-trim"), in execution order.
/// </param>
public sealed record CompactResult(
    SystemHealthSnapshot Before,
    SystemHealthSnapshot After,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string>? ActionsPerformed = null);
