using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Tests.Sitemaps.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Sitemaps;

/// <summary>
/// Sitemap ingestion (CAP-A-008, FS-CRAWL-001 §5.5): URL seeding with
/// discovery source sitemap, recursive index expansion with cycle/depth/count
/// guards, canonicalized deduplication, tolerant diagnostics.
/// </summary>
public sealed class SitemapIngestorTests
{
    private static SitemapIngestionRequest Request(params string[] seeds) =>
        new(AuditId: 7, SeedUrls: seeds.Length > 0 ? seeds : new[] { SitemapTestSupport.Base + "/" });

    [Fact]
    public async Task Ingest_UrlSet_SeedsCanonicalUrlsWithSitemapSource()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200,
                SitemapTestSupport.UrlSetWithMetadata(SitemapTestSupport.Base + "/pages/orphan.html", "2026-01-02", "weekly", "0.6"),
                "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        SitemapSeededUrl seeded = Assert.Single(result.Urls);
        Assert.Equal(SitemapTestSupport.Base + "/pages/orphan.html", seeded.CanonicalUrl);
        Assert.Equal("site.example", seeded.Host);
        Assert.Equal(SitemapTestSupport.Base + "/sitemap.xml", seeded.SourceSitemapUrl);
        Assert.Equal("2026-01-02", seeded.LastModified);
        Assert.Equal("weekly", seeded.ChangeFrequency);
        Assert.Equal(0.6, seeded.Priority);

        FrontierEntry entry = Assert.Single(result.CreateFrontierEntries());
        Assert.Equal(DiscoverySource.Sitemap, entry.DiscoverySource);
        Assert.Equal(0, entry.Depth);
        Assert.Equal(seeded.CanonicalUrl, entry.Url);
        Assert.Single(result.Sitemaps);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Ingest_NestedIndexes_ExpandedRecursively()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/index.xml", 200, SitemapTestSupport.Index(SitemapTestSupport.Base + "/child-index.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/child-index.xml", 200, SitemapTestSupport.Index(SitemapTestSupport.Base + "/leaf.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/leaf.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/deep-page"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(7, new[] { SitemapTestSupport.Base + "/" }, ConfiguredSitemapSeeds: new[] { SitemapTestSupport.Base + "/index.xml" }));

