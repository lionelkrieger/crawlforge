using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;

namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Eight-step audit pipeline orchestrator (FS-CRAWL-001 §7.2): runs the
/// analyzer family over one fetched page and collects findings, inventory,
/// and metric contributions.
/// </summary>
public interface IAuditPipeline
{
    /// <summary>Audits one page through the full pipeline.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings, element inventory, and metric contributions.</returns>
    Task<PageAuditResult> RunAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tolerant HTML parser (FS-CRAWL-001 §7.2): never fails on malformed markup;
/// reports parse warnings instead.
/// </summary>
public interface ITolerantDomParser
{
    /// <summary>Parses HTML into a tolerant document model.</summary>
    /// <param name="html">Raw HTML.</param>
    /// <param name="url">Source URL of the document.</param>
    /// <returns>The parsed document with warnings.</returns>
    DomDocument Parse(string html, string url);
}

/// <summary>
/// Extracts the DOM element inventory (FS-CRAWL-001 §7.3): one row per
/// extracted element group instance (schema catalog: url_elements).
/// </summary>
public interface IElementInventoryExtractor
{
    /// <summary>Extracts element groups from a parsed document.</summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Inventory items in extraction order.</returns>
    Task<IReadOnlyList<ElementInventoryItem>> ExtractAsync(DomDocument document, CancellationToken cancellationToken = default);
}

/// <summary>
/// A parsed document in the contract DOM model. Implementations map their
/// internal parser output onto this dependency-free shape.
/// </summary>
/// <param name="Url">Source URL of the document.</param>
/// <param name="Root">Root node of the document tree.</param>
/// <param name="ParseWarnings">Tolerant-parse warnings.</param>
public sealed record DomDocument(
    string Url,
    DomNode Root,
    IReadOnlyList<string> ParseWarnings);

/// <summary>
/// One node of the contract DOM tree.
/// </summary>
/// <param name="TagName">Element tag name (lowercase).</param>
/// <param name="Attributes">Element attributes.</param>
/// <param name="Children">Child nodes in document order.</param>
/// <param name="TextContent">Direct text content of the node.</param>
public sealed record DomNode(
    string TagName,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<DomNode> Children,
    string TextContent);

/// <summary>
/// Everything a page-level analyzer receives: crawl facts, configuration,
/// fetch result, parsed and rendered documents, resolved scope, and proxy
/// captures. Additive context members may be appended as optional properties.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="Url">The URL record under audit.</param>
/// <param name="Configuration">Configuration snapshot governing the audit.</param>
/// <param name="Fetch">Fetch result, when the page was fetched over HTTP.</param>
/// <param name="Document">Parsed response document, when available.</param>
/// <param name="RenderedDocument">Parsed rendered document, when rendered.</param>
/// <param name="ScopedText">Resolved scoped visible text, when scoping ran.</param>
/// <param name="CapturedRequests">Proxy-captured requests, when capture ran.</param>
public sealed record PageAuditContext(
    long AuditId,
    UrlRecordDto Url,
    CrawlConfigurationDto Configuration,
    HttpFetchResult? Fetch,
    DomDocument? Document,
    DomDocument? RenderedDocument,
    ScopedVisibleText? ScopedText,
    IReadOnlyList<ProxyCapturedRequest>? CapturedRequests);

/// <summary>
/// Aggregated output of one page audit.
/// </summary>
/// <param name="Findings">Findings produced for the page.</param>
/// <param name="Inventory">Element inventory items.</param>
/// <param name="Metrics">Metric contributions.</param>
public sealed record PageAuditResult(
    IReadOnlyList<FindingDraft> Findings,
    IReadOnlyList<ElementInventoryItem> Inventory,
    IReadOnlyList<MetricContribution> Metrics);

/// <summary>
/// One extracted element group instance (schema catalog: url_elements).
/// </summary>
/// <param name="Group">Element group name (open string; see Constants.ElementGroups).</param>
/// <param name="PayloadJson">Structured payload of the group instance.</param>
/// <param name="Ordinal">Zero-based instance ordinal within the group.</param>
public sealed record ElementInventoryItem(string Group, string PayloadJson, int Ordinal);

/// <summary>
/// A finding produced by an analyzer before persistence. Persisted findings
/// additionally carry identifiers (see <see cref="FindingDto"/>).
/// </summary>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="Category">Taxonomy category.</param>
/// <param name="Severity">Finding severity.</param>
/// <param name="UrlId">URL reference; null for site-wide findings.</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
/// <param name="RecommendationKey">Optional recommendation catalog key.</param>
public sealed record FindingDraft(
    string RuleKey,
    Enums.AuditCategory Category,
    Enums.FindingSeverity Severity,
    long? UrlId,
    string EvidenceJson,
    string? RecommendationKey);

/// <summary>
/// One metric contribution produced during auditing (schema catalog:
/// metric_summaries).
/// </summary>
/// <param name="MetricKey">Stable metric key (see Constants.MetricKeys).</param>
/// <param name="DimensionsJson">Dimension payload.</param>
/// <param name="Value">Numeric value.</param>
public sealed record MetricContribution(string MetricKey, string DimensionsJson, double Value);
