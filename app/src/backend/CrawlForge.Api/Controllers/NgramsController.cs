using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Intelligence.NGrams;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// N-gram / topical density explorer (FS-CRAWL-002 §5.7): site-summary or
/// per-URL rows, cannibalization clusters, and topical gaps. All reads are
/// constant-cost projections of the materialized read model and persisted
/// findings; computation is fully offline.
/// </summary>
[Route("api/audits")]
public sealed class NgramsController : ApiControllerBase
{
    private readonly NGramReadModel _readModel;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="readModel">N-gram read model.</param>
    /// <param name="audits">Audit read repository.</param>
    public NgramsController(NGramReadModel readModel, IReadRepository<AuditDto> audits)
    {
        _readModel = readModel;
        _audits = audits;
    }

    /// <summary>Lists n-gram rows (site summaries, or per-URL when urlId is set).</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="urlId">Optional URL filter (switches to per-URL rows).</param>
    /// <param name="zone">Zone filter.</param>
    /// <param name="n">Gram size filter.</param>
    /// <param name="minCount">Minimum count/DF filter.</param>
    /// <param name="sort">Sort field (df/tf/count).</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged n-gram rows.</returns>
    [HttpGet("{id:long}/ngrams")]
    public async Task<IActionResult> ListNgrams(
        long id,
        [FromQuery] string? urlId = null,
        [FromQuery] string? zone = null,
        [FromQuery] int? n = null,
        [FromQuery] long? minCount = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (!TryBindSort(sort, order, new[] { "df", "tf", "count" }, out var sortBy, out var descending, out var error))
        {
            return ValidationError(error!);
        }

        if (!TryParseZone(zone, out var textZone))
        {
            return ValidationError($"Unknown zone '{zone}'.", [new ApiFieldError("zone", "Use title, headings, body, or inbound-anchor.")]);
        }

        long? parsedUrlId = null;
        if (urlId is not null && !long.TryParse(urlId, out var urlIdValue))
        {
            return ValidationError($"Invalid urlId '{urlId}'.", [new ApiFieldError("urlId", "Provide a numeric URL identifier.")]);
        }
        else if (urlId is not null)
        {
            parsedUrlId = long.Parse(urlId);
        }

        var query = new NgramListQuery(
            Scope: parsedUrlId is null ? "site" : "url",
            Zone: textZone,
            Order: n,
            UrlId: parsedUrlId,
            MinDf: minCount,
            SortBy: sortBy ?? "df",
            Descending: descending,
            Offset: Math.Max(0, offset),
            Limit: Math.Clamp(limit, 1, MaxPageSize));
        var response = await _readModel.ListAsync(id, query, cancellationToken);

        var items = response.SiteItems.Count > 0
            ? response.SiteItems.Select(s => new NgramRowWire(s.Term, s.Order, ZoneWire(s.Zone), s.TotalFrequency, s.MeanTermFrequency, null)).ToList()
            : response.UrlItems.Select(u => new NgramRowWire(u.Term, u.Order, ZoneWire(u.Zone), u.Count, u.TermFrequency, u.UrlId.ToString())).ToList();
        return OkEnvelope(items, new PaginationMetadata(response.Offset, response.Limit, response.TotalCount));
    }

    /// <summary>Lists keyword-cannibalization clusters.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="minCount">Minimum cluster size.</param>
    /// <param name="zone">Zone filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged clusters.</returns>
    [HttpGet("{id:long}/ngrams/cannibalization")]
    public async Task<IActionResult> Cannibalization(
        long id,
        [FromQuery] int? minCount = null,
        [FromQuery] string? zone = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (!TryParseZone(zone, out var textZone))
        {
            return ValidationError($"Unknown zone '{zone}'.", [new ApiFieldError("zone", "Use title, headings, body, or inbound-anchor.")]);
        }

        var clusters = await _readModel.ListClustersAsync(
            id, textZone, order: null, minClusterSize: Math.Max(2, minCount ?? 2),
            offset: Math.Max(0, offset), limit: Math.Clamp(limit, 1, MaxPageSize), cancellationToken: cancellationToken);
        var items = clusters
            .Select(c => new NgramCannibalizationClusterWire(
                c.Term,
                c.Members.Select(m => new CannibalizationPageWire(m.UrlId.ToString(), m.CanonicalUrl, (long)Math.Round(m.TermFrequency))).ToList()))
            .ToList();
        return OkEnvelope(items, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), items.Count));
    }

    /// <summary>Lists topical gaps (anchor demand without on-page supply).</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="minCount">Minimum anchor DF override.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged gap rows.</returns>
    [HttpGet("{id:long}/ngrams/gaps")]
    public async Task<IActionResult> Gaps(
        long id,
        [FromQuery] long? minCount = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var gaps = await _readModel.ListGapsAsync(
            id,
            new NgramGapsQuery((int?)minCount, Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize)),
            cancellationToken);
        var items = gaps
            .Select(g => new TopicalGapWire(
                g.Term,
                g.AnchorDocumentFrequency,
                g.Threshold,
                g.Threshold > 0 ? Math.Clamp((g.Threshold - g.AnchorDocumentFrequency) / (double)g.Threshold, 0.0, 1.0) : 0.0))
            .ToList();
        return OkEnvelope(items, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), items.Count));
    }

    private static bool TryParseZone(string? zone, out TextZone? value)
    {
        value = null;
        if (zone is null)
        {
            return true;
        }

        switch (zone.ToLowerInvariant())
        {
            case "title": value = TextZone.Title; return true;
            case "headings": value = TextZone.Headings; return true;
            case "body": value = TextZone.Body; return true;
            case "inbound-anchor": value = TextZone.AnchorText; return true;
            default: return false;
        }
    }

    private static string ZoneWire(TextZone zone) => zone switch
    {
        TextZone.Title => "title",
        TextZone.Headings => "headings",
        TextZone.AnchorText => "inbound-anchor",
        _ => "body",
    };
}
