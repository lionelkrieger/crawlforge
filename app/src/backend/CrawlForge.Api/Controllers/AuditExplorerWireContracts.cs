namespace CrawlForge.Api.Controllers;

/// <summary>Wire URL record summary row.</summary>
/// <param name="Id">URL record identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="StatusCode">Final HTTP status code (0 when unfetched).</param>
/// <param name="StatusClass">HTTP status class wire value.</param>
/// <param name="ContentType">Response content type, when known.</param>
/// <param name="Depth">Link depth from the seeds.</param>
/// <param name="Indexable">Whether the URL is indexable.</param>
/// <param name="Title">Document title, when extracted.</param>
/// <param name="BlockReason">Additive: block reason for blocked entries.</param>
/// <param name="WafProvider">Additive: detected WAF provider for blocked entries.</param>
public sealed record UrlRecordSummaryWire(
    string Id,
    string CanonicalUrl,
    int StatusCode,
    string StatusClass,
    string? ContentType,
    int Depth,
    bool Indexable,
    string? Title,
    string? BlockReason,
    string? WafProvider);

/// <summary>One hop in a redirect chain.</summary>
/// <param name="Url">Hop URL.</param>
/// <param name="StatusCode">Hop status code.</param>
/// <param name="Location">Location header target, when present.</param>
public sealed record RedirectHopWire(string Url, int StatusCode, string? Location);

/// <summary>Per-element-type counts of the page element inventory.</summary>
/// <param name="Counts">Element group → count.</param>
/// <param name="TotalElements">Total inventory rows.</param>
public sealed record ElementInventoryWire(IReadOnlyDictionary<string, int> Counts, int TotalElements);

/// <summary>Wire link edge row.</summary>
/// <param name="SourceUrlId">Source URL identifier.</param>
/// <param name="TargetUrlId">Target URL identifier (resolved internal target).</param>
/// <param name="SourceUrl">Source canonical URL.</param>
/// <param name="TargetUrl">Target canonical URL.</param>
/// <param name="Internal">Whether the edge is internal.</param>
/// <param name="AnchorText">Anchor text, when present.</param>
/// <param name="DomZone">Additive: DOM-zone classification key.</param>
/// <param name="EquityWeight">Additive: zone weight applied to the edge.</param>
/// <param name="WeightedEquity">Additive: weighted link-equity value.</param>
public sealed record LinkEdgeWire(
    string SourceUrlId,
    string TargetUrlId,
    string SourceUrl,
    string TargetUrl,
    bool Internal,
    string? AnchorText,
    string? DomZone,
    double? EquityWeight,
    double? WeightedEquity);

/// <summary>Wire content-scope diagnostics for a URL.</summary>
/// <param name="Included">Whether scoped content was included.</param>
/// <param name="MatchedIncludeSelectors">Include selectors that matched.</param>
/// <param name="MatchedExcludeSelectors">Exclude selectors that matched.</param>
/// <param name="ScopedWordCount">Word count after scoping.</param>
/// <param name="TotalWordCount">Word count before scoping.</param>
public sealed record ScopeDiagnosticsWire(bool Included, IReadOnlyList<string> MatchedIncludeSelectors, IReadOnlyList<string> MatchedExcludeSelectors, int ScopedWordCount, int TotalWordCount);

/// <summary>Wire SERP preview metrics.</summary>
/// <param name="TitlePixelWidth">Title pixel width.</param>
/// <param name="DescriptionPixelWidth">Description pixel width.</param>
/// <param name="TitleTruncated">Whether the title exceeds its budget.</param>
/// <param name="DescriptionTruncated">Whether the description exceeds its budget.</param>
public sealed record SerpMetricsWire(double TitlePixelWidth, double DescriptionPixelWidth, bool TitleTruncated, bool DescriptionTruncated);

/// <summary>Wire header-vs-HTML conflict detail.</summary>
/// <param name="HeaderName">Conflicting header name.</param>
/// <param name="HeaderValue">Header value.</param>
/// <param name="HtmlValue">HTML value, when present.</param>
/// <param name="Conflict">Whether a conflict was observed.</param>
public sealed record HeaderConflictDetailWire(string HeaderName, string HeaderValue, string? HtmlValue, bool Conflict);

/// <summary>Wire WAF evidence (local observation only).</summary>
/// <param name="Snippet">Matched body snippet, when present.</param>
/// <param name="Headers">Observed headers, when present.</param>
/// <param name="SignatureKey">Matched signature key, when signature-based.</param>
public sealed record WafEvidenceWire(string? Snippet, IReadOnlyDictionary<string, string>? Headers, string? SignatureKey);

