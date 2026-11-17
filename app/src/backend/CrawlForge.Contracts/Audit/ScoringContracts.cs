using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Audit scoring service (FS-CRAWL-001 §8.1): computes per-category and
/// overall scores from findings, excluding suppressed findings. Scores are
/// comparable across audits and drive the score trend.
/// </summary>
public interface IAuditScoringService
{
    /// <summary>Computes category and overall scores for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Score drafts; a null category denotes the overall score.</returns>
    Task<IReadOnlyList<AuditScoreDraft>> ComputeAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A computed score before persistence (schema catalog: audit_scores).
/// </summary>
/// <param name="Category">Scored category; null for the overall score.</param>
/// <param name="Score">Score value (0–100).</param>
/// <param name="ErrorCount">Contributing error count.</param>
/// <param name="WarningCount">Contributing warning count.</param>
/// <param name="NoticeCount">Contributing notice count.</param>
/// <param name="PassCount">Contributing pass count.</param>
public sealed record AuditScoreDraft(
    AuditCategory? Category,
    double Score,
    int ErrorCount,
    int WarningCount,
    int NoticeCount,
    int PassCount);

/// <summary>
/// Finding suppression service (FS-CRAWL-001 §7.5): project-scoped, reversible
/// suppressions excluded from counts and scores.
/// </summary>
public interface IFindingSuppressionService
{
    /// <summary>Suppresses a rule key and/or finding scope within a project.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="request">Suppression target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted suppression state.</returns>
    Task<FindingSuppressionDto> SuppressAsync(long projectId, FindingSuppressionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses a suppression by its key.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="suppressionKey">Suppression key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a suppression was removed.</returns>
    Task<bool> UnsuppressAsync(long projectId, string suppressionKey, CancellationToken cancellationToken = default);

    /// <summary>Lists all suppressions of a project.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Project suppressions.</returns>
    Task<IReadOnlyList<FindingSuppressionDto>> ListAsync(long projectId, CancellationToken cancellationToken = default);

    /// <summary>Tests whether a finding is covered by a suppression.</summary>
    /// <param name="suppression">Suppression to test.</param>
    /// <param name="ruleKey">Finding rule key.</param>
    /// <param name="urlId">Finding URL reference, if any.</param>
    /// <returns>True when the suppression covers the finding.</returns>
    bool IsSuppressed(FindingSuppressionDto suppression, string ruleKey, long? urlId);
}

/// <summary>
/// A suppression request: rule key and/or finding scope.
/// </summary>
/// <param name="RuleKey">Optional rule key to suppress.</param>
/// <param name="UrlId">Optional URL scope of the suppression.</param>
/// <param name="FindingScope">Optional free-form finding scope descriptor.</param>
public sealed record FindingSuppressionRequest(
    string? RuleKey,
    long? UrlId,
    string? FindingScope);
