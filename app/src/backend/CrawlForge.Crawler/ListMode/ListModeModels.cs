using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Discovery modes of List Mode (FS-CRAWL-002 §10.2). <see cref="ListOnly"/>
/// forces the depth ceiling to 0 and disables link discovery and sitemap
/// expansion; <see cref="ListBounded"/> runs discovery but enqueues only URLs
/// present in the uploaded list after canonicalization.
/// </summary>
public enum ListDiscoveryMode
{
    /// <summary>List Mode is not configured; baseline crawl behavior applies.</summary>
    Off = 0,

    /// <summary>Only uploaded URLs are crawled; depth ceiling forced to 0.</summary>
    ListOnly = 1,

    /// <summary>Discovery runs, but only on-list URLs are enqueued.</summary>
    ListBounded = 2,
}

/// <summary>
/// Comparison strictness of the migration redirect-matrix validator
/// (FS-CRAWL-002 §10.3): <see cref="Exact"/> compares full canonical URLs;
/// <see cref="Normalized"/> applies the staging↔production host mapping and
/// compares host + path + query scheme-insensitively (§10.5 path alignment).
/// </summary>
public enum MatrixComparisonStrictness
{
    /// <summary>Full canonical URL equality.</summary>
    Exact = 0,

    /// <summary>Host-mapped, scheme-insensitive host + path + query equality.</summary>
    Normalized = 1,
}

/// <summary>
/// Scope of an on-the-fly rewrite rule (FS-CRAWL-002 §10.4). The locked
/// <see cref="UrlRewriteRule"/> contract carries pattern/replacement/enabled;
/// scope is a List Mode configuration dimension applied by the ingestor.
/// </summary>
public enum RewriteRuleScope
{
    /// <summary>The rule applies to seed/list URLs only.</summary>
    SeedsOnly = 0,

    /// <summary>The rule applies to seeds and discovered URLs.</summary>
    AllDiscovered = 1,
}

/// <summary>
/// List Mode configuration for one audit (FS-CRAWL-002 §10.2–§10.5).
/// All values are local configuration; nothing here contacts any endpoint.
/// </summary>
/// <param name="Mode">Resolved discovery mode.</param>
/// <param name="BoundedDepthCeiling">Depth ceiling for list-bounded discovery; list-only always forces 0.</param>
/// <param name="RewriteRules">Ordered regex rewrite rules applied during canonicalization (§10.4).</param>
/// <param name="HostMappings">Staging↔production host-mapping rules (§10.5).</param>
/// <param name="RedirectHopLimit">Redirect-hop ceiling resolved from the crawl limits (§4.2); chains above it are chain-exceeded.</param>
/// <param name="Strictness">Matrix comparison strictness (§10.3).</param>
public sealed record ListModeOptions(
    ListDiscoveryMode Mode = ListDiscoveryMode.ListOnly,
    int BoundedDepthCeiling = 1,
    IReadOnlyList<UrlRewriteRule>? RewriteRules = null,
    IReadOnlyList<HostMappingRule>? HostMappings = null,
    int RedirectHopLimit = 10,
    MatrixComparisonStrictness Strictness = MatrixComparisonStrictness.Exact)
{
    /// <summary>Shared baseline options: list-only, no rewrites, no host mapping, ten-hop ceiling.</summary>
    public static ListModeOptions Default { get; } = new();
}

/// <summary>
/// Resolved per-audit List Mode policy consumed by the discovery gate.
/// The allowed-URL set holds canonical URLs of the uploaded list.
/// </summary>
/// <param name="Mode">Resolved discovery mode.</param>
/// <param name="DepthCeiling">Effective depth ceiling (0 for list-only).</param>
/// <param name="DiscoveryEnabled">Whether link/sitemap discovery may enqueue at all.</param>
/// <param name="AllowedCanonicalUrls">Canonical list URLs (list-bounded membership test).</param>
public sealed record ListModePolicy(
    ListDiscoveryMode Mode,
    int DepthCeiling,
    bool DiscoveryEnabled,
    IReadOnlySet<string> AllowedCanonicalUrls);

/// <summary>
/// One staging↔production host-mapping rule (FS-CRAWL-002 §10.5) with an
/// optional path-prefix transform applied when the host is mapped.
/// </summary>
/// <param name="SourceHost">Source host (e.g. staging.example.com).</param>
/// <param name="TargetHost">Target host (e.g. www.example.com).</param>
/// <param name="SourcePathPrefix">Optional path prefix on the source host.</param>
/// <param name="TargetPathPrefix">Replacement prefix on the target host.</param>
public sealed record HostMappingRule(
    string SourceHost,
    string TargetHost,
    string? SourcePathPrefix = null,
    string? TargetPathPrefix = null);