/// <summary>Wire WAF/challenge detection.</summary>
/// <param name="Provider">Detected provider wire value.</param>
/// <param name="ChallengeClass">Challenge class wire value.</param>
/// <param name="Confidence">Detection confidence in [0,1].</param>
/// <param name="SignatureKey">Matched catalogue signature key, when applicable.</param>
/// <param name="Evidence">Supporting evidence, when present.</param>
public sealed record WafDetectionWire(string Provider, string ChallengeClass, double Confidence, string? SignatureKey, WafEvidenceWire? Evidence);

/// <summary>Wire n-gram row.</summary>
/// <param name="Ngram">N-gram term.</param>
/// <param name="N">Gram size.</param>
/// <param name="Zone">Analyzer zone key.</param>
/// <param name="Count">Occurrences.</param>
/// <param name="Density">Topical density ratio, when computed.</param>
/// <param name="UrlId">URL reference for per-URL rows; null for site rows.</param>
public sealed record NgramRowWire(string Ngram, int N, string Zone, long Count, double? Density, string? UrlId);

/// <summary>Wire semantic relation attached to a URL detail record.</summary>
/// <param name="RelatedUrlId">Related URL identifier.</param>
/// <param name="RelatedCanonicalUrl">Related canonical URL.</param>
/// <param name="Similarity">Cosine similarity in [0,1].</param>
/// <param name="Kind">Relation kind.</param>
public sealed record SemanticRelationWire(string RelatedUrlId, string RelatedCanonicalUrl, double Similarity, string Kind);

/// <summary>Wire full URL detail record.</summary>
/// <param name="Id">URL record identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="StatusCode">Final HTTP status code.</param>
/// <param name="StatusClass">HTTP status class wire value.</param>
/// <param name="ContentType">Response content type.</param>
/// <param name="Depth">Link depth.</param>
/// <param name="Indexable">Indexability flag.</param>
/// <param name="Title">Document title.</param>
/// <param name="BlockReason">Additive block reason.</param>
/// <param name="WafProvider">Additive WAF provider.</param>
/// <param name="ResponseHeaders">Persisted header directives projected as a header map.</param>
/// <param name="ElementInventory">Element inventory counts.</param>
/// <param name="Findings">Findings scoped to this URL.</param>
/// <param name="InboundLinks">Inbound link edges.</param>
/// <param name="OutboundLinks">Outbound link edges.</param>
/// <param name="RedirectChain">Redirect chain hops.</param>
/// <param name="WafDetection">Additive WAF detection, when observed.</param>
/// <param name="ScopeDiagnostics">Additive content-scope diagnostics.</param>
/// <param name="TopNgrams">Additive top n-grams for this URL.</param>
/// <param name="SerpMetrics">Additive SERP preview metrics.</param>
/// <param name="HeaderConflicts">Additive header-vs-HTML conflicts.</param>
/// <param name="SemanticRelations">Additive semantic relations.</param>
public sealed record UrlDetailWire(
    string Id,
    string CanonicalUrl,
    int StatusCode,
    string StatusClass,
    string? ContentType,
    int Depth,
    bool Indexable,
    string? Title,
    string? BlockReason,
    string? WafProvider,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    ElementInventoryWire ElementInventory,
    IReadOnlyList<FindingWire> Findings,
    IReadOnlyList<LinkEdgeWire> InboundLinks,
    IReadOnlyList<LinkEdgeWire> OutboundLinks,
    IReadOnlyList<RedirectHopWire> RedirectChain,
    WafDetectionWire? WafDetection,
    ScopeDiagnosticsWire? ScopeDiagnostics,
    IReadOnlyList<NgramRowWire>? TopNgrams,
    SerpMetricsWire? SerpMetrics,
    IReadOnlyList<HeaderConflictDetailWire>? HeaderConflicts,
    IReadOnlyList<SemanticRelationWire>? SemanticRelations);

/// <summary>Wire crawl health card.</summary>
/// <param name="TotalUrls">Total URL records.</param>
/// <param name="Fetched">Fetched count.</param>
/// <param name="ErrorCount">Error count.</param>
/// <param name="BlockedCount">Blocked count.</param>
/// <param name="AverageTtfbMs">Average TTFB in milliseconds, when observed.</param>
public sealed record CrawlHealthWire(long TotalUrls, long Fetched, long ErrorCount, long BlockedCount, double? AverageTtfbMs);

