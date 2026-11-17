using System.Text.Json;
using System.Web;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Intelligence.NGrams;

namespace CrawlForge.Mcp;

/// <summary>
/// Read-only data ports for the MCP resource surface. Every port is a locked
/// repository or an accepted side-effect-free read model; the Host binds them
/// at composition time (same pattern as SemanticPersistence).
/// </summary>
/// <param name="Audits">Audit rows.</param>
/// <param name="Findings">Finding rows.</param>
/// <param name="Urls">URL records.</param>
/// <param name="Metrics">Metric summaries.</param>
/// <param name="Scores">Audit scores.</param>
/// <param name="Schedules">Schedule definitions.</param>
/// <param name="ScheduleRuns">Schedule firing history.</param>
/// <param name="WafDetections">WAF detection rows.</param>
/// <param name="ThrottleEvents">AIMD host-throttle history.</param>
/// <param name="RedirectMappings">Redirect matrix rows.</param>
/// <param name="LogImports">Log import records.</param>
/// <param name="LogAggregates">Per-URL log hit aggregates.</param>
/// <param name="Opportunities">Semantic link opportunities.</param>
/// <param name="Clusters">Semantic cluster memberships.</param>
/// <param name="AnalyticsDetections">Analytics tag/container detections.</param>
/// <param name="ExtractedContent">Custom extraction values.</param>
/// <param name="GovernanceRows">AI-bot governance matrix rows.</param>
/// <param name="NgramFrequencies">Per-URL n-gram frequencies.</param>
/// <param name="Artifacts">Content artifact index rows.</param>
/// <param name="ContentStore">Stored content artifacts.</param>
/// <param name="Ngrams">Accepted n-gram read model.</param>
/// <param name="Geo">Accepted GEO readiness analyzer (pure, local).</param>
/// <param name="LogCorrelation">Accepted log correlation engine (pure, local).</param>
/// <param name="Lifecycle">Audit-scoped lifecycle port for live progress.</param>
public sealed record McpResourcePorts(
    IReadRepository<AuditDto> Audits,
    IReadRepository<FindingDto> Findings,
    IReadRepository<UrlRecordDto> Urls,
    IReadRepository<MetricSummaryDto> Metrics,
    IReadRepository<AuditScoreDto> Scores,
    IReadRepository<ScheduleDefinitionDto> Schedules,
    IReadRepository<ScheduleRunDto> ScheduleRuns,
    IReadRepository<WafDetectionDto> WafDetections,
    IReadRepository<HostThrottleEventDto> ThrottleEvents,
    IReadRepository<RedirectMappingDto> RedirectMappings,
    IReadRepository<LogImportDto> LogImports,
    IReadRepository<LogUrlAggregateDto> LogAggregates,
    IReadRepository<SemanticLinkOpportunityDto> Opportunities,
    IReadRepository<SemanticClusterDto> Clusters,
    IReadRepository<AnalyticsDetectionDto> AnalyticsDetections,
    IReadRepository<ExtractedContentDto> ExtractedContent,
    IReadRepository<GovernanceMatrixRowDto> GovernanceRows,
    IReadRepository<NgramFrequencyDto> NgramFrequencies,
    IReadRepository<ContentArtifactDto> Artifacts,
    IContentStore ContentStore,
    NGramReadModel Ngrams,
    IGeoReadinessAnalyzer Geo,
    ILogCorrelationEngine LogCorrelation,
    IAuditLifecyclePort Lifecycle);

/// <summary>
/// A requested resource URI is unknown or malformed (maps to JSON-RPC
/// invalid params).
/// </summary>
public sealed class McpResourceNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="uri">The offending URI.</param>
    public McpResourceNotFoundException(string uri)
        : base($"Unknown or malformed resource URI: {uri}")
    {
        Uri = uri;
    }

    /// <summary>The offending URI.</summary>
    public string Uri { get; }
}

