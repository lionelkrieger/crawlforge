using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// N-gram analyzer configuration and mandatory bounds (FS-CRAWL-002 §5.5–§5.6).
/// Excess beyond a bound is DISCARDED AND COUNTED — never an audit failure.
/// The analyzer is enabled by default within these bounds (§1.4 rule 4).
/// </summary>
/// <param name="TopKPerUrlZoneOrder">Top K terms by count retained per URL/zone/order (default 250).</param>
/// <param name="SiteMinDocumentFrequency">Site summaries retain terms with DF at least this value (default 2).</param>
/// <param name="TopMSiteTerms">Plus the top M terms by total frequency (default 100,000).</param>
/// <param name="MinTokenLength">Minimum token length (default 2).</param>
/// <param name="ExcludeNumericTokens">Whether purely numeric tokens are excluded.</param>
/// <param name="TitleZoneWeight">Primary-term density weight for the title zone (default 2.0).</param>
/// <param name="HeadingsZoneWeight">Primary-term density weight for H1/headings text (default 1.5).</param>
/// <param name="CannibalizationEnabled">Whether keyword-cannibalization detection runs.</param>
/// <param name="TopicalGapEnabled">Whether topical-gap detection runs.</param>
/// <param name="MinAnchorDf">Minimum inbound-anchor DF for a topical gap (default 3).</param>
/// <param name="MinClusterSize">Minimum cannibalization cluster size (default 2).</param>
/// <param name="MinTermFrequency">Minimum per-URL TF for primary-term candidacy (default 0).</param>
/// <param name="IndexableOnly">Whether detection restricts to indexable URLs (default true).</param>
/// <param name="TopSiteNGrams">Top site n-grams surfaced in charts/metrics.</param>
/// <param name="PreferredLanguage">Fallback analysis language when the page declares none.</param>
public sealed record NGramAnalyzerOptions(
    int TopKPerUrlZoneOrder = 250,
    int SiteMinDocumentFrequency = 2,
    int TopMSiteTerms = 100_000,
    int MinTokenLength = 2,
    bool ExcludeNumericTokens = false,
    double TitleZoneWeight = 2.0,
    double HeadingsZoneWeight = 1.5,
    bool CannibalizationEnabled = true,
    bool TopicalGapEnabled = true,
    int MinAnchorDf = 3,
    int MinClusterSize = 2,
    double MinTermFrequency = 0.0,
    bool IndexableOnly = true,
    int TopSiteNGrams = 500,
    string PreferredLanguage = "en")
{
    /// <summary>Default options (§5.5–§5.6 defaults).</summary>
    public static NGramAnalyzerOptions Default { get; } = new();
}

/// <summary>
/// Discard-and-count bound diagnostics (FS-CRAWL-002 §5.6): surfaced in audit
/// counters and hub diagnostics; never fails the audit.
/// </summary>
/// <param name="TailTermsDiscarded">Per-URL/zone/order tail terms dropped at the top-K bound.</param>
/// <param name="SiteRowsDiscarded">Site-summary rows dropped by the DF/top-M retention bound.</param>
public sealed record NGramBoundsCounters(long TailTermsDiscarded = 0, long SiteRowsDiscarded = 0)
{
    /// <summary>Whether any bound discarded data.</summary>
    public bool AnyDiscarded => TailTermsDiscarded > 0 || SiteRowsDiscarded > 0;

    /// <summary>Combines two counter sets.</summary>
    /// <param name="other">Counters to add.</param>
    /// <returns>The summed counters.</returns>
    public NGramBoundsCounters Add(NGramBoundsCounters other) =>
        new(TailTermsDiscarded + other.TailTermsDiscarded, SiteRowsDiscarded + other.SiteRowsDiscarded);
}

/// <summary>
/// Rich per-URL analysis output: frequency rows plus bound counters. The
/// locked <see cref="Contracts.Intelligence.INgramAnalyzer"/> surface returns
/// the rows; the pipeline persists the counters as audit diagnostics.
/// </summary>
/// <param name="Rows">Per-zone n-gram frequency rows (persisted to ngram_frequencies).</param>
/// <param name="Counters">Discard-and-count diagnostics.</param>
public sealed record NGramUrlAnalysis(
    IReadOnlyList<NgramFrequencyDto> Rows,
    NGramBoundsCounters Counters);

/// <summary>
/// One competing URL inside a cannibalization cluster (§5.5 evidence:
/// per-URL TF and canonical target).
/// </summary>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="CanonicalUrl">The URL's canonical URL.</param>
/// <param name="TermFrequency">Per-URL TF of the shared primary term.</param>
/// <param name="DeclaredCanonicalTarget">The page's declared rel=canonical target, when present.</param>
public sealed record NgramClusterMember(
    long UrlId,
    string CanonicalUrl,
    double TermFrequency,
    string? DeclaredCanonicalTarget);

/// <summary>
/// One keyword-cannibalization cluster (FS-CRAWL-002 §5.5): indexable URLs
/// sharing an identical primary 2-gram or 3-gram in the title or headings
/// zone. Severity is Warning, downgraded to Notice when every member declares
/// the same canonical target (consolidation already signaled).
/// </summary>
/// <param name="Zone">Zone of the shared primary term.</param>
/// <param name="Order">N-gram order (2 or 3).</param>
/// <param name="Term">Shared primary term.</param>
/// <param name="Members">Competing URLs (sorted by URL id).</param>
/// <param name="SiteDocumentFrequency">Site-wide DF of the term in the zone/order.</param>
/// <param name="SameCanonicalTarget">Whether every member declares the same canonical target.</param>
public sealed record NgramCannibalizationCluster(
    TextZone Zone,
    int Order,
    string Term,
    IReadOnlyList<NgramClusterMember> Members,
    long SiteDocumentFrequency,
    bool SameCanonicalTarget);

/// <summary>
/// One topical gap (FS-CRAWL-002 §5.5): anchor demand without on-page supply —
/// inbound-anchor DF at or above the threshold with zero DF in the title and
/// headings zones site-wide.
/// </summary>
/// <param name="Term">The gap term.</param>
/// <param name="Order">N-gram order.</param>
/// <param name="AnchorDocumentFrequency">Inbound-anchor DF of the term.</param>
/// <param name="Threshold">Configured minimum anchor DF.</param>
/// <param name="SampleSourceUrls">Sample source URLs of the inbound anchors.</param>
public sealed record NgramTopicalGap(
    string Term,
    int Order,
    long AnchorDocumentFrequency,
    int Threshold,
    IReadOnlyList<string> SampleSourceUrls);

/// <summary>
/// Site-wide pass output (FS-CRAWL-002 §5.4–§5.5): materialized summaries,
/// cannibalization clusters, topical gaps, findings, and metric contributions.
/// </summary>
/// <param name="Summaries">Materialized ngram_site_summaries rows.</param>
/// <param name="Clusters">Keyword-cannibalization clusters.</param>
/// <param name="Gaps">Topical gaps.</param>
/// <param name="Findings">Cannibalization and gap findings.</param>
/// <param name="Metrics">ngram-top, cannibalization-clusters, topical-gaps contributions.</param>
/// <param name="Counters">Discard-and-count diagnostics.</param>
public sealed record NgramSitePassResult(
    IReadOnlyList<NgramSiteSummaryDto> Summaries,
    IReadOnlyList<NgramCannibalizationCluster> Clusters,
    IReadOnlyList<NgramTopicalGap> Gaps,
    IReadOnlyList<FindingDraft> Findings,
    IReadOnlyList<MetricContribution> Metrics,
    NGramBoundsCounters Counters);

/// <summary>
/// N-gram explorer list query (FS-CRAWL-002 §5.7: GET /api/audits/{id}/ngrams).
/// Documented read shape for the phase-05 REST/MCP layer.
/// </summary>
/// <param name="Scope">"site" (site-summary rows) or "url" (per-URL rows).</param>
/// <param name="Zone">Optional zone filter.</param>
/// <param name="Order">Optional order filter.</param>
/// <param name="Query">Optional term substring filter.</param>
/// <param name="UrlId">Optional URL filter (required context for scope=url).</param>
/// <param name="MinDf">Optional minimum document frequency (site scope).</param>
/// <param name="SortBy">Sort field: "df", "tf", or "count" (default "df").</param>
/// <param name="Descending">Sort direction (default true).</param>
/// <param name="Offset">Zero-based page offset.</param>
/// <param name="Limit">Page size (default 50).</param>
public sealed record NgramListQuery(
    string Scope = "site",
    TextZone? Zone = null,
    int? Order = null,
    string? Query = null,
    long? UrlId = null,
    long? MinDf = null,
    string SortBy = "df",
    bool Descending = true,
    int Offset = 0,
    int Limit = 50);

/// <summary>
/// N-gram explorer list response (paged rows; §5.7). Site scope returns
/// site-summary rows; url scope returns per-URL frequency rows.
/// </summary>
/// <param name="SiteItems">Site-summary rows (scope=site).</param>
/// <param name="UrlItems">Per-URL frequency rows (scope=url).</param>
/// <param name="TotalCount">Total rows matching the query.</param>
/// <param name="Offset">Zero-based offset of the page.</param>
/// <param name="Limit">Requested page size.</param>
public sealed record NgramListResponse(
    IReadOnlyList<NgramSiteSummaryDto> SiteItems,
    IReadOnlyList<NgramFrequencyDto> UrlItems,
    long TotalCount,
    int Offset,
    int Limit);

/// <summary>
/// Topical-gap list query (FS-CRAWL-002 §5.7: GET /api/audits/{id}/ngrams/gaps).
/// </summary>
/// <param name="MinAnchorDf">Minimum anchor DF override; null uses the configured threshold.</param>
/// <param name="Offset">Zero-based page offset.</param>
/// <param name="Limit">Page size (default 50).</param>
public sealed record NgramGapsQuery(int? MinAnchorDf = null, int Offset = 0, int Limit = 50);

/// <summary>
/// MCP <c>ngram-analysis</c> resource payload (FS-CRAWL-002 §5.8): a bounded,
/// read-only projection of the REST endpoints — site-summary rows, per-URL
/// rows, and top cannibalization clusters.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="TopTerms">Top site n-grams by document frequency.</param>
/// <param name="UrlFrequencies">Per-URL rows when a urlId filter is applied.</param>
/// <param name="Clusters">Top cannibalization clusters.</param>
/// <param name="Counters">Bound diagnostics.</param>
public sealed record NgramAnalysisResource(
    long AuditId,
    IReadOnlyList<NgramSiteSummaryDto> TopTerms,
    IReadOnlyList<NgramFrequencyDto> UrlFrequencies,
    IReadOnlyList<NgramCannibalizationCluster> Clusters,
    NGramBoundsCounters Counters);
