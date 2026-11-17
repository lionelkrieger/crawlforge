using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Semantic / embedding explorer (FS-CRAWL-002 §12.8): run the local embedding
/// job, top-K similar URLs, internal-link opportunities, and cannibalization
/// clusters. Embeddings come only from the bundled offline model or a
/// loopback-only daemon (exclusion E7); all reads are local projections.
/// </summary>
[Route("api/audits")]
public sealed class SemanticsController : ApiControllerBase
{
    private const double DefaultSimilarityThreshold = 0.5;
    private const int DefaultTopK = 10;

    private readonly ISemanticLinkAnalyzer _analyzer;
    private readonly IReadRepository<SemanticLinkOpportunityDto> _opportunities;
    private readonly IReadRepository<SemanticClusterDto> _clusters;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="analyzer">Semantic link analyzer.</param>
    /// <param name="opportunities">Opportunity read repository.</param>
    /// <param name="clusters">Cluster read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="audits">Audit read repository.</param>
    public SemanticsController(
        ISemanticLinkAnalyzer analyzer,
        IReadRepository<SemanticLinkOpportunityDto> opportunities,
        IReadRepository<SemanticClusterDto> clusters,
        IReadRepository<UrlRecordDto> urls,
        IReadRepository<AuditDto> audits)
    {
        _analyzer = analyzer;
        _opportunities = opportunities;
        _clusters = clusters;
        _urls = urls;
        _audits = audits;
    }

    /// <summary>Runs the local embedding job for an audit.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="request">Run request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Run acceptance (audit-scoped job identifier).</returns>
    [HttpPost("{id:long}/semantics/run")]
    public async Task<IActionResult> Run(long id, [FromBody] SemanticsRunRequest request, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        await _analyzer.RunAsync(id, new SemanticAnalysisOptions(DefaultSimilarityThreshold, DefaultTopK), cancellationToken);
        return OkEnvelope(new SemanticsRunResponse(id.ToString(), id.ToString(), "completed"));
    }

    /// <summary>Returns top-K similar URLs by cosine similarity.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="urlId">Reference URL record.</param>
    /// <param name="k">Maximum neighbours.</param>
    /// <param name="minSimilarity">Similarity floor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Similar URLs in descending similarity.</returns>
    [HttpGet("{id:long}/semantics/similar")]
    public async Task<IActionResult> Similar(
        long id,
        [FromQuery] string? urlId = null,
        [FromQuery] int k = DefaultTopK,
        [FromQuery] double? minSimilarity = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (!long.TryParse(urlId, out var referenceId))
        {
            return ValidationError("A numeric urlId is required.", [new ApiFieldError("urlId", "Provide the reference URL identifier.")]);
        }

        var opportunities = await _opportunities.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string>
        {
            ["auditId"] = id.ToString(),
            ["sourceUrlId"] = referenceId.ToString(),
        }), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var floor = minSimilarity ?? 0.0;
        var items = opportunities.Items
            .Where(o => o.Similarity >= floor)
            .OrderByDescending(o => o.Similarity)
            .Take(Math.Clamp(k, 1, 100))
            .Select(o => new SimilarUrlWire(o.TargetUrlId.ToString(), urlMap.GetValueOrDefault(o.TargetUrlId, string.Empty), o.Similarity))
            .ToList();
        return OkEnvelope(items);
    }

    /// <summary>Lists semantic internal-link recommendations.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="urlId">Source URL filter.</param>
    /// <param name="minSimilarity">Similarity floor.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged link opportunities.</returns>
    [HttpGet("{id:long}/semantics/link-opportunities")]
    public async Task<IActionResult> LinkOpportunities(
        long id,
        [FromQuery] string? urlId = null,
        [FromQuery] double? minSimilarity = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (urlId is not null)
        {
            filters["sourceUrlId"] = urlId;
        }

        var opportunities = await _opportunities.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var floor = minSimilarity ?? 0.0;
        var projected = opportunities.Items
            .Where(o => o.Similarity >= floor)
            .OrderByDescending(o => o.Similarity)
            .Select(o => new LinkOpportunityWire(
                o.SourceUrlId.ToString(),
                urlMap.GetValueOrDefault(o.SourceUrlId, string.Empty),
                o.TargetUrlId.ToString(),
                urlMap.GetValueOrDefault(o.TargetUrlId, string.Empty),
                o.Similarity,
                ProjectWireMapper.ParseStringList(o.AnchorCandidatesJson).FirstOrDefault()))
            .ToList();
        var page = projected.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), projected.Count));
    }

    /// <summary>Lists semantic cannibalization clusters.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="minSimilarity">Member similarity floor.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged clusters.</returns>
    [HttpGet("{id:long}/semantics/clusters")]
    public async Task<IActionResult> Clusters(
        long id,
        [FromQuery] double? minSimilarity = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var rows = await _clusters.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var floor = minSimilarity ?? 0.0;
        var clusters = rows.Items
            .GroupBy(c => c.ClusterId)
            .Select(g =>
            {
                var members = g.Where(m => m.CentroidSimilarity >= floor)
                    .OrderByDescending(m => m.CentroidSimilarity)
                    .ToList();
                var centroid = members.FirstOrDefault();
                return new SemanticClusterWire(
                    g.Key.ToString(),
                    null,
                    centroid?.UrlId.ToString(),
                    members.Select(m => new SemanticClusterMemberWire(m.UrlId.ToString(), urlMap.GetValueOrDefault(m.UrlId, string.Empty), m.CentroidSimilarity)).ToList());
            })
            .Where(c => c.Members.Count > 0)
            .OrderBy(c => c.ClusterId, StringComparer.Ordinal)
            .ToList();
        var page = clusters.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), clusters.Count));
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadUrlMapAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        return urls.Items.ToDictionary(u => u.Id, u => u.CanonicalUrl);
    }
}
