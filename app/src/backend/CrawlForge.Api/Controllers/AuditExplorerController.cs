using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Audit report explorers (FS-CRAWL-001 §10.2): URL list/detail, links,
/// findings, dashboard summary, chart series, graph, and scores. All reads are
/// local projections of persisted rows; additive detail fields ride the
/// element inventory and additive tables (FS-CRAWL-002 Appendix B).
/// </summary>
[Route("api/audits")]
public sealed class AuditExplorerController : ApiControllerBase
{
    private const int GraphDefaultMaxNodes = 500;
    private const int GraphDefaultMaxEdges = 2000;

    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<UrlElementDto> _elements;
    private readonly IReadRepository<LinkEdgeDto> _links;
    private readonly IReadRepository<FindingDto> _findings;
    private readonly IReadRepository<MetricSummaryDto> _metrics;
    private readonly IReadRepository<AuditScoreDto> _scores;
    private readonly IReadRepository<WafDetectionDto> _wafDetections;
    private readonly IReadRepository<HostThrottleEventDto> _throttleEvents;
    private readonly IReadRepository<NgramFrequencyDto> _ngrams;
    private readonly IReadRepository<SemanticLinkOpportunityDto> _semanticLinks;

    /// <summary>Creates the controller.</summary>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="elements">Element inventory read repository.</param>
    /// <param name="links">Link edge read repository.</param>
    /// <param name="findings">Finding read repository.</param>
    /// <param name="metrics">Metric summary read repository.</param>
    /// <param name="scores">Audit score read repository.</param>
    /// <param name="wafDetections">WAF detection read repository.</param>
    /// <param name="throttleEvents">Host throttle event read repository.</param>
    /// <param name="ngrams">N-gram frequency read repository.</param>
    /// <param name="semanticLinks">Semantic link opportunity read repository.</param>
    public AuditExplorerController(
        IReadRepository<AuditDto> audits,
        IReadRepository<UrlRecordDto> urls,
        IReadRepository<UrlElementDto> elements,
        IReadRepository<LinkEdgeDto> links,
        IReadRepository<FindingDto> findings,
        IReadRepository<MetricSummaryDto> metrics,
        IReadRepository<AuditScoreDto> scores,
        IReadRepository<WafDetectionDto> wafDetections,
        IReadRepository<HostThrottleEventDto> throttleEvents,
        IReadRepository<NgramFrequencyDto> ngrams,
        IReadRepository<SemanticLinkOpportunityDto> semanticLinks)
    {
        _audits = audits;
        _urls = urls;
        _elements = elements;
        _links = links;
        _findings = findings;
        _metrics = metrics;
        _scores = scores;
        _wafDetections = wafDetections;
        _throttleEvents = throttleEvents;
        _ngrams = ngrams;
        _semanticLinks = semanticLinks;
    }

    /// <summary>Lists URL records with filters and pagination.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="statusClass">Optional status class filter.</param>
    /// <param name="contentType">Optional content type filter.</param>
    /// <param name="depth">Optional depth filter.</param>
    /// <param name="indexable">Optional indexability filter.</param>
    /// <param name="search">Optional URL text search.</param>
    /// <param name="blockReason">Additive block-reason filter.</param>
    /// <param name="wafProvider">Additive WAF provider filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged URL summaries.</returns>
    [HttpGet("{id:long}/urls")]
    public async Task<ObjectResult> ListUrlsAsync(
        long id,
        [FromQuery] string? statusClass,
        [FromQuery] string? contentType,
        [FromQuery] int? depth,
        [FromQuery] bool? indexable,
        [FromQuery] string? search,
        [FromQuery] string? blockReason,
        [FromQuery] string? wafProvider,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["auditId"] = id.ToString() };
        if (statusClass is not null)
        {
            var mapped = statusClass switch
            {
                "1xx" => CrawlStatusClass.NotCrawled,
                "2xx" => CrawlStatusClass.Success,
                "3xx" => CrawlStatusClass.Redirect,
                "4xx" => CrawlStatusClass.ClientError,
                "5xx" => CrawlStatusClass.ServerError,
                _ => (CrawlStatusClass?)null,
            };
            if (mapped is null)
            {
                return ValidationError($"Unknown status class '{statusClass}'.", [new ApiFieldError("statusClass", "Use 1xx, 2xx, 3xx, 4xx, or 5xx.")]);
            }

            filters["statusClass"] = ((int)mapped.Value).ToString();
        }

