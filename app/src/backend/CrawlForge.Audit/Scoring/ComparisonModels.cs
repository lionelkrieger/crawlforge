using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// One finding participating in a cross-audit comparison. Identity across
/// audits is (rule key, canonical URL) because URL surrogate keys differ
/// between audits; site-wide findings carry a null canonical URL.
/// </summary>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="Category">Taxonomy category.</param>
/// <param name="Severity">Finding severity.</param>
/// <param name="CanonicalUrl">Canonical URL, or null for site-wide findings.</param>
/// <param name="UrlId">URL surrogate key within the owning audit.</param>
public sealed record FindingComparisonEntry(
    string RuleKey,
    AuditCategory Category,
    FindingSeverity Severity,
    string? CanonicalUrl,
    long? UrlId);

/// <summary>
/// One metric delta between two audits, keyed by (metric key, dimensions).
/// A null base/target value marks a metric present in only one audit; the
/// delta is null unless both values exist.
/// </summary>
/// <param name="MetricKey">Stable metric key.</param>
/// <param name="DimensionsJson">Dimension payload identifying the series.</param>
/// <param name="BaseValue">Value in the base audit, when present.</param>
/// <param name="TargetValue">Value in the target audit, when present.</param>
/// <param name="Delta">Target minus base, when both exist.</param>
public sealed record MetricDelta(
    string MetricKey,
    string DimensionsJson,
    double? BaseValue,
    double? TargetValue,
    double? Delta);

/// <summary>
/// One score delta between two audits. A null category denotes the overall
/// score; null base/target values mark a score present in only one audit.
/// </summary>
/// <param name="Category">Scored category, or null for the overall score.</param>
/// <param name="BaseScore">Score in the base audit, when present.</param>
/// <param name="TargetScore">Score in the target audit, when present.</param>
/// <param name="Delta">Target minus base, when both exist.</param>
public sealed record ScoreDelta(
    AuditCategory? Category,
    double? BaseScore,
    double? TargetScore,
    double? Delta);

/// <summary>
/// Cross-audit comparison read model (FS-CRAWL-001 §8.2): new and resolved
/// findings, metric deltas, and score deltas between a base and a target
/// audit. Computed fully offline from stored aggregates and findings;
/// suppressed findings are excluded from the default comparison, consistent
/// with default counts and scoring (§7.5).
/// </summary>
/// <param name="BaseAuditId">Earlier (base) audit reference.</param>
/// <param name="TargetAuditId">Later (target) audit reference.</param>
/// <param name="NewFindings">Findings present in the target but not the base.</param>
/// <param name="ResolvedFindings">Findings present in the base but not the target.</param>
/// <param name="MetricDeltas">Metric deltas keyed by (metric key, dimensions).</param>
/// <param name="ScoreDeltas">Score deltas per category plus overall.</param>
public sealed record AuditComparisonResult(
    long BaseAuditId,
    long TargetAuditId,
    IReadOnlyList<FindingComparisonEntry> NewFindings,
    IReadOnlyList<FindingComparisonEntry> ResolvedFindings,
    IReadOnlyList<MetricDelta> MetricDeltas,
    IReadOnlyList<ScoreDelta> ScoreDeltas);
