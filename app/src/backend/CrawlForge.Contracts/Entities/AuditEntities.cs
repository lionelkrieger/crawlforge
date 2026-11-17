using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Audit judgment (schema catalog: findings). Site-wide findings carry a null
/// URL reference. New rule keys and categories arrive additively
/// (see Constants.FindingRuleKeys).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="Category">Taxonomy category.</param>
/// <param name="Severity">Finding severity.</param>
/// <param name="UrlId">URL reference; null for site-wide findings.</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
/// <param name="RecommendationKey">Optional recommendation catalog key.</param>
/// <param name="IsSuppressed">Whether a project suppression covers this finding.</param>
public sealed record FindingDto(
    long Id,
    long AuditId,
    string RuleKey,
    AuditCategory Category,
    FindingSeverity Severity,
    long? UrlId,
    string EvidenceJson,
    string? RecommendationKey,
    bool IsSuppressed);

/// <summary>
/// Precomputed aggregate (schema catalog: metric_summaries). Powers dashboards
/// and charts; new metric keys arrive additively (see Constants.MetricKeys).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="MetricKey">Stable metric key.</param>
/// <param name="DimensionsJson">Dimension payload.</param>
/// <param name="Value">Numeric value.</param>
public sealed record MetricSummaryDto(
    long Id,
    long AuditId,
    string MetricKey,
    string DimensionsJson,
    double Value);

/// <summary>
/// Custom extraction value (schema catalog: extracted_content). One row per
/// captured value; pruned with retention.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="RuleName">Capturing rule name.</param>
/// <param name="Ordinal">Zero-based value ordinal within the rule.</param>
/// <param name="TypedValueJson">Typed value payload; null when invalid/missing.</param>
/// <param name="ValueType">Typed value kind.</param>
/// <param name="IsInvalid">Whether the value failed type/bounds validation.</param>
/// <param name="IsTruncated">Whether the value was truncated at a bound.</param>
public sealed record ExtractedContentDto(
    long Id,
    long AuditId,
    long UrlId,
    string RuleName,
    int Ordinal,
    string? TypedValueJson,
    ExtractedValueType ValueType,
    bool IsInvalid,
    bool IsTruncated);

/// <summary>
/// Custom search match aggregate (schema catalog: content_search_results).
/// Per-item aggregates for the overview.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="SearchItemKey">Search item reference (key).</param>
/// <param name="MatchCount">Number of matches on the page.</param>
/// <param name="SnippetsJson">Matched snippets payload.</param>
public sealed record ContentSearchResultDto(
    long Id,
    long AuditId,
    long UrlId,
    string SearchItemKey,
    int MatchCount,
    string? SnippetsJson);

/// <summary>
/// Custom JavaScript snippet output (schema catalog: snippet_results).
/// Dynamic columns; bounded rows.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="SnippetKey">Snippet reference (key).</param>
/// <param name="ColumnsJson">Typed column definitions payload.</param>
/// <param name="RowsJson">Bounded result rows payload.</param>
public sealed record SnippetResultDto(
    long Id,
    long AuditId,
    long UrlId,
    string SnippetKey,
    string ColumnsJson,
    string RowsJson);

/// <summary>
/// Detected technology (schema catalog: technology_detections). Feeds the
/// technology explorer and aggregate.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="TechnologyKey">Stable technology key.</param>
/// <param name="Category">Technology category.</param>
/// <param name="Confidence">Detection confidence (0–1).</param>
public sealed record TechnologyDetectionDto(
    long Id,
    long AuditId,
    long UrlId,
    string TechnologyKey,
    string Category,
    double Confidence);

/// <summary>
/// Category or overall score (schema catalog: audit_scores). A null category
/// denotes the overall score. Comparable across audits; drives the score trend.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="Category">Scored category; null for the overall score.</param>
/// <param name="Score">Score value (0–100).</param>
/// <param name="ErrorCount">Contributing error count.</param>
/// <param name="WarningCount">Contributing warning count.</param>
/// <param name="NoticeCount">Contributing notice count.</param>
/// <param name="PassCount">Contributing pass count.</param>
public sealed record AuditScoreDto(
    long Id,
    long AuditId,
    AuditCategory? Category,
    double Score,
    int ErrorCount,
    int WarningCount,
    int NoticeCount,
    int PassCount);

/// <summary>
/// User-suppressed finding/rule (schema catalog: finding_suppressions).
/// Project-scoped, reversible, excluded from counts and scores.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="SuppressionKey">Stable key used by the REST delete route.</param>
/// <param name="RuleKey">Suppressed rule key, when rule-scoped.</param>
/// <param name="UrlId">Suppressed URL scope, when URL-scoped.</param>
/// <param name="FindingScope">Free-form finding scope descriptor, when used.</param>
/// <param name="IsSuppressed">Suppression flag (reversible).</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
public sealed record FindingSuppressionDto(
    long Id,
    long ProjectId,
    string SuppressionKey,
    string? RuleKey,
    long? UrlId,
    string? FindingScope,
    bool IsSuppressed,
    DateTimeOffset CreatedAt);
