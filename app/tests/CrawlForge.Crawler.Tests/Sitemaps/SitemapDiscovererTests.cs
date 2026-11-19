using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Tests.Sitemaps.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Sitemaps;

/// <summary>
/// Sitemap discovery (CAP-A-008): robots.txt Sitemap directives + /sitemap.xml
/// default probe + configured seeds, deduplication, tolerant failure handling.
/// </summary>
public sealed class SitemapDiscovererTests
{
    [Fact]
    public async Task Discover_RobotsDirectiveAndProbe_BothDiscoveredInOrder()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap-main.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200, SitemapTestSupport.UrlSet(), "application/xml");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/");

        Assert.Equal(2, result.Sitemaps.Count);
        Assert.Equal(SitemapTestSupport.Base + "/sitemap-main.xml", result.Sitemaps[0].Url);
        Assert.Equal(SitemapSourceKind.RobotsDirective, result.Sitemaps[0].Source);
        Assert.Equal(SitemapTestSupport.Base + "/sitemap.xml", result.Sitemaps[1].Url);
        Assert.Equal(SitemapSourceKind.DefaultProbe, result.Sitemaps[1].Source);
        Assert.NotNull(result.Robots);
    }

    [Fact]
    public async Task Discover_ProbeMatchesDirective_DeduplicatedKeepingDirectiveSource()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200, SitemapTestSupport.UrlSet(), "application/xml");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/");

        Assert.Single(result.Sitemaps);
        Assert.Equal(SitemapSourceKind.RobotsDirective, result.Sitemaps[0].Source);
    }

    [Fact]
    public async Task Discover_RobotsUnreachable_ProbeStillRuns()
    {
        var handler = new RoutingHttpMessageHandler()
            .RouteException(SitemapTestSupport.Base + "/robots.txt", new HttpRequestException(HttpRequestError.NameResolutionError))
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200, SitemapTestSupport.UrlSet(), "application/xml");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/");

        Assert.Single(result.Sitemaps);
        Assert.Equal(SitemapSourceKind.DefaultProbe, result.Sitemaps[0].Source);
        Assert.Contains(result.Diagnostics, d => d.Contains("unreachable"));
        Assert.Null(result.Robots);
    }

    [Fact]
    public async Task Discover_Probe404_DiagnosticAndDirectiveKept()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/maps.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "not found");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/");

        Assert.Single(result.Sitemaps);
        Assert.Equal(SitemapTestSupport.Base + "/maps.xml", result.Sitemaps[0].Url);
        Assert.Contains(result.Diagnostics, d => d.Contains("404"));
    }

    [Fact]
    public async Task Discover_ConfiguredSeeds_AddedAndNonHttpRejected()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "not found");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(
            SitemapTestSupport.Base + "/",
            configuredSitemapSeeds: new[] { SitemapTestSupport.Base + "/custom.xml", "ftp://site.example/bad.xml" });

        Assert.Single(result.Sitemaps);
        Assert.Equal(SitemapTestSupport.Base + "/custom.xml", result.Sitemaps[0].Url);
        Assert.Equal(SitemapSourceKind.ConfiguredSeed, result.Sitemaps[0].Source);
        Assert.Contains(result.Diagnostics, d => d.Contains("ftp://"));
    }

    [Fact]
    public async Task Discover_RelativeDirective_ResolvedAgainstRobotsUrl()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, "User-agent: *\nSitemap: /maps/main.xml\n", "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "not found");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/");

        Assert.Single(result.Sitemaps);
        Assert.Equal(SitemapTestSupport.Base + "/maps/main.xml", result.Sitemaps[0].Url);
    }

    [Fact]
    public async Task Discover_SuppliedRobots_DoesNotRefetchRobots()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200, SitemapTestSupport.UrlSet(), "application/xml");
        var discoverer = SitemapTestSupport.CreateDiscoverer(handler);
        var robots = new RobotsDocument(Array.Empty<RobotsGroup>(), new[] { SitemapTestSupport.Base + "/supplied.xml" });

        SitemapDiscoveryResult result = await discoverer.DiscoverAsync(SitemapTestSupport.Base + "/", robots);

        Assert.Equal(2, result.Sitemaps.Count);
        Assert.Equal(SitemapTestSupport.Base + "/supplied.xml", result.Sitemaps[0].Url);
        Assert.Single(handler.Requests); // probe only
        Assert.Same(robots, result.Robots);
    }

    [Fact]
    public async Task Discover_NonHttpSeed_Throws()
    {
        var discoverer = SitemapTestSupport.CreateDiscoverer(new RoutingHttpMessageHandler());

        await Assert.ThrowsAsync<ArgumentException>(() => discoverer.DiscoverAsync("ftp://site.example/"));
    }
}
