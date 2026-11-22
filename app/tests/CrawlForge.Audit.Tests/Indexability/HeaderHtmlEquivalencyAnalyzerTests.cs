using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Header/HTML directive equivalency (FS-CRAWL-002 §15.4, criterion #48):
/// conflicts emit the three locked rule keys as ERROR findings with DUAL
/// evidence; agreement records Pass annotations; the header_directives
/// element group is populated from parsed headers.
/// </summary>
public sealed class HeaderHtmlEquivalencyAnalyzerTests
{
    [Fact]
    public async Task HeaderNoindex_VsHtmlIndex_EmitsErrorWithDualEvidence()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" },
            head: new[] { DomBuilder.Meta("robots", "index, follow") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        Assert.Equal(AuditCategory.Indexability, conflict.Category);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("noindex", evidence.RootElement.GetProperty("headerValue").GetString());
        Assert.Equal("index, follow", evidence.RootElement.GetProperty("htmlValue").GetString());
        Assert.Equal("noindex", evidence.RootElement.GetProperty("directive").GetString());
        Assert.Equal("noindex", evidence.RootElement.GetProperty("diff").GetProperty("header").GetString());
        Assert.Equal("index", evidence.RootElement.GetProperty("diff").GetProperty("html").GetString());
    }

    [Fact]
    public async Task HtmlNoindex_VsHeaderIndex_EmitsError()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "index, follow" },
            head: new[] { DomBuilder.Meta("robots", "noindex") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("index", evidence.RootElement.GetProperty("directive").GetString());
        Assert.Equal("index", evidence.RootElement.GetProperty("diff").GetProperty("header").GetString());
        Assert.Equal("noindex", evidence.RootElement.GetProperty("diff").GetProperty("html").GetString());
    }

    [Fact]
    public async Task Agreement_RecordsPassAnnotation()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex, nofollow" },
            head: new[] { DomBuilder.Meta("robots", "noindex, nofollow") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft pass = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Pass, pass.Severity);
    }

    [Fact]
    public async Task AgentScopedConflict_CarriesAgentEvidence()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "googlebot: noindex" },
            head: new[] { DomBuilder.Meta("googlebot", "index") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft conflict = Assert.Single(findings, f => f.Severity == FindingSeverity.Error);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("googlebot", evidence.RootElement.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task AgentScopedHeader_VsGenericHtmlMeta_StillCompares()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "googlebot: noindex" },
            head: new[] { DomBuilder.Meta("robots", "index") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task HeaderOnlyDirectives_NoHtmlCounterpart_NoConflict()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" },
            head: Array.Empty<DomNode>());
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task NoneDirective_ExpandsToBothConflicts()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "none" },
            head: new[] { DomBuilder.Meta("robots", "index, follow") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Equal(2, findings.Count(f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Error));
    }

    [Fact]
    public async Task CanonicalConflict_EmitsErrorWithResolvedDualEvidence()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["Link"] = "<https://example.com/header-canonical>; rel=\"canonical\"" },
            head: new[] { DomBuilder.Link("canonical", "/html-canonical") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        Assert.Equal(AuditCategory.Indexability, conflict.Category);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("https://example.com/header-canonical", evidence.RootElement.GetProperty("resolvedHeader").GetString());
        Assert.Equal("https://example.com/html-canonical", evidence.RootElement.GetProperty("resolvedHtml").GetString());
        Assert.False(string.IsNullOrEmpty(evidence.RootElement.GetProperty("headerValue").GetString()));
        Assert.False(string.IsNullOrEmpty(evidence.RootElement.GetProperty("htmlValue").GetString()));
    }

    [Fact]
    public async Task CanonicalAgreement_RelativeAndAbsolute_RecordsPass()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["Link"] = "<https://example.com/page>; rel=\"canonical\"" },
            head: new[] { DomBuilder.Link("canonical", "/page") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft pass = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Pass, pass.Severity);
    }

    [Fact]
    public async Task HreflangConflict_EmitsErrorInI18nCategory()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["Link"] = "<https://example.com/en>; rel=\"alternate\"; hreflang=\"en\"" },
            head: new[] { DomBuilder.Link("alternate", "/english", "en") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.HreflangHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        Assert.Equal(AuditCategory.Internationalization, conflict.Category); // D-021: hreflang -> Internationalization
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("en", evidence.RootElement.GetProperty("hreflang").GetString());
        Assert.Equal("https://example.com/en", evidence.RootElement.GetProperty("resolvedHeader").GetString());
        Assert.Equal("https://example.com/english", evidence.RootElement.GetProperty("resolvedHtml").GetString());
    }

    [Fact]
    public async Task HreflangAgreement_RecordsPass()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string> { ["Link"] = "<https://example.com/en>; rel=\"alternate\"; hreflang=\"en\"" },
            head: new[] { DomBuilder.Link("alternate", "/en", "en") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft pass = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.HreflangHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Pass, pass.Severity);
    }

    [Fact]
    public async Task MultipleHreflangs_OnlyConflictingPairEmitsError()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string>
            {
                ["Link"] = "<https://example.com/en>; rel=\"alternate\"; hreflang=\"en\", <https://example.com/de-header>; rel=\"alternate\"; hreflang=\"de\"",
            },
            head: new[] { DomBuilder.Link("alternate", "/en", "en"), DomBuilder.Link("alternate", "/de-html", "de") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Equal(2, findings.Count(f => f.RuleKey == FindingRuleKeys.HreflangHeaderHtmlConflict));
        Assert.Single(findings, f => f.Severity == FindingSeverity.Error);
        Assert.Single(findings, f => f.Severity == FindingSeverity.Pass);
    }

    [Fact]
    public async Task NoHeaderDirectives_NoFindings()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string>(),
            head: new[] { DomBuilder.Meta("robots", "noindex") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        Assert.Empty(await analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public void BuildHeaderDirectivesInventory_PopulatesHeaderDirectivesGroup()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string>
            {
                ["X-Robots-Tag"] = "noindex",
                ["Link"] = "<https://example.com/page>; rel=\"canonical\"",
            },
            head: Array.Empty<DomNode>());
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        ElementInventoryItem? item = analyzer.BuildHeaderDirectivesInventory(context);

        Assert.NotNull(item);
        Assert.Equal(ElementGroups.HeaderDirectives, item.Group);
        Assert.Equal(0, item.Ordinal);
        using JsonDocument payload = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal("noindex", payload.RootElement.GetProperty("robots")[0].GetProperty("directives")[0].GetString());
        Assert.Equal("canonical", payload.RootElement.GetProperty("links")[0].GetProperty("relation").GetString());
    }

    [Fact]
    public void BuildHeaderDirectivesInventory_NoHeaders_ReturnsNull()
    {
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();
        Assert.Null(analyzer.BuildHeaderDirectivesInventory(IndexabilityContextFactory.Context()));
    }

    [Fact]
    public async Task Analyzer_AcceptsContractParserOutput()
    {
        // Contract-level interaction: the analyzer consumes whatever an
        // ITolerantDomParser implementation produces (doubled here).
        ITolerantDomParser parser = new FakeTolerantDomParser();
        DomDocument document = parser.Parse(
            "<html><head><meta name=\"robots\" content=\"index, follow\"></head><body>ok</body></html>",
            IndexabilityContextFactory.PageUrl);
        PageAuditContext context = IndexabilityContextFactory.Context(
            document: document,
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" }));
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task Rerun_FromSameStoredContext_IsDeterministic()
    {
        PageAuditContext context = ContextWith(
            headers: new Dictionary<string, string>
            {
                ["X-Robots-Tag"] = "noindex",
                ["Link"] = "<https://example.com/a>; rel=\"canonical\"",
            },
            head: new[] { DomBuilder.Meta("robots", "index"), DomBuilder.Link("canonical", "/b") });
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        IReadOnlyList<FindingDraft> first = await analyzer.AnalyzeAsync(context);
        IReadOnlyList<FindingDraft> second = await analyzer.AnalyzeAsync(context);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }

    private static PageAuditContext ContextWith(IReadOnlyDictionary<string, string> headers, IReadOnlyList<DomNode> head)
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Equivalency", head, new[] { DomBuilder.Text("p", "content") }));
        return IndexabilityContextFactory.Context(
            document: document,
            fetch: IndexabilityContextFactory.Fetch(headers: headers));
    }
}