/// <summary>
/// One accepted entry of a parsed URL list, carrying its 1-based source line
/// for per-line diagnostics (FS-CRAWL-002 §10.2).
/// </summary>
/// <param name="LineNumber">1-based line number in the input.</param>
/// <param name="Url">Raw absolute http(s) URL as supplied.</param>
public sealed record ListInputEntry(int LineNumber, string Url);

/// <summary>
/// One rejected input line. Malformed entries never fail the audit; they are
/// collected into a bounded diagnostic list (FS-CRAWL-002 §10.2).
/// </summary>
/// <param name="LineNumber">1-based line number in the input.</param>
/// <param name="RawText">The rejected line content.</param>
/// <param name="Reason">Human-readable rejection reason.</param>
public sealed record ListLineDiagnostic(int LineNumber, string RawText, string Reason);

/// <summary>
/// Result of parsing a URL list from .txt, .csv, or clipboard text.
/// </summary>
/// <param name="Entries">Accepted entries in input order.</param>
/// <param name="Diagnostics">Bounded per-line rejection diagnostics.</param>
/// <param name="InvalidCount">Total rejected lines (may exceed the diagnostic bound).</param>
public sealed record ListParseResult(
    IReadOnlyList<ListInputEntry> Entries,
    IReadOnlyList<ListLineDiagnostic> Diagnostics,
    int InvalidCount);

/// <summary>
/// One accepted migration-matrix row (FS-CRAWL-002 §10.3):
/// Source_URL → Expected_Target_URL, both absolute http(s) URLs.
/// </summary>
/// <param name="Ordinal">Zero-based row ordinal within the matrix.</param>
/// <param name="SourceUrl">Source URL of the migration row.</param>
/// <param name="ExpectedTargetUrl">Expected target URL after migration.</param>
public sealed record MatrixInputRow(int Ordinal, string SourceUrl, string ExpectedTargetUrl);

/// <summary>
/// Result of parsing a two-column migration matrix from .csv, .txt, or clipboard.
/// </summary>
/// <param name="Rows">Accepted rows in ordinal order.</param>
/// <param name="Diagnostics">Bounded per-line rejection diagnostics.</param>
/// <param name="InvalidCount">Total rejected lines (may exceed the diagnostic bound).</param>
public sealed record MatrixParseResult(
    IReadOnlyList<MatrixInputRow> Rows,
    IReadOnlyList<ListLineDiagnostic> Diagnostics,
    int InvalidCount);

/// <summary>
/// Outcome of a matrix upload (FS-CRAWL-002 §10.3/§10.7): persisted mapping
/// counters plus the optional list-only auto-seeding result.
/// </summary>
/// <param name="PersistedCount">Mapping rows persisted to redirect_mappings.</param>
/// <param name="DuplicateCount">Rows skipped because (audit, source URL) already exists.</param>
/// <param name="InvalidCount">Rows rejected by per-line validation.</param>
/// <param name="Seeding">List ingestion result when the upload auto-seeded the audit; otherwise null.</param>
public sealed record MatrixUploadResult(
    int PersistedCount,
    int DuplicateCount,
    int InvalidCount,
    ListIngestionResult? Seeding);

/// <summary>
/// Decision of the List Mode discovery gate for one discovered URL.
/// </summary>
/// <param name="Enqueue">Whether the discovery may enter the frontier.</param>
/// <param name="SkipReason">Skip reason when not enqueued (e.g. <c>out-of-list</c>); otherwise null.</param>
public sealed record DiscoveryDecision(bool Enqueue, string? SkipReason)
{
    /// <summary>The discovery is allowed into the frontier.</summary>
    public static DiscoveryDecision Allowed { get; } = new(true, null);

    /// <summary>Creates a skip decision with the given reason.</summary>
    /// <param name="reason">Machine-readable skip reason.</param>
    /// <returns>The skip decision.</returns>
    public static DiscoveryDecision Skip(string reason) => new(false, reason);
}

/// <summary>
/// One recorded off-list discovery skip (FS-CRAWL-002 §10.2: off-list
/// discoveries are recorded as skipped with reason <c>out-of-list</c>).
/// </summary>
/// <param name="Url">Canonical URL of the skipped discovery.</param>
/// <param name="Source">How the URL was discovered.</param>
/// <param name="Reason">Skip reason.</param>
public sealed record DiscoverySkip(string Url, DiscoverySource Source, string Reason);

/// <summary>
/// Per-rule rewrite diagnostic counter (FS-CRAWL-002 §10.4).
/// </summary>
/// <param name="RuleIndex">Zero-based position of the rule in application order.</param>
/// <param name="Pattern">The rule pattern.</param>
/// <param name="RewriteCount">How many URLs this rule changed.</param>
public sealed record RewriteRuleCounter(int RuleIndex, string Pattern, int RewriteCount);

