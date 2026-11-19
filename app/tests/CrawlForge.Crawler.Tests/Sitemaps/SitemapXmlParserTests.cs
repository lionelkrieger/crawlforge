using CrawlForge.Crawler.Sitemaps;
using Xunit;

namespace CrawlForge.Crawler.Tests.Sitemaps;

/// <summary>
/// Tolerant XML sitemap parsing (FS-CRAWL-001 §5.4/§5.5): urlset entries with
/// metadata, sitemapindex children, namespace tolerance, hostile-input
/// tolerance, and per-document bounds.
/// </summary>
public sealed class SitemapXmlParserTests
{
    private const string Namespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    [Fact]
    public void Parse_UrlSetWithNamespace_ExtractsEntriesAndMetadata()
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="{Namespace}">
              <url><loc>http://site.example/</loc><lastmod>2026-01-01</lastmod><changefreq>daily</changefreq><priority>0.8</priority></url>
              <url><loc>http://site.example/orphan.html</loc></url>
            </urlset>
            """;

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.NotNull(result.Document);
        Assert.Equal(SitemapDocumentKind.UrlSet, result.Document!.Kind);
        Assert.Equal(2, result.Document.Urls.Count);
        Assert.Equal("http://site.example/", result.Document.Urls[0].Url);
        Assert.Equal("2026-01-01", result.Document.Urls[0].LastModified);
        Assert.Equal("daily", result.Document.Urls[0].ChangeFrequency);
        Assert.Equal(0.8, result.Document.Urls[0].Priority);
        Assert.Null(result.Document.Urls[1].LastModified);
        Assert.Null(result.Document.Urls[1].Priority);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parse_UrlSetWithoutNamespace_ExtractsEntries()
    {
        var xml = "<urlset><url><loc>http://site.example/a</loc></url></urlset>";

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.NotNull(result.Document);
        Assert.Equal(SitemapDocumentKind.UrlSet, result.Document!.Kind);
        Assert.Single(result.Document.Urls);
        Assert.Equal("http://site.example/a", result.Document.Urls[0].Url);
    }

    [Fact]
    public void Parse_SitemapIndex_ExtractsChildLocations()
    {
        var xml = $"""
            <sitemapindex xmlns="{Namespace}">
              <sitemap><loc>http://site.example/sitemap-a.xml</loc><lastmod>2026-01-01</lastmod></sitemap>
              <sitemap><loc>http://site.example/sitemap-b.xml</loc></sitemap>
            </sitemapindex>
            """;

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.NotNull(result.Document);
        Assert.Equal(SitemapDocumentKind.SitemapIndex, result.Document!.Kind);
        Assert.Empty(result.Document.Urls);
        Assert.Equal(new[] { "http://site.example/sitemap-a.xml", "http://site.example/sitemap-b.xml" }, result.Document.ChildSitemapUrls);
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsDiagnosticWithoutThrowing()
    {
        SitemapParseResult result = SitemapXmlParser.Parse("<urlset><url><loc>http://site.example/</loc>");

        Assert.Null(result.Document);
        Assert.Contains(result.Diagnostics, d => d.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsDiagnostic()
    {
        SitemapParseResult result = SitemapXmlParser.Parse("   ");

        Assert.Null(result.Document);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void Parse_UnknownRoot_ReturnsDiagnostic()
    {
        SitemapParseResult result = SitemapXmlParser.Parse("<html><body>not a sitemap</body></html>");

        Assert.Null(result.Document);
        Assert.Contains(result.Diagnostics, d => d.Contains("html"));
    }

    [Fact]
    public void Parse_UrlWithoutLoc_SkippedWithDiagnostic()
    {
        var xml = "<urlset><url><priority>0.5</priority></url><url><loc>http://site.example/ok</loc></url></urlset>";

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.NotNull(result.Document);
        Assert.Single(result.Document!.Urls);
        Assert.Equal("http://site.example/ok", result.Document.Urls[0].Url);
        Assert.Contains(result.Diagnostics, d => d.Contains("without a <loc>"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    public void Parse_InvalidPriority_Ignored(string priority)
    {
        var xml = $"<urlset><url><loc>http://site.example/</loc><priority>{priority}</priority></url></urlset>";

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.NotNull(result.Document);
        Assert.Null(result.Document!.Urls[0].Priority);
    }

    [Fact]
    public void Parse_EntryBound_TruncatesWithDiagnostic()
    {
        var xml = "<urlset>" +
            string.Concat(Enumerable.Range(0, 5).Select(i => $"<url><loc>http://site.example/{i}</loc></url>")) +
            "</urlset>";

        SitemapParseResult result = SitemapXmlParser.Parse(xml, maxEntries: 3);

        Assert.NotNull(result.Document);
        Assert.Equal(3, result.Document!.Urls.Count);
        Assert.Contains(result.Diagnostics, d => d.Contains("truncated"));
    }

    [Fact]
    public void Parse_HostileDtd_RejectedWithoutThrowing()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE urlset [<!ENTITY xxe "expanded">]>
            <urlset><url><loc>&xxe;</loc></url></urlset>
            """;

        SitemapParseResult result = SitemapXmlParser.Parse(xml);

        Assert.Null(result.Document);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Parse_InvalidBound_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SitemapXmlParser.Parse("<urlset/>", maxEntries: 0));
    }
}
