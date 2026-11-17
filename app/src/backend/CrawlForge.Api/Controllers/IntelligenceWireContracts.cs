using System.Text.Json;

namespace CrawlForge.Api.Controllers;

/// <summary>One blocked-URL row (GET /api/audits/{id}/waf-blocks).</summary>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="CanonicalUrl">Blocked canonical URL.</param>
/// <param name="DetectedAt">UTC detection time.</param>
/// <param name="Provider">Detected provider wire key.</param>
/// <param name="ChallengeClass">Challenge class wire key.</param>
/// <param name="Confidence">Detection confidence in [0,1].</param>
/// <param name="SignatureKey">Matched signature key, when any.</param>
/// <param name="Evidence">Supporting evidence, when present.</param>
public sealed record WafBlockWire(
    string UrlId,
    string CanonicalUrl,
    DateTimeOffset DetectedAt,
    string Provider,
    string ChallengeClass,
    double Confidence,
    string? SignatureKey,
    WafEvidenceWire? Evidence);

/// <summary>WAF block summary (provider/class counts and block rate).</summary>
/// <param name="TotalBlocked">Total blocked URLs.</param>
/// <param name="BlockRate">Blocked / fetched, in [0,1].</param>
/// <param name="ByProvider">Counts per provider wire key.</param>
/// <param name="ByChallengeClass">Counts per challenge class wire key.</param>
public sealed record WafBlocksSummaryWire(long TotalBlocked, double BlockRate, IReadOnlyDictionary<string, long> ByProvider, IReadOnlyDictionary<string, long> ByChallengeClass);

/// <summary>One effective WAF signature.</summary>
/// <param name="Key">Stable signature key.</param>
/// <param name="Provider">Provider wire key.</param>
/// <param name="ChallengeClass">Challenge class wire key.</param>
/// <param name="Pattern">Detection pattern (joined local markers).</param>
/// <param name="Source">bundled or user.</param>
/// <param name="Enabled">Whether the entry is enabled.</param>
/// <param name="Description">Optional description.</param>
public sealed record WafSignatureWire(string Key, string Provider, string ChallengeClass, string Pattern, string Source, bool Enabled, string? Description);

/// <summary>Effective signature catalogue.</summary>
/// <param name="BundledVersion">Bundled catalogue version.</param>
/// <param name="Signatures">Effective signatures.</param>
public sealed record WafSignatureCatalogueWire(string BundledVersion, IReadOnlyList<WafSignatureWire> Signatures);

/// <summary>POST /api/waf-signatures request.</summary>
/// <param name="Key">Stable signature key.</param>
/// <param name="Provider">Provider wire key.</param>
/// <param name="ChallengeClass">Challenge class wire key.</param>
/// <param name="Pattern">Detection pattern.</param>
/// <param name="Description">Optional description.</param>
public sealed record WafSignatureCreateRequest(string Key, string Provider, string ChallengeClass, string Pattern, string? Description);

/// <summary>PUT /api/waf-signatures/{key} request (partial).</summary>
/// <param name="Provider">Provider wire key.</param>
/// <param name="ChallengeClass">Challenge class wire key.</param>
/// <param name="Pattern">Detection pattern.</param>
/// <param name="Enabled">Enabled flag.</param>
/// <param name="Description">Description; null clears.</param>
public sealed record WafSignatureUpdateRequest(string? Provider, string? ChallengeClass, string? Pattern, bool? Enabled, string? Description);

/// <summary>DELETE /api/waf-signatures/{key} response.</summary>
/// <param name="Key">Signature key.</param>
/// <param name="Deleted">Whether a user entry was deleted.</param>
/// <param name="BundledDefaultRestored">Whether a bundled default was restored.</param>
public sealed record WafSignatureDeleteResponse(string Key, bool Deleted, bool BundledDefaultRestored);

/// <summary>One AIMD adjustment record.</summary>
/// <param name="Timestamp">Adjustment time (UTC).</param>
/// <param name="Trigger">Adjustment trigger key.</param>
/// <param name="RateBefore">Rate before adjustment.</param>
/// <param name="RateAfter">Rate after adjustment.</param>
/// <param name="ConcurrencyBefore">Concurrency before (0 when not exposed).</param>
/// <param name="ConcurrencyAfter">Concurrency after (0 when not exposed).</param>
/// <param name="TtfbSampleMs">TTFB sample, when exposed.</param>
public sealed record HostAdjustmentWire(DateTimeOffset Timestamp, string Trigger, double RateBefore, double RateAfter, int ConcurrencyBefore, int ConcurrencyAfter, double? TtfbSampleMs);

