using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Additive per-block validation detail produced by the structured-data engine
/// (FS-CRAWL-001 §7.13). The locked <see cref="StructuredDataBlock"/> contract
/// shape is preserved verbatim inside <see cref="Block"/>; the detail members
/// carry the advisory and relationship facts the pipeline rule adapter reports
/// as findings (CAP-B2-073 entity relationships, CAP-B2-075 missing required
/// properties, CAP-B2-076 missing recommended properties).
/// </summary>
/// <param name="Block">The locked-contract block (format, type, validity, errors, eligibility).</param>
/// <param name="Ordinal">Zero-based block ordinal in document order within the page.</param>
/// <param name="MissingRequired">Vocabulary-required properties that are absent or empty (named).</param>
/// <param name="MissingRecommended">Vocabulary-recommended properties that are absent or empty (advisory).</param>
/// <param name="NestedEntityTypes">Raw type names of direct nested entities (relationships).</param>
/// <param name="DeprecatedEntry">
/// Additive (CR-030/D-021): the bundled retired/restricted rich-result list
/// entry for the block's type, when the type is on the list; null otherwise.
/// The pipeline rule adapter reports it as <c>structured-data.deprecated-term</c>.
/// </param>
public sealed record StructuredDataBlockDetail(
    StructuredDataBlock Block,
    int Ordinal,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> MissingRecommended,
    IReadOnlyList<string> NestedEntityTypes,
    DeprecatedRichResultEntry? DeprecatedEntry = null);

/// <summary>
/// Detailed structured-data validation result: every parsed block on the page
/// in document order, each with its locked-contract outcome plus the additive
/// detail. The single-page checker tool (UI-19) and the audit pipeline rule
/// adapter both consume this one engine output.
/// </summary>
/// <param name="Blocks">Per-block details in document order.</param>
public sealed record StructuredDataDetailedResult(IReadOnlyList<StructuredDataBlockDetail> Blocks);
