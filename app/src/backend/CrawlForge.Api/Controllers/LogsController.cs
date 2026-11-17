using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Intelligence.LogAnalyzer;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Server-log import management and correlation (FS-CRAWL-002 §11.7). Imports
/// are offline local-file streaming; DNS bot verification is the only optional
/// network action — user-triggered, off by default, per-import consent (E8).
/// Import management is never exposed via MCP (§11.1 boundary).
/// </summary>
[Route("api")]
public sealed class LogsController : ApiControllerBase
{
    private readonly ILogImportService _imports;
    private readonly ILogCorrelationReadModel _correlation;
    private readonly ILogDnsVerificationTool _dns;
    private readonly IReadRepository<LogImportDto> _importRows;
    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IReadRepository<CrawlConfigurationDto> _configurations;
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<UrlRecordDto> _urls;

    /// <summary>Creates the controller.</summary>
    /// <param name="imports">Log import service.</param>
    /// <param name="correlation">Correlation read model.</param>
    /// <param name="dns">DNS verification tool.</param>
    /// <param name="importRows">Import read repository.</param>
    /// <param name="projects">Project read repository.</param>
    /// <param name="configurations">Configuration read repository.</param>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="urls">URL read repository.</param>
    public LogsController(
        ILogImportService imports,
        ILogCorrelationReadModel correlation,
        ILogDnsVerificationTool dns,
        IReadRepository<LogImportDto> importRows,
        IReadRepository<ProjectDto> projects,
        IReadRepository<CrawlConfigurationDto> configurations,
        IReadRepository<AuditDto> audits,
        IReadRepository<UrlRecordDto> urls)
    {
        _imports = imports;
        _correlation = correlation;
        _dns = dns;
        _importRows = importRows;
        _projects = projects;
        _configurations = configurations;
        _audits = audits;
        _urls = urls;
    }

    /// <summary>Imports a local log file into a project's aggregates.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="request">Import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import acceptance.</returns>
    [HttpPost("projects/{id:long}/logs/import")]
    public async Task<IActionResult> Import(long id, [FromBody] LogImportRequestWire request, CancellationToken cancellationToken)
    {
        if (await _projects.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Project '{id}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return ValidationError("A local file path is required.", [new ApiFieldError("filePath", "Provide the local log file path.")]);
        }

        LogFileFormat? format = null;
        if (request.Format is not null)
        {
            if (!TryParseFormat(request.Format, out var parsedFormat))
            {
                return ValidationError($"Unknown log format '{request.Format}'.", [new ApiFieldError("format", "Use combined, common, w3c, cloudflare, or custom.")]);
            }

            format = parsedFormat;
        }

        var baseUri = await DeriveBaseUriAsync(id, cancellationToken);
        var outcome = await _imports.ImportAsync(
            new LogImportRequest(id, request.FilePath, baseUri, format, VerificationMode: BotVerificationMode.CidrOnly),
            cancellationToken);
        return OkEnvelope(new LogImportStartResponse(outcome.ImportId.ToString(), ImportStateWire(outcome.Status)));
    }

