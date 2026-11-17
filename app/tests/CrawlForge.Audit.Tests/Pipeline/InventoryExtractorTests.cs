using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Element inventory extraction (FS-CRAWL-001 §7.3 + FS-CRAWL-002 Appendix A
/// additive groups): one url_elements row per group instance.
/// </summary>
public class InventoryExtractorTests
{
    private readonly AngleSharpDomParser _parser = new();
    private readonly ElementInventoryExtractor _extractor = new();

    private async Task<IReadOnlyList<ElementInventoryItem>> Extract(string html, string url = "https://example.test/page")
    {
        var document = _parser.Parse(html, url);
        return await _extractor.ExtractAsync(document);
    }

    private static JsonElement Payload(ElementInventoryItem item) =>
        JsonDocument.Parse(item.PayloadJson).RootElement;

    [Fact]
    public async Task HeadFactsAreExtracted()
    {
        var items = await Extract(PipelineTestContext.SampleHtml);
        var head = Payload(items.Single(i => i.Group == "document_head"));

        Assert.Equal("Sample Page", head.GetProperty("title").GetString());
        Assert.Equal("A sample page for pipeline tests.", head.GetProperty("metaDescription").GetString());
        Assert.Equal("index,follow", head.GetProperty("metaRobots").GetString());
        Assert.Equal("https://example.test/page", head.GetProperty("canonicalUrl").GetString());
    }

    [Fact]
    public async Task HeadingsOutlineIsOrderedWithLevels()
    {
        var items = await Extract(
            "<html><body><h1>One</h1><h2>Two</h2><h3>Three</h3><h2>Four</h2></body></html>");
        var outline = Payload(items.Single(i => i.Group == "headings")).GetProperty("outline");

        Assert.Equal(4, outline.GetArrayLength());
        Assert.Equal(1, outline[0].GetProperty("level").GetInt32());
        Assert.Equal("One", outline[0].GetProperty("text").GetString());
        Assert.Equal(2, outline[3].GetProperty("level").GetInt32());
        Assert.Equal("Four", outline[3].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnchorsCarryHrefTextRelTargetAndPosition()
    {
        var items = await Extract(
            "<html><body><a href=\"/one\" rel=\"nofollow\" target=\"_blank\">First</a><a href=\"/two\">Second</a></body></html>");
        var anchors = items.Where(i => i.Group == "anchors").ToArray();

        Assert.Equal(2, anchors.Length);
        Assert.Equal(0, anchors[0].Ordinal);
        Assert.Equal(1, anchors[1].Ordinal);
        var first = Payload(anchors[0]);
        Assert.Equal("/one", first.GetProperty("href").GetString());
        Assert.Equal("First", first.GetProperty("text").GetString());
        Assert.Equal("nofollow", first.GetProperty("rel").GetString());
        Assert.Equal("_blank", first.GetProperty("target").GetString());
    }

    [Fact]
    public async Task ImagesCarryAltDimensionsAndLazyLoading()
    {
        var items = await Extract(
            "<html><body><img src=\"/a.png\" alt=\"A picture\" width=\"10\" height=\"20\" loading=\"lazy\"></body></html>");
        var image = Payload(items.Single(i => i.Group == "images"));

        Assert.Equal("/a.png", image.GetProperty("src").GetString());
        Assert.Equal("A picture", image.GetProperty("alt").GetString());
        Assert.Equal("10", image.GetProperty("width").GetString());
        Assert.Equal("lazy", image.GetProperty("loading").GetString());
    }

    [Fact]
    public async Task StructuredDataBlocksAreCapturedWithBounds()
    {
        var items = await Extract(
            "<html><head><script type=\"application/ld+json\">{\"@type\":\"Article\"}</script></head>" +
            "<body><div itemscope itemtype=\"https://schema.org/Product\"><span itemprop=\"name\">Widget</span></div></body></html>");
        var blocks = items.Where(i => i.Group == "structured_data").ToArray();

        Assert.Equal(2, blocks.Length);
        var jsonLd = Payload(blocks[0]);
        Assert.Equal("json-ld", jsonLd.GetProperty("type").GetString());
        Assert.Contains("Article", jsonLd.GetProperty("raw").GetString(), StringComparison.Ordinal);
        var microdata = Payload(blocks[1]);
        Assert.Equal("microdata", microdata.GetProperty("type").GetString());
        Assert.Equal("https://schema.org/Product", microdata.GetProperty("itemType").GetString());
    }

    [Fact]
    public async Task ResourcesReportPlacementAndLoadingFlags()
    {
        var items = await Extract(
            "<html><head><script src=\"/h.js\" defer></script><link rel=\"stylesheet\" href=\"/s.css\"></head>" +
            "<body><script src=\"/b.js\" async></script><script>inline()</script></body></html>");
        var resources = Payload(items.Single(i => i.Group == "resources"));

        Assert.Equal(3, resources.GetProperty("scriptCount").GetInt32());
        Assert.Equal(1, resources.GetProperty("stylesheetCount").GetInt32());
        var scripts = resources.GetProperty("scripts");
        Assert.Equal("head", scripts[0].GetProperty("placement").GetString());
        Assert.True(scripts[0].GetProperty("defer").GetBoolean());
        Assert.Equal("body", scripts[1].GetProperty("placement").GetString());
        Assert.True(scripts[1].GetProperty("async").GetBoolean());
        Assert.True(resources.GetProperty("inlineScriptBytes").GetInt32() > 0);
    }

    [Fact]
    public async Task TextStatisticsReportFullVisibleWordCountAndLanguage()
    {
        var items = await Extract(PipelineTestContext.SampleHtml);
        var stats = Payload(items.Single(i => i.Group == "text_statistics"));

        Assert.Equal("en", stats.GetProperty("declaredLanguage").GetString());
        var words = stats.GetProperty("visibleWordCount").GetInt32();
        Assert.True(words > 10);
    }

    [Fact]
    public async Task TablesReportRowColumnStructureAndHeaders()
    {
        var items = await Extract(
            "<html><body><table><thead><tr><th>H1</th><th>H2</th></tr></thead>" +
            "<tbody><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></tbody></table></body></html>");
        var table = Payload(items.Single(i => i.Group == "tables"));

        Assert.Equal(3, table.GetProperty("rows").GetInt32());
        Assert.Equal(2, table.GetProperty("columns").GetInt32());
        Assert.True(table.GetProperty("hasThead").GetBoolean());
        Assert.Equal("H1", table.GetProperty("headerCells")[0].GetString());
    }

    [Fact]
    public async Task LinkRelationsSeparatePaginationAlternatesAndOther()
    {
        var items = await Extract(
            "<html><head><link rel=\"next\" href=\"/page2\"><link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed\">" +
            "<link rel=\"author\" href=\"/about\"></head><body></body></html>");
        var relations = Payload(items.Single(i => i.Group == "link_relations"));

        Assert.Equal("/page2", relations.GetProperty("pagination")[0].GetProperty("href").GetString());
        Assert.Equal("/feed", relations.GetProperty("alternateFormats")[0].GetProperty("href").GetString());
        Assert.Equal("author", relations.GetProperty("other")[0].GetProperty("rel").GetString());
    }

    [Fact]
    public async Task AdditiveGroupsAreEmitted()
    {
        var html = """
            <html><head><title>A title</title><meta name="description" content="A description."></head>
            <body>
              <div id="cookie-consent-banner">We use cookies</div>
              <script src="https://cdn.onetrust.example/consent.js"></script>
              <script>window.dataLayer = window.dataLayer || []; dataLayer.push({"event":"pageview"});</script>
            </body></html>
            """;
        var items = await Extract(html);

        var consent = Payload(items.Single(i => i.Group == ElementGroups.ConsentState));
        Assert.True(consent.GetProperty("detected").GetBoolean());
        Assert.Contains("onetrust", consent.GetProperty("platformHints").EnumerateArray().Select(e => e.GetString()));

        var dataLayer = Payload(items.Single(i => i.Group == ElementGroups.DataLayerSnapshot));
        Assert.True(dataLayer.GetProperty("pushCount").GetInt32() >= 2);
        Assert.True(dataLayer.GetProperty("snippets").GetArrayLength() > 0);

        var serp = Payload(items.Single(i => i.Group == ElementGroups.SerpMetrics));
        Assert.Equal("A title".Length, serp.GetProperty("titleLength").GetInt32());
        Assert.Equal("A description.".Length, serp.GetProperty("descriptionLength").GetInt32());
    }

    [Fact]
    public async Task LlmsStructureIsExtractedForLlmsTxtDocuments()
    {
        var llms = "# Example\n\n- [Docs](https://example.test/docs)\n- [API](https://example.test/api)\n\n## Guides\n\n- [Start](https://example.test/start)";
        var items = await Extract("<html><body><pre>" + llms + "</pre></body></html>", "https://example.test/llms.txt");
        var structure = Payload(items.Single(i => i.Group == ElementGroups.LlmsStructure));

        Assert.Equal(3, structure.GetProperty("linkCount").GetInt32());
        Assert.True(structure.GetProperty("sections").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task DataLayerSnapshotsAreBounded()
    {
        var big = new string('x', 5000);
        var html = "<html><body><script>dataLayer.push({\"" + big + "\"});</script></body></html>";
        var items = await Extract(html);
        var dataLayer = Payload(items.Single(i => i.Group == ElementGroups.DataLayerSnapshot));

        foreach (var snippet in dataLayer.GetProperty("snippets").EnumerateArray())
        {
            Assert.True(snippet.GetString()!.Length <= 1024);
        }
    }
}
