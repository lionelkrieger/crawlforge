using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>Canned HTTP fetcher; counts calls to prove the pipeline never fetches.</summary>
internal sealed class FakeHttpFetcher : IHttpFetcher
{
    private readonly HttpFetchResult _result;

    public FakeHttpFetcher(string body, int statusCode = 200, string url = "https://example.test/page")
    {
        _result = new HttpFetchResult(
            url,
            statusCode,
            new Dictionary<string, string> { ["content-type"] = "text/html; charset=utf-8" },
            Encoding.UTF8.GetBytes(body),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50));
    }

    public int FetchCount { get; private set; }

    public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        FetchCount++;
        return Task.FromResult(_result);
    }
}

/// <summary>Rule analyzer double returning configured findings and recording contexts.</summary>
internal sealed class RecordingRuleAnalyzer : IAuditRuleAnalyzer
{
    private readonly IReadOnlyList<FindingDraft> _findings;

    public RecordingRuleAnalyzer(IReadOnlyList<string> ruleKeys, IReadOnlyList<FindingDraft>? findings = null)
    {
        SupportedRuleKeys = ruleKeys;
        _findings = findings ?? [];
    }

    public IReadOnlyList<string> SupportedRuleKeys { get; }

    public int Calls { get; private set; }

    public PageAuditContext? LastContext { get; private set; }

    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastContext = context;
        return Task.FromResult(_findings);
    }
}

/// <summary>Rule analyzer double that always throws (failure-isolation tests).</summary>
internal sealed class ThrowingRuleAnalyzer : IAuditRuleAnalyzer
{
    public IReadOnlyList<string> SupportedRuleKeys => ["throwing.rule"];

    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("analyzer boom");
}

/// <summary>WAF evaluator double returning a configured verdict.</summary>
internal sealed class FakeWafEvaluator : IWafChallengeEvaluator
{
    private readonly WafEvaluationResult _verdict;

    public FakeWafEvaluator(bool isChallenge = false)
    {
        _verdict = new WafEvaluationResult(
            isChallenge,
            isChallenge ? WafProvider.Cloudflare : WafProvider.Unknown,
            isChallenge ? WafChallengeClass.Interstitial : WafChallengeClass.Unknown,
            [],
            "{}");
    }

    public WafEvaluationResult Evaluate(WafEvaluationInput input) => _verdict;
}

/// <summary>Rendering pool double returning canned rendered HTML.</summary>
internal sealed class FakeRenderingPool : IRenderingPool
{
    private readonly string _html;

    public FakeRenderingPool(string html)
    {
        _html = html;
    }

    public int RenderCount { get; private set; }

    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        RenderCount++;
        return Task.FromResult(new RenderedPage(
            request.Url,
            _html,
            ReadOnlyMemory<byte>.Empty,
            [],
            null));
    }
}

/// <summary>Shared context builders for pipeline tests.</summary>
internal static class PipelineTestContext
{
    public const string SampleHtml = """
        <html lang="en">
        <head>
          <title>Sample Page</title>
          <meta name="description" content="A sample page for pipeline tests.">
          <meta name="robots" content="index,follow">
          <link rel="canonical" href="https://example.test/page">
        </head>
        <body>
          <nav class="mega-menu"><a href="/nav1">Nav one</a> <a href="/nav2">Nav two</a></nav>
          <main id="content">
            <h1>Main heading</h1>
            <p>Alpha beta gamma delta epsilon zeta.</p>
            <p>Eta theta iota kappa lambda mu nu.</p>
            <a href="/internal">Internal link</a>
          </main>
          <footer>Footer boilerplate text here.</footer>
          <script>var hidden = "script text";</script>
        </body>
        </html>
        """;

    public static CrawlConfigurationDto NewConfig(long projectId = 1, ContentScopeDefinition? scope = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new CrawlConfigurationDto(
            Id: 0,
            ProjectId: projectId,
            Name: "test",
            SeedsJson: "[\"https://example.test/\"]",
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

    public static UrlRecordDto NewUrl(long urlId = 1, long auditId = 7) => new(
        Id: urlId,
        AuditId: auditId,
        CanonicalUrl: "https://example.test/page",
        Host: "example.test",
        Path: "/page",
        Depth: 0,
        DiscoverySource: DiscoverySource.Seed,
        StatusClass: CrawlStatusClass.Success,
        FinalStatusCode: 200,
        RedirectTargetUrl: null,
        ContentType: "text/html",
        SizeBytes: 1024,
        TimingJson: null,
        RobotsState: RobotsState.Allowed,
        IndexabilityFlags: IndexabilityFlags.None,
        IsIndexable: true);

    public static PageAuditContext Create(
        DomDocument? document = null,
        HttpFetchResult? fetch = null,
        CrawlConfigurationDto? config = null,
        DomDocument? rendered = null,
        ScopedVisibleText? scoped = null,
        long urlId = 1,
        long auditId = 7) => new(
            AuditId: auditId,
            Url: NewUrl(urlId, auditId),
            Configuration: config ?? NewConfig(),
            Fetch: fetch,
            Document: document,
            RenderedDocument: rendered,
            ScopedText: scoped,
            CapturedRequests: null);
}