/// <summary>Manual host-rate pin.</summary>
/// <param name="PinnedRate">Pinned rate, when set.</param>
/// <param name="PinnedConcurrency">Pinned concurrency (null; locked override is rate-only).</param>
public sealed record HostRateOverrideWire(double? PinnedRate, double? PinnedConcurrency);

/// <summary>Per-host AIMD state row.</summary>
/// <param name="Host">Host name.</param>
/// <param name="EffectiveRate">Effective requests per second.</param>
/// <param name="Concurrency">Effective concurrency.</param>
/// <param name="TtfbEwmaMs">EWMA-smoothed TTFB.</param>
/// <param name="RollingBaselineRate">Rolling baseline rate.</param>
/// <param name="Throttled">Whether the host is throttled.</param>
/// <param name="Override">Manual pin, when set.</param>
/// <param name="History">Adjustment history, when requested.</param>
public sealed record HostStateWire(
    string Host,
    double EffectiveRate,
    int Concurrency,
    double? TtfbEwmaMs,
    double RollingBaselineRate,
    bool Throttled,
    HostRateOverrideWire? Override,
    IReadOnlyList<HostAdjustmentWire>? History);

/// <summary>POST /api/audits/{id}/hosts/{host}/override request.</summary>
/// <param name="Rate">Manual rate; null clears the pin.</param>
/// <param name="Concurrency">Concurrency pin (accepted; locked override is rate-only).</param>
public sealed record HostOverrideRequest(double? Rate, double? Concurrency);

/// <summary>One cannibalization cluster page.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="Occurrences">Occurrence weight.</param>
public sealed record CannibalizationPageWire(string UrlId, string CanonicalUrl, long Occurrences);

/// <summary>One cannibalization cluster.</summary>
/// <param name="Ngram">Contested n-gram term.</param>
/// <param name="Pages">Competing pages.</param>
public sealed record NgramCannibalizationClusterWire(string Ngram, IReadOnlyList<CannibalizationPageWire> Pages);

/// <summary>One topical gap row.</summary>
/// <param name="Ngram">Gap term.</param>
/// <param name="ObservedCount">Observed anchor DF.</param>
/// <param name="ExpectedCount">Expected threshold.</param>
/// <param name="GapScore">Shortfall ratio in [0,1].</param>
public sealed record TopicalGapWire(string Ngram, long ObservedCount, long ExpectedCount, double GapScore);

/// <summary>One detected analytics tag/container.</summary>
/// <param name="Name">Vendor/tag name.</param>
/// <param name="ContainerId">Container identifier, when present.</param>
/// <param name="Kind">Tag kind key.</param>
/// <param name="UrlId">URL reference, when per-URL.</param>
/// <param name="Occurrences">Occurrence count.</param>
public sealed record AnalyticsTagWire(string Name, string? ContainerId, string Kind, string? UrlId, long Occurrences);

/// <summary>Consent Mode v2 / beacon rollup.</summary>
/// <param name="ConsentModeV2Present">Whether Consent Mode v2 was observed.</param>
/// <param name="BeaconPresent">Whether a beacon was observed.</param>
/// <param name="PagesWithConsent">Pages with consent signals.</param>
/// <param name="PagesWithoutConsent">Pages without consent signals.</param>
public sealed record AnalyticsConsentSummaryWire(bool ConsentModeV2Present, bool BeaconPresent, long PagesWithConsent, long PagesWithoutConsent);

/// <summary>Analytics response (tags, consent, findings).</summary>
/// <param name="Tags">Detected tags.</param>
/// <param name="Consent">Consent rollup.</param>
/// <param name="Findings">Analytics findings.</param>
public sealed record AnalyticsResponseWire(IReadOnlyList<AnalyticsTagWire> Tags, AnalyticsConsentSummaryWire Consent, IReadOnlyList<FindingWire> Findings);

