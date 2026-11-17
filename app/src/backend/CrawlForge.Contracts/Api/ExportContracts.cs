using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Api;

/// <summary>
/// Export engine (FS-CRAWL-001 §8.4/§8.5): generates spreadsheet, CSV, HTML
/// bundle, PDF, and XML sitemap exports. Long operations return immediately
/// with an identifier; progress flows over the hub.
/// </summary>
public interface IExportEngine
{
    /// <summary>Creates an export job and returns immediately.</summary>
    /// <param name="request">Export request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created export job.</returns>
    Task<ExportJobDto> CreateExportAsync(ExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads the current state of an export job.</summary>
    /// <param name="exportJobId">Export job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export job, or null when absent.</returns>
    Task<ExportJobDto?> GetExportAsync(long exportJobId, CancellationToken cancellationToken = default);

    /// <summary>Opens the generated export file for download.</summary>
    /// <param name="exportJobId">Export job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable file stream owned by the caller.</returns>
    Task<Stream> OpenExportFileAsync(long exportJobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An export creation request (REST: POST /api/exports). Dataset selection
/// includes the additive datasets waf-blocks, ngrams, redirect-matrix,
/// semantic-opportunity, and analytics (FS-CRAWL-002 Appendix B).
/// </summary>
/// <param name="AuditId">Audit to export.</param>
/// <param name="Format">Output format.</param>
/// <param name="Datasets">Selected dataset keys.</param>
public sealed record ExportRequest(long AuditId, ExportFormat Format, IReadOnlyList<string> Datasets);

/// <summary>
/// An export job.
/// </summary>
/// <param name="Id">Export job identifier.</param>
/// <param name="AuditId">Audit being exported.</param>
/// <param name="Format">Output format.</param>
/// <param name="Status">Job status.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="FilePath">Local file path when completed.</param>
/// <param name="Datasets">Selected dataset keys.</param>
public sealed record ExportJobDto(
    long Id,
    long AuditId,
    ExportFormat Format,
    ExportJobStatus Status,
    DateTimeOffset CreatedAt,
    string? FilePath,
    IReadOnlyList<string> Datasets);

/// <summary>
/// Delivery to user-configured export destinations (FS-CRAWL-001 §8.5).
/// Destinations are off by default, none are seeded, and credentials are
/// user-owned secrets used solely for the user's own outbound connections —
/// never vendor services (exclusion row 7).
/// </summary>
public interface IExportDestinationDelivery
{
    /// <summary>Tests reachability and credentials of a destination.</summary>
    /// <param name="destinationId">Destination identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reachability and credential verdict.</returns>
    Task<DestinationTestResult> TestAsync(long destinationId, CancellationToken cancellationToken = default);

    /// <summary>Delivers a completed export to a destination.</summary>
    /// <param name="exportJobId">Export job identifier.</param>
    /// <param name="destinationId">Destination identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when delivery finishes.</returns>
    Task DeliverAsync(long exportJobId, long destinationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Destination test verdict (REST: POST /api/destinations/{id}/test).
/// </summary>
/// <param name="IsReachable">Whether the endpoint is reachable.</param>
/// <param name="CredentialsValid">Whether the user's credentials were accepted.</param>
/// <param name="Message">Human-readable verdict detail.</param>
public sealed record DestinationTestResult(bool IsReachable, bool CredentialsValid, string? Message);
