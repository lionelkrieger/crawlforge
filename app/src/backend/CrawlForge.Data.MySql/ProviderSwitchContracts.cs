using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.MySql;

/// <summary>
/// One provider endpoint in a provider switch: the provider abstraction, its
/// versioned schema migrator, and the project archive service bound to it. All
/// three are locked Phase-00 contracts; the composition root binds them to the
/// embedded or the external provider.
/// </summary>
/// <param name="Provider">Provider abstraction (connection + connection test).</param>
/// <param name="Migrator">Versioned migrator for this provider's dialect.</param>
/// <param name="Archive">Project archive import/export bound to this provider.</param>
public sealed record ProviderSwitchEndpoint(
    IDbProvider Provider,
    ISchemaMigrator Migrator,
    IProjectArchiveService Archive);

/// <summary>
/// Outcome of a provider-switch operation (criterion #18). Switching is an explicit,
/// user-initiated operation using the archive export/import mechanism — never
/// automatic (FS-CRAWL-001 §2.4).
/// </summary>
/// <param name="Success">Whether the operation completed.</param>
/// <param name="FailureReason">Failure detail, when unsuccessful.</param>
/// <param name="SchemaVersion">Target schema version after migration.</param>
/// <param name="ImportResult">Archive import counters, when an import ran.</param>
public sealed record ProviderSwitchResult(
    bool Success,
    string? FailureReason,
    int SchemaVersion,
    ProjectArchiveImportResult? ImportResult)
{
    /// <summary>Creates a failed result with no migration or import side effects.</summary>
    /// <param name="reason">Failure detail.</param>
    /// <returns>The failed result.</returns>
    public static ProviderSwitchResult Failed(string reason) => new(false, reason, 0, null);
}

/// <summary>
/// One precomputed metric row read for report-parity comparison (§8.1: aggregates
/// keyed by audit and metric with a dimension payload).
/// </summary>
/// <param name="MetricKey">Stable metric key.</param>
/// <param name="DimensionsJson">Dimension payload.</param>
/// <param name="Value">Numeric value.</param>
public sealed record MetricSummaryRow(string MetricKey, string DimensionsJson, double Value);

/// <summary>
/// One audit score row read for report-parity comparison (§8.1: per-category and
/// overall scores stored per audit so they are comparable across audits).
/// </summary>
/// <param name="Category">
/// Category key (the <c>AuditCategory</c> name), or the overall marker
/// <c>"overall"</c> for the overall score (NULL category in the schema).
/// </param>
/// <param name="Score">Score value.</param>
/// <param name="ContributingCountsJson">
/// Contributing counts payload composed from the four audit_scores count columns
/// (camelCase errorCount/warningCount/noticeCount/passCount; identical on both
/// providers — DEFECT D10).
/// </param>
public sealed record AuditScoreRow(string Category, double Score, string ContributingCountsJson);

/// <summary>
/// Report-parity verdict between two providers for one audit: the switch contract
/// requires identical report output on either provider (criterion #18).
/// </summary>
/// <param name="Identical">Whether both reports match exactly.</param>
/// <param name="MetricsCompared">Number of metric rows compared.</param>
/// <param name="ScoresCompared">Number of score rows compared.</param>
/// <param name="Differences">Human-readable differences, when not identical.</param>
public sealed record ReportParityResult(
    bool Identical,
    int MetricsCompared,
    int ScoresCompared,
    IReadOnlyList<string> Differences);