/// <summary>Per-container coverage across indexable pages.</summary>
/// <param name="ContainerId">Container identifier.</param>
/// <param name="ContainerName">Container name, when known.</param>
/// <param name="IndexablePages">Indexable page count.</param>
/// <param name="PagesWithContainer">Pages carrying the container.</param>
/// <param name="Coverage">Coverage ratio in [0,1].</param>
public sealed record ContainerCoverageWire(string ContainerId, string? ContainerName, long IndexablePages, long PagesWithContainer, double Coverage);

/// <summary>Per-host llms.txt validation row.</summary>
/// <param name="Host">Host name.</param>
/// <param name="Url">llms.txt URL.</param>
/// <param name="Present">Whether llms.txt was found.</param>
/// <param name="Valid">Whether it follows the convention.</param>
/// <param name="Issues">Validation issues.</param>
/// <param name="CheckedAt">Check time (UTC).</param>
public sealed record LlmsTxtValidationWire(string Host, string Url, bool Present, bool Valid, IReadOnlyList<string> Issues, DateTimeOffset CheckedAt);

/// <summary>One bot-governance matrix row.</summary>
/// <param name="BotName">Bundled agent profile name.</param>
/// <param name="Host">Host, when established.</param>
/// <param name="Allowed">Effective allow/deny verdict.</param>
/// <param name="RobotsTxtAllowed">Robots verdict, when established.</param>
/// <param name="LlmsTxtAllowed">llms.txt verdict, when established.</param>
/// <param name="Notes">Human-readable notes.</param>
public sealed record BotGovernanceRowWire(string BotName, string? Host, bool Allowed, bool? RobotsTxtAllowed, bool? LlmsTxtAllowed, string? Notes);

/// <summary>POST /api/audits/{id}/redirect-matrix request.</summary>
/// <param name="Rows">Migration mapping rows.</param>
/// <param name="AutoSeedAudit">Whether to auto-seed a list-mode audit.</param>
public sealed record RedirectMatrixUploadRequest(IReadOnlyList<RedirectMatrixRowWire> Rows, bool? AutoSeedAudit);

/// <summary>One migration mapping row.</summary>
/// <param name="SourceUrl">Old URL.</param>
/// <param name="TargetUrl">Expected new URL.</param>
public sealed record RedirectMatrixRowWire(string SourceUrl, string TargetUrl);

/// <summary>Upload acceptance.</summary>
/// <param name="MatrixId">Matrix identifier (audit-scoped).</param>
/// <param name="RowCount">Accepted row count.</param>
/// <param name="AuditId">Auto-seeded audit identifier, when requested.</param>
public sealed record RedirectMatrixUploadResponse(string MatrixId, int RowCount, string? AuditId);

/// <summary>One per-row redirect-matrix result.</summary>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="SourceUrl">Source URL.</param>
/// <param name="ExpectedTargetUrl">Expected target URL.</param>
/// <param name="Verdict">Row verdict wire key.</param>
/// <param name="HopChain">Observed hop chain.</param>
/// <param name="CanonicalUrl">Canonical URL of the final page.</param>
/// <param name="Evidence">Evidence detail.</param>
public sealed record RedirectMatrixResultWire(
    string UrlId,
    string SourceUrl,
    string ExpectedTargetUrl,
    string Verdict,
    IReadOnlyList<RedirectHopWire> HopChain,
    string? CanonicalUrl,
    string? Evidence);

/// <summary>POST /api/audits/{id}/semantics/run request.</summary>
/// <param name="Rerun">Whether to re-embed already-vectorized pages.</param>
public sealed record SemanticsRunRequest(bool? Rerun);

/// <summary>Semantics run acceptance.</summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="JobId">Job identifier (audit-scoped; no separate job table).</param>
/// <param name="State">Embedding job state wire key.</param>
public sealed record SemanticsRunResponse(string AuditId, string JobId, string State);

/// <summary>One cosine-similarity neighbour.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="Similarity">Cosine similarity in [0,1].</param>
public sealed record SimilarUrlWire(string UrlId, string CanonicalUrl, double Similarity);

/// <summary>One semantic internal-link recommendation.</summary>
/// <param name="SourceUrlId">Source URL identifier.</param>
/// <param name="SourceCanonicalUrl">Source canonical URL.</param>
/// <param name="TargetUrlId">Target URL identifier.</param>
/// <param name="TargetCanonicalUrl">Target canonical URL.</param>
/// <param name="Similarity">Cosine similarity in [0,1].</param>
/// <param name="AnchorSuggestion">Anchor text suggestion, when present.</param>
public sealed record LinkOpportunityWire(string SourceUrlId, string SourceCanonicalUrl, string TargetUrlId, string TargetCanonicalUrl, double Similarity, string? AnchorSuggestion);

