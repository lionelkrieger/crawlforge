namespace CrawlForge.Contracts.Persistence;

/// <summary>
/// Project archive import/export (FS-CRAWL-001 §9): portable project archives
/// for local backup and transfer. Archives are local artifacts; no remote
/// service is involved.
/// </summary>
public interface IProjectArchiveService
{
    /// <summary>Exports a project and its audits into an archive stream.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="destination">Destination stream owned by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the archive is written.</returns>
    Task ExportAsync(long projectId, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Imports a project archive.</summary>
    /// <param name="archive">Archive stream owned by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import counters.</returns>
    Task<ProjectArchiveImportResult> ImportAsync(Stream archive, CancellationToken cancellationToken = default);
}

/// <summary>
/// Project archive import result.
/// </summary>
/// <param name="ProjectId">Identifier of the imported project.</param>
/// <param name="AuditsImported">Number of audits imported.</param>
/// <param name="UrlsImported">Number of URL records imported.</param>
public sealed record ProjectArchiveImportResult(long ProjectId, int AuditsImported, int UrlsImported);
