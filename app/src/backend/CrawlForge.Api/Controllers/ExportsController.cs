using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Export jobs (FS-CRAWL-001 §10.2): creation delegates to the locked export
/// engine (long operations return immediately; progress flows over the hub),
/// file download streams the local artifact without a JSON envelope.
/// Additive GET /api/exports/{id} exposes job state for polling.
/// </summary>
[Route("api/exports")]
public sealed class ExportsController : ApiControllerBase
{
    private readonly IExportEngine _engine;
    private readonly IExportDestinationDelivery _delivery;
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<ExportDestinationDto> _destinations;

    /// <summary>Creates the controller.</summary>
    /// <param name="engine">Locked export engine.</param>
    /// <param name="delivery">Locked destination delivery.</param>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="destinations">Destination read repository.</param>
    public ExportsController(
        IExportEngine engine,
        IExportDestinationDelivery delivery,
        IReadRepository<AuditDto> audits,
        IReadRepository<ExportDestinationDto> destinations)
    {
        _engine = engine;
        _delivery = delivery;
        _audits = audits;
        _destinations = destinations;
    }

    /// <summary>Creates an export job; returns immediately with the job state.</summary>
    /// <param name="request">Audit, format, datasets, optional destination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Export identifier and state.</returns>
    [HttpPost]
    public async Task<ObjectResult> CreateAsync([FromBody] ExportCreateRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.AuditId, out var auditId) || await _audits.GetByIdAsync(auditId, cancellationToken) is null)
        {
            return NotFoundError($"Audit {request.AuditId} does not exist.");
        }

        long? destinationId = null;
        if (request.DestinationId is not null)
        {
            if (!long.TryParse(request.DestinationId, out var parsedDestination) || await _destinations.GetByIdAsync(parsedDestination, cancellationToken) is null)
            {
                return NotFoundError($"Destination {request.DestinationId} does not exist.");
            }

            destinationId = parsedDestination;
        }

        // WP-09-11 wiring (WP-09-10 additive surface): json/jsonl/text/markdown
        // travel as wire strings through the additive engine surface while the
        // locked ExportFormat enum amendment is pending. The locked interface
        // is unchanged; the concrete engine exposes the additive creation.
        if (CrawlForge.Export.AdditiveExportFormats.IsKnown(request.Format))
        {
            if (_engine is not CrawlForge.Export.ExportEngine additiveEngine)
            {
                return ValidationError($"Export format '{request.Format}' is unavailable.", [new ApiFieldError("format", "The additive export engine is not registered.")]);
            }

            if (request.Datasets.Count == 0 && request.Format is not ("text" or "markdown"))
            {
                return ValidationError("At least one dataset must be selected.", [new ApiFieldError("datasets", "Select one or more dataset keys.")]);
            }

            var additiveJob = await additiveEngine.CreateAdditiveExportAsync(auditId, request.Format, request.Datasets, cancellationToken);
            if (destinationId is not null && additiveJob.Status == ExportJobStatus.Completed)
            {
                try
                {
                    await _delivery.DeliverAsync(additiveJob.Id, destinationId.Value, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Recorded in the delivery ledger and export-delivery hub event.
                }
            }

            return CreatedEnvelope(new ExportCreateResponse(additiveJob.Id.ToString(), StateWire(additiveJob.Status)));
        }

        if (!TryParseFormat(request.Format, out var format))
        {
            return ValidationError($"Unknown export format '{request.Format}'.", [new ApiFieldError("format", "Use xlsx, csv, html, pdf, xml-sitemap, json, jsonl, text, or markdown.")]);
        }

        if (request.Datasets.Count == 0)
        {
            return ValidationError("At least one dataset must be selected.", [new ApiFieldError("datasets", "Select one or more dataset keys.")]);
        }

        var job = await _engine.CreateExportAsync(new ExportRequest(auditId, format, request.Datasets), cancellationToken);

        // Best-effort inline delivery when the engine completes synchronously.
        // Asynchronous jobs are delivered by the explicit deliver route or by
        // schedule completion actions once the export-completed event fires
        // (Host wiring); delivery failures never fail the export itself.
        if (destinationId is not null && job.Status == ExportJobStatus.Completed)
        {
            try
            {
                await _delivery.DeliverAsync(job.Id, destinationId.Value, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Recorded in the delivery ledger and export-delivery hub event.
            }
        }

        return CreatedEnvelope(new ExportCreateResponse(job.Id.ToString(), StateWire(job.Status)));
    }

    /// <summary>Reads one export job (additive polling route).</summary>
    /// <param name="id">Export job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Export job record.</returns>
    [HttpGet("{id:long}")]
    public async Task<ObjectResult> GetAsync(long id, CancellationToken cancellationToken)
    {
        // WP-09-11: the unified additive projection covers baseline AND
        // additive jobs (the locked GetExportAsync returns null for additive).
        var job = await GetAnyExportAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFoundError($"Export job {id} does not exist.");
        }

        return OkEnvelope(new ExportJobWire(
            job.Id.ToString(),
            job.AuditId.ToString(),
            job.Format,
            StateWire(job.Status),
            job.CreatedAt,
            job.FilePath,
            job.Datasets));
    }

    /// <summary>Streams the generated export file (not JSON-enveloped).</summary>
    /// <param name="id">Export job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file stream.</returns>
    [HttpGet("{id:long}/file")]
    public async Task<IActionResult> DownloadAsync(long id, CancellationToken cancellationToken)
    {
        var job = await GetAnyExportAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFoundError($"Export job {id} does not exist.");
        }

        if (job.Status != ExportJobStatus.Completed)
        {
            return ConflictError($"Export job {id} is not completed (state {StateWire(job.Status)}).");
        }

        try
        {
            var stream = await _engine.OpenExportFileAsync(id, cancellationToken);
            var fileName = job.FilePath is null ? $"export-{id}" : Path.GetFileName(job.FilePath);
            return File(stream, ContentTypeForWire(job.Format), fileName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return NotFoundError($"Export file for job {id} is unavailable.");
        }
    }

    /// <summary>
    /// Unified job lookup: the additive projection when the concrete engine is
    /// registered (covers additive formats), the locked surface otherwise.
    /// </summary>
    private async Task<CrawlForge.Export.AdditiveExportJobDto?> GetAnyExportAsync(long id, CancellationToken cancellationToken)
    {
        if (_engine is CrawlForge.Export.ExportEngine additiveEngine)
        {
            return await additiveEngine.GetAdditiveExportAsync(id, cancellationToken);
        }

        var job = await _engine.GetExportAsync(id, cancellationToken);
        return job is null ? null : new CrawlForge.Export.AdditiveExportJobDto(
            job.Id, job.AuditId, FormatWire(job.Format), job.Status, job.CreatedAt, job.FilePath, job.Datasets);
    }

    /// <summary>Parses a wire format key.</summary>
    /// <param name="wire">Wire key.</param>
    /// <param name="format">Parsed format.</param>
    /// <returns>True when recognized.</returns>
    public static bool TryParseFormat(string? wire, out ExportFormat format)
    {
        switch (wire)
        {
            case "xlsx":
                format = ExportFormat.Excel;
                return true;
            case "csv":
                format = ExportFormat.Csv;
                return true;
            case "html":
                format = ExportFormat.Html;
                return true;
            case "pdf":
                format = ExportFormat.Pdf;
                return true;
            case "xml-sitemap":
                format = ExportFormat.XmlSitemap;
                return true;
            default:
                format = ExportFormat.Excel;
                return false;
        }
    }

    /// <summary>Projects a format to its wire key.</summary>
    /// <param name="format">Stored format.</param>
    /// <returns>Wire key.</returns>
    public static string FormatWire(ExportFormat format) => format switch
    {
        ExportFormat.Excel => "xlsx",
        ExportFormat.Csv => "csv",
        ExportFormat.Html => "html",
        ExportFormat.Pdf => "pdf",
        ExportFormat.XmlSitemap => "xml-sitemap",
        _ => "xml-sitemap",
    };

    private static string StateWire(ExportJobStatus status) => ApiEnumConverter<ExportJobStatus>.ToWireValue(status);

    private static string ContentTypeForWire(string formatWire) => formatWire switch
    {
        // The accepted Export wire convention names the workbook format
        // "excel" (ExportWireExtensions.ToWireValue); "xlsx" is the creation
        // alias. Both stream the same OOXML bytes.
        "excel" or "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        // §8.4 locked writer output: the CSV export is a ZIP archive of
        // RFC 4180 dataset CSVs; the HTML export is a single self-contained
        // document. Labels match the actual bytes (defect D11).
        "csv" => "application/zip",
        "html" => "text/html",
        "pdf" => "application/pdf",
        "json" => "application/json",
        "jsonl" => "application/x-ndjson",
        "text" or "markdown" => "application/zip",
        _ => "application/xml",
    };
}
