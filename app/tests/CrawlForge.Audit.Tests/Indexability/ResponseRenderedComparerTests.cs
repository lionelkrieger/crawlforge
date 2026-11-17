using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Response-vs-rendered comparison (FS-CRAWL-001 §7.18): status, title,
/// metadata, directive, text, link, structured-data, and resource deltas
/// surface as field-level differences and findings.
/// </summary>
public sealed class ResponseRenderedComparerTests
{
    [Fact]
    public async Task IdenticalDocuments_NoDifferences()
    {
        DomDocument raw = SimpleDocument("Same title", "Same content", "/link");
        DomDocument rendered = SimpleDocument("Same title", "Same content", "/link");
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.Empty(comparison.Differences);
        Assert.Empty(comparison.Findings);
    }

    [Fact]
    public async Task NoRenderedDocument_EmptyComparison()
    {
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: SimpleDocument("T", "C", "/l")));

        Assert.Empty(comparison.Differences);
        Assert.Empty(comparison.Findings);
    }

    [Fact]
    public async Task TitleAlteredByScript_EmitsDeltaAndWarning()
    {
        DomDocument raw = SimpleDocument("Original title", "Content", "/link");
        DomDocument rendered = SimpleDocument("Rewritten title", "Content", "/link");
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        ContentDifference delta = Assert.Single(comparison.Differences, d => d.Field == "title");
        Assert.Equal("Original title", delta.ResponseValue);
        Assert.Equal("Rewritten title", delta.RenderedValue);
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderTitleDelta && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task MetadataAlteredByScript_EmitsWarning()
    {
        DomDocument raw = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("T", new[] { DomBuilder.Meta("description", "Original description") }, null));
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("T", new[] { DomBuilder.Meta("description", "Script-rewritten description") }, null));
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.Contains(comparison.Differences, d => d.Field == "metadata");
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderMetadataDelta && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task DirectiveAlteredByScript_EmitsDelta()
    {
        DomDocument raw = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("T", new[] { DomBuilder.Meta("robots", "index") }, null));
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("T", new[] { DomBuilder.Meta("robots", "noindex") }, null));
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.Contains(comparison.Differences, d => d.Field == "directives");
    }

    [Fact]
    public async Task LinksOnlyAfterRendering_EmitsLinkDelta()
    {
        DomDocument raw = SimpleDocument("T", "Content");
        DomDocument rendered = SimpleDocument("T", "Content", "/injected-by-script");
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        ContentDifference delta = Assert.Single(comparison.Differences, d => d.Field == "links");
        Assert.DoesNotContain("/injected-by-script", delta.ResponseValue, StringComparison.Ordinal);
        Assert.Contains("/injected-by-script", delta.RenderedValue, StringComparison.Ordinal);
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderLinksDelta);
    }

    [Fact]
    public async Task TextOnlyAfterRendering_EmitsTextDelta()
    {
        DomDocument raw = SimpleDocument("T", "Short");
        DomDocument rendered = SimpleDocument("T", "Short plus a large amount of script-injected content that changes the visible text.");
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.Contains(comparison.Differences, d => d.Field == "text");
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderTextDelta && f.Category == AuditCategory.Content);
    }

    [Fact]
    public async Task StructuredDataInjectedByScript_EmitsDelta()
    {
        DomDocument raw = SimpleDocument("T", "C");
        DomNode jsonLd = DomBuilder.Text("script", "{\"@type\":\"Product\"}");
        DomNode withType = jsonLd with { Attributes = new Dictionary<string, string> { ["type"] = "application/ld+json" } };
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("T", null, new[] { DomBuilder.Text("p", "C"), withType }));
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison comparison = await comparer.CompareAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.Contains(comparison.Differences, d => d.Field == "structured-data");
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderStructuredDataDelta);
    }

    [Fact]
    public async Task StatusDelta_EmitsWarning()
    {
        DomDocument raw = SimpleDocument("T", "C");
        var comparer = new ResponseRenderedComparer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            document: raw,
            renderedDocument: raw,
            url: IndexabilityContextFactory.UrlRecord(CrawlStatusClass.Redirect, 302),
            fetch: IndexabilityContextFactory.Fetch(statusCode: 200));

        ResponseRenderedComparison comparison = await comparer.CompareAsync(context);

        ContentDifference delta = Assert.Single(comparison.Differences, d => d.Field == "status");
        Assert.Equal("200", delta.ResponseValue);
        Assert.Equal("302", delta.RenderedValue);
        Assert.Contains(comparison.Findings, f => f.RuleKey == IndexabilityRuleKeys.RenderStatusDelta);
    }

    [Fact]
    public async Task Rerun_IsDeterministic()
    {
        DomDocument raw = SimpleDocument("A", "Alpha", "/one");
        DomDocument rendered = SimpleDocument("B", "Beta", "/two");
        PageAuditContext context = IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered);
        var comparer = new ResponseRenderedComparer();

        ResponseRenderedComparison first = await comparer.CompareAsync(context);
        ResponseRenderedComparison second = await comparer.CompareAsync(context);

        Assert.Equal(first.Differences.Count, second.Differences.Count);
        for (int i = 0; i < first.Differences.Count; i++)
        {
            Assert.Equal(first.Differences[i], second.Differences[i]);
        }

        Assert.Equal(first.Findings.Count, second.Findings.Count);
    }

    private static DomDocument SimpleDocument(string title, string content, params string[] links)
    {
        var body = new List<DomNode> { DomBuilder.Text("p", content) };
        body.AddRange(links.Select(l => DomBuilder.Anchor(l, "link")));
        return DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page(title, null, body));
    }
}