/// <summary>
/// MCP resource surface (FS-CRAWL-001 §11.4 + Appendix D): all fifteen
/// read-only resources — seven baseline and eight additive — with additive
/// payload fields on the parents. URIs use the <c>crawlforge://</c> scheme;
/// listed definitions carry <c>{auditId}</c>/<c>{urlId}</c> templates that
/// callers substitute on read. Every read is bounded and side-effect free.
/// </summary>
public sealed class McpResourceSurface : IMcpResourceSurface
{
    /// <summary>Default row bound per resource read.</summary>
    public const int DefaultReadLimit = 200;

    private const string JsonMime = "application/json";
    private readonly McpResourcePorts _ports;

    /// <summary>Creates the resource surface.</summary>
    /// <param name="ports">Read-only data ports.</param>
    public McpResourceSurface(McpResourcePorts ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<McpResourceDefinition>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Definitions);

    /// <inheritdoc/>
    public async Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "crawlforge", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpResourceNotFoundException(uri);
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = HttpUtility.ParseQueryString(parsed.Query);
        object payload;

        if (parsed.Host.Equals("audits", StringComparison.OrdinalIgnoreCase) && segments.Length == 0)
        {
            payload = await AuditListAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (parsed.Host.Equals("schedules", StringComparison.OrdinalIgnoreCase) && segments.Length == 0)
        {
            payload = await ScheduleStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (parsed.Host.Equals("audits", StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 2
            && long.TryParse(segments[0], out var auditId))
        {
            payload = segments[1] switch
            {
                "summary" => await AuditSummaryAsync(auditId, cancellationToken).ConfigureAwait(false),
                "high-severity-findings" => await HighSeverityAsync(auditId, cancellationToken).ConfigureAwait(false),
                "progress" => await CrawlProgressAsync(auditId, cancellationToken).ConfigureAwait(false),
                "ngrams" => await NgramAnalysisAsync(auditId, query, cancellationToken).ConfigureAwait(false),
                "waf-blocks" => await WafBlocksAsync(auditId, cancellationToken).ConfigureAwait(false),
                "host-throttle" => await HostThrottleAsync(auditId, cancellationToken).ConfigureAwait(false),
                "redirect-matrix" => await RedirectMatrixAsync(auditId, cancellationToken).ConfigureAwait(false),
                "log-correlation" => await LogCorrelationAsync(auditId, query, cancellationToken).ConfigureAwait(false),
                "semantic-links" => await SemanticLinksAsync(auditId, cancellationToken).ConfigureAwait(false),
                "geo-readiness" => await GeoReadinessAsync(auditId, query, cancellationToken).ConfigureAwait(false),
                "analytics-coverage" => await AnalyticsCoverageAsync(auditId, query, cancellationToken).ConfigureAwait(false),
                "urls" when segments.Length >= 3 && long.TryParse(segments[2], out var urlId) =>
                    segments.Length >= 4 && segments[3] == "content"
                        ? await ExtractedContentAsync(auditId, urlId, cancellationToken).ConfigureAwait(false)
                        : await UrlDetailAsync(auditId, urlId, cancellationToken).ConfigureAwait(false),
                _ => throw new McpResourceNotFoundException(uri),
            };
        }
        else
        {
            throw new McpResourceNotFoundException(uri);
        }

        return new McpResourceContent(uri, JsonMime, JsonSerializer.Serialize(payload, McpProtocol.JsonOptions));
    }

    private static readonly IReadOnlyList<McpResourceDefinition> Definitions =
    [
        new("crawlforge://audits/{auditId}/summary", "audit-summary", "Audit overview: status, scores, metric summaries, finding counts, WAF counts.", JsonMime),
        new("crawlforge://audits/{auditId}/high-severity-findings", "high-severity-findings", "Error and Warning findings with evidence and recommendations.", JsonMime),
        new("crawlforge://audits/{auditId}/urls/{urlId}", "url-detail", "URL record with findings, WAF detection, scope diagnostics, top n-grams, SERP metrics, semantic relations.", JsonMime),
        new("crawlforge://audits", "audit-list", "All audits with status and phase.", JsonMime),
        new("crawlforge://schedules", "schedule-status", "Schedule definitions with next-due and recent runs.", JsonMime),
        new("crawlforge://audits/{auditId}/progress", "crawl-progress", "Live progress with throttle summary and redirect-matrix counters.", JsonMime),
        new("crawlforge://audits/{auditId}/urls/{urlId}/content", "extracted-content", "Custom extraction values and stored body text for one URL.", JsonMime),
        new("crawlforge://audits/{auditId}/ngrams", "ngram-analysis", "N-gram site summaries and per-URL frequencies (zone/order filters).", JsonMime),
        new("crawlforge://audits/{auditId}/waf-blocks", "waf-blocks", "WAF detections with provider, challenge class, and evidence.", JsonMime),
        new("crawlforge://audits/{auditId}/host-throttle", "host-throttle", "Per-host AIMD throttle history.", JsonMime),
        new("crawlforge://audits/{auditId}/redirect-matrix", "redirect-matrix-results", "Redirect matrix rows and counters.", JsonMime),
        new("crawlforge://audits/{auditId}/log-correlation?import={logImportId}", "log-correlation", "True orphans, budget waste, and uncrawled indexable URLs for a log import.", JsonMime),
        new("crawlforge://audits/{auditId}/semantic-links", "semantic-link-opportunities", "Semantic link opportunities and cannibalization clusters (suggestions only).", JsonMime),
        new("crawlforge://audits/{auditId}/geo-readiness", "geo-readiness", "llms.txt validation results and AI-bot governance summary.", JsonMime),
        new("crawlforge://audits/{auditId}/analytics-coverage", "analytics-coverage", "Per-container analytics coverage across indexable pages.", JsonMime),
    ];

    private static Dictionary<string, string> AuditFilter(long auditId) =>
        new() { ["AuditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        IReadRepository<T> repository,
        Dictionary<string, string> filters,
        CancellationToken cancellationToken,
        string? sortBy = null,
        bool descending = false)
        where T : class
    {
        var page = await repository.ListAsync(new Contracts.Common.ListQuery(
            Offset: 0, Limit: DefaultReadLimit, Filters: filters, SortBy: sortBy, SortDescending: descending), cancellationToken)
            .ConfigureAwait(false);
        return page.Items;
    }

    private async Task<object> AuditListAsync(CancellationToken cancellationToken)
    {
        var audits = await ReadAsync(_ports.Audits, [], cancellationToken, "Id", true).ConfigureAwait(false);
        return new { audits };
    }

    private async Task<object> ScheduleStatusAsync(CancellationToken cancellationToken)
    {
        var schedules = await ReadAsync(_ports.Schedules, [], cancellationToken).ConfigureAwait(false);
        var runs = await ReadAsync(_ports.ScheduleRuns, [], cancellationToken, "Id", true).ConfigureAwait(false);
        return new { schedules, recentRuns = runs };
    }

    private async Task<object> AuditSummaryAsync(long auditId, CancellationToken cancellationToken)
    {
        var audit = await _ports.Audits.GetByIdAsync(auditId, cancellationToken).ConfigureAwait(false);
        var scores = await ReadAsync(_ports.Scores, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var metrics = await ReadAsync(_ports.Metrics, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var findings = await ReadAsync(_ports.Findings, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var wafDetections = await ReadAsync(_ports.WafDetections, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var severityCounts = findings
            .Where(f => !f.IsSuppressed)
            .GroupBy(f => f.Severity)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        return new
        {
            audit,
            scores,
            metrics,
            findingCountsBySeverity = severityCounts,
            waf = new
            {
                detectionCount = wafDetections.Count,
                resolvedByRendering = wafDetections.Count(d => d.ResolvedByRendering),
                byProvider = wafDetections.GroupBy(d => d.Provider.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
            },
        };
    }

    private async Task<object> HighSeverityAsync(long auditId, CancellationToken cancellationToken)
    {
        var findings = await ReadAsync(_ports.Findings, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var high = findings
            .Where(f => f.Severity is FindingSeverity.Error or FindingSeverity.Warning)
            .OrderByDescending(f => f.Severity)
            .ToArray();
        return new { auditId, findings = high };
    }

    private async Task<object> CrawlProgressAsync(long auditId, CancellationToken cancellationToken)
    {
        var progress = await _ports.Lifecycle.GetProgressAsync(auditId, cancellationToken).ConfigureAwait(false);
        var throttle = await ReadAsync(_ports.ThrottleEvents, AuditFilter(auditId), cancellationToken, "Id", true).ConfigureAwait(false);
        var mappings = await ReadAsync(_ports.RedirectMappings, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var latestPerHost = throttle
            .GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().NewRate);
        return new
        {
            progress,
            throttleSummary = latestPerHost,
            matrixCounters = new { redirectMappings = mappings.Count },
        };
    }

    private async Task<object> UrlDetailAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        var url = await _ports.Urls.GetByIdAsync(urlId, cancellationToken).ConfigureAwait(false);
        if (url is null || url.AuditId != auditId)
        {
            throw new McpResourceNotFoundException($"crawlforge://audits/{auditId}/urls/{urlId}");
        }

        var findings = (await ReadAsync(_ports.Findings, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(f => f.UrlId == urlId).ToArray();
        var waf = (await ReadAsync(_ports.WafDetections, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(d => d.UrlId == urlId).ToArray();
        var ngrams = (await ReadAsync(_ports.NgramFrequencies, AuditFilter(auditId), cancellationToken, "Count", true).ConfigureAwait(false))
            .Where(n => n.UrlId == urlId && n.Zone != TextZone.AnchorText)
            .Take(10).ToArray();
        var opportunities = (await ReadAsync(_ports.Opportunities, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(o => o.SourceUrlId == urlId || o.TargetUrlId == urlId).ToArray();
        var clusters = (await ReadAsync(_ports.Clusters, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(c => c.UrlId == urlId).ToArray();
        var audit = await _ports.Audits.GetByIdAsync(auditId, cancellationToken).ConfigureAwait(false);
        var serpMetrics = (await ReadAsync(_ports.Metrics, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(m => m.MetricKey.StartsWith("serp", StringComparison.OrdinalIgnoreCase)).ToArray();
        var headerConflicts = findings
            .Where(f => f.RuleKey.StartsWith("header.", StringComparison.OrdinalIgnoreCase)).ToArray();

        return new
        {
            url,
            findings,
            wafDetection = waf,
            contentScopeDiagnostics = audit?.ResolvedContentScopeJson,
            topNgrams = ngrams,
            serpMetrics,
            headerConflicts,
            semanticRelations = new { opportunities, clusters },
        };
    }

    private async Task<object> ExtractedContentAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        var values = (await ReadAsync(_ports.ExtractedContent, AuditFilter(auditId), cancellationToken).ConfigureAwait(false))
            .Where(v => v.UrlId == urlId).ToArray();
        var artifacts = (await ReadAsync(_ports.Artifacts, AuditFilter(auditId), cancellationToken, "Id", true).ConfigureAwait(false))
            .Where(a => a.UrlId == urlId).ToArray();
        string? bodyText = null;
        var body = artifacts.Where(a => a.Kind == ContentArtifactKind.Body).OrderByDescending(a => a.Id).FirstOrDefault();
        if (body is not null)
        {
            await using var stream = await _ports.ContentStore.OpenReadAsync(body.StorageKey, cancellationToken).ConfigureAwait(false);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                bodyText = text.Length <= 20000 ? text : text[..20000];
            }
        }

        return new { auditId, urlId, extractedValues = values, bodyText };
    }

    private async Task<object> NgramAnalysisAsync(long auditId, System.Collections.Specialized.NameValueCollection query, CancellationToken cancellationToken)
    {
        var zone = Enum.TryParse<TextZone>(query["zone"], ignoreCase: true, out var z) ? z : (TextZone?)null;
        var order = int.TryParse(query["order"], out var o) ? o : (int?)null;
        var list = await _ports.Ngrams.ListAsync(auditId, new NgramListQuery(
            Scope: query["scope"] ?? "site", Zone: zone, Order: order, Offset: 0, Limit: DefaultReadLimit), cancellationToken)
            .ConfigureAwait(false);
        return new { auditId, ngrams = list };
    }

    private async Task<object> WafBlocksAsync(long auditId, CancellationToken cancellationToken)
    {
        var detections = await ReadAsync(_ports.WafDetections, AuditFilter(auditId), cancellationToken, "Id", true).ConfigureAwait(false);
        return new { auditId, detections };
    }

    private async Task<object> HostThrottleAsync(long auditId, CancellationToken cancellationToken)
    {
        var events = await ReadAsync(_ports.ThrottleEvents, AuditFilter(auditId), cancellationToken, "Id", true).ConfigureAwait(false);
        return new { auditId, events };
    }

    private async Task<object> RedirectMatrixAsync(long auditId, CancellationToken cancellationToken)
    {
        var mappings = await ReadAsync(_ports.RedirectMappings, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        return new { auditId, mappings, counters = new { total = mappings.Count } };
    }

    private async Task<object> LogCorrelationAsync(long auditId, System.Collections.Specialized.NameValueCollection query, CancellationToken cancellationToken)
    {
        if (!long.TryParse(query["import"], out var logImportId))
        {
            throw new McpResourceNotFoundException($"crawlforge://audits/{auditId}/log-correlation (missing import query)");
        }

        var import = await _ports.LogImports.GetByIdAsync(logImportId, cancellationToken).ConfigureAwait(false);
        var correlation = await _ports.LogCorrelation.CorrelateAsync(auditId, logImportId, cancellationToken).ConfigureAwait(false);
        return new { auditId, logImportId, import, correlation };
    }

    private async Task<object> SemanticLinksAsync(long auditId, CancellationToken cancellationToken)
    {
        var opportunities = await ReadAsync(_ports.Opportunities, AuditFilter(auditId), cancellationToken, "Similarity", true).ConfigureAwait(false);
        var clusters = await ReadAsync(_ports.Clusters, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        return new { auditId, opportunities, clusters, note = "Suggestions only; never auto-applied." };
    }

    private async Task<object> GeoReadinessAsync(long auditId, System.Collections.Specialized.NameValueCollection query, CancellationToken cancellationToken)
    {
        var readiness = await _ports.Geo.AnalyzeAsync(auditId, cancellationToken).ConfigureAwait(false);
        var governance = await ReadAsync(_ports.GovernanceRows, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var agentFilter = query["agent"];
        if (!string.IsNullOrEmpty(agentFilter))
        {
            governance = governance.Where(r => r.AgentName.Contains(agentFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return new
        {
            auditId,
            readinessScore = readiness.ReadinessScore,
            hostResults = readiness.HostResults,
            governanceSummary = new
            {
                rows = governance,
                allow = governance.Count(r => r.Verdict == GovernanceVerdict.Allow),
                deny = governance.Count(r => r.Verdict == GovernanceVerdict.Deny),
            },
        };
    }

    private async Task<object> AnalyticsCoverageAsync(long auditId, System.Collections.Specialized.NameValueCollection query, CancellationToken cancellationToken)
    {
        var detections = await ReadAsync(_ports.AnalyticsDetections, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var vendorFilter = query["vendor"];
        if (!string.IsNullOrEmpty(vendorFilter))
        {
            detections = detections.Where(d => d.Vendor.Contains(vendorFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        var urls = await ReadAsync(_ports.Urls, AuditFilter(auditId), cancellationToken).ConfigureAwait(false);
        var indexableCount = urls.Count(u => u.IsIndexable);
        var byContainer = detections
            .GroupBy(d => new { d.Vendor, Container = d.ContainerId ?? "(none)" })
            .Select(g => new
            {
                g.Key.Vendor,
                containerId = g.Key.Container,
                urlCount = g.Select(d => d.UrlId).Distinct().Count(),
                detectionCount = g.Count(),
            })
            .ToArray();
        return new
        {
            auditId,
            indexableUrlCount = indexableCount,
            coveredUrlCount = detections.Select(d => d.UrlId).Distinct().Count(),
            byContainer,
            detections,
        };
    }
}
