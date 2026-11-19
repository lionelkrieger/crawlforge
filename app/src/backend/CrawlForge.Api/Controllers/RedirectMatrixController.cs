using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.ListMode;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Redirect migration matrix (FS-CRAWL-002 §10.7): upload mapping rows
/// (optionally auto-seeding a list-mode audit) and read per-row validation
/// verdicts with hop chains and canonical evidence. The matrix is audit-scoped;
/// its identifier is the owning audit.
/// </summary>
[Route("api/audits")]
public sealed class RedirectMatrixController : ApiControllerBase
{
    private readonly IReadRepository<RedirectMappingDto> _mappings;
    private readonly IWriteRepository<RedirectMappingDto> _mappingWrites;
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IRedirectMatrixValidator _validator;
    private readonly IAuditExecutionService _execution;
    private readonly IListModeIngestor _ingestor;

    /// <summary>Creates the controller.</summary>
    /// <param name="mappings">Mapping read repository.</param>
    /// <param name="mappingWrites">Mapping write repository.</param>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="validator">Redirect-matrix validator.</param>
    /// <param name="execution">Audit execution service (auto-seed).</param>
    /// <param name="ingestor">List-mode ingestor (auto-seed).</param>
    public RedirectMatrixController(
        IReadRepository<RedirectMappingDto> mappings,
        IWriteRepository<RedirectMappingDto> mappingWrites,
        IReadRepository<AuditDto> audits,
        IReadRepository<UrlRecordDto> urls,
        IRedirectMatrixValidator validator,
        IAuditExecutionService execution,
        IListModeIngestor ingestor)
    {
        _mappings = mappings;
        _mappingWrites = mappingWrites;
        _audits = audits;
        _urls = urls;
        _validator = validator;
        _execution = execution;
        _ingestor = ingestor;
    }

    /// <summary>Uploads migration mapping rows, optionally auto-seeding a list-mode audit.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="request">Upload request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upload acceptance with the auto-seeded audit identifier.</returns>
    [HttpPost("{id:long}/redirect-matrix")]
    public async Task<IActionResult> Upload(long id, [FromBody] RedirectMatrixUploadRequest request, CancellationToken cancellationToken)
    {
        var audit = await _audits.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (request.Rows.Count == 0)
        {
            return ValidationError("At least one mapping row is required.", [new ApiFieldError("rows", "Provide source/target mapping rows.")]);
        }

        foreach (var row in request.Rows)
        {
            if (!Uri.TryCreate(row.SourceUrl, UriKind.Absolute, out _) || !Uri.TryCreate(row.TargetUrl, UriKind.Absolute, out _))
            {
                return ValidationError("Mapping rows must be absolute URLs.", [new ApiFieldError("rows", "Each row needs absolute source and target URLs.")]);
            }
        }

        var existing = await _mappings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        foreach (var mapping in existing.Items)
        {
            await _mappingWrites.DeleteAsync(mapping.Id, cancellationToken);
        }

        for (var ordinal = 0; ordinal < request.Rows.Count; ordinal++)
        {
            await _mappingWrites.CreateAsync(new RedirectMappingDto(0, id, request.Rows[ordinal].SourceUrl, request.Rows[ordinal].TargetUrl, ordinal), cancellationToken);
        }

        string? seededAuditId = null;
        if (request.AutoSeedAudit == true)
        {
            var newAuditId = await _execution.StartAuditAsync(audit.ProjectId, audit.ConfigurationId, ScheduleTriggerType.Manual, null, cancellationToken);
            await _ingestor.IngestAsync(newAuditId, request.Rows.Select(r => r.SourceUrl).ToList(), allowDiscovery: false, cancellationToken);
            seededAuditId = newAuditId.ToString();
        }

        return OkEnvelope(new RedirectMatrixUploadResponse(id.ToString(), request.Rows.Count, seededAuditId));
    }

    /// <summary>Lists per-row redirect-matrix validation results.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="verdict">Verdict filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged per-row results.</returns>
    [HttpGet("{id:long}/redirect-matrix/results")]
    public async Task<IActionResult> Results(
        long id,
        [FromQuery] string? verdict = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        // WP-09-06 D4 (G-MATRIX-VERDICT): the frozen verdict vocabulary is the
        // metric-dimension vocabulary (MatrixVerdictNames) — REST == hub ==
        // metric dimensions: pass/target-mismatch/chain-exceeded/final-non-200/
        // canonical-mismatch. Case-insensitive input, canonical output.
        RedirectMatrixVerdict? verdictFilter = null;
        if (verdict is not null)
        {
            try
            {
                verdictFilter = MatrixVerdictNames.Parse(verdict.ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                return ValidationError($"Unknown verdict '{verdict}'.", [new ApiFieldError("verdict", "Use pass, target-mismatch, chain-exceeded, final-non-200, or canonical-mismatch.")]);
            }
        }

        var mappings = await _mappings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }, "Ordinal", false), cancellationToken);
        var results = await _validator.ValidateAsync(id, mappings.Items, cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);

        var projected = results
            .Select(r => ToResult(r, urlMap))
            .Where(r => verdictFilter is null || string.Equals(r.Verdict, MatrixVerdictNames.Of(verdictFilter.Value), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var page = projected.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), projected.Count));
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

    private static RedirectMatrixResultWire ToResult(RedirectMatrixRowResult result, IReadOnlyDictionary<string, long> urlMap)
    {
        var chain = result.Chain
            .OrderBy(h => h.Ordinal)
            .Select(h => new RedirectHopWire(h.FromUrl, h.StatusCode, h.ToUrl))
            .ToList();
        return new RedirectMatrixResultWire(
            urlMap.TryGetValue(result.Mapping.SourceUrl, out var urlId) ? urlId.ToString() : string.Empty,
            result.Mapping.SourceUrl,
            result.Mapping.ExpectedTargetUrl,
            MatrixVerdictNames.Of(result.Verdict),
            chain,
            result.CanonicalUrl,
            result.ActualFinalUrl);
    }
}
