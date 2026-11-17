using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// Additive metric keys owned by the link-graph workpiece (metric_summaries is an
/// open-key surface; existing keys are never repurposed). The spec-mandated
/// weighted key lives in <see cref="Contracts.Constants.MetricKeys.LinkEquityWeighted"/>;
/// the unweighted distribution is retained for comparability (FS-CRAWL-002 §15.2).
/// Per-URL equity rows carry dimensions <c>{"urlId":&lt;id&gt;}</c>.
/// </summary>
public static class LinkGraphMetricKeys
{
    /// <summary>Unweighted link-equity distribution (comparability baseline, §15.2).</summary>
    public const string LinkEquityUnweighted = "link-equity-unweighted";

    /// <summary>Count of orphan URLs (no internal in-links, self-loops excluded).</summary>
    public const string OrphanCount = "orphan-count";

    /// <summary>Orphan rate over all crawled URLs (0–1).</summary>
    public const string OrphanRate = "orphan-rate";

    /// <summary>Internal link-edge count.</summary>
    public const string InternalLinkCount = "internal-link-count";

    /// <summary>External link-edge count.</summary>
    public const string ExternalLinkCount = "external-link-count";

    /// <summary>Connected components of the internal link graph (undirected).</summary>
    public const string ConnectedComponents = "connected-components";

    /// <summary>Equity concentration: share of total weighted equity held by the top 1% of URLs (0–1).</summary>
    public const string EquityConcentration = "equity-concentration";
}

/// <summary>
/// One node of the link-graph read model. Shape is documented for the phase-06
/// Sigma.js/Graphology surfaces: <see cref="UrlId"/> is the stable node key
/// (stringify for Graphology), <see cref="CanonicalUrl"/> the label, and the
/// equity/degree fields drive weighted/unweighted node sizing (§15.2 toggle).
/// </summary>
/// <param name="UrlId">url_records identifier (node key).</param>
/// <param name="CanonicalUrl">Canonical URL (node label).</param>
/// <param name="Host">Host of the URL.</param>
/// <param name="Depth">Crawl depth.</param>
/// <param name="WeightedEquity">Zone-weighted equity score (0 when not computed).</param>
/// <param name="UnweightedEquity">Unweighted equity score (0 when not computed).</param>
/// <param name="InDegree">Internal in-links (self-loops excluded).</param>
/// <param name="OutDegree">Internal out-links.</param>
/// <param name="IsOrphan">True when the URL has no internal in-links.</param>
/// <param name="IsSeed">True when the URL entered as a seed or list-mode input.</param>
public sealed record LinkGraphNode(
    long UrlId,
    string CanonicalUrl,
    string Host,
    int Depth,
    double WeightedEquity,
    double UnweightedEquity,
    long InDegree,
    long OutDegree,
    bool IsOrphan,
    bool IsSeed);

/// <summary>
/// One edge of the link-graph read model. Internal edges reference both node
/// keys; external edges carry a null target key and the canonical target URL
/// (consumers may render external stub nodes). Zone and evidence ride the
/// additive §15.5 columns.
/// </summary>
/// <param name="EdgeId">link_edges identifier (stable edge key).</param>
/// <param name="SourceUrlId">Source node key.</param>
/// <param name="TargetUrlId">Target node key; null for external targets.</param>
/// <param name="CanonicalTargetUrl">Canonical target URL.</param>
/// <param name="Zone">Classified DOM zone, when present.</param>
/// <param name="ZoneEvidence">Evidence supporting the zone classification.</param>
/// <param name="RelationFlags">Observed relation flags.</param>
/// <param name="IsInternal">Whether the target is internal to the crawl scope.</param>
/// <param name="WeightedEquity">Per-edge transferred equity, when computed.</param>
public sealed record LinkGraphEdge(
    long EdgeId,
    long SourceUrlId,
    long? TargetUrlId,
    string CanonicalTargetUrl,
    DomZone? Zone,
    string? ZoneEvidence,
    LinkRelationFlags RelationFlags,
    bool IsInternal,
    double? WeightedEquity);

