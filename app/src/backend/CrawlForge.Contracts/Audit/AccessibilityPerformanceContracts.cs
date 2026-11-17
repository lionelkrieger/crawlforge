namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Accessibility analyzer (FS-CRAWL-001 v1.4): accessibility rule families
/// over the parsed/rendered document.
/// </summary>
public interface IAccessibilityAnalyzer
{
    /// <summary>Analyzes accessibility of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accessibility findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rendered Core Web Vitals analyzer (FS-CRAWL-001 v1.4): measures LCP, CLS,
/// and INP from the rendered page plus TTFB from the fetch.
/// </summary>
public interface ICoreWebVitalsAnalyzer
{
    /// <summary>Measures Core Web Vitals of a rendered page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Vital measurements and findings.</returns>
    Task<CoreWebVitalsResult> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Core Web Vitals measurement result. Null vitals indicate the metric could
/// not be measured for the page.
/// </summary>
/// <param name="LcpMilliseconds">Largest Contentful Paint in milliseconds.</param>
/// <param name="Cls">Cumulative Layout Shift score.</param>
/// <param name="InpMilliseconds">Interaction to Next Paint in milliseconds.</param>
/// <param name="TtfbMilliseconds">Time to First Byte in milliseconds.</param>
/// <param name="Findings">Vital-related findings.</param>
public sealed record CoreWebVitalsResult(
    double? LcpMilliseconds,
    double? Cls,
    double? InpMilliseconds,
    double TtfbMilliseconds,
    IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// Performance budget evaluator (FS-CRAWL-001 v1.4): compares rendered
/// performance against user-configured budgets.
/// </summary>
public interface IPerformanceBudgetEvaluator
{
    /// <summary>Evaluates a page against a performance budget.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="budget">Budget to evaluate against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Budget violation findings.</returns>
    Task<IReadOnlyList<FindingDraft>> EvaluateAsync(
        PageAuditContext context,
        PerformanceBudget budget,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A user-configured performance budget. Null fields are not evaluated.
/// </summary>
/// <param name="MaxTotalBytes">Maximum total transferred bytes.</param>
/// <param name="MaxRequests">Maximum request count.</param>
/// <param name="MaxLcpMilliseconds">Maximum LCP.</param>
/// <param name="MaxCls">Maximum CLS.</param>
/// <param name="MaxInpMilliseconds">Maximum INP.</param>
public sealed record PerformanceBudget(
    long? MaxTotalBytes = null,
    int? MaxRequests = null,
    double? MaxLcpMilliseconds = null,
    double? MaxCls = null,
    double? MaxInpMilliseconds = null);

/// <summary>
/// Code coverage analyzer (FS-CRAWL-001 v1.4): measures used vs unused
/// JavaScript/CSS bytes on the rendered page.
/// </summary>
public interface ICodeCoverageAnalyzer
{
    /// <summary>Measures code coverage of a rendered page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Coverage measurements.</returns>
    Task<CodeCoverageResult> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Code coverage measurement result.
/// </summary>
/// <param name="UsedBytes">Bytes of code actually used.</param>
/// <param name="TotalBytes">Total bytes of code loaded.</param>
/// <param name="UnusedPercent">Percentage of unused code (0–100).</param>
public sealed record CodeCoverageResult(long UsedBytes, long TotalBytes, double UnusedPercent);
