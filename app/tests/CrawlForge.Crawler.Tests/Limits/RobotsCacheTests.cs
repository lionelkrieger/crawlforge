using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Per-host robots caching, TTL expiry, virtual robots precedence, and
/// crawl-delay exposure (FS-CRAWL-001 §4.4; FS-CRAWL-002 §3.2).
/// </summary>
public sealed class RobotsCacheTests
{
    private readonly FakeHttpFetcher _fetcher = new();
    private readonly RobotsTxtParser _parser = new();
    private readonly ManualTimeProvider _time = new();

    private RobotsCache CreateCache(VirtualRobotsStore? virtualRobots = null) =>
        new(_fetcher, _parser, virtualRobots, new RobotsCacheOptions(), _time);

    [Fact]
    public async Task FetchesOncePerTtl()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /no");
        var cache = CreateCache();

        var first = await cache.GetDocumentAsync("example.com");
        var second = await cache.GetDocumentAsync("example.com");

        Assert.Single(_fetcher.RequestedUrls);
        Assert.Equal(first, second);
        Assert.False(_parser.Evaluate(first, "https://example.com/no", "CrawlForge").Allowed);
    }

    [Fact]
    public async Task TtlExpiryTriggersRefetch()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /no");
        var cache = CreateCache();

        await cache.GetDocumentAsync("example.com");
        _time.Advance(TimeSpan.FromHours(25));
        await cache.GetDocumentAsync("example.com");

        Assert.Equal(2, _fetcher.RequestedUrls.Count);
    }

    [Fact]
    public async Task VirtualOverrideTakesPrecedenceWithoutFetching()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /");
        var virtualRobots = new VirtualRobotsStore();
        virtualRobots.Set("example.com", "User-agent: *\nDisallow: /virtual-only");
        var cache = CreateCache(virtualRobots);

        var document = await cache.GetDocumentAsync("example.com");

        Assert.Empty(_fetcher.RequestedUrls);
        Assert.True(_parser.Evaluate(document, "https://example.com/anything", "CrawlForge").Allowed);
        Assert.False(_parser.Evaluate(document, "https://example.com/virtual-only", "CrawlForge").Allowed);
    }

    [Fact]
    public async Task VirtualOverrideCrawlDelayIsHonored()
    {
        var virtualRobots = new VirtualRobotsStore();
        virtualRobots.Set("example.com", "User-agent: *\nCrawl-delay: 7");
        var cache = CreateCache(virtualRobots);

        await cache.GetDocumentAsync("example.com");

        Assert.Equal(7, cache.GetCrawlDelaySeconds("example.com"));
    }

    [Fact]
    public async Task ClearingVirtualOverrideRestoresFetchedDocument()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /fetched");
        var virtualRobots = new VirtualRobotsStore();
        virtualRobots.Set("example.com", "User-agent: *\nDisallow: /virtual");
        var cache = CreateCache(virtualRobots);

        await cache.GetDocumentAsync("example.com");
        virtualRobots.Clear("example.com");
        var document = await cache.GetDocumentAsync("example.com");

        Assert.Single(_fetcher.RequestedUrls);
        Assert.False(_parser.Evaluate(document, "https://example.com/fetched", "CrawlForge").Allowed);
        Assert.True(_parser.Evaluate(document, "https://example.com/virtual", "CrawlForge").Allowed);
    }

    [Fact]
    public async Task NotFoundMeansNoRestrictions()
    {
        var cache = CreateCache();

        var document = await cache.GetDocumentAsync("missing.example");

        Assert.Empty(document.Groups);
        Assert.True(_parser.Evaluate(document, "https://missing.example/page", "CrawlForge").Allowed);
    }

    [Fact]
    public async Task TransientFailureCachesPermissiveFallbackForNegativeTtl()
    {
        _fetcher.ThrowOnFetch = new HttpRequestException("connection refused");
        var cache = CreateCache();

        var document = await cache.GetDocumentAsync("down.example");
        Assert.True(_parser.Evaluate(document, "https://down.example/page", "CrawlForge").Allowed);
        Assert.Single(_fetcher.RequestedUrls);

        _time.Advance(TimeSpan.FromMinutes(1));
        await cache.GetDocumentAsync("down.example");
        Assert.Single(_fetcher.RequestedUrls);

        _time.Advance(TimeSpan.FromMinutes(5));
        _fetcher.ThrowOnFetch = null;
        _fetcher.SetResponse("https://down.example/robots.txt", 200, "User-agent: *\nDisallow: /back");
        var recovered = await cache.GetDocumentAsync("down.example");

        Assert.Equal(2, _fetcher.RequestedUrls.Count);
        Assert.False(_parser.Evaluate(recovered, "https://down.example/back", "CrawlForge").Allowed);
    }

    [Fact]
    public async Task CrawlDelayIsExposedFromCachedDocument()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nCrawl-delay: 3.5");
        var cache = CreateCache();

        Assert.Null(cache.GetCrawlDelaySeconds("example.com"));
        await cache.GetDocumentAsync("example.com");

        Assert.Equal(3.5, cache.GetCrawlDelaySeconds("example.com"));
    }

    [Fact]
    public async Task InvalidateForcesRefetch()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /no");
        var cache = CreateCache();

        await cache.GetDocumentAsync("example.com");
        cache.Invalidate("example.com");
        await cache.GetDocumentAsync("example.com");

        Assert.Equal(2, _fetcher.RequestedUrls.Count);
    }

    [Fact]
    public async Task HostNormalizationIsCaseInsensitive()
    {
        _fetcher.SetResponse("https://example.com/robots.txt", 200, "User-agent: *\nDisallow: /no");
        var cache = CreateCache();

        await cache.GetDocumentAsync("EXAMPLE.COM");
        await cache.GetDocumentAsync("example.com");

        Assert.Single(_fetcher.RequestedUrls);
    }
}
