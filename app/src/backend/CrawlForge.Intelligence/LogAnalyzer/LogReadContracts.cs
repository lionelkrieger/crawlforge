using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Hub event names of the log analyzer (FS-CRAWL-002 §11.8). Events are open
/// strings (additive alongside Constants.HubEventNames); published through the
/// locked IRealtimeHub with a 1-second throttle on progress.
/// </summary>
public static class LogHubEventNames
{
    /// <summary>Import progress; at most once per second while importing.</summary>
    public const string LogImportProgress = "log-import-progress";

    /// <summary>Import finalization; once per import (completed or failed).</summary>
    public const string LogImportCompleted = "log-import-completed";
}

/// <summary>
/// Stable rule keys of the Log Intelligence category (FS-CRAWL-002 §11.5).
/// </summary>
public static class LogRuleKeys
{
    /// <summary>Search-bot hits on URLs never discovered by the crawl (Warning).</summary>
    public const string TrueOrphan = "logs.true-orphan";

    /// <summary>Search-bot budget consumed by low-value URLs (Warning).</summary>
    public const string CrawlBudgetWaste = "logs.crawl-budget-waste";

    /// <summary>Indexable crawled URLs with zero search-bot hits (Notice).</summary>
    public const string UncrawledIndexable = "logs.uncrawled-indexable";
}

/// <summary>
/// One import record as surfaced by the read contract.
/// </summary>
/// <param name="Import">The persisted import row.</param>
/// <param name="Counters">Parsed counters payload (lines, window facets, bot IP samples).</param>
public sealed record LogImportRecord(LogImportDto Import, LogImportCountersPayload Counters);

/// <summary>
/// Typed view of log_imports.counters_json (import-level facets per the
/// orchestrator ruling: window first/last-seen, bytes, query-variant totals,
/// and the bounded bot-IP sample for the consented DNS tool).
/// </summary>
/// <param name="LinesParsed">Total lines read.</param>
/// <param name="LinesAccepted">Lines accepted into aggregation.</param>
/// <param name="LinesRejected">Malformed lines discarded.</param>
/// <param name="LinesOutOfWindow">Well-formed lines outside the window.</param>
/// <param name="ErrorSamples">Bounded rejected-line samples.</param>
/// <param name="FirstSeen">Earliest accepted hit (UTC), when any.</param>
/// <param name="LastSeen">Latest accepted hit (UTC), when any.</param>
/// <param name="BytesServed">Total response bytes across accepted hits.</param>
/// <param name="QueryVariantTotal">Distinct raw query variants observed (bounded per URL).</param>
/// <param name="UniqueUrls">Distinct canonical URLs aggregated.</param>
/// <param name="BotIpSamples">Bounded sample of bot hits for the DNS verify tool.</param>
/// <param name="CatalogueVersion">Bot catalogue version used.</param>
/// <param name="CatalogueRevision">Bot catalogue revision used.</param>
public sealed record LogImportCountersPayload(
    long LinesParsed,
    long LinesAccepted,
    long LinesRejected,
    long LinesOutOfWindow,
    IReadOnlyList<string> ErrorSamples,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    long BytesServed,
    long QueryVariantTotal,
    long UniqueUrls,
    IReadOnlyList<BotIpSample> BotIpSamples,
    string? CatalogueVersion,
    string? CatalogueRevision);

/// <summary>
/// One sampled bot hit retained for the user-triggered DNS verification tool.
/// </summary>
/// <param name="Ip">Client IP address.</param>
/// <param name="Family">Classified bot family label.</param>
/// <param name="UserAgent">Observed user-agent.</param>
/// <param name="Tier">Verification tier at import time (claimed or cidr-verified).</param>
public sealed record BotIpSample(string Ip, string Family, string UserAgent, string Tier);

/// <summary>
/// Correlation view selector (REST: view=summary|orphans|waste|uncrawled).
/// </summary>
public enum LogCorrelationView
{
    /// <summary>Counts across all three views.</summary>
    Summary = 0,

    /// <summary>True orphans view.</summary>
    Orphans = 1,

    /// <summary>Crawl budget waste view.</summary>
    Waste = 2,

    /// <summary>Uncrawled indexable view.</summary>
    Uncrawled = 3
}

/// <summary>
/// One correlation row with per-class evidence (FS-CRAWL-002 §11.5).
/// </summary>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="HitCount">Total hits in the import window.</param>
/// <param name="SearchBotHits">Search-purpose bot hits.</param>
/// <param name="BotFamily">Dominant bot family label, when identified.</param>
/// <param name="InCrawl">Whether the URL exists in the audit's url_records.</param>
/// <param name="EvidenceJson">Per-class structured evidence.</param>
public sealed record LogCorrelationRow(
    string CanonicalUrl,
    long HitCount,
    long SearchBotHits,
    string? BotFamily,
    bool InCrawl,
    string EvidenceJson);

