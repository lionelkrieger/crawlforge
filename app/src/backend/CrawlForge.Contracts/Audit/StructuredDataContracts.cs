namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Structured-data validator (FS-CRAWL-001 §7.13): parses JSON-LD, Microdata,
/// and RDFa blocks and reports validation / rich-result outcomes.
/// </summary>
public interface IStructuredDataValidator
{
    /// <summary>Validates structured data found in markup.</summary>
    /// <param name="markup">HTML or markup fragment containing structured data.</param>
    /// <param name="sourceUrl">Optional source URL for context.</param>
    /// <returns>Parsed blocks with validation outcomes.</returns>
    StructuredDataValidationResult Validate(string markup, string? sourceUrl);
}

/// <summary>
/// Structured-data validation result.
/// </summary>
/// <param name="Blocks">Parsed structured-data blocks.</param>
public sealed record StructuredDataValidationResult(IReadOnlyList<StructuredDataBlock> Blocks);

/// <summary>
/// One parsed structured-data block.
/// </summary>
/// <param name="Format">Markup format (json-ld, microdata, rdfa).</param>
/// <param name="Type">Primary schema type, when identified.</param>
/// <param name="IsValid">Whether the block validates.</param>
/// <param name="Errors">Validation errors.</param>
/// <param name="RichResultEligible">Whether the block is rich-result eligible.</param>
public sealed record StructuredDataBlock(
    string Format,
    string? Type,
    bool IsValid,
    IReadOnlyList<string> Errors,
    bool RichResultEligible);

/// <summary>
/// Internationalization analyzer (FS-CRAWL-001 §7.13): hreflang and language
/// annotation checks, including header/HTML equivalency conflicts.
/// </summary>
public interface II18nAnalyzer
{
    /// <summary>Analyzes i18n annotations of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>i18n findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mobile-friendliness analyzer (FS-CRAWL-001 §7.13): viewport, tap targets,
/// and mobile rendering checks.
/// </summary>
public interface IMobileFriendlinessAnalyzer
{
    /// <summary>Analyzes mobile friendliness of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Mobile-friendliness findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Technology detector (FS-CRAWL-001 §7.15): detects technologies from
/// fingerprints. Fingerprint catalogues are bundled assets; runtime fetch is
/// forbidden (exclusion E2 applies to catalogue services).
/// </summary>
public interface ITechnologyDetector
{
    /// <summary>Detects technologies on a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Technology detections with confidence.</returns>
    Task<IReadOnlyList<TechnologyDetectionDraft>> DetectAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// A technology detection before persistence (schema catalog:
/// technology_detections).
/// </summary>
/// <param name="TechnologyKey">Stable technology key.</param>
/// <param name="Category">Technology category.</param>
/// <param name="Confidence">Detection confidence (0–1).</param>
public sealed record TechnologyDetectionDraft(string TechnologyKey, string Category, double Confidence);
