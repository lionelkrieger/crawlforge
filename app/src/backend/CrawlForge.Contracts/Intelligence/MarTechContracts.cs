using CrawlForge.Contracts.Audit;

namespace CrawlForge.Contracts.Intelligence;

/// <summary>
/// MarTech / analytics / tag-integrity auditor (FS-CRAWL-002 §14): builds the
/// tag/container inventory from DOM plus proxy capture and raises Analytics
/// &amp; Tagging findings. Vendor names are classification labels for detection
/// targets only (clean-room vocabulary).
/// </summary>
public interface IMarTechAuditor
{
    /// <summary>Audits tags and containers of a page.</summary>
    /// <param name="context">Page audit context (includes proxy captures).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detections and findings.</returns>
    Task<MarTechAuditResult> AuditAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// MarTech audit result.
/// </summary>
/// <param name="Detections">Tag/container detections.</param>
/// <param name="Findings">Analytics &amp; Tagging findings.</param>
public sealed record MarTechAuditResult(
    IReadOnlyList<AnalyticsDetectionDraft> Detections,
    IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// One tag/container detection before persistence (schema catalog:
/// analytics_detections).
/// </summary>
/// <param name="Vendor">Vendor label.</param>
/// <param name="ContainerId">Container/account identifier observed, when present.</param>
/// <param name="DetectionSource">Where the detection came from (dom, proxy).</param>
/// <param name="Placement">Placement detail, when established.</param>
public sealed record AnalyticsDetectionDraft(
    string Vendor,
    string? ContainerId,
    string DetectionSource,
    string? Placement);

/// <summary>
/// dataLayer inspector (FS-CRAWL-002 §14): inspects the dataLayer snapshot
/// against the configured schema and reports malformed payloads.
/// </summary>
public interface IDataLayerInspector
{
    /// <summary>Inspects the dataLayer of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Snapshot, validity, issues, and findings.</returns>
    Task<DataLayerInspectionResult> InspectAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// dataLayer inspection result.
/// </summary>
/// <param name="SnapshotJson">Bounded dataLayer snapshot payload.</param>
/// <param name="IsWellFormed">Whether the payload matches the configured schema.</param>
/// <param name="Issues">Human-readable schema issues.</param>
/// <param name="Findings">dataLayer findings.</param>
public sealed record DataLayerInspectionResult(
    string? SnapshotJson,
    bool IsWellFormed,
    IReadOnlyList<string> Issues,
    IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// Consent Mode v2 analyzer (FS-CRAWL-002 §14): inspects consent state and
/// raises consent-compliance findings.
/// </summary>
public interface IConsentModeAnalyzer
{
    /// <summary>Analyzes Consent Mode v2 state of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Consent state and findings.</returns>
    Task<ConsentModeAnalysisResult> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consent Mode analysis result.
/// </summary>
/// <param name="ConsentStateJson">Observed consent state payload.</param>
/// <param name="ConsentModePresent">Whether Consent Mode v2 was detected.</param>
/// <param name="Findings">Consent-compliance findings.</param>
public sealed record ConsentModeAnalysisResult(
    string? ConsentStateJson,
    bool ConsentModePresent,
    IReadOnlyList<FindingDraft> Findings);
