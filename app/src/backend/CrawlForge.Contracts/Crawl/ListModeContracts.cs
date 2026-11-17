using CrawlForge.Contracts.Entities;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Ingests user-supplied URL lists as crawl seeds with bounded discovery
/// (FS-CRAWL-002 §10 List Mode). Ingestion is local; list-mode audits reproduce
/// baseline behavior when unconfigured.
/// </summary>
public interface IListModeIngestor
{
    /// <summary>Ingests a URL list into an audit's frontier.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urls">User-supplied URLs.</param>
    /// <param name="allowDiscovery">Whether bounded link discovery from list seeds is enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ingestion counters.</returns>
    Task<ListIngestionResult> IngestAsync(
        long auditId,
        IReadOnlyList<string> urls,
        bool allowDiscovery,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Counters produced by list ingestion.
/// </summary>
/// <param name="IngestedCount">URLs accepted as seeds.</param>
/// <param name="DuplicateCount">URLs skipped as duplicates.</param>
/// <param name="InvalidCount">URLs rejected as invalid.</param>
public sealed record ListIngestionResult(int IngestedCount, int DuplicateCount, int InvalidCount);

/// <summary>
/// Validates migration redirect matrices (FS-CRAWL-002 §10): per-row verdicts,
/// hop chains, and canonical evidence for staging↔production host mappings.
/// </summary>
public interface IRedirectMatrixValidator
{
    /// <summary>Validates every mapping row of an audit's migration matrix.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="mappings">Matrix rows to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-row verdicts in ordinal order.</returns>
    Task<IReadOnlyList<RedirectMatrixRowResult>> ValidateAsync(
        long auditId,
        IReadOnlyList<RedirectMappingDto> mappings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validation result of one redirect-matrix row.
/// </summary>
/// <param name="Mapping">The validated mapping row.</param>
/// <param name="Verdict">Row verdict.</param>
/// <param name="ActualFinalUrl">Observed final URL, when reachable.</param>
/// <param name="HopCount">Number of observed redirect hops.</param>
/// <param name="Chain">Observed hop chain.</param>
/// <param name="CanonicalUrl">Canonical URL of the final page, when present.</param>
public sealed record RedirectMatrixRowResult(
    RedirectMappingDto Mapping,
    Enums.RedirectMatrixVerdict Verdict,
    string? ActualFinalUrl,
    int HopCount,
    IReadOnlyList<RedirectHop> Chain,
    string? CanonicalUrl);