        SitemapSeededUrl seeded = Assert.Single(result.Urls);
        Assert.Equal(SitemapTestSupport.Base + "/deep-page", seeded.CanonicalUrl);
        Assert.Equal(SitemapTestSupport.Base + "/leaf.xml", seeded.SourceSitemapUrl);
        Assert.Equal(3, result.Sitemaps.Count);
        Assert.Equal(SitemapSourceKind.IndexRecursion, result.Sitemaps[1].Source);
        Assert.Equal(SitemapSourceKind.IndexRecursion, result.Sitemaps[2].Source);
    }

    [Fact]
    public async Task Ingest_IndexCycle_FetchedOnce()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/loop.xml", 200,
                SitemapTestSupport.Index(SitemapTestSupport.Base + "/loop.xml", SitemapTestSupport.Base + "/leaf.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/leaf.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/page"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(7, new[] { SitemapTestSupport.Base + "/" }, ConfiguredSitemapSeeds: new[] { SitemapTestSupport.Base + "/loop.xml" }));

        Assert.Single(result.Urls);
        Assert.Equal(2, result.Sitemaps.Count); // loop.xml + leaf.xml, no repeat
        Assert.Equal(4, handler.Requests.Count); // robots, probe, loop.xml, leaf.xml
    }

    [Fact]
    public async Task Ingest_DepthBound_StopsRecursionWithDiagnostic()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/index0.xml", 200, SitemapTestSupport.Index(SitemapTestSupport.Base + "/index1.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/index1.xml", 200, SitemapTestSupport.Index(SitemapTestSupport.Base + "/index2.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/index2.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/too-deep"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler, new SitemapIngestionOptions(MaxIndexDepth: 1));

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(7, new[] { SitemapTestSupport.Base + "/" }, ConfiguredSitemapSeeds: new[] { SitemapTestSupport.Base + "/index0.xml" }));

        Assert.Empty(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("depth bound"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsoluteUri.EndsWith("/index2.xml"));
    }

    [Fact]
    public async Task Ingest_DocumentBound_StopsWithDiagnostic()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/index.xml", 200,
                SitemapTestSupport.Index(SitemapTestSupport.Base + "/a.xml", SitemapTestSupport.Base + "/b.xml", SitemapTestSupport.Base + "/c.xml"), "application/xml")
            .Route(SitemapTestSupport.Base + "/a.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/a"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler, new SitemapIngestionOptions(MaxSitemaps: 2));

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(7, new[] { SitemapTestSupport.Base + "/" }, ConfiguredSitemapSeeds: new[] { SitemapTestSupport.Base + "/index.xml" }));

        Assert.Single(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("2-document bound"));
    }

    [Fact]
    public async Task Ingest_DuplicateUrlsAcrossSitemaps_FirstSourceWins()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200,
                SitemapTestSupport.Robots(SitemapTestSupport.Base + "/one.xml") + $"Sitemap: {SitemapTestSupport.Base}/two.xml\n", "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/one.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/shared"), "application/xml")
            .Route(SitemapTestSupport.Base + "/two.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/shared", SitemapTestSupport.Base + "/only-two"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Equal(2, result.Urls.Count);
        Assert.Equal(SitemapTestSupport.Base + "/one.xml", result.Urls[0].SourceSitemapUrl);
        Assert.Equal(SitemapTestSupport.Base + "/shared", result.Urls[0].CanonicalUrl);
        Assert.Equal(SitemapTestSupport.Base + "/two.xml", result.Urls[1].SourceSitemapUrl);
    }

    [Fact]
    public async Task Ingest_Non200Sitemap_DiagnosticWithoutFailure()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/gone.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/gone.xml", 500, "server error");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Empty(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("HTTP 500"));
    }

    [Fact]
    public async Task Ingest_MalformedSitemap_DiagnosticWithoutFailure()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/broken.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .Route(SitemapTestSupport.Base + "/broken.xml", 200, "<urlset><url>", "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Empty(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("malformed"));
    }

    [Fact]
    public async Task Ingest_SitemapFetchFailure_DiagnosticWithoutFailure()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/dead.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 404, "missing")
            .RouteException(SitemapTestSupport.Base + "/dead.xml", new HttpRequestException(HttpRequestError.NameResolutionError));
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Empty(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("failed to fetch"));
    }

    [Fact]
    public async Task Ingest_TotalUrlBound_StopsCollectingWithDiagnostic()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200,
                SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/a", SitemapTestSupport.Base + "/b"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler, new SitemapIngestionOptions(MaxTotalUrls: 1));

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Single(result.Urls);
        Assert.Contains(result.Diagnostics, d => d.Contains("1-URL bound"));
    }

    [Fact]
    public async Task Ingest_CachedRobotsByHost_NotRefetched()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200, SitemapTestSupport.UrlSet(SitemapTestSupport.Base + "/page"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);
        var robots = new RobotsDocument(Array.Empty<RobotsGroup>(), new[] { SitemapTestSupport.Base + "/sitemap.xml" });

        SitemapIngestionResult result = await ingestor.IngestAsync(
            new SitemapIngestionRequest(7, new[] { SitemapTestSupport.Base + "/" },
                RobotsByHost: new Dictionary<string, RobotsDocument> { ["site.example"] = robots }));

        Assert.Single(result.Urls);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/robots.txt");
    }

    [Fact]
    public async Task Ingest_NonHttpAndRelativeLocs_HandledTolerantly()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200,
                "<urlset>" +
                $"<url><loc>ftp://site.example/file</loc></url>" +
                $"<url><loc>/relative-page</loc></url>" +
                "</urlset>", "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        SitemapSeededUrl seeded = Assert.Single(result.Urls);
        Assert.Equal(SitemapTestSupport.Base + "/relative-page", seeded.CanonicalUrl);
        Assert.Contains(result.Diagnostics, d => d.Contains("ftp://"));
    }

    [Fact]
    public async Task Ingest_CanonicalizationDedup_SessionVariantsCollapse()
    {
        var handler = new RoutingHttpMessageHandler()
            .Route(SitemapTestSupport.Base + "/robots.txt", 200, SitemapTestSupport.Robots(SitemapTestSupport.Base + "/sitemap.xml"), "text/plain")
            .Route(SitemapTestSupport.Base + "/sitemap.xml", 200,
                SitemapTestSupport.UrlSet(
                    SitemapTestSupport.Base + "/page?utm_source=newsletter",
                    SitemapTestSupport.Base + "/page"), "application/xml");
        var ingestor = SitemapTestSupport.CreateIngestor(handler);

        SitemapIngestionResult result = await ingestor.IngestAsync(Request());

        Assert.Single(result.Urls);
        Assert.Equal(SitemapTestSupport.Base + "/page", result.Urls[0].CanonicalUrl);
    }
}