/// <summary>
/// Paged correlation read result.
/// </summary>
/// <param name="View">The requested view.</param>
/// <param name="Rows">The returned page of rows.</param>
/// <param name="TotalCount">Total rows in the view.</param>
/// <param name="OutOfScopeOrphans">Out-of-scope true orphans, reported separately (§11.5).</param>
public sealed record LogCorrelationPage(
    LogCorrelationView View,
    IReadOnlyList<LogCorrelationRow> Rows,
    long TotalCount,
    IReadOnlyList<LogCorrelationRow> OutOfScopeOrphans);

/// <summary>
/// Local import-management read/write contract for phase-05. REST mapping
/// (FS-CRAWL-002 §11.7): POST /api/projects/{id}/logs/import → ImportAsync
/// (returns the import identifier immediately; progress over the hub);
/// GET /api/projects/{id}/logs → ListImportsAsync (pagination);
/// DELETE /api/logs/{id} → PurgeImportAsync (aggregates removed transactionally).
/// Import management is NOT exposed via MCP (§11.1 boundary).
/// </summary>
public interface ILogImportService
{
    /// <summary>Streams a local log file into aggregates (offline; never blocks a crawl).</summary>
    /// <param name="request">Import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The import outcome with final counters.</returns>
    Task<LogImportOutcome> ImportAsync(LogImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists a project's imports, newest first.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import records with counters.</returns>
    Task<IReadOnlyList<LogImportRecord>> ListImportsAsync(long projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Purges an import and its aggregates transactionally.</summary>
    /// <param name="importId">Import identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the purge is durable.</returns>
    Task PurgeImportAsync(long importId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One log import request. The base URI resolves relative request paths to
/// absolute URLs before canonicalization under the target audit's query policy
/// (§11.4); project hosts scope the correlation's orphan view.
/// </summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="SourceFilePath">Local log file path (local disk only).</param>
/// <param name="BaseUri">Base URI for relative paths (e.g. https://example.com).</param>
/// <param name="Format">Format override; null auto-detects.</param>
/// <param name="WindowStart">Analysis window start (UTC), when selected.</param>
/// <param name="WindowEnd">Analysis window end (UTC, exclusive), when selected.</param>
/// <param name="VerificationMode">Bot verification mode (CidrOnly never touches the network).</param>
/// <param name="ProjectHosts">In-scope hosts; null derives from BaseUri.</param>
public sealed record LogImportRequest(
    long ProjectId,
    string SourceFilePath,
    string BaseUri,
    LogFileFormat? Format = null,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null,
    BotVerificationMode VerificationMode = BotVerificationMode.CidrOnly,
    IReadOnlyList<string>? ProjectHosts = null);

/// <summary>
/// Import outcome.
/// </summary>
/// <param name="ImportId">Persisted import identifier.</param>
/// <param name="Status">Final import status.</param>
/// <param name="Counters">Final counters payload.</param>
/// <param name="Error">Failure message, when failed.</param>
public sealed record LogImportOutcome(
    long ImportId,
    LogImportStatus Status,
    LogImportCountersPayload Counters,
    string? Error);

/// <summary>
/// Local correlation read contract for phase-05. REST mapping (§11.7):
/// GET /api/audits/{id}/logs/correlation?import={importId}&amp;view=... →
/// GetCorrelationPageAsync (filters + pagination). MCP resource
/// <c>log-correlation</c> (audit reference; import reference; view; pagination)
/// is a read-only projection of the same surface; import management is never
/// exposed via MCP.
/// </summary>
public interface ILogCorrelationReadModel
{
    /// <summary>Returns one paged correlation view with evidence.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="logImportId">Import identifier.</param>
    /// <param name="view">Requested view.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The correlation page.</returns>
    Task<LogCorrelationPage> GetCorrelationPageAsync(
        long auditId,
        long logImportId,
        LogCorrelationView view,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Local consented DNS verification tool contract for phase-05. REST mapping
/// (§11.7): POST /api/logs/{id}/verify-dns with an explicit consent flag and
/// bounded sample size. The tool refuses to run without consent; results are
/// returned and logged locally (persisted into the import counters payload).
/// </summary>
public interface ILogDnsVerificationTool
{
    /// <summary>Verifies a bounded sample of an import's bot IPs (user-triggered only).</summary>
    /// <param name="importId">Import identifier.</param>
    /// <param name="consent">Explicit per-import user consent; false refuses the operation.</param>
    /// <param name="sampleSize">Maximum IPs to verify (bounded).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-IP verification results.</returns>
    Task<IReadOnlyList<DnsVerificationResult>> VerifySampleAsync(
        long importId,
        bool consent,
        int sampleSize = 20,
        CancellationToken cancellationToken = default);
}