/// <summary>One semantic cluster member.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="Similarity">Centroid similarity in [0,1].</param>
public sealed record SemanticClusterMemberWire(string UrlId, string CanonicalUrl, double Similarity);

/// <summary>One semantic cannibalization cluster.</summary>
/// <param name="ClusterId">Cluster identifier.</param>
/// <param name="Label">Cluster label, when present.</param>
/// <param name="CentroidUrlId">Centroid URL identifier.</param>
/// <param name="Members">Cluster members.</param>
public sealed record SemanticClusterWire(string ClusterId, string? Label, string? CentroidUrlId, IReadOnlyList<SemanticClusterMemberWire> Members);

/// <summary>POST /api/projects/{id}/logs/import request.</summary>
/// <param name="FilePath">Local log file path.</param>
/// <param name="Format">Format key; null auto-detects.</param>
public sealed record LogImportRequestWire(string FilePath, string? Format);

/// <summary>Log import acceptance.</summary>
/// <param name="ImportId">Import identifier.</param>
/// <param name="State">Import state wire key.</param>
public sealed record LogImportStartResponse(string ImportId, string State);

/// <summary>Streaming import counters.</summary>
/// <param name="LinesParsed">Lines read.</param>
/// <param name="LinesAccepted">Lines accepted.</param>
/// <param name="LinesRejected">Lines rejected.</param>
public sealed record LogImportCountersWire(long LinesParsed, long LinesAccepted, long LinesRejected);

/// <summary>Correlation summary counts.</summary>
/// <param name="OrphanCount">True orphan count.</param>
/// <param name="BudgetWasteCount">Budget waste count.</param>
/// <param name="UncrawledIndexableCount">Uncrawled indexable count.</param>
public sealed record LogCorrelationSummaryWire(long OrphanCount, long BudgetWasteCount, long UncrawledIndexableCount);

/// <summary>One log import record.</summary>
/// <param name="Id">Import identifier.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="FileName">Source file name.</param>
/// <param name="Format">Format wire key, when known.</param>
/// <param name="State">Import state wire key.</param>
/// <param name="StartedAt">Start time (UTC).</param>
/// <param name="CompletedAt">Completion time, when terminal.</param>
/// <param name="Counters">Import counters.</param>
/// <param name="Correlation">Correlation summary, when computed.</param>
/// <param name="DnsVerificationConsented">Whether DNS verification was consented.</param>
public sealed record LogImportWire(
    string Id,
    string ProjectId,
    string FileName,
    string? Format,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    LogImportCountersWire Counters,
    LogCorrelationSummaryWire? Correlation,
    bool DnsVerificationConsented);

/// <summary>One correlation row.</summary>
/// <param name="Url">Observed URL.</param>
/// <param name="CanonicalUrl">Canonical URL, when matched.</param>
/// <param name="UrlId">URL record identifier, when in crawl.</param>
/// <param name="Hits">Total hits.</param>
/// <param name="BotHits">Search-bot hits.</param>
/// <param name="LastSeen">Last seen time, when known.</param>
public sealed record LogCorrelationRowWire(string Url, string? CanonicalUrl, string? UrlId, long Hits, long BotHits, DateTimeOffset? LastSeen);

/// <summary>Correlation response (summary + rows).</summary>
/// <param name="Summary">Summary counts.</param>
/// <param name="Rows">Rows of the selected view.</param>
public sealed record LogCorrelationResponseWire(LogCorrelationSummaryWire Summary, IReadOnlyList<LogCorrelationRowWire> Rows);

/// <summary>POST /api/logs/{id}/verify-dns request.</summary>
/// <param name="Consent">Explicit per-import consent.</param>
public sealed record DnsVerifyRequest(bool Consent);

/// <summary>DNS verification acceptance.</summary>
/// <param name="ImportId">Import identifier.</param>
/// <param name="Started">Whether verification started.</param>
/// <param name="Message">Verdict detail.</param>
public sealed record DnsVerifyResponse(string ImportId, bool Started, string? Message);
