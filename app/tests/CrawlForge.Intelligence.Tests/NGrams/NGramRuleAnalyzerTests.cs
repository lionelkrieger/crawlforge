using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>
/// Rule-host tests: criterion #38 scoped body/headings with baseline parity,
/// cannibalization clusters with canonical downgrade, anchor-demand topical
/// gaps, exclusions, and metric contributions.
/// </summary>
public sealed class NGramRuleAnalyzerTests
{
    /// <summary>Unscoped baseline: body uses full visible text; all headings included.</summary>
    [Fact]
    public async Task Unscoped_Baseline_Uses_Full_Text_And_All_Headings()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var (auditId, urlId, rule, _) = await CreateFixtureAsync(stack);
        var document = DomBuilder.Page("https://example.com/a", "alpha beta", "gamma delta", "epsilon zeta eta",
            extraHeadings: [DomBuilder.Element("h2", "theta iota")]);
        var context = ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, "https://example.com/a"), document);

        var analysis = await rule.AnalyzePageAsync(context);

        Assert.NotNull(analysis);
        var bodyTerms = analysis.Rows.Where(r => r.Zone == TextZone.Body && r.Order == 1).Select(r => r.Term).ToHashSet();
        Assert.Contains("epsilon", bodyTerms);
        Assert.Contains("zeta", bodyTerms);
        var headingTerms = analysis.Rows.Where(r => r.Zone == TextZone.Headings && r.Order == 1).Select(r => r.Term).ToHashSet();
        Assert.Contains("gamma", headingTerms);
        Assert.Contains("theta", headingTerms);
    }

    /// <summary>Criterion #38: scoped text on the context feeds body; headings use containment.</summary>
    [Fact]
    public async Task Scoped_Context_Feeds_Body_And_Filters_Headings()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var (auditId, urlId, rule, _) = await CreateFixtureAsync(stack);
        var document = DomBuilder.Page("https://example.com/a", "alpha beta", "gamma delta", "epsilon zeta eta",
            extraHeadings: [DomBuilder.Element("h2", "theta iota")]);
        var context = ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, "https://example.com/a"), document,
            scopedText: new ScopedVisibleText("gamma delta and main content", 5, []));

        var analysis = await rule.AnalyzePageAsync(context);

        Assert.NotNull(analysis);
        var bodyTerms = analysis.Rows.Where(r => r.Zone == TextZone.Body && r.Order == 1).Select(r => r.Term).ToHashSet();
        Assert.Contains("main", bodyTerms);
        Assert.DoesNotContain("epsilon", bodyTerms);
        var headingTerms = analysis.Rows.Where(r => r.Zone == TextZone.Headings && r.Order == 1).Select(r => r.Term).ToHashSet();
        Assert.Contains("gamma", headingTerms);
        Assert.DoesNotContain("theta", headingTerms);
        // Title is never scoped (head fact).
        Assert.Contains(analysis.Rows, r => r.Zone == TextZone.Title && r.Term == "alpha");
    }

    /// <summary>Configuration scope resolves through the keystone resolver.</summary>
    [Fact]
    public async Task Configuration_Scope_Resolves_Through_Resolver()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var resolver = new RecordingScopeResolver("resolved scoped text");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider, resolver);
        var document = DomBuilder.Page("https://example.com/a", "alpha beta", "gamma delta", "epsilon zeta");
        var scope = new ContentScopeDefinition(["main"], []);
        var context = ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, "https://example.com/a"), document,
            configuration: ContextFactory.Configuration(scope));

        var analysis = await rule.AnalyzePageAsync(context);

        Assert.NotNull(analysis);
        Assert.Single(resolver.Calls);
        var bodyTerms = analysis.Rows.Where(r => r.Zone == TextZone.Body && r.Order == 1).Select(r => r.Term).ToHashSet();
        Assert.Contains("resolved", bodyTerms);
        Assert.DoesNotContain("epsilon", bodyTerms);
    }

    /// <summary>WAF-blocked and non-HTML records are excluded (§5.1).</summary>
    [Fact]
    public async Task Blocked_And_NonHtml_Records_Are_Excluded()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var (auditId, urlId, rule, _) = await CreateFixtureAsync(stack);
        var document = DomBuilder.Page("https://example.com/a", "alpha beta", null, "body");

        var blocked = await rule.AnalyzePageAsync(ContextFactory.Create(auditId,
            ContextFactory.Url(urlId, auditId, "https://example.com/a", statusClass: CrawlStatusClass.Blocked), document));
        var pdf = await rule.AnalyzePageAsync(ContextFactory.Create(auditId,
            ContextFactory.Url(urlId, auditId, "https://example.com/a", contentType: "application/pdf"), document));

        Assert.Null(blocked);
        Assert.Null(pdf);
    }

    /// <summary>Identical primary title bigrams on indexable URLs cluster as Warning.</summary>
    [Fact]
    public async Task Cannibalization_Clusters_Indexable_URLs_With_Warning()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        for (var i = 1; i <= 2; i++)
        {
            var url = $"https://example.com/page{i}";
            var urlId = await stack.SeedUrlAsync(auditId, url);
            var document = DomBuilder.Page(url, "alpha beta", null, "body text");
            await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, url), document));
        }

        var result = await rule.RunSitePassAsync(auditId);

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal("alpha beta", cluster.Term);
        Assert.Equal(TextZone.Title, cluster.Zone);
        Assert.Equal(2, cluster.Order);
        Assert.Equal(2, cluster.Members.Count);
        Assert.Equal(2, cluster.SiteDocumentFrequency);
        Assert.False(cluster.SameCanonicalTarget);
        var finding = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.ContentKeywordCannibalization);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(AuditCategory.Content, finding.Category);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("members").GetArrayLength());
    }

    /// <summary>Uniform declared canonical targets downgrade the cluster to Notice (§5.5).</summary>
    [Fact]
    public async Task Cannibalization_Downgrades_On_Uniform_Canonical_Target()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        for (var i = 1; i <= 2; i++)
        {
            var url = $"https://example.com/page{i}";
            var urlId = await stack.SeedUrlAsync(auditId, url);
            var document = DomBuilder.Page(url, "alpha beta", null, "body text", canonicalHref: "https://example.com/consolidated");
            await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, url), document));
        }

        var result = await rule.RunSitePassAsync(auditId);

        var cluster = Assert.Single(result.Clusters);
        Assert.True(cluster.SameCanonicalTarget);
        var finding = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.ContentKeywordCannibalization);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
    }

    /// <summary>Non-indexable URLs do not form cannibalization clusters.</summary>
    [Fact]
    public async Task Cannibalization_Excludes_NonIndexable_URLs()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        for (var i = 1; i <= 2; i++)
        {
            var url = $"https://example.com/page{i}";
            var urlId = await stack.SeedUrlAsync(auditId, url, indexable: i == 1);
            var document = DomBuilder.Page(url, "alpha beta", null, "body text");
            await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, url, indexable: i == 1), document));
        }

        var result = await rule.RunSitePassAsync(auditId);

        Assert.Empty(result.Clusters);
    }

    /// <summary>Anchor demand without on-page supply emits a topical gap with samples.</summary>
    [Fact]
    public async Task TopicalGap_Emits_On_Anchor_Demand_Without_OnPage_Supply()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        var sourceUrl = "https://example.com/source";
        var sourceId = await stack.SeedUrlAsync(auditId, sourceUrl);
        var sourceDocument = DomBuilder.Page(sourceUrl, "source page", null, "source body");
        await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(sourceId, auditId, sourceUrl), sourceDocument));
        for (var i = 1; i <= 3; i++)
        {
            var url = $"https://example.com/target{i}";
            var urlId = await stack.SeedUrlAsync(auditId, url);
            await stack.SeedLinkEdgeAsync(auditId, sourceId, urlId, "green widgets");
            var document = DomBuilder.Page(url, $"target {i}", null, "target body");
            await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, url), document));
        }

        var result = await rule.RunSitePassAsync(auditId);

        // All orders qualify: unigrams green/widgets and the bigram, each DF 3.
        Assert.Equal(3, result.Gaps.Count);
        var gap = Assert.Single(result.Gaps, g => g.Term == "green widgets");
        Assert.Equal(3, gap.AnchorDocumentFrequency);
        Assert.Contains(sourceUrl, gap.SampleSourceUrls);
        var finding = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.ContentTopicalGap && f.EvidenceJson.Contains("green widgets"));
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(3, evidence.RootElement.GetProperty("anchorDf").GetInt64());
    }

    /// <summary>On-page title supply suppresses the gap (zero-DF condition).</summary>
    [Fact]
    public async Task TopicalGap_Suppressed_When_Term_Appears_On_Page()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        var sourceUrl = "https://example.com/source";
        var sourceId = await stack.SeedUrlAsync(auditId, sourceUrl);
        var sourceDocument = DomBuilder.Page(sourceUrl, "source page", null, "source body");
        await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(sourceId, auditId, sourceUrl), sourceDocument));
        for (var i = 1; i <= 3; i++)
        {
            var url = $"https://example.com/target{i}";
            var urlId = await stack.SeedUrlAsync(auditId, url);
            await stack.SeedLinkEdgeAsync(auditId, sourceId, urlId, "green widgets");
            var title = i == 1 ? "green widgets sale" : $"target {i}";
            var document = DomBuilder.Page(url, title, null, "target body");
            await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, url), document));
        }

        var result = await rule.RunSitePassAsync(auditId);

        Assert.Empty(result.Gaps);
    }

    /// <summary>Metric contributions: ngram-top per zone/order, cluster and gap counts.</summary>
    [Fact]
    public async Task SitePass_Emits_Metric_Contributions()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var document = DomBuilder.Page("https://example.com/a", "alpha beta", null, "body text");
        await rule.AnalyzePageAsync(ContextFactory.Create(auditId, ContextFactory.Url(urlId, auditId, "https://example.com/a"), document));

        var result = await rule.RunSitePassAsync(auditId);

        Assert.Contains(result.Metrics, m => m.MetricKey == MetricKeys.NgramTop);
        var clusters = Assert.Single(result.Metrics, m => m.MetricKey == MetricKeys.CannibalizationClusters);
        Assert.Equal(0, clusters.Value);
        var gaps = Assert.Single(result.Metrics, m => m.MetricKey == MetricKeys.TopicalGaps);
        Assert.Equal(0, gaps.Value);
        Assert.Equal(
            [FindingRuleKeys.ContentKeywordCannibalization, FindingRuleKeys.ContentTopicalGap],
            rule.SupportedRuleKeys);
    }

    private static async Task<(long AuditId, long UrlId, NGramRuleAnalyzer Rule, NGramAnalyzer Analyzer)> CreateFixtureAsync(NGramTestStack stack)
    {
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var rule = new NGramRuleAnalyzer(analyzer, new StopWordFilter(), stack.Provider);
        return (auditId, urlId, rule, analyzer);
    }
}