/// <summary>
/// Site-wide link-graph aggregates computed by streaming over link_edges and
/// url_records (bounded memory at 500k+ URLs). Orphan definition: a URL with no
/// internal in-links from another URL (self-loops excluded); seeds are reported
/// separately so consumers can distinguish entry points from true orphans.
/// </summary>
/// <param name="UrlCount">Total url_records rows for the audit.</param>
/// <param name="InternalEdgeCount">Internal link edges.</param>
/// <param name="ExternalEdgeCount">External link edges.</param>
/// <param name="OrphanCount">URLs without internal in-links.</param>
/// <param name="OrphanRate">OrphanCount / UrlCount (0 when empty).</param>
/// <param name="SeedCount">URLs discovered as seeds or list inputs.</param>
/// <param name="AverageInDegree">Mean internal in-degree over all URLs.</param>
/// <param name="MaxInDegree">Maximum internal in-degree.</param>
/// <param name="ConnectedComponentCount">Undirected components of the internal graph (isolated URLs count).</param>
/// <param name="ZoneDistribution">Edge counts per classified DOM zone.</param>
public sealed record LinkGraphAggregate(
    long UrlCount,
    long InternalEdgeCount,
    long ExternalEdgeCount,
    long OrphanCount,
    double OrphanRate,
    long SeedCount,
    double AverageInDegree,
    long MaxInDegree,
    long ConnectedComponentCount,
    IReadOnlyDictionary<DomZone, long> ZoneDistribution);

/// <summary>
/// One orphan URL surfaced by streaming orphan detection.
/// </summary>
/// <param name="UrlId">url_records identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="Host">Host of the URL.</param>
/// <param name="Depth">Crawl depth.</param>
/// <param name="DiscoverySource">How the URL entered the crawl.</param>
/// <param name="IsSeed">True for seed/list entry points.</param>
public sealed record OrphanUrlRecord(
    long UrlId,
    string CanonicalUrl,
    string Host,
    int Depth,
    DiscoverySource DiscoverySource,
    bool IsSeed);

/// <summary>
/// Bounded window query for the interactive graph surface. The snapshot returns
/// the top nodes by weighted equity (id order when equity is absent) plus the
/// edges between them, so the UI never materializes a 500k-node graph at once;
/// full-graph consumers use the streaming feeds instead.
/// </summary>
/// <param name="NodeLimit">Maximum nodes in the snapshot window.</param>
/// <param name="IncludeExternalEdges">Also include external edges whose source is in the window.</param>
/// <param name="ZoneFilter">Restrict edges to one DOM zone (§15.5 domZone filter).</param>
public sealed record LinkGraphQuery(
    int NodeLimit = 2000,
    bool IncludeExternalEdges = false,
    DomZone? ZoneFilter = null);

/// <summary>
/// A bounded graph window plus whole-graph totals for UI context.
/// </summary>
/// <param name="Aggregate">Whole-graph aggregates.</param>
/// <param name="Nodes">The returned node window.</param>
/// <param name="Edges">Edges within the window (plus optional external edges).</param>
/// <param name="TotalNodeCount">Total nodes in the audit graph.</param>
/// <param name="TotalEdgeCount">Total edges in the audit graph.</param>
public sealed record LinkGraphSnapshot(
    LinkGraphAggregate Aggregate,
    IReadOnlyList<LinkGraphNode> Nodes,
    IReadOnlyList<LinkGraphEdge> Edges,
    long TotalNodeCount,
    long TotalEdgeCount);

/// <summary>
/// Local link-graph analysis contract (no locked Contracts interface exists for
/// graph aggregates; promoted via serialized amendment if phase-05 needs it).
/// Implementations stream over the persisted graph with bounded memory.
/// </summary>
public interface ILinkGraphAnalyzer
{
    /// <summary>Computes whole-graph aggregates in streaming passes.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate summary.</returns>
    Task<LinkGraphAggregate> ComputeAggregateAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Streams orphan URLs (no internal in-links, self-loops excluded).</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Orphan records in url_records id order.</returns>
    IAsyncEnumerable<OrphanUrlRecord> StreamOrphansAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local graph read-model contract for the phase-06 Sigma.js/Graphology surfaces
/// (approved local pattern, ILinkDiscoverySink precedent). Node/edge shapes are
/// documented on <see cref="LinkGraphNode"/> and <see cref="LinkGraphEdge"/>:
/// Graphology imports nodes keyed by UrlId and edges keyed by EdgeId; node
/// sizing toggles between WeightedEquity and UnweightedEquity (§15.2/§15.6).
/// </summary>
public interface ILinkGraphReadModel
{
    /// <summary>Returns a bounded graph window plus whole-graph totals.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="query">Window query; null uses defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot.</returns>
    Task<LinkGraphSnapshot> GetSnapshotAsync(long auditId, LinkGraphQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Streams every node with degrees and equity (full-graph feed, bounded memory).</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nodes in url_records id order.</returns>
    IAsyncEnumerable<LinkGraphNode> StreamNodesAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Streams every edge (full-graph feed, bounded memory).</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Edges in link_edges id order.</returns>
    IAsyncEnumerable<LinkGraphEdge> StreamEdgesAsync(long auditId, CancellationToken cancellationToken = default);
}