/// <summary>
/// One verdict aggregate of the migration matrix (FS-CRAWL-002 §10.3:
/// metric_summaries key <c>redirect-matrix</c> dimensioned by verdict).
/// </summary>
/// <param name="Verdict">The verdict dimension.</param>
/// <param name="Count">Number of matrix rows carrying the verdict.</param>
public sealed record MatrixVerdictCount(RedirectMatrixVerdict Verdict, int Count);

/// <summary>
/// One row of the <c>matrix-row-validated</c> hub batch (FS-CRAWL-002 §10.8),
/// in the frozen frontend wire shape (<c>contracts/hub/events.ts</c>
/// MatrixRowValidatedEntry: <c>urlId: EntityId</c>, <c>sourceUrl: string</c>,
/// <c>verdict: string</c>). G-MATRIX-VERDICT component-side fix: identifiers
/// serialize as strings and the verdict as its §10.3/§10.6 dimension name
/// (<see cref="MatrixVerdictNames"/>), never as a numeric enum. The REST
/// results surface carries the same verdict values. No identity dimensions
/// (privacy invariant).
/// </summary>
/// <param name="UrlId">
/// url_records identifier of the source URL as a string EntityId; the empty
/// string when absent (matching the REST results mapper).
/// </param>
/// <param name="SourceUrl">Source URL of the validated matrix row.</param>
/// <param name="Verdict">Verdict dimension name: pass | target-mismatch | chain-exceeded | final-non-200 | canonical-mismatch.</param>
public sealed record MatrixRowValidatedItem(string UrlId, string SourceUrl, string Verdict);

/// <summary>
/// Payload of the <c>matrix-row-validated</c> hub event (FS-CRAWL-002 §10.8):
/// audit identifier plus a batch of validated rows. The audit identifier is a
/// string EntityId per the frozen frontend payload contract (G-MATRIX-VERDICT
/// / G-G component-side fix).
/// </summary>
/// <param name="AuditId">Audit identifier as a string EntityId.</param>
/// <param name="Rows">Batch of validated rows.</param>
public sealed record MatrixRowValidatedPayload(string AuditId, IReadOnlyList<MatrixRowValidatedItem> Rows);

/// <summary>
/// Hub emission contract constants for the migration matrix (FS-CRAWL-002 §10.8).
/// </summary>
public static class MatrixHubContract
{
    /// <summary>Hub event name: matrix-row-validated (server → shell).</summary>
    public const string EventName = "matrix-row-validated";

    /// <summary>Maximum batch emission rate: at most once per second.</summary>
    public static TimeSpan MaxEmissionInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows per emitted batch before a flush is forced.</summary>
    public const int BatchSize = 50;

    /// <summary>Builds the throttle key coalescing matrix batches of one audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <returns>The throttle key.</returns>
    public static string ThrottleKey(long auditId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{EventName}:{auditId}");
}

/// <summary>
/// Verdict dimension names of the <c>redirect-matrix</c> metric key. The names
/// mirror the additive finding rule-key suffixes (FS-CRAWL-002 §10.3/§10.6).
/// </summary>
public static class MatrixVerdictNames
{
    /// <summary>Returns the stable dimension name of a verdict.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The dimension name (e.g. <c>chain-exceeded</c>).</returns>
    public static string Of(RedirectMatrixVerdict verdict) => verdict switch
    {
        RedirectMatrixVerdict.Pass => "pass",
        RedirectMatrixVerdict.TargetMismatch => "target-mismatch",
        RedirectMatrixVerdict.ChainExceeded => "chain-exceeded",
        RedirectMatrixVerdict.FinalNon200 => "final-non-200",
        RedirectMatrixVerdict.CanonicalMismatch => "canonical-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown redirect-matrix verdict."),
    };

    /// <summary>Parses a dimension name back into a verdict.</summary>
    /// <param name="name">The dimension name.</param>
    /// <returns>The verdict.</returns>
    public static RedirectMatrixVerdict Parse(string name) => name switch
    {
        "pass" => RedirectMatrixVerdict.Pass,
        "target-mismatch" => RedirectMatrixVerdict.TargetMismatch,
        "chain-exceeded" => RedirectMatrixVerdict.ChainExceeded,
        "final-non-200" => RedirectMatrixVerdict.FinalNon200,
        "canonical-mismatch" => RedirectMatrixVerdict.CanonicalMismatch,
        _ => throw new ArgumentException($"Unknown redirect-matrix verdict dimension '{name}'.", nameof(name)),
    };
}
