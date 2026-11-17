using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Sample-crawl mode (FS-CRAWL-001 §5.1, criterion #34): depth/page-bounded
/// preview through the SAME pipeline, with bounds flowing through the standard
/// limits resolution as the per-audit layer — no special resolution path.
/// </summary>
public sealed class SampleCrawlTests
{
    [Fact]
    public void ToAuditOverrides_MapsSampleBoundsOnly()
    {
        var settings = new SampleCrawlSettings(MaxPages: 25, MaxDepth: 2);

        CrawlLimitOverrides overrides = SampleCrawlLimits.ToAuditOverrides(settings);

        Assert.Equal(25, overrides.MaxUrls);
        Assert.Equal(2, overrides.MaxDepth);
        Assert.Null(overrides.MaxDurationSeconds);
        Assert.Null(overrides.MaxConcurrency);
        Assert.Null(overrides.RequestsPerSecond);
        Assert.Null(overrides.FetchTimeoutSeconds);
    }

    [Fact]
    public void SampleBounds_FlowThroughStandardResolver_AsAuditLayer()
    {
        var resolver = new RecordingLimitsResolver();
        var project = new CrawlLimitOverrides(MaxUrls: 5000, MaxDepth: 10, MaxConcurrency: 8);
        var application = new CrawlLimitOverrides(MaxUrls: 1000, RequestsPerSecond: 4.0);
        var settings = new SampleCrawlSettings(MaxPages: 20, MaxDepth: 1);

        ResolvedCrawlLimits resolved = resolver.Resolve(
            SampleCrawlLimits.ToAuditOverrides(settings), project, application);

        // The resolver saw exactly the standard three layers, untouched.
        (CrawlLimitOverrides? audit, CrawlLimitOverrides? projectSeen, CrawlLimitOverrides? appSeen) = Assert.Single(resolver.Calls);
        Assert.Equal(20, audit!.MaxUrls);
        Assert.Equal(1, audit.MaxDepth);
        Assert.Same(project, projectSeen);
        Assert.Same(application, appSeen);

        // Priority order holds: sample (audit) bounds win; other fields inherit.
        Assert.Equal(20, resolved.MaxUrls);
        Assert.Equal(1, resolved.MaxDepth);
        Assert.Equal(8, resolved.MaxConcurrency);
        Assert.Equal(4.0, resolved.RequestsPerSecond);
    }

    [Fact]
    public void SampleBounds_WithoutSample_ResolutionUnchanged()
    {
        var resolver = new RecordingLimitsResolver();
        var project = new CrawlLimitOverrides(MaxUrls: 5000, MaxDepth: 10);

        ResolvedCrawlLimits baseline = resolver.Resolve(null, project, null);
        ResolvedCrawlLimits withSample = resolver.Resolve(SampleCrawlLimits.ToAuditOverrides(new SampleCrawlSettings(10, 1)), project, null);

        Assert.Equal(5000, baseline.MaxUrls);
        Assert.Equal(10, baseline.MaxDepth);
        Assert.Equal(10, withSample.MaxUrls);
        Assert.Equal(1, withSample.MaxDepth);
    }

    [Fact]
    public async Task Runner_PageBound_StopsAtLimit()
    {
        var handler = new ScriptedHttpMessageHandler();
        for (int i = 0; i < 5; i++)
        {
            handler.Enqueue(ScriptedHttpMessageHandler.Response(200, "page"));
        }

        SampleCrawlRunner runner = CreateRunner(handler);
        var limits = new ResolvedCrawlLimits(3, null, null, 4, 2.0, 30);
        string[] seeds = Enumerable.Range(0, 5).Select(i => $"http://target.example.com/{i}").ToArray();

        SampleCrawlSummary summary = await runner.RunAsync(seeds, limits);

        Assert.Equal(3, summary.Pages.Count);
        Assert.True(summary.PageBoundReached);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(summary.Pages, p => Assert.Equal(CrawlStatusClass.Success, p.StatusClass));
    }

    [Fact]
    public async Task Runner_DepthBound_StopsDiscoveryBeyondCeiling()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "seed"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "depth1"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "depth2-never-fetched"));
        SampleCrawlRunner runner = CreateRunner(handler);
        var limits = new ResolvedCrawlLimits(null, 1, null, 4, 2.0, 30);

        SampleCrawlSummary summary = await runner.RunAsync(
            new[] { "http://target.example.com/seed" },
            limits,
            page => page.Depth switch
            {
                0 => new[] { "http://target.example.com/d1" },
                _ => new[] { "http://target.example.com/d2" },
            });

        Assert.Equal(2, summary.Pages.Count);
        Assert.Equal(0, summary.Pages[0].Depth);
        Assert.Equal(1, summary.Pages[1].Depth);
        Assert.False(summary.PageBoundReached);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Runner_FetchFailure_RecordedWithClassification()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.NameResolutionError));
        SampleCrawlRunner runner = CreateRunner(handler, new HttpFetcherOptions(maxRetryAttempts: 0));
        var limits = new ResolvedCrawlLimits(null, null, null, 4, 2.0, 30);

        SampleCrawlSummary summary = await runner.RunAsync(new[] { "http://missing.example.com/" }, limits);

        SampleCrawlPage page = Assert.Single(summary.Pages);
        Assert.Equal(CrawlStatusClass.FetchError, page.StatusClass);
        Assert.Equal(nameof(FetchFailureKind.NameResolution), page.Error);
        Assert.Null(page.StatusCode);
    }

    [Fact]
    public async Task Runner_DeduplicatesSeeds_AndUsesSameFetchPipeline()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "a"))
            .Enqueue(ScriptedHttpMessageHandler.Response(404, "b"));
        SampleCrawlRunner runner = CreateRunner(handler);
        var limits = new ResolvedCrawlLimits(null, null, null, 4, 2.0, 30);

        SampleCrawlSummary summary = await runner.RunAsync(
            new[] { "http://target.example.com/a", "http://target.example.com/a", "http://target.example.com/b" },
            limits);

        Assert.Equal(2, summary.Pages.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(CrawlStatusClass.ClientError, summary.Pages[1].StatusClass);
    }

    [Fact]
    public void Settings_InvalidBounds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleCrawlLimits.ToAuditOverrides(new SampleCrawlSettings(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleCrawlLimits.ToAuditOverrides(new SampleCrawlSettings(5, -1)));
    }

    private static SampleCrawlRunner CreateRunner(ScriptedHttpMessageHandler handler, HttpFetcherOptions? options = null)
    {
        var client = new HttpClient(handler);
        var fetcher = new HttpFetcher(options ?? new HttpFetcherOptions(retryBackoffBaseMilliseconds: 1), client);
        return new SampleCrawlRunner(fetcher);
    }
}