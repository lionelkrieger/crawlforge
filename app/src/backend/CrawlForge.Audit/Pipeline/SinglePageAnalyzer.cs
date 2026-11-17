using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Pipeline;

/// <summary>Local options for standalone single-page analysis.</summary>
public sealed record SinglePageAnalyzerOptions
{
    /// <summary>Optional default scope applied to the analysis (§4.6 embedding).</summary>
    public ContentScopeDefinition? DefaultScope { get; init; }

    /// <summary>Device profile used when rendering is requested.</summary>
    public DeviceProfile? RenderDevice { get; init; }
}

/// <summary>
/// Standalone single-page analysis (FS-CRAWL-001 §7.7, criterion #10): fetches
/// and audits one URL WITHOUT creating a project or crawl, applying the same
/// pipeline. This ad-hoc fetch is the explicit §7.7 mode — the pipeline itself
/// never fetches. Results carry the additive WAF verdict and scope diagnostics
/// (FS-CRAWL-002 Appendix B) and may be saved as a lightweight local record by
/// the caller.
/// </summary>
public sealed class SinglePageAnalyzer : ISinglePageAnalyzer
{
    private const int WafBodySnippetBound = 4096;

    private static readonly DeviceProfile DefaultDesktop = new(
        "desktop", 1920, 1080, "CrawlForge-SinglePage", IsMobile: false);

    private readonly IHttpFetcher _fetcher;
    private readonly ITolerantDomParser _parser;
    private readonly IAuditPipeline _pipeline;
    private readonly IWafChallengeEvaluator? _wafEvaluator;
    private readonly IRenderingPool? _renderingPool;
    private readonly IContentScopeResolver? _scopeResolver;
    private readonly SinglePageAnalyzerOptions _options;

    /// <summary>Creates the analyzer with composition-time optional services.</summary>
    /// <param name="fetcher">HTTP fetcher (ad-hoc §7.7 fetch).</param>
    /// <param name="parser">Tolerant DOM parser.</param>
    /// <param name="pipeline">The shared audit pipeline.</param>
    /// <param name="wafEvaluator">Optional WAF challenge evaluator.</param>
    /// <param name="renderingPool">Optional rendering pool (Render requests).</param>
    /// <param name="scopeResolver">Optional scope resolver for diagnostics.</param>
    /// <param name="options">Optional local options.</param>
    public SinglePageAnalyzer(
        IHttpFetcher fetcher,
        ITolerantDomParser parser,
        IAuditPipeline pipeline,
        IWafChallengeEvaluator? wafEvaluator = null,
        IRenderingPool? renderingPool = null,
        IContentScopeResolver? scopeResolver = null,
        SinglePageAnalyzerOptions? options = null)
    {
        _fetcher = fetcher;
        _parser = parser;
        _pipeline = pipeline;
        _wafEvaluator = wafEvaluator;
        _renderingPool = renderingPool;
        _scopeResolver = scopeResolver;
        _options = options ?? new SinglePageAnalyzerOptions();
    }

    /// <inheritdoc />
    public async Task<SinglePageAnalysisResult> AnalyzeAsync(
        SinglePageAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var fetch = await _fetcher.FetchAsync(new HttpFetchRequest(request.Url), cancellationToken);
        var bodyText = Encoding.UTF8.GetString(fetch.Body.Span);

        var wafVerdict = _wafEvaluator?.Evaluate(new WafEvaluationInput(
            fetch.FinalUrl,
            fetch.StatusCode,
            fetch.Headers,
            bodyText.Length <= WafBodySnippetBound ? bodyText : bodyText[..WafBodySnippetBound]));

        var document = _parser.Parse(bodyText, fetch.FinalUrl);

        DomDocument? rendered = null;
        if (request.Render && _renderingPool is not null)
        {
            var page = await _renderingPool.RenderAsync(
                new RenderRequest(fetch.FinalUrl, _options.RenderDevice ?? DefaultDesktop),
                cancellationToken);
            rendered = _parser.Parse(page.Html, page.FinalUrl);
        }

        var scope = _options.DefaultScope;
        var scopedText = scope is null ? null : _scopeResolver?.Resolve(rendered ?? document, scope);

        var urlRecord = BuildSyntheticUrlRecord(fetch);
        var configuration = BuildSyntheticConfiguration(request.Url, scope);
        var context = new PageAuditContext(
            AuditId: 0,
            Url: urlRecord,
            Configuration: configuration,
            Fetch: fetch,
            Document: document,
            RenderedDocument: rendered,
            ScopedText: scopedText,
            CapturedRequests: null);

        var result = await _pipeline.RunAsync(context, cancellationToken);

        var findings = request.RuleKeys is null
            ? result.Findings
            : result.Findings.Where(f => request.RuleKeys.Contains(f.RuleKey, StringComparer.Ordinal)).ToArray();

        return new SinglePageAnalysisResult(findings, result.Inventory, wafVerdict, scopedText);
    }

    private static UrlRecordDto BuildSyntheticUrlRecord(HttpFetchResult fetch)
    {
        var uri = new Uri(fetch.FinalUrl);
        var statusClass = fetch.StatusCode switch
        {
            >= 200 and < 300 => CrawlStatusClass.Success,
            >= 300 and < 400 => CrawlStatusClass.Redirect,
            >= 400 and < 500 => CrawlStatusClass.ClientError,
            >= 500 and < 600 => CrawlStatusClass.ServerError,
            _ => CrawlStatusClass.FetchError,
        };

        fetch.Headers.TryGetValue("content-type", out var contentType);

        return new UrlRecordDto(
            Id: 0,
            AuditId: 0,
            CanonicalUrl: fetch.FinalUrl,
            Host: uri.Host,
            Path: uri.AbsolutePath,
            Depth: 0,
            DiscoverySource: DiscoverySource.Seed,
            StatusClass: statusClass,
            FinalStatusCode: fetch.StatusCode,
            RedirectTargetUrl: null,
            ContentType: contentType,
            SizeBytes: fetch.Body.Length,
            TimingJson: null,
            RobotsState: RobotsState.Allowed,
            IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: fetch.StatusCode is >= 200 and < 300);
    }

    private static CrawlConfigurationDto BuildSyntheticConfiguration(string url, ContentScopeDefinition? scope)
    {
        var now = DateTimeOffset.UtcNow;
        return new CrawlConfigurationDto(
            Id: 0,
            ProjectId: 0,
            Name: "single-page",
            SeedsJson: "[\"" + url.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"]",
            ScopeRulesJson: null,
            Depth: null,
            LimitOverrides: null,
            RenderingMode: RenderMode.None,
            PolitenessJson: null,
            ProxyRoutingJson: null,
            CustomExtractionRulesJson: null,
            HeadersJson: null,
            CookiesJson: null,
            CreatedAt: now,
            UpdatedAt: now)
        {
            ContentScope = scope,
        };
    }
}
