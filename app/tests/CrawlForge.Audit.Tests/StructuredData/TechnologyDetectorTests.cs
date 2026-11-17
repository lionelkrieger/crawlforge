using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Technology detection suite (FS-CRAWL-001 §7.15, criterion #29): bundled
/// catalogue matching across headers, cookies, meta, scripts, and HTML with
/// weighted confidence. Zero runtime fetch — the catalogue is a local asset.
/// </summary>
public class TechnologyDetectorTests
{
    private static TechnologyDetector CreateDetector() =>
        new(TechnologyFingerprintCatalogue.Load(StructuredDataRealStack.CatalogueAssetPath), new TestDomParser());

    private static string Page(string head = "", string body = "") =>
        $"""<html><head>{head}</head><body>{body}</body></html>""";

    [Fact]
    public void CatalogueLoads_WithVersionAndAllCategories()
    {
        var catalogue = TechnologyFingerprintCatalogue.Load(StructuredDataRealStack.CatalogueAssetPath);

        Assert.Equal("2026.1", catalogue.Version);
        Assert.Equal("r1", catalogue.Revision);
        Assert.Equal(50, catalogue.Technologies.Count);
        Assert.Contains(catalogue.Technologies, t => t.Key == "wordpress" && t.Category == "CMS");
        Assert.Contains(catalogue.Technologies, t => t.Key == "google-analytics" && t.Category == "Analytics");
        Assert.Contains(catalogue.Technologies, t => t.Key == "cloudflare" && t.Category == "CDN");
        Assert.Contains(catalogue.Technologies, t => t.Key == "shopify" && t.Category == "Ecommerce");
        Assert.All(catalogue.Technologies, t => Assert.False(string.IsNullOrWhiteSpace(t.Key)));
    }

    [Fact]
    public async Task WordPressGeneratorMeta_IsDefinitive()
    {
        var context = StructuredDataTestContext.Create(
            Page("""<meta name="generator" content="WordPress 6.4.2" />"""));

        var detections = await CreateDetector().DetectAsync(context);

        var wordpress = Assert.Single(detections, d => d.TechnologyKey == "wordpress");
        Assert.Equal("CMS", wordpress.Category);
        Assert.Equal(1.0, wordpress.Confidence);
    }

    [Fact]
    public async Task WordPressCorroboration_CapsConfidenceAtOne()
    {
        var headers = new Dictionary<string, string>
        {
            ["Set-Cookie"] = "wordpress_logged_in_abc=user; Path=/",
        };
        var context = StructuredDataTestContext.Create(
            Page("""<script src="https://example.test/wp-content/themes/x/app.js"></script>"""),
            headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        var wordpress = Assert.Single(detections, d => d.TechnologyKey == "wordpress");
        Assert.Equal(1.0, wordpress.Confidence);
    }

    [Fact]
    public async Task ShopifyHeaders_AreDetected()
    {
        var headers = new Dictionary<string, string>
        {
            ["x-shopid"] = "1234567",
            ["x-shardid"] = "42",
        };
        var context = StructuredDataTestContext.Create(Page(), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        var shopify = Assert.Single(detections, d => d.TechnologyKey == "shopify");
        Assert.Equal(1.0, shopify.Confidence);
    }

    [Fact]
    public async Task CloudflareHeadersAndCookies_AreDetected()
    {
        var headers = new Dictionary<string, string>
        {
            ["cf-ray"] = "8abc123-AMS",
            ["Set-Cookie"] = "__cf_bm=token; Path=/; HttpOnly",
        };
        var context = StructuredDataTestContext.Create(Page(), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "cloudflare" && d.Confidence == 1.0);
    }

    [Fact]
    public async Task NextJsScriptAndDataMarker_AreDetected()
    {
        var context = StructuredDataTestContext.Create(
            Page("""<script src="/_next/static/chunks/main.js"></script>""", """<script id="__NEXT_DATA__" type="application/json">{}</script>"""));

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "nextjs" && d.Confidence == 1.0);
    }

    [Fact]
    public async Task ReactScript_ProducesPartialConfidence()
    {
        var context = StructuredDataTestContext.Create(
            Page("""<script src="https://cdn.example.test/react.production.min.js"></script>"""));

        var detections = await CreateDetector().DetectAsync(context);

        var react = Assert.Single(detections, d => d.TechnologyKey == "react");
        Assert.Equal(0.7, react.Confidence);
    }

    [Fact]
    public async Task GoogleAnalyticsGtag_IsDetected()
    {
        var context = StructuredDataTestContext.Create(
            Page("""<script async src="https://www.googletagmanager.com/gtag/js?id=G-ABC123"></script>"""));

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "google-analytics" && d.Confidence >= 0.9);
    }

    [Fact]
    public async Task TagManagerScript_IsDefinitive()
    {
        var context = StructuredDataTestContext.Create(
            Page("""<script src="https://www.googletagmanager.com/gtm.js?id=GTM-ABC123"></script>"""));

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "google-tag-manager" && d.Confidence == 1.0);
    }

    [Fact]
    public async Task PhpSessionCookie_IsDetected()
    {
        var headers = new Dictionary<string, string> { ["Set-Cookie"] = "PHPSESSID=abc123; path=/" };
        var context = StructuredDataTestContext.Create(Page(), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "php" && d.Confidence == 0.7);
    }

    [Fact]
    public async Task PlainPage_ProducesNoDetections()
    {
        var context = StructuredDataTestContext.Create(Page("", "<p>hello world</p>"));

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Empty(detections);
    }

    [Fact]
    public async Task Detections_AreOrderedByConfidenceThenKey()
    {
        var headers = new Dictionary<string, string>
        {
            ["server"] = "nginx",
            ["x-powered-by"] = "PHP/8.3",
        };
        var context = StructuredDataTestContext.Create(
            Page("""<meta name="generator" content="WordPress 6.4" />"""), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Equal("wordpress", detections[0].TechnologyKey);
        Assert.True(detections.SequenceEqual(detections.OrderByDescending(d => d.Confidence).ThenBy(d => d.TechnologyKey, StringComparer.Ordinal)));
    }

    [Fact]
    public async Task CookiePrefix_RequiresNameBoundary()
    {
        var headers = new Dictionary<string, string>
        {
            ["Set-Cookie"] = "notwordpress_x=1; Path=/",
        };
        var context = StructuredDataTestContext.Create(Page(), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.DoesNotContain(detections, d => d.TechnologyKey == "wordpress");
    }

    [Fact]
    public async Task HeaderMatching_IsCaseInsensitive()
    {
        var headers = new Dictionary<string, string> { ["Server"] = "NGINX/1.25" };
        var context = StructuredDataTestContext.Create(Page(), headers: headers);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "nginx");
    }

    [Fact]
    public async Task DomOnlyContext_WithoutFetch_StillDetects()
    {
        var parser = new TestDomParser();
        var html = Page("""<meta name="generator" content="Drupal 10" />""");
        var url = new Contracts.Entities.UrlRecordDto(
            1, 1, "https://example.test/page", "example.test", "/page", 0,
            Contracts.Enums.DiscoverySource.Seed, Contracts.Enums.CrawlStatusClass.Success, 200, null,
            "text/html", html.Length, null, Contracts.Enums.RobotsState.Allowed, Contracts.Enums.IndexabilityFlags.None, true);
        var configuration = new Contracts.Entities.CrawlConfigurationDto(
            1, 1, "test", "[]", null, null, null, Contracts.Enums.RenderMode.None,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var context = new Contracts.Audit.PageAuditContext(
            1, url, configuration, null, parser.Parse(html, url.CanonicalUrl), null, null, null);

        var detections = await CreateDetector().DetectAsync(context);

        Assert.Contains(detections, d => d.TechnologyKey == "drupal" && d.Confidence == 1.0);
    }

    [Fact]
    public void CatalogueRejectsDuplicateKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            { "version": "t", "technologies": [
              { "key": "dup", "category": "CMS" }, { "key": "dup", "category": "CMS" } ] }
            """);
        try
        {
            Assert.Throws<InvalidDataException>(() => TechnologyFingerprintCatalogue.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CatalogueRejectsInvalidWeights()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            { "version": "t", "technologies": [
              { "key": "bad", "category": "CMS", "html": [ { "pattern": "x", "weight": 1.5 } ] } ] }
            """);
        try
        {
            Assert.Throws<InvalidDataException>(() => TechnologyFingerprintCatalogue.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingAsset_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            TechnologyFingerprintCatalogue.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
    }
}
