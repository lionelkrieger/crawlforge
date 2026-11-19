using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Sitemaps;
using Xunit;

namespace CrawlForge.Crawler.Tests.Sitemaps;

/// <summary>
/// Sitemap-vs-crawl correlation (CAP-B-052/053/054): missing, non-indexable
/// (CR-024 search-indexability), and redirecting listed URLs, with evidence
/// shapes the conformance suite will lock.
/// </summary>
public sealed class SitemapCorrelationAnalyzerTests
{
    private const string Url = "http://site.example/pages/a.html";
    private readonly SitemapCorrelationAnalyzer _analyzer = new();

    private static SitemapUrlCrawlFact Fact(
        string url = Url,
        long? urlId = 11,
        bool wasCrawled = true,
        CrawlStatusClass statusClass = CrawlStatusClass.Success,
        int? statusCode = 200,
        RobotsState robots = RobotsState.Allowed,
        IndexabilityFlags flags = IndexabilityFlags.None,
        string? canonical = null,
        IReadOnlyList<RedirectHop>? chain = null) =>
        new(url, urlId, wasCrawled, statusClass, statusCode, robots, flags, canonical, chain ?? Array.Empty<RedirectHop>());

    [Fact]
    public void Analyze_UrlAbsentFromCrawl_MissingUrlFinding()
    {
        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, Array.Empty<SitemapUrlCrawlFact>());

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(SitemapRuleKeys.MissingUrl, finding.RuleKey);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.UrlId);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(Url, evidence.RootElement.GetProperty("url").GetString());
        Assert.Equal("not-crawled", evidence.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Analyze_NotCrawledFact_MissingUrlFinding()
    {
        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, new[] { Fact(wasCrawled: false, urlId: null) });

        Assert.Equal(SitemapRuleKeys.MissingUrl, Assert.Single(findings).RuleKey);
    }

    [Fact]
    public void Analyze_RedirectingUrl_RedirectingFindingWithChainEvidence()
    {
        var chain = new[]
        {
            new RedirectHop(0, Url, "http://site.example/pages/b.html", 301),
            new RedirectHop(1, "http://site.example/pages/b.html", "http://site.example/pages/c.html", 302),
        };
        var fact = Fact(statusClass: CrawlStatusClass.Redirect, statusCode: 200, chain: chain);

        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, new[] { fact });

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(SitemapRuleKeys.Redirecting, finding.RuleKey);
        Assert.Equal(11, finding.UrlId);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("hopCount").GetInt32());
        Assert.Equal("http://site.example/pages/c.html", evidence.RootElement.GetProperty("finalUrl").GetString());
        JsonElement hop0 = evidence.RootElement.GetProperty("chain")[0];
        Assert.Equal(0, hop0.GetProperty("ordinal").GetInt32());
        Assert.Equal(Url, hop0.GetProperty("url").GetString());
        Assert.Equal("http://site.example/pages/b.html", hop0.GetProperty("toUrl").GetString());
        Assert.Equal(301, hop0.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public void Analyze_RedirectingUrl_NotDoubleFlaggedAsNonIndexable()
    {
        var chain = new[] { new RedirectHop(0, Url, "http://site.example/target", 301) };
        var fact = Fact(statusClass: CrawlStatusClass.Redirect, statusCode: 301, chain: chain);

        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, new[] { fact });

        Assert.Equal(SitemapRuleKeys.Redirecting, Assert.Single(findings).RuleKey);
    }

    [Theory]
    [InlineData(404, RobotsState.Allowed, IndexabilityFlags.None, null, "non-200-status")]
    [InlineData(200, RobotsState.Disallowed, IndexabilityFlags.None, null, "robots-disallowed")]
    [InlineData(200, RobotsState.Allowed, IndexabilityFlags.Noindex, null, "noindex")]
    [InlineData(200, RobotsState.Allowed, IndexabilityFlags.None, "http://site.example/other", "canonical-mismatch")]
    public void Analyze_NonIndexableUrl_Cr024Reasons(int status, RobotsState robots, IndexabilityFlags flags, string? canonical, string expectedReason)
    {
        var fact = Fact(statusCode: status, robots: robots, flags: flags, canonical: canonical);

        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, new[] { fact });

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(SitemapRuleKeys.NonIndexable, finding.RuleKey);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        var reasons = evidence.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains(expectedReason, reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Url)]
    public void Analyze_IndexableUrl_NoFinding(string? canonical)
    {
        var fact = Fact(canonical: canonical);

        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url }, new[] { fact });

        Assert.Empty(findings);
    }

    [Fact]
    public void IsSearchIndexable_Cr024Semantics()
    {
        Assert.True(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact()));
        Assert.True(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact(canonical: Url)));
        Assert.False(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact(statusCode: 301)));
        Assert.False(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact(robots: RobotsState.Disallowed)));
        Assert.False(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact(flags: IndexabilityFlags.Noindex)));
        Assert.False(SitemapCorrelationAnalyzer.IsSearchIndexable(Fact(canonical: "http://site.example/elsewhere")));
    }

    [Fact]
    public void Analyze_DuplicateSitemapUrls_ReportedOnce()
    {
        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { Url, Url }, Array.Empty<SitemapUrlCrawlFact>());

        Assert.Single(findings);
    }

    [Fact]
    public void Analyze_MixedSet_OneFindingPerDefectiveUrlInDeclarationOrder()
    {
        const string missing = "http://site.example/missing";
        const string redirecting = "http://site.example/redirecting";
        const string noindex = "http://site.example/noindex";
        const string ok = "http://site.example/ok";
        var facts = new[]
        {
            Fact(redirecting, urlId: 2, statusClass: CrawlStatusClass.Redirect, statusCode: 301,
                chain: new[] { new RedirectHop(0, redirecting, ok, 301) }),
            Fact(noindex, urlId: 3, flags: IndexabilityFlags.Noindex),
            Fact(ok, urlId: 4),
        };

        IReadOnlyList<FindingDto> findings = _analyzer.Analyze(7, new[] { missing, redirecting, noindex, ok }, facts);

        Assert.Equal(3, findings.Count);
        Assert.Equal(SitemapRuleKeys.MissingUrl, findings[0].RuleKey);
        Assert.Equal(SitemapRuleKeys.Redirecting, findings[1].RuleKey);
        Assert.Equal(SitemapRuleKeys.NonIndexable, findings[2].RuleKey);
    }
}