        if (contentType is not null)
        {
            filters["contentType"] = contentType;
        }

        if (depth is not null)
        {
            filters["depth"] = depth.Value.ToString();
        }

        if (indexable is not null)
        {
            filters["isIndexable"] = indexable.Value ? "true" : "false";
        }

        if (blockReason is not null)
        {
            filters["blockReason"] = blockReason;
        }

        if (wafProvider is not null)
        {
            if (!ApiEnumConverter<WafProvider>.TryParse(wafProvider, out var provider))
            {
                return ValidationError($"Unknown WAF provider '{wafProvider}'.", [new ApiFieldError("wafProvider", "Use a known provider key.")]);
            }

            filters["wafProvider"] = ((int)provider).ToString();
        }

        if (!TryBindSort(sort, order, ["id", "canonicalUrl", "depth", "statusCode"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var repositorySort = sortBy == "statusCode" ? "FinalStatusCode" : sortBy;
        var query = BindListQuery(offset, limit, repositorySort, descending, filters);

        if (string.IsNullOrWhiteSpace(search))
        {
            var page = await _urls.ListAsync(query, cancellationToken);
            var titles = await LoadTitlesAsync(page.Items, cancellationToken);
            var items = page.Items.Select(u => ToSummary(u, titles.GetValueOrDefault(u.Id))).ToList();
            return OkEnvelope(items, PaginationOf(page));
        }

        var all = await _urls.ListAsync(new ListQuery(0, 10_000, filters, repositorySort, descending), cancellationToken);
        var matched = all.Items.Where(u => u.CanonicalUrl.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var matchedPage = matched.Skip(query.Offset).Take(query.Limit).ToList();
        var matchedTitles = await LoadTitlesAsync(matchedPage, cancellationToken);
        return OkEnvelope(
            matchedPage.Select(u => ToSummary(u, matchedTitles.GetValueOrDefault(u.Id))).ToList(),
            new PaginationMetadata(query.Offset, query.Limit, matched.Count));
    }

    /// <summary>Reads full URL detail including additive diagnostics.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>URL detail.</returns>
    [HttpGet("{id:long}/urls/{urlId:long}")]
    public async Task<ObjectResult> GetUrlDetailAsync(long id, long urlId, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var url = await _urls.GetByIdAsync(urlId, cancellationToken);
        if (url is null || url.AuditId != id)
        {
            return NotFoundError($"URL {urlId} does not exist in audit {id}.");
        }

        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var elements = await _elements.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["urlId"] = urlId.ToString() }), cancellationToken);
        var findings = await _findings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["urlId"] = urlId.ToString() }), cancellationToken);
        var outbound = await _links.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["sourceUrlId"] = urlId.ToString() }), cancellationToken);
        var inbound = await _links.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["targetUrlId"] = urlId.ToString() }), cancellationToken);

        var inventory = elements.Items
            .GroupBy(e => e.ElementGroup, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var title = ExtractTitle(elements.Items);
        var headers = ExtractResponseHeaders(elements.Items);
        // WP-09-06 D4 (WP-09-01 handoff recipe): the persisted redirect-chain
        // element is the source of truth — lossless multi-hop mapping
        // (hop.url→Url, hop.statusCode→StatusCode, hop.toUrl→Location). The
        // legacy single-hop synthesis from RedirectTargetUrl stays as the
        // fallback for rows written before chain persistence was wired.
        var chainRow = elements.Items.FirstOrDefault(e => e.ElementGroup == RedirectChainElementGroup.GroupName);
        var chainPayload = RedirectChainJson.DeserializePayload(chainRow?.PayloadJson);
        var redirectChain = chainPayload is not null
            ? chainPayload.Hops.OrderBy(h => h.Ordinal).Select(h => new RedirectHopWire(h.Url, h.StatusCode, h.ToUrl)).ToArray()
            : url.RedirectTargetUrl is null
                ? Array.Empty<RedirectHopWire>()
                : new[] { new RedirectHopWire(url.CanonicalUrl, url.FinalStatusCode ?? 0, url.RedirectTargetUrl) };

        var waf = await _wafDetections.ListAsync(new ListQuery(0, 1, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["urlId"] = urlId.ToString() }), cancellationToken);
        var ngrams = await _ngrams.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["urlId"] = urlId.ToString() }), cancellationToken);
        var semantic = await _semanticLinks.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["sourceUrlId"] = urlId.ToString() }), cancellationToken);

        var detail = new UrlDetailWire(
            url.Id.ToString(),
            url.CanonicalUrl,
            url.FinalStatusCode ?? 0,
            StatusClassWire(url),
            url.ContentType,
            url.Depth,
            url.IsIndexable,
            title,
            url.BlockReason,
            url.WafProvider is { } provider ? ApiEnumConverter<WafProvider>.ToWireValue(provider) : null,
            headers,
            new ElementInventoryWire(inventory, elements.Items.Count),
            findings.Items.Select(AuditWireMapper.ToFinding).ToList(),
            inbound.Items.Select(e => ToLinkEdge(e, urlMap)).ToList(),
            outbound.Items.Select(e => ToLinkEdge(e, urlMap)).ToList(),
            redirectChain,
            waf.Items.Count > 0 ? ToWafDetection(waf.Items[0]) : null,
            ExtractScopeDiagnostics(elements.Items),
            ngrams.Items.Count > 0
                ? ngrams.Items.OrderByDescending(n => n.Count).Take(10).Select(n => ToNgramRow(n, urlId)).ToList()
                : null,
            ExtractSerpMetrics(elements.Items),
            ExtractHeaderConflicts(findings.Items),
            semantic.Items.Count > 0
                ? semantic.Items.Select(s => new SemanticRelationWire(
                    s.TargetUrlId.ToString(),
                    urlMap.GetValueOrDefault(s.TargetUrlId) ?? string.Empty,
                    s.Similarity,
                    "link-opportunity")).ToList()
                : null);

        return OkEnvelope(detail);
    }

    /// <summary>Lists link edges with source/target/internal/domZone filters.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="sourceUrlId">Optional source filter.</param>
    /// <param name="targetUrlId">Optional target filter.</param>
    /// <param name="internal">Optional internal/external filter.</param>
    /// <param name="domZone">Additive DOM-zone filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged link edges.</returns>
    [HttpGet("{id:long}/links")]
    public async Task<ObjectResult> ListLinksAsync(
        long id,
        [FromQuery] string? sourceUrlId,
        [FromQuery] string? targetUrlId,
        [FromQuery] bool? @internal,
        [FromQuery] string? domZone,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["auditId"] = id.ToString() };
        if (sourceUrlId is not null)
        {
            filters["sourceUrlId"] = sourceUrlId;
        }

        if (targetUrlId is not null)
        {
            filters["targetUrlId"] = targetUrlId;
        }

        if (@internal is not null)
        {
            filters["isInternal"] = @internal.Value ? "true" : "false";
        }

        if (domZone is not null)
        {
            var zone = domZone switch
            {
                "header" => DomZone.Header,
                "navigation" => DomZone.Navigation,
                "mainContent" => DomZone.MainContent,
                "sidebar" => DomZone.Sidebar,
                "footer" => DomZone.Footer,
                "breadcrumb" => DomZone.Breadcrumb,
                "other" => DomZone.Other,
                _ => (DomZone?)null,
            };
            if (zone is null)
            {
                return ValidationError($"Unknown DOM zone '{domZone}'.", [new ApiFieldError("domZone", "Use a known zone key.")]);
            }

            filters["domZone"] = ((int)zone.Value).ToString();
        }

        if (!TryBindSort(sort, order, ["id", "sourceUrlId", "targetUrlId"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var page = await _links.ListAsync(BindListQuery(offset, limit, sortBy, descending, filters), cancellationToken);
        return OkEnvelope(page.Items.Select(e => ToLinkEdge(e, urlMap)).ToList(), PaginationOf(page));
    }

    /// <summary>Lists findings with severity/category/ruleKey/urlId filters.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="severity">Optional severity filter.</param>
    /// <param name="category">Optional category filter.</param>
    /// <param name="ruleKey">Optional rule key filter.</param>
    /// <param name="urlId">Optional URL filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged findings.</returns>
    [HttpGet("{id:long}/findings")]
    public async Task<ObjectResult> ListFindingsAsync(
        long id,
        [FromQuery] string? severity,
        [FromQuery] string? category,
        [FromQuery] string? ruleKey,
        [FromQuery] string? urlId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["auditId"] = id.ToString() };
        if (severity is not null)
        {
            if (!ApiEnumConverter<FindingSeverity>.TryParse(severity, out var parsedSeverity))
            {
                return ValidationError($"Unknown severity '{severity}'.", [new ApiFieldError("severity", "Use a known severity key.")]);
            }

            filters["severity"] = ((int)parsedSeverity).ToString();
        }

        if (category is not null)
        {
            if (!ApiEnumConverter<AuditCategory>.TryParse(category, out var parsedCategory))
            {
                return ValidationError($"Unknown category '{category}'.", [new ApiFieldError("category", "Use a known category key.")]);
            }

            filters["category"] = ((int)parsedCategory).ToString();
        }

        if (ruleKey is not null)
        {
            filters["ruleKey"] = ruleKey;
        }

        if (urlId is not null)
        {
            filters["urlId"] = urlId;
        }

        if (!TryBindSort(sort, order, ["id", "ruleKey", "severity", "category"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var page = await _findings.ListAsync(BindListQuery(offset, limit, sortBy, descending, filters), cancellationToken);
        return OkEnvelope(page.Items.Select(AuditWireMapper.ToFinding).ToList(), PaginationOf(page));
    }

    /// <summary>Reads the dashboard summary cards.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dashboard summary.</returns>
    [HttpGet("{id:long}/summary")]
    public async Task<ObjectResult> GetSummaryAsync(long id, CancellationToken cancellationToken)
    {
        var audit = await _audits.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var findings = await _findings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var counters = AuditWireMapper.ParseCounters(audit.CountersJson);

        var countsByStatus = urls.Items
            .GroupBy(StatusClassWire, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var activeFindings = findings.Items.Where(f => !f.IsSuppressed).ToList();
        var countsBySeverity = activeFindings
            .GroupBy(f => ApiEnumConverter<FindingSeverity>.ToWireValue(f.Severity), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var countsByCategory = activeFindings
            .GroupBy(f => ApiEnumConverter<AuditCategory>.ToWireValue(f.Category), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

        var ttfbSamples = urls.Items
            .Select(u => ProjectWireMapper.ParseObject(u.TimingJson)["ttfbMs"]?.GetValue<double>())
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        var crawlHealth = new CrawlHealthWire(
            urls.TotalCount,
            counters.Fetched,
            counters.Error,
            counters.Blocked,
            ttfbSamples.Count > 0 ? Math.Round(ttfbSamples.Average(), 3) : null);

        var waf = await _wafDetections.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        WafSummaryWire? wafCard = null;
        if (waf.Items.Count > 0)
        {
            wafCard = new WafSummaryWire(
                waf.Items.Count,
                counters.Fetched > 0 ? Math.Round((double)waf.Items.Count / counters.Fetched, 4) : 0,
                waf.Items.GroupBy(d => ApiEnumConverter<WafProvider>.ToWireValue(d.Provider), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal),
                waf.Items.GroupBy(d => ApiEnumConverter<WafChallengeClass>.ToWireValue(d.ChallengeClass), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal));
        }

        var throttle = await _throttleEvents.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        ThrottleSummaryWire? throttleCard = null;
        if (throttle.Items.Count > 0)
        {
            throttleCard = new ThrottleSummaryWire(
                throttle.Items.Select(t => t.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Math.Round(throttle.Items.Average(t => t.NewRate), 4),
                throttle.Items.Count);
        }

        return OkEnvelope(new AuditDashboardSummaryWire(countsByStatus, countsBySeverity, countsByCategory, crawlHealth, wafCard, throttleCard));
    }

    /// <summary>Reads one pre-bucketed chart series from the metric aggregates.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="metric">Metric key.</param>
    /// <param name="bucketSize">Advisory bucket size (aggregates arrive pre-bucketed).</param>
    /// <param name="bucketBy">Advisory bucket dimension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chart series.</returns>
    [HttpGet("{id:long}/charts/{metric}")]
    public async Task<ObjectResult> GetChartSeriesAsync(long id, string metric, [FromQuery] int? bucketSize, [FromQuery] string? bucketBy, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var rows = await _metrics.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString(), ["metricKey"] = metric }), cancellationToken);
        var points = rows.Items.Select(row =>
        {
            var dimensions = ProjectWireMapper.ParseObject(row.DimensionsJson);
            var label = dimensions["label"]?.GetValue<string>() ?? metric;
            var wireDimensions = dimensions
                .Where(d => d.Value is not null && d.Key != "label")
                .ToDictionary(d => d.Key, d => d.Value!.ToString(), StringComparer.Ordinal);
            return new ChartPointWire(label, row.Value, wireDimensions.Count > 0 ? wireDimensions : null);
        }).ToList();

        return OkEnvelope(new ChartSeriesResponseWire(metric, points));
    }

    /// <summary>Reads a graph projection with render caps and full totals.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="type">Graph type: structure-tree, link-network, or redirect-chains.</param>
    /// <param name="maxDepth">Optional depth cap.</param>
    /// <param name="maxNodes">Optional node cap.</param>
    /// <param name="maxEdges">Optional edge cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph data.</returns>
    [HttpGet("{id:long}/graph")]
    public async Task<ObjectResult> GetGraphAsync(
        long id,
        [FromQuery] string? type,
        [FromQuery] int? maxDepth,
        [FromQuery] int? maxNodes,
        [FromQuery] int? maxEdges,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        if (type is not ("structure-tree" or "link-network" or "redirect-chains"))
        {
            return ValidationError($"Unknown graph type '{type}'.", [new ApiFieldError("type", "Use structure-tree, link-network, or redirect-chains.")]);
        }

        var nodeCap = Math.Clamp(maxNodes ?? GraphDefaultMaxNodes, 1, 10_000);
        var edgeCap = Math.Clamp(maxEdges ?? GraphDefaultMaxEdges, 1, 50_000);

        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var links = await _links.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);

        var candidates = maxDepth is null ? urls.Items : urls.Items.Where(u => u.Depth <= maxDepth.Value).ToList();
        var ordered = candidates.OrderBy(u => u.Depth).ThenBy(u => u.Id).ToList();
        var nodes = ordered.Take(nodeCap).ToList();
        var nodeIds = nodes.Select(u => u.Id).ToHashSet();

        var equityByTarget = links.Items
            .Where(e => e.TargetUrlId is not null && e.WeightedEquity is not null)
            .GroupBy(e => e.TargetUrlId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.WeightedEquity!.Value));
        var rankedEquity = equityByTarget.Values.OrderBy(v => v).ToList();

        var edges = type switch
        {
            "redirect-chains" => urls.Items
                .Where(u => u.RedirectTargetUrl is not null)
                .Select(u => (Source: u.Id, Target: urls.Items.FirstOrDefault(t => t.CanonicalUrl == u.RedirectTargetUrl)?.Id ?? 0, Weight: (double?)null))
                .Where(e => e.Target != 0 && nodeIds.Contains(e.Source) && nodeIds.Contains(e.Target))
                .ToList(),
            "structure-tree" => BuildSpanningTree(links.Items, nodeIds),
            _ => links.Items
                .Where(e => e.TargetUrlId is not null && nodeIds.Contains(e.SourceUrlId) && nodeIds.Contains(e.TargetUrlId.Value))
                .Select(e => (Source: e.SourceUrlId, Target: e.TargetUrlId!.Value, Weight: e.WeightedEquity))
                .ToList(),
        };

        var totalEdges = edges.Count;
        var renderedEdges = edges.Take(edgeCap).ToList();
        var graphNodes = nodes.Select(u => new GraphNodeWire(
            u.Id.ToString(),
            u.CanonicalUrl,
            null,
            u.StatusClass == CrawlStatusClass.NotCrawled ? null : StatusClassWire(u),
            u.Depth,
            equityByTarget.TryGetValue(u.Id, out var equity) && rankedEquity.Count > 0
                ? Math.Round(100.0 * rankedEquity.BinarySearch(equity) / rankedEquity.Count, 2)
                : null)).ToList();

        return OkEnvelope(new GraphDataWire(
            graphNodes,
            renderedEdges.Select(e => new GraphEdgeWire(e.Source.ToString(), e.Target.ToString(), e.Weight)).ToList(),
            urls.TotalCount,
            totalEdges,
            ordered.Count > nodeCap || totalEdges > edgeCap));
    }

    /// <summary>Reads per-category and overall scores.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Audit scores.</returns>
    [HttpGet("{id:long}/scores")]
    public async Task<ObjectResult> GetScoresAsync(long id, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        var rows = await _scores.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var overall = rows.Items.FirstOrDefault(s => s.Category is null);
        var categories = rows.Items
            .Where(s => s.Category is not null)
            .Select(s => new CategoryScoreWire(
                ApiEnumConverter<AuditCategory>.ToWireValue(s.Category!.Value),
                s.Score,
                s.ErrorCount + s.WarningCount + s.NoticeCount + s.PassCount))
            .ToList();

        return OkEnvelope(new AuditScoresWire(overall?.Score ?? 0, categories));
    }

    private static List<(long Source, long Target, double? Weight)> BuildSpanningTree(IReadOnlyList<LinkEdgeDto> links, HashSet<long> nodeIds)
    {
        var tree = new List<(long, long, double?)>();
        var visited = new HashSet<long>();
        foreach (var edge in links.Where(e => e.TargetUrlId is not null && nodeIds.Contains(e.SourceUrlId) && nodeIds.Contains(e.TargetUrlId.Value)))
        {
            if (visited.Add(edge.TargetUrlId!.Value))
            {
                tree.Add((edge.SourceUrlId, edge.TargetUrlId.Value, edge.WeightedEquity));
            }
        }

        return tree;
    }

    private async Task<Dictionary<long, string>> LoadUrlMapAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        return urls.Items.ToDictionary(u => u.Id, u => u.CanonicalUrl);
    }

    private async Task<Dictionary<long, string?>> LoadTitlesAsync(IReadOnlyList<UrlRecordDto> urls, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<long, string?>();
        foreach (var url in urls)
        {
            var head = await _elements.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
            {
                ["urlId"] = url.Id.ToString(),
                ["elementGroup"] = "document_head",
            }), cancellationToken);
            titles[url.Id] = head.Items.Count > 0
                ? ProjectWireMapper.ParseObject(head.Items[0].PayloadJson)["title"]?.GetValue<string>()
                : null;
        }

        return titles;
    }

    private static UrlRecordSummaryWire ToSummary(UrlRecordDto url, string? title) =>
        new(
            url.Id.ToString(),
            url.CanonicalUrl,
            url.FinalStatusCode ?? 0,
            StatusClassWire(url),
            url.ContentType,
            url.Depth,
            url.IsIndexable,
            title,
            url.BlockReason,
            url.WafProvider is { } provider ? ApiEnumConverter<WafProvider>.ToWireValue(provider) : null);

    /// <summary>
    /// Derives the wire status class: from the final status code when present,
    /// otherwise from the stored crawl status class (Blocked→2xx challenge
    /// pages, FetchError→5xx, NotCrawled→1xx).
    /// </summary>
    private static string StatusClassWire(UrlRecordDto url)
    {
        if (url.FinalStatusCode is { } code)
        {
            return code switch
            {
                >= 100 and < 200 => "1xx",
                >= 200 and < 300 => "2xx",
                >= 300 and < 400 => "3xx",
                >= 400 and < 500 => "4xx",
                _ => "5xx",
            };
        }

        return url.StatusClass switch
        {
            CrawlStatusClass.Success => "2xx",
            CrawlStatusClass.Redirect => "3xx",
            CrawlStatusClass.ClientError => "4xx",
            CrawlStatusClass.ServerError => "5xx",
            CrawlStatusClass.Blocked => "2xx",
            CrawlStatusClass.FetchError => "5xx",
            _ => "1xx",
        };
    }

    private static LinkEdgeWire ToLinkEdge(LinkEdgeDto edge, IReadOnlyDictionary<long, string> urlMap)
    {
        var zoneEvidence = ProjectWireMapper.ParseObject(edge.ZoneEvidence);
        return new LinkEdgeWire(
            edge.SourceUrlId.ToString(),
            edge.TargetUrlId?.ToString() ?? string.Empty,
            urlMap.GetValueOrDefault(edge.SourceUrlId) ?? string.Empty,
            edge.CanonicalTargetUrl,
            edge.IsInternal,
            edge.AnchorText,
            edge.DomZone is { } zone ? DomZoneWire(zone) : null,
            zoneEvidence["weight"]?.GetValue<double>(),
            edge.WeightedEquity);
    }

    private static string DomZoneWire(DomZone zone) => zone switch
    {
        DomZone.Header => "header",
        DomZone.Navigation => "navigation",
        DomZone.MainContent => "mainContent",
        DomZone.Sidebar => "sidebar",
        DomZone.Footer => "footer",
        DomZone.Breadcrumb => "breadcrumb",
        _ => "other",
    };

    private static string? ExtractTitle(IReadOnlyList<UrlElementDto> elements)
    {
        var head = elements.FirstOrDefault(e => e.ElementGroup == "document_head");
        return head is null ? null : ProjectWireMapper.ParseObject(head.PayloadJson)["title"]?.GetValue<string>();
    }

    private static IReadOnlyDictionary<string, string> ExtractResponseHeaders(IReadOnlyList<UrlElementDto> elements)
    {
        var directives = elements.FirstOrDefault(e => e.ElementGroup == ElementGroups.HeaderDirectives);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (directives is null)
        {
            return headers;
        }

        var payload = ProjectWireMapper.ParseObject(directives.PayloadJson);
        var map = new (string Node, string Header)[]
        {
            ("xRobotsTag", "x-robots-tag"),
            ("link", "link"),
            ("contentLanguage", "content-language"),
            ("contentType", "content-type"),
            ("xFrameOptions", "x-frame-options"),
            ("strictTransportSecurity", "strict-transport-security"),
            ("contentSecurityPolicy", "content-security-policy"),
        };
        foreach (var (node, header) in map)
        {
            if (payload[node]?.GetValue<string>() is { Length: > 0 } value)
            {
                headers[header] = value;
            }
        }

        return headers;
    }

    private static ScopeDiagnosticsWire? ExtractScopeDiagnostics(IReadOnlyList<UrlElementDto> elements)
    {
        var scope = elements.FirstOrDefault(e => e.ElementGroup == ElementGroups.ContentScope);
        if (scope is null)
        {
            return null;
        }

        var payload = ProjectWireMapper.ParseObject(scope.PayloadJson);
        var configured = payload["configured"]?.GetValue<bool>() ?? false;
        var scopedWordCount = payload["scopedWordCount"]?.GetValue<int>() ?? 0;
        var fullWordCount = payload["fullWordCount"]?.GetValue<int>() ?? 0;
        var includeSelectors = payload["includeSelectors"]?.AsArray().Select(n => n?.GetValue<string>() ?? string.Empty).ToList() ?? [];
        var excludeSelectors = payload["excludeSelectors"]?.AsArray().Select(n => n?.GetValue<string>() ?? string.Empty).ToList() ?? [];

        var matchedInclude = new List<string>();
        var matchedExclude = new List<string>();
        if (payload["perSelector"] is JsonArray perSelector)
        {
            foreach (var entry in perSelector.OfType<JsonObject>())
            {
                var expression = entry["expression"]?.GetValue<string>();
                var matchCount = entry["matchCount"]?.GetValue<int>() ?? 0;
                if (expression is null || matchCount == 0)
                {
                    continue;
                }

                if (includeSelectors.Contains(expression))
                {
                    matchedInclude.Add(expression);
                }

                if (excludeSelectors.Contains(expression))
                {
                    matchedExclude.Add(expression);
                }
            }
        }

        return new ScopeDiagnosticsWire(!configured || scopedWordCount > 0, matchedInclude, matchedExclude, scopedWordCount, fullWordCount);
    }

    private static SerpMetricsWire? ExtractSerpMetrics(IReadOnlyList<UrlElementDto> elements)
    {
        var serp = elements.FirstOrDefault(e => e.ElementGroup == ElementGroups.SerpMetrics);
        if (serp is null)
        {
            return null;
        }

        var payload = ProjectWireMapper.ParseObject(serp.PayloadJson);
        if (payload["title"] is not JsonObject title || payload["description"] is not JsonObject description)
        {
            return null;
        }

        return new SerpMetricsWire(
            title["widthPixels"]?.GetValue<double>() ?? 0,
            description["widthPixels"]?.GetValue<double>() ?? 0,
            title["isTruncated"]?.GetValue<bool>() ?? false,
            description["isTruncated"]?.GetValue<bool>() ?? false);
    }

    private static IReadOnlyList<HeaderConflictDetailWire>? ExtractHeaderConflicts(IReadOnlyList<FindingDto> findings)
    {
        var conflicts = findings
            .Where(f => f.RuleKey is FindingRuleKeys.IndexabilityHeaderHtmlConflict or FindingRuleKeys.CanonicalHeaderHtmlConflict or FindingRuleKeys.HreflangHeaderHtmlConflict)
            .Select(f =>
            {
                var evidence = ProjectWireMapper.ParseObject(f.EvidenceJson);
                // WP-09-11 (§15.6 fidelity): the header name follows the rule —
                // robots directives ride X-Robots-Tag; canonical and hreflang
                // equivalency compare the Link header against the HTML tags.
                var headerName = f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict
                    ? "x-robots-tag"
                    : "link";
                return new HeaderConflictDetailWire(
                    headerName,
                    evidence["headerValue"]?.GetValue<string>() ?? string.Empty,
                    evidence["htmlValue"]?.GetValue<string>(),
                    f.Severity != FindingSeverity.Pass);
            })
            .ToList();
        return conflicts.Count > 0 ? conflicts : null;
    }

    private static WafDetectionWire ToWafDetection(WafDetectionDto detection)
    {
        var evidence = ProjectWireMapper.ParseObject(detection.EvidenceJson);
        var headers = evidence["headers"] as JsonObject;
        return new WafDetectionWire(
            ApiEnumConverter<WafProvider>.ToWireValue(detection.Provider),
            ApiEnumConverter<WafChallengeClass>.ToWireValue(detection.ChallengeClass),
            evidence["confidence"]?.GetValue<double>() ?? 1.0,
            detection.SignatureKey,
            new WafEvidenceWire(
                evidence["snippet"]?.GetValue<string>(),
                headers?.ToDictionary(h => h.Key, h => h.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal),
                detection.SignatureKey));
    }

    private static NgramRowWire ToNgramRow(NgramFrequencyDto ngram, long? urlId) =>
        new(
            ngram.Term,
            ngram.Order,
            ngram.Zone switch
            {
                TextZone.Title => "title",
                TextZone.Headings => "headings",
                TextZone.AnchorText => "inbound-anchor",
                _ => "body",
            },
            ngram.Count,
            ngram.TermFrequency,
            urlId?.ToString());
}