    /// <summary>Lists a project's log imports, newest first.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged import records.</returns>
    [HttpGet("projects/{id:long}/logs")]
    public async Task<IActionResult> List(long id, [FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (await _projects.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Project '{id}' was not found.");
        }

        var records = await _imports.ListImportsAsync(id, Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), cancellationToken);
        var items = records.Select(ToImportWire).ToList();
        return OkEnvelope(items, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), items.Count));
    }

    /// <summary>Purges a log import and its aggregates.</summary>
    /// <param name="id">Import identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deletion confirmation.</returns>
    [HttpDelete("logs/{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        if (await _importRows.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Log import '{id}' was not found.");
        }

        await _imports.PurgeImportAsync(id, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    /// <summary>Returns a correlation view joining log hits with crawl coverage.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="view">View selector.</param>
    /// <param name="importId">Import filter; defaults to the project's latest import.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The correlation summary and rows.</returns>
    [HttpGet("audits/{id:long}/logs/correlation")]
    public async Task<IActionResult> Correlation(
        long id,
        [FromQuery] string? view = null,
        [FromQuery] string? importId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var audit = await _audits.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (!TryParseView(view, out var correlationView))
        {
            return ValidationError($"Unknown correlation view '{view}'.", [new ApiFieldError("view", "Use orphans, budget-waste, or uncrawled-indexable.")]);
        }

        var resolvedImportId = await ResolveImportIdAsync(audit.ProjectId, importId, cancellationToken);
        if (resolvedImportId is null)
        {
            return OkEnvelope(new LogCorrelationResponseWire(new LogCorrelationSummaryWire(0, 0, 0), []));
        }

        var orphanCount = (await _correlation.GetCorrelationPageAsync(id, resolvedImportId.Value, LogCorrelationView.Orphans, 0, 1, cancellationToken)).TotalCount;
        var wasteCount = (await _correlation.GetCorrelationPageAsync(id, resolvedImportId.Value, LogCorrelationView.Waste, 0, 1, cancellationToken)).TotalCount;
        var uncrawledCount = (await _correlation.GetCorrelationPageAsync(id, resolvedImportId.Value, LogCorrelationView.Uncrawled, 0, 1, cancellationToken)).TotalCount;
        var summary = new LogCorrelationSummaryWire(orphanCount, wasteCount, uncrawledCount);

        var page = await _correlation.GetCorrelationPageAsync(id, resolvedImportId.Value, correlationView, Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var rows = page.Rows.Select(r => ToCorrelationRow(r, urlMap)).ToList();
        return OkEnvelope(new LogCorrelationResponseWire(summary, rows), new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), page.TotalCount));
    }

    /// <summary>Runs consented DNS bot verification for an import.</summary>
    /// <param name="id">Import identifier.</param>
    /// <param name="request">Consent request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification acceptance verdict.</returns>
    [HttpPost("logs/{id:long}/verify-dns")]
    public async Task<IActionResult> VerifyDns(long id, [FromBody] DnsVerifyRequest request, CancellationToken cancellationToken)
    {
        if (await _importRows.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Log import '{id}' was not found.");
        }

        if (!request.Consent)
        {
            return OkEnvelope(new DnsVerifyResponse(id.ToString(), false, "DNS verification requires explicit per-import consent; nothing was sent."));
        }

        var results = await _dns.VerifySampleAsync(id, true, 20, cancellationToken);
        return OkEnvelope(new DnsVerifyResponse(id.ToString(), true, $"Verified {results.Count} bot IP sample(s)."));
    }

    private async Task<string> DeriveBaseUriAsync(long projectId, CancellationToken cancellationToken)
    {
        var configurations = await _configurations.ListAsync(new ListQuery(0, 100, new Dictionary<string, string> { ["projectId"] = projectId.ToString() }), cancellationToken);
        var configuration = configurations.Items.FirstOrDefault();
        var startUrls = ProjectWireMapper.ParseStringList(configuration?.SeedsJson);
        if (startUrls.Count > 0 && Uri.TryCreate(startUrls[0], UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}";
        }

        return "https://localhost";
    }

    private async Task<long?> ResolveImportIdAsync(long projectId, string? importId, CancellationToken cancellationToken)
    {
        if (importId is not null && long.TryParse(importId, out var explicitId))
        {
            return explicitId;
        }

        var records = await _imports.ListImportsAsync(projectId, 0, 1, cancellationToken);
        return records.Count > 0 ? records[0].Import.Id : null;
    }

    private async Task<IReadOnlyDictionary<string, long>> LoadUrlMapAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.Items)
        {
            map[url.CanonicalUrl] = url.Id;
        }

        return map;
    }

    private static LogImportWire ToImportWire(LogImportRecord record)
    {
        var import = record.Import;
        var terminal = import.Status is LogImportStatus.Completed or LogImportStatus.Failed;
        return new LogImportWire(
            import.Id.ToString(),
            import.ProjectId.ToString(),
            Path.GetFileName(import.SourceFilePath) ?? string.Empty,
            FormatWire(import.Format),
            ImportStateWire(import.Status),
            import.ImportedAt,
            terminal ? import.ImportedAt : null,
            new LogImportCountersWire(record.Counters.LinesParsed, record.Counters.LinesAccepted, record.Counters.LinesRejected),
            null,
            import.VerificationMode != BotVerificationMode.CidrOnly);
    }

    private static LogCorrelationRowWire ToCorrelationRow(LogCorrelationRow row, IReadOnlyDictionary<string, long> urlMap)
    {
        var evidence = ProjectWireMapper.ParseObject(row.EvidenceJson);
        DateTimeOffset? lastSeen = null;
        if (evidence["lastSeen"]?.GetValue<string>() is { } lastSeenText && DateTimeOffset.TryParse(lastSeenText, out var parsed))
        {
            lastSeen = parsed;
        }

        return new LogCorrelationRowWire(
            row.CanonicalUrl,
            row.CanonicalUrl,
            urlMap.TryGetValue(row.CanonicalUrl, out var urlId) ? urlId.ToString() : null,
            row.HitCount,
            row.SearchBotHits,
            lastSeen);
    }

    private static string ImportStateWire(LogImportStatus status) => status switch
    {
        LogImportStatus.Pending => "queued",
        LogImportStatus.Importing => "running",
        LogImportStatus.Completed => "completed",
        _ => "failed",
    };

    private static string? FormatWire(LogFileFormat? format) => format switch
    {
        LogFileFormat.ApacheNginxCombined => "combined",
        LogFileFormat.IisW3c => "w3c",
        LogFileFormat.CloudflareExport => "cloudflare",
        LogFileFormat.Custom => "custom",
        _ => null,
    };

    private static bool TryParseFormat(string text, out LogFileFormat format)
    {
        switch (text.ToLowerInvariant())
        {
            case "combined":
            case "common": format = LogFileFormat.ApacheNginxCombined; return true;
            case "w3c": format = LogFileFormat.IisW3c; return true;
            case "cloudflare": format = LogFileFormat.CloudflareExport; return true;
            case "custom": format = LogFileFormat.Custom; return true;
            default: format = default; return false;
        }
    }

    private static bool TryParseView(string? view, out LogCorrelationView value)
    {
        value = LogCorrelationView.Summary;
        if (view is null)
        {
            return true;
        }

        switch (view.ToLowerInvariant())
        {
            case "orphans": value = LogCorrelationView.Orphans; return true;
            case "budget-waste": value = LogCorrelationView.Waste; return true;
            case "uncrawled-indexable": value = LogCorrelationView.Uncrawled; return true;
            case "summary": value = LogCorrelationView.Summary; return true;
            default: return false;
        }
    }
}
