using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// MarTech analytics explorer (FS-CRAWL-002 §14.5): tag inventory, consent
/// rollup, analytics findings, and per-container coverage across indexable
/// pages. All reads are local projections of persisted detections.
/// </summary>
[Route("api/audits")]
public sealed class AnalyticsController : ApiControllerBase
{
    private readonly IReadRepository<AnalyticsDetectionDto> _detections;
    private readonly IReadRepository<FindingDto> _findings;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="detections">Analytics detection read repository.</param>
    /// <param name="findings">Finding read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="audits">Audit read repository.</param>
    public AnalyticsController(
        IReadRepository<AnalyticsDetectionDto> detections,
        IReadRepository<FindingDto> findings,
        IReadRepository<UrlRecordDto> urls,
        IReadRepository<AuditDto> audits)
    {
        _detections = detections;
        _findings = findings;
        _urls = urls;
        _audits = audits;
    }

    /// <summary>Returns the tag inventory, consent rollup, and analytics findings.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="view">View selector (tags/consent/findings); advisory.</param>
    /// <param name="containerId">Container filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analytics response.</returns>
    [HttpGet("{id:long}/analytics")]
    public async Task<IActionResult> Analytics(
        long id,
        [FromQuery] string? view = null,
        [FromQuery] string? containerId = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (view is not null && view is not ("tags" or "consent" or "findings"))
        {
            return ValidationError($"Unknown view '{view}'.", [new ApiFieldError("view", "Use tags, consent, or findings.")]);
        }

        var detections = await _detections.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var filtered = containerId is null
            ? detections.Items
            : detections.Items.Where(d => string.Equals(d.ContainerId, containerId, StringComparison.Ordinal)).ToList();

        var tags = filtered
            .GroupBy(d => (d.Vendor, d.ContainerId, d.DetectionSource))
            .Select(g => new AnalyticsTagWire(
                g.Key.Vendor,
                g.Key.ContainerId,
                g.Key.DetectionSource,
                null,
                g.Count()))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var consent = BuildConsent(detections.Items);
        var findingsPage = await _findings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string>
        {
            ["auditId"] = id.ToString(),
            ["category"] = ((int)AuditCategory.AnalyticsTagging).ToString(),
        }), cancellationToken);
        var findings = findingsPage.Items.Select(AuditWireMapper.ToFinding).ToList();

        return OkEnvelope(new AnalyticsResponseWire(tags, consent, findings));
    }

    /// <summary>Returns per-container coverage across indexable pages.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="containerId">Container filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged container coverage rows.</returns>
    [HttpGet("{id:long}/analytics/coverage")]
    public async Task<IActionResult> Coverage(
        long id,
        [FromQuery] string? containerId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var indexablePages = urls.Items.Count(u => u.IsIndexable);
        var detections = await _detections.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);

        var coverage = detections.Items
            .Where(d => d.ContainerId is not null)
            .Where(d => containerId is null || string.Equals(d.ContainerId, containerId, StringComparison.Ordinal))
            .GroupBy(d => d.ContainerId!, StringComparer.Ordinal)
            .Select(g =>
            {
                var pagesWithContainer = g.Select(d => d.UrlId).Distinct().Count();
                return new ContainerCoverageWire(
                    g.Key,
                    null,
                    indexablePages,
                    pagesWithContainer,
                    indexablePages > 0 ? Math.Min(1.0, (double)pagesWithContainer / indexablePages) : 0.0);
            })
            .OrderBy(c => c.ContainerId, StringComparer.Ordinal)
            .ToList();

        var page = coverage.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), coverage.Count));
    }

    private static AnalyticsConsentSummaryWire BuildConsent(IReadOnlyList<AnalyticsDetectionDto> detections)
    {
        var consentDetections = detections
            .Where(d => d.Vendor.Contains("consent", StringComparison.OrdinalIgnoreCase)
                || (d.Placement?.Contains("consent", StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        var pagesWithConsent = consentDetections.Select(d => d.UrlId).Distinct().Count();
        var distinctPages = detections.Select(d => d.UrlId).Distinct().Count();
        return new AnalyticsConsentSummaryWire(
            consentDetections.Count > 0,
            detections.Any(d => string.Equals(d.DetectionSource, "proxy", StringComparison.OrdinalIgnoreCase)),
            pagesWithConsent,
            Math.Max(0, distinctPages - pagesWithConsent));
    }
}
