using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Standalone single-page analysis (REST: POST /api/analysis/single-page):
/// findings and element inventory for one URL without a full audit.
/// </summary>
public interface ISinglePageAnalyzer
{
    /// <summary>Analyzes a single page on demand.</summary>
    /// <param name="request">Single-page analysis request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings, inventory, and additive diagnostics.</returns>
    Task<SinglePageAnalysisResult> AnalyzeAsync(SinglePageAnalysisRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A single-page analysis request.
/// </summary>
/// <param name="Url">Target URL.</param>
/// <param name="Render">Whether to render the page before analysis.</param>
/// <param name="RuleKeys">Optional rule filter restricting the analysis.</param>
public sealed record SinglePageAnalysisRequest(
    string Url,
    bool Render = false,
    IReadOnlyList<string>? RuleKeys = null);

/// <summary>
/// Result of a single-page analysis, including the additive WAF verdict and
/// scope diagnostics (FS-CRAWL-002 Appendix B).
/// </summary>
/// <param name="Findings">Findings for the page.</param>
/// <param name="Inventory">Element inventory for the page.</param>
/// <param name="WafVerdict">WAF verdict, when evaluated.</param>
/// <param name="ScopeDiagnostics">Resolved scope diagnostics, when scoping ran.</param>
public sealed record SinglePageAnalysisResult(
    IReadOnlyList<FindingDraft> Findings,
    IReadOnlyList<ElementInventoryItem> Inventory,
    WafEvaluationResult? WafVerdict,
    ScopedVisibleText? ScopeDiagnostics);

/// <summary>
/// Generic rule host (FS-CRAWL-001 §7.4): runs an extensible set of audit rules
/// over the page context. New rule keys arrive additively in the taxonomy.
/// </summary>
public interface IAuditRuleAnalyzer
{
    /// <summary>Rule keys this host can evaluate.</summary>
    IReadOnlyList<string> SupportedRuleKeys { get; }

    /// <summary>Evaluates the supported rules against a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings produced by the rules.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Indexability analyzer (FS-CRAWL-001 §7.6): status classes, robots state,
/// meta/header directives, and canonicalization checks.
/// </summary>
public interface IIndexabilityAnalyzer
{
    /// <summary>Analyzes indexability of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Indexability findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Header/HTML directive equivalency analyzer (FS-CRAWL-002 §15): detects
/// conflicts between HTTP-header directives and their HTML equivalents
/// (indexability, canonical, hreflang).
/// </summary>
public interface IHeaderHtmlEquivalencyAnalyzer
{
    /// <summary>Compares header directives with HTML directives.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Conflict findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Response-vs-rendered comparer (FS-CRAWL-001 v1.4): compares the raw HTTP
/// response with the rendered document to surface client-side differences.
/// </summary>
public interface IResponseRenderedComparer
{
    /// <summary>Compares response and rendered content of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Differences and findings.</returns>
    Task<ResponseRenderedComparison> CompareAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a response-vs-rendered comparison.
/// </summary>
/// <param name="Differences">Field-level differences.</param>
/// <param name="Findings">Findings derived from the differences.</param>
public sealed record ResponseRenderedComparison(
    IReadOnlyList<ContentDifference> Differences,
    IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// One field-level difference between response and rendered content.
/// </summary>
/// <param name="Field">Compared field (title, headings, text, metadata).</param>
/// <param name="ResponseValue">Value in the raw response.</param>
/// <param name="RenderedValue">Value in the rendered document.</param>
public sealed record ContentDifference(string Field, string ResponseValue, string RenderedValue);

/// <summary>
/// Soft-404 detector (FS-CRAWL-001 v1.4): distinguishes soft-404 pages from
/// genuine content and from WAF challenge pages (FS-CRAWL-002 §7.12 distinction).
/// </summary>
public interface ISoft404Detector
{
    /// <summary>Evaluates whether a page is a soft 404.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Soft-404 verdict with confidence and evidence.</returns>
    Task<Soft404Verdict> DetectAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Soft-404 verdict.
/// </summary>
/// <param name="IsSoft404">Whether the page is classified as a soft 404.</param>
/// <param name="Confidence">Classification confidence (0–1).</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
public sealed record Soft404Verdict(bool IsSoft404, double Confidence, string EvidenceJson);
