using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Intelligence;

/// <summary>
/// Offline streaming server-log parser (FS-CRAWL-002 §11): reads local disk
/// only, streams entries with bounded memory (scale invariant). No remote
/// enrichment is ever performed (exclusion E8).
/// </summary>
public interface ILogFileParser
{
    /// <summary>Streams log entries from a local log stream.</summary>
    /// <param name="logStream">Log file stream.</param>
    /// <param name="format">Log format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async stream of parsed entries.</returns>
    IAsyncEnumerable<LogEntry> ParseAsync(Stream logStream, LogFileFormat format, CancellationToken cancellationToken = default);
}

/// <summary>
/// One parsed server-log entry.
/// </summary>
/// <param name="Timestamp">Request timestamp (UTC).</param>
/// <param name="ClientIp">Client IP address, when present.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Requested path.</param>
/// <param name="StatusCode">Response status code.</param>
/// <param name="UserAgent">User-agent string, when present.</param>
/// <param name="ResponseBytes">Response size in bytes, when present.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string? ClientIp,
    string Method,
    string Path,
    int StatusCode,
    string? UserAgent,
    long? ResponseBytes);

/// <summary>
/// Bot classifier and verifier (FS-CRAWL-002 §11.3): classification uses
/// bundled CIDR ranges and agent profiles only. DNS verification is the single
/// optional network action — user-triggered, off by default, per-import
/// consent, locally logged (exclusion E8).
/// </summary>
public interface IBotVerifier
{
    /// <summary>Classifies a hit using bundled CIDR ranges and agent profiles.</summary>
    /// <param name="clientIp">Client IP address, when present.</param>
    /// <param name="userAgent">User-agent string, when present.</param>
    /// <returns>Bot classification.</returns>
    BotClassification Classify(string? clientIp, string? userAgent);

    /// <summary>
    /// Performs user-consented DNS verification of a bot hit. Must only be
    /// invoked from an explicit user action with per-import consent.
    /// </summary>
    /// <param name="clientIp">Client IP address to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>DNS verification verdict.</returns>
    Task<DnsVerificationResult> VerifyDnsAsync(string clientIp, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bot classification of one log hit.
/// </summary>
/// <param name="IsBot">Whether the hit is classified as a bot.</param>
/// <param name="AgentName">Matched agent name, when identified.</param>
/// <param name="Family">Bot family label, when identified.</param>
/// <param name="CidrMatched">Whether a bundled CIDR range matched.</param>
public sealed record BotClassification(bool IsBot, string? AgentName, string? Family, bool CidrMatched);

/// <summary>
/// DNS verification verdict for one bot hit.
/// </summary>
/// <param name="ClientIp">Verified IP address.</param>
/// <param name="Verified">Whether forward/reverse DNS confirmed the agent.</param>
/// <param name="Hostname">Resolved hostname, when available.</param>
public sealed record DnsVerificationResult(string ClientIp, bool Verified, string? Hostname);

/// <summary>
/// Crawl-vs-log correlation engine (FS-CRAWL-002 §11.5): true orphans, crawl
/// budget waste, and uncrawled indexable URLs.
/// </summary>
public interface ILogCorrelationEngine
{
    /// <summary>Correlates an audit's crawl graph with a log import.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="logImportId">Log import identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Correlation views.</returns>
    Task<LogCorrelationResult> CorrelateAsync(long auditId, long logImportId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Log correlation result views.
/// </summary>
/// <param name="TrueOrphans">Indexable URLs hit by bots but absent from the crawl graph.</param>
/// <param name="BudgetWaste">Non-indexable URLs consuming crawl/bot budget.</param>
/// <param name="UncrawledIndexable">Indexable URLs hit by search bots but never crawled.</param>
public sealed record LogCorrelationResult(
    IReadOnlyList<LogCorrelatedUrl> TrueOrphans,
    IReadOnlyList<LogCorrelatedUrl> BudgetWaste,
    IReadOnlyList<LogCorrelatedUrl> UncrawledIndexable);

/// <summary>
/// One URL in a log correlation view.
/// </summary>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="HitCount">Aggregated hit count in the import window.</param>
/// <param name="BotFamily">Dominant bot family, when identified.</param>
public sealed record LogCorrelatedUrl(string CanonicalUrl, long HitCount, string? BotFamily);
