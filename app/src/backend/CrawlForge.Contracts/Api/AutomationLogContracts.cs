using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Api;

/// <summary>
/// Local automation action log (FS-CRAWL-001 §11; schema catalog:
/// automation_action_log): records every local agent invocation. The log is
/// user-viewable and purgeable, pruned by retention, and carries no identity
/// dimensions (privacy invariant).
/// </summary>
public interface IAutomationActionLog
{
    /// <summary>Appends an invocation record.</summary>
    /// <param name="entry">Invocation record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the record is durable.</returns>
    Task LogAsync(AutomationActionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Lists invocation records with pagination.</summary>
    /// <param name="query">Pagination, filter, and sort specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of records.</returns>
    Task<PagedResult<AutomationActionLogDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Purges all invocation records (user action).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of records purged.</returns>
    Task<int> PurgeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One automation invocation record before persistence.
/// </summary>
/// <param name="Transport">Transport the invocation arrived on.</param>
/// <param name="TargetName">Tool/resource name invoked.</param>
/// <param name="ArgumentsSummary">Bounded argument summary.</param>
/// <param name="Outcome">Invocation outcome.</param>
public sealed record AutomationActionEntry(
    AutomationTransportKind Transport,
    string TargetName,
    string ArgumentsSummary,
    AutomationOutcome Outcome);
