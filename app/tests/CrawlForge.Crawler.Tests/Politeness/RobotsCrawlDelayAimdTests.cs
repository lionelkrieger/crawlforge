using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Tests.Limits;
using Xunit;

namespace CrawlForge.Crawler.Tests.Politeness;

/// <summary>
/// G07 crawl-delay source path: the RobotsCache (which implements
/// ICrawlDelaySource) feeds the AIMD controller, so a robots crawl-delay
/// caps the per-host rate and spacing — the wiring the engine must adopt
/// (ctor crawlDelaySource argument) is proven here at component level.
/// </summary>
public class RobotsCrawlDelayAimdTests
{
    private const string Host = "example.com";

    private static RobotsCache CreateCache(string robotsBody, ManualTimeProvider time)
    {
        var fetcher = new FakeHttpFetcher();
        fetcher.SetResponse($"http://{Host}/robots.txt", 200, robotsBody);
        return new RobotsCache(
            fetcher,
            new RobotsTxtParser(),
            options: new RobotsCacheOptions { Scheme = "http", CrawlerUserAgent = "CrawlForge" },
            timeProvider: time);
    }

    /// <summary>The cache surfaces the crawl-delay of the selecting group.</summary>
    [Fact]
    public async Task CacheSurfacesCrawlDelay()
    {
        var time = new ManualTimeProvider();
        var cache = CreateCache("User-agent: *\nCrawl-delay: 10\nDisallow: /private/\n", time);
        Assert.Null(cache.GetCrawlDelaySeconds(Host)); // not fetched yet

        await cache.GetDocumentAsync(Host);
        Assert.Equal(10, cache.GetCrawlDelaySeconds(Host));
    }

    /// <summary>The crawl-delay caps the initial rate even with a high configured rate.</summary>
    [Fact]
    public async Task CrawlDelayCapsInitialRate()
    {
        var time = new ManualTimeProvider();
        var cache = CreateCache("User-agent: *\nCrawl-delay: 10\n", time);
        await cache.GetDocumentAsync(Host);

        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 5.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            crawlDelay: cache,
            time: time);
        var state = controller.GetState(Host);
        Assert.True(state.CurrentRate <= 0.1 + 1e-9, $"rate {state.CurrentRate} must respect the 1/10s crawl-delay cap");
    }

    /// <summary>WaitAsync spacing honors the crawl-delay floor deterministically.</summary>
    [Fact]
    public async Task WaitAsyncSpacingHonorsCrawlDelay()
    {
        var time = new ManualTimeProvider();
        var cache = CreateCache("User-agent: *\nCrawl-delay: 10\n", time);
        await cache.GetDocumentAsync(Host);

        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 5.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            crawlDelay: cache,
            time: time);

        await controller.WaitAsync(Host); // first request: immediate
        var second = controller.WaitAsync(Host).AsTask();
        Assert.False(second.IsCompleted); // must wait the 10s crawl-delay interval

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Without a crawl-delay the same controller does not impose the floor.</summary>
    [Fact]
    public async Task NoCrawlDelayMeansNoFloor()
    {
        var time = new ManualTimeProvider();
        var cache = CreateCache("User-agent: *\nDisallow: /private/\n", time);
        await cache.GetDocumentAsync(Host);
        Assert.Null(cache.GetCrawlDelaySeconds(Host));

        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 5.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            crawlDelay: cache,
            time: time);
        await controller.WaitAsync(Host);
        var second = controller.WaitAsync(Host).AsTask();
        time.Advance(TimeSpan.FromSeconds(0.2)); // 1/5s interval
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The robots-blocked verdict path: disallowed URLs are reported, never fetched.</summary>
    [Fact]
    public async Task DisallowedUrlsAreReportedNotFetched()
    {
        var time = new ManualTimeProvider();
        var fetcher = new FakeHttpFetcher();
        fetcher.SetResponse($"http://{Host}/robots.txt", 200, "User-agent: *\nDisallow: /private/\n");
        var cache = new RobotsCache(
            fetcher,
            new RobotsTxtParser(),
            options: new RobotsCacheOptions { Scheme = "http", CrawlerUserAgent = "CrawlForge" },
            timeProvider: time);

        var document = await cache.GetDocumentAsync(Host);
        var parser = new RobotsTxtParser();
        var verdict = parser.Evaluate(document, $"http://{Host}/private/blocked.html", "CrawlForge");
        Assert.False(verdict.Allowed);

        var finding = RobotsBlockFindingFactory.Create(
            3, $"http://{Host}/private/blocked.html", verdict, document, "CrawlForge", $"http://{Host}/robots.txt");
        Assert.Equal("indexability.robots-blocked", finding.RuleKey);
        // Only robots.txt was fetched; the blocked URL never was.
        Assert.Equal([$"http://{Host}/robots.txt"], fetcher.RequestedUrls);
    }
}