/// <summary>Wire host-throttle rollup.</summary>
/// <param name="ThrottledHostCount">Hosts with throttle adjustments.</param>
/// <param name="MeanEffectiveRate">Mean effective rate across hosts.</param>
/// <param name="AdjustmentCount">Total throttle adjustments.</param>
public sealed record ThrottleSummaryWire(int ThrottledHostCount, double MeanEffectiveRate, int AdjustmentCount);

/// <summary>Wire WAF summary card.</summary>
/// <param name="TotalBlocked">Total WAF-blocked URLs.</param>
/// <param name="BlockRate">Blocked / fetched ratio in [0,1].</param>
/// <param name="ByProvider">Provider → count.</param>
/// <param name="ByChallengeClass">Challenge class → count.</param>
public sealed record WafSummaryWire(long TotalBlocked, double BlockRate, IReadOnlyDictionary<string, long> ByProvider, IReadOnlyDictionary<string, long> ByChallengeClass);

/// <summary>Wire dashboard summary.</summary>
/// <param name="CountsByStatus">Status class → count.</param>
/// <param name="CountsBySeverity">Severity → count.</param>
/// <param name="CountsByCategory">Category → count.</param>
/// <param name="CrawlHealth">Crawl health card.</param>
/// <param name="Waf">Additive WAF card, when detections exist.</param>
/// <param name="Throttle">Additive throttle card, when events exist.</param>
public sealed record AuditDashboardSummaryWire(
    IReadOnlyDictionary<string, long> CountsByStatus,
    IReadOnlyDictionary<string, long> CountsBySeverity,
    IReadOnlyDictionary<string, long> CountsByCategory,
    CrawlHealthWire CrawlHealth,
    WafSummaryWire? Waf,
    ThrottleSummaryWire? Throttle);

/// <summary>One pre-bucketed chart series point.</summary>
/// <param name="Label">Point label.</param>
/// <param name="Value">Point value.</param>
/// <param name="Dimensions">Point dimensions, when present.</param>
public sealed record ChartPointWire(string Label, double Value, IReadOnlyDictionary<string, string>? Dimensions);

/// <summary>Wire chart series response.</summary>
/// <param name="Metric">Metric key.</param>
/// <param name="Points">Pre-bucketed points.</param>
public sealed record ChartSeriesResponseWire(string Metric, IReadOnlyList<ChartPointWire> Points);

/// <summary>Wire graph node.</summary>
/// <param name="Id">URL record identifier.</param>
/// <param name="Url">Canonical URL.</param>
/// <param name="Label">Display label, when present.</param>
/// <param name="StatusClass">Status class wire value, when fetched.</param>
/// <param name="Depth">Link depth.</param>
/// <param name="LinkEquityPercentile">Link-equity percentile, when computed.</param>
public sealed record GraphNodeWire(string Id, string Url, string? Label, string? StatusClass, int Depth, double? LinkEquityPercentile);

/// <summary>Wire graph edge.</summary>
/// <param name="Source">Source URL identifier.</param>
/// <param name="Target">Target URL identifier.</param>
/// <param name="Weight">Edge weight, when computed.</param>
public sealed record GraphEdgeWire(string Source, string Target, double? Weight);

/// <summary>Wire graph data with full totals.</summary>
/// <param name="Nodes">Rendered nodes.</param>
/// <param name="Edges">Rendered edges.</param>
/// <param name="TotalNodeCount">Total nodes before capping.</param>
/// <param name="TotalEdgeCount">Total edges before capping.</param>
/// <param name="Truncated">Whether the render cap truncated the graph.</param>
public sealed record GraphDataWire(IReadOnlyList<GraphNodeWire> Nodes, IReadOnlyList<GraphEdgeWire> Edges, long TotalNodeCount, long TotalEdgeCount, bool Truncated);

/// <summary>Wire category score.</summary>
/// <param name="Category">Category wire value.</param>
/// <param name="Score">Score in 0..100.</param>
/// <param name="FindingCount">Findings counted for the category.</param>
public sealed record CategoryScoreWire(string Category, double Score, int FindingCount);

/// <summary>Wire audit scores.</summary>
/// <param name="Overall">Overall score in 0..100.</param>
/// <param name="Categories">Per-category scores.</param>
public sealed record AuditScoresWire(double Overall, IReadOnlyList<CategoryScoreWire> Categories);
