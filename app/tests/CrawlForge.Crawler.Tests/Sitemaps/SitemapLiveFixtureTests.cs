using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using CrawlForge.Crawler.Tests.Sitemaps.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Sitemaps;

/// <summary>
/// Live component proof against the builder's OWN fixture-site copy on port
/// 47313 (read-only server.mjs, never edited; coordinator/sibling ports
/// 47311/47312 untouched): sitemap discovery + ingestion, hop-by-hop redirect
/// recording → persistence → classification → findings (the G27 defect path),
/// redirect-loop detection, internal 404, and external DNS failure (the
/// WP-03-04 reopen). All requests are GETs; the fixture recorder's nonGet
/// list must stay empty.
/// </summary>
public sealed class SitemapLiveFixtureTests : IClassFixture<SitemapLiveFixtureTests.ServerFixture>
{
    private const long AuditId = 9001;
    private readonly ServerFixture _server;
    private readonly UrlCanonicalizer _canonicalizer = new();

    public SitemapLiveFixtureTests(ServerFixture server)
    {
        _server = server;
    }

    public sealed class ServerFixture : IAsyncLifetime
    {
        public FixtureSiteServer Server { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Server = await FixtureSiteServer.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await Server.DisposeAsync();
        }
    }

    private static HttpFetcher CreateFetcher(IRedirectChainRecorder? recorder = null, IRedirectChainStore? store = null) =>
        new(
            new HttpFetcherOptions(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1),
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }),
            recorder ?? (store is null ? null : new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store))));

    [Fact]
    public async Task Live_DiscoverAndIngest_FixtureSitemapSeedsOrphanUrl()
    {
        await _server.Server.ResetAsync();
        using var fetcher = CreateFetcher();
        var ingestor = new SitemapIngestor(fetcher, new RobotsTxtParser(), _canonicalizer);

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(AuditId, new[] { _server.Server.BaseUrl + "/" }));

        Assert.Equal(8, result.Urls.Count);
        var canonicalKeys = result.Urls.Select(u => u.CanonicalUrl).ToList();
        Assert.Contains(_canonicalizer.Canonicalize(_server.Server.BaseUrl + "/pages/orphan.html"), canonicalKeys);
        Assert.Contains(_canonicalizer.Canonicalize(_server.Server.BaseUrl + "/redirect/chain-1"), canonicalKeys);
        SitemapSeededUrl orphan = result.Urls.First(u => u.CanonicalUrl.EndsWith("/pages/orphan.html"));
        Assert.Equal(_server.Server.BaseUrl + "/sitemap.xml", orphan.SourceSitemapUrl);
        Assert.All(result.Urls, u => Assert.Equal("127.0.0.1", u.Host));
        Assert.All(result.CreateFrontierEntries(), e => Assert.Equal(DiscoverySource.Sitemap, e.DiscoverySource));
        Assert.Contains(result.Sitemaps, s => s.Source == SitemapSourceKind.RobotsDirective);
    }

    [Fact]
    public async Task Live_RedirectChain_RecordedPersistedClassifiedAndReported()
    {
        await _server.Server.ResetAsync();
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
        using var fetcher = CreateFetcher(recorder);
        var originalUrl = _server.Server.BaseUrl + "/redirect/chain-1";

        FetchContextScope.Current = new FetchContext(AuditId, 101);
        HttpFetchResult result;
        try
        {
            result = await fetcher.FetchAsync(new HttpFetchRequest(originalUrl));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        // Terminal facts: three hops 301>302>301 end at the redirect target.
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(_server.Server.BaseUrl + "/pages/redirect-target.html", result.FinalUrl);

        IReadOnlyList<RedirectHop> chain = await recorder.GetChainAsync(AuditId, 101);
        Assert.Equal(3, chain.Count);
        Assert.Equal(new[] { 301, 302, 301 }, chain.Select(h => h.StatusCode));
        Assert.Equal(originalUrl, chain[0].FromUrl);
        Assert.Equal(_server.Server.BaseUrl + "/redirect/chain-2", chain[0].ToUrl);
        Assert.Equal(_server.Server.BaseUrl + "/pages/redirect-target.html", chain[2].ToUrl);

        // Hop-by-hop durable persistence (url_elements mechanism double).
        RedirectChainPayload? payload = await store.GetPayloadAsync(101);
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Hops.Count);
        Assert.False(payload.LoopDetected);
        Assert.Equal(originalUrl, payload.Hops[0].Url);
        Assert.Equal(301, payload.Hops[0].StatusCode);

        // Probe-2 defect fix: the SOURCE record is 3xx + chain, not 200.
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(originalUrl, result, chain);
        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(301, outcome.SourceStatusCode);
        Assert.Equal(_server.Server.BaseUrl + "/redirect/chain-2", outcome.RedirectTargetUrl);
        Assert.True(outcome.IsRedirectSource);
        Assert.Equal(200, outcome.FinalStatusCode);

        // Terminal outcome persisted with final facts.
        await store.SaveOutcomeAsync(101, RedirectChainPersistence.BuildOutcome(chain, result.FinalUrl, result.StatusCode));
        payload = await store.GetPayloadAsync(101);
        Assert.Equal(result.FinalUrl, payload!.FinalUrl);
        Assert.Equal(200, payload.FinalStatusCode);

        // Findings: chain reporting + inappropriate (302 hop) with full evidence.
        var analyzer = new RedirectFindingAnalyzer();
        IReadOnlyList<Contracts.Entities.FindingDto> findings = analyzer.AnalyzeChain(AuditId, 101, originalUrl, chain, result.FinalUrl, result.StatusCode);
        Assert.Contains(findings, f => f.RuleKey == RedirectRuleKeys.Chain && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, f => f.RuleKey == RedirectRuleKeys.Inappropriate);

        // Sitemap correlation: chain-1 is sitemap-listed and redirecting.
        var correlation = new SitemapCorrelationAnalyzer();
        var fact = new SitemapUrlCrawlFact(
            _canonicalizer.Canonicalize(originalUrl), 101, true,
            outcome.StatusClass, outcome.FinalStatusCode, RobotsState.Allowed, IndexabilityFlags.None, null, chain);
        IReadOnlyList<Contracts.Entities.FindingDto> correlated = correlation.Analyze(AuditId, new[] { fact.CanonicalUrl }, new[] { fact });
        Assert.Equal(SitemapRuleKeys.Redirecting, Assert.Single(correlated).RuleKey);
    }

    [Fact]
    public async Task Live_RedirectLoop_DetectedPersistedAndReported()
    {
        await _server.Server.ResetAsync();
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
        using var fetcher = CreateFetcher(recorder);
        var originalUrl = _server.Server.BaseUrl + "/redirect/loop-a";

        FetchContextScope.Current = new FetchContext(AuditId, 102);
        RedirectChainException violation;
        try
        {
            violation = await Assert.ThrowsAsync<RedirectChainException>(
                () => fetcher.FetchAsync(new HttpFetchRequest(originalUrl)));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        Assert.Equal(RedirectChainViolation.Loop, violation.Reason);

        // The observed prefix is durable; the crawl did not hang.
        IReadOnlyList<RedirectHop> partial = await recorder.GetChainAsync(AuditId, 102);
        Assert.Single(partial);
        Assert.Equal(_server.Server.BaseUrl + "/redirect/loop-b", partial[0].ToUrl);
        RedirectChainPayload? payload = await store.GetPayloadAsync(102);
        Assert.Single(payload!.Hops);

        await store.SaveOutcomeAsync(102, RedirectChainPersistence.BuildOutcome(partial, null, null, violation.Reason));
        payload = await store.GetPayloadAsync(102);
        Assert.True(payload!.LoopDetected);

        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyViolation(originalUrl, violation.Reason, partial);
        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(301, outcome.SourceStatusCode);

        var analyzer = new RedirectFindingAnalyzer();
        Contracts.Entities.FindingDto finding = analyzer.AnalyzeViolation(AuditId, 102, originalUrl, violation, partial);
        Assert.Equal(RedirectRuleKeys.Loop, finding.RuleKey);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Contains("\"violation\":\"loop\"", finding.EvidenceJson);
    }

    [Fact]
    public async Task Live_BrokenInternalLink_TargetReturns404()
    {
        await _server.Server.ResetAsync();
        using var fetcher = CreateFetcher();

        HttpFetchResult result = await fetcher.FetchAsync(
            new HttpFetchRequest(_server.Server.BaseUrl + "/pages/does-not-exist.html"));

        Assert.Equal(404, result.StatusCode);
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(
            _server.Server.BaseUrl + "/pages/does-not-exist.html", result, Array.Empty<RedirectHop>());
        Assert.Equal(CrawlStatusClass.ClientError, outcome.StatusClass);
        Assert.False(outcome.IsRedirectSource);
    }

    [Fact]
    public async Task Live_BrokenExternalLink_DnsFailureClassified()
    {
        using var fetcher = CreateFetcher();

        FetchFailedException failure = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("https://broken.example.invalid/page", TimeoutSeconds: 10)));

        Assert.Equal(FetchFailureKind.NameResolution, failure.Kind);
    }

    [Fact]
    public async Task Live_CookieGate_WithRecorderAndJarHook_Returns200AfterOneSelfRedirect()
    {
        await _server.Server.ResetAsync();
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
        var jar = new CookieJarFetchHook();
        using var fetcher = new HttpFetcher(
            new HttpFetcherOptions(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1),
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }),
            recorder, fetchHook: jar);
        var gateUrl = _server.Server.BaseUrl + "/cookie-gate/";

        FetchContextScope.Current = new FetchContext(AuditId, 103);
        HttpFetchResult result;
        try
        {
            result = await fetcher.FetchAsync(new HttpFetchRequest(gateUrl));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        // The gate returned 200 after ONE cookie-driven self-redirect, and the
        // self-hop is durable with its Set-Cookie (cf-wp-09-02 composition).
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(gateUrl, result.FinalUrl);
        RedirectChainPayload? payload = await store.GetPayloadAsync(103);
        Assert.Single(payload!.Hops);
        Assert.Equal(302, payload.Hops[0].StatusCode);
        Assert.Equal(gateUrl, payload.Hops[0].Url);
        Assert.Equal(gateUrl, payload.Hops[0].ToUrl);
        Assert.Contains(payload.Hops[0].SetCookies!, c => c.Contains("fixture_gate=1"));
    }

    [Fact]
    public async Task Live_FixtureRecorder_NoNonGetRequests()
    {
        using JsonDocument requests = await _server.Server.RequestsAsync();
        Assert.Empty(requests.RootElement.GetProperty("nonGet").EnumerateArray());
    }
}
