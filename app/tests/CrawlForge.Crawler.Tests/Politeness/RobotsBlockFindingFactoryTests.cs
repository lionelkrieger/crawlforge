using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Politeness;

/// <summary>
/// Robots-blocked crawl-time finding (FS-CRAWL-001 §4.4/§5.4, fixture trap
/// robots-blocked): the disallowing rule is the evidence, the URL record is
/// classified Blocked/robots/Disallowed, and the key matches the offline
/// IndexabilityAnalyzer emission (one stable key, two paths).
/// </summary>
public class RobotsBlockFindingFactoryTests
{
    private const string FixtureRobotsBody = "User-agent: *\nDisallow: /private/\nSitemap: http://127.0.0.1:47314/sitemap.xml\n";

    private static (RobotsDocument Document, RobotsTxtParser Parser) FixtureRobots()
    {
        var parser = new RobotsTxtParser();
        return (parser.Parse(FixtureRobotsBody), parser);
    }

    /// <summary>The fixture trap URL is disallowed and produces the finding with rule evidence.</summary>
    [Fact]
    public void DisallowedUrlProducesFindingWithRuleEvidence()
    {
        var (document, parser) = FixtureRobots();
        const string url = "http://127.0.0.1:47314/private/blocked.html";
        var verdict = parser.Evaluate(document, url, "CrawlForge");
        Assert.False(verdict.Allowed);

        var finding = RobotsBlockFindingFactory.Create(
            11, url, verdict, document, "CrawlForge", "http://127.0.0.1:47314/robots.txt");
        Assert.Equal("indexability.robots-blocked", finding.RuleKey);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(11, finding.UrlId);

        Assert.Contains("/private/", finding.EvidenceJson);
        Assert.Contains("\"allow\":false", finding.EvidenceJson);
        Assert.Contains("robots.txt", finding.EvidenceJson);
        Assert.Contains("Disallowed", finding.EvidenceJson);
        Assert.Contains("CrawlForge", finding.EvidenceJson);
    }

    /// <summary>An allow verdict never produces a finding.</summary>
    [Fact]
    public void AllowedVerdictIsRejected()
    {
        var (document, parser) = FixtureRobots();
        var verdict = parser.Evaluate(document, "http://127.0.0.1:47314/pages/home.html", "CrawlForge");
        Assert.True(verdict.Allowed);
        Assert.Throws<ArgumentException>(() => RobotsBlockFindingFactory.Create(
            11, "http://127.0.0.1:47314/pages/home.html", verdict, document, "CrawlForge", "http://127.0.0.1:47314/robots.txt"));
    }

    /// <summary>Crawl-delay of the selecting group is surfaced in the evidence.</summary>
    [Fact]
    public void CrawlDelayIsSurfacedInEvidence()
    {
        var parser = new RobotsTxtParser();
        var document = parser.Parse("User-agent: *\nCrawl-delay: 7\nDisallow: /private/\n");
        var verdict = parser.Evaluate(document, "http://host/private/x", "CrawlForge");
        var finding = RobotsBlockFindingFactory.Create(1, "http://host/private/x", verdict, document, "CrawlForge", "http://host/robots.txt");
        Assert.Contains("\"crawlDelaySeconds\":7", finding.EvidenceJson);
    }

    /// <summary>The record classification matches the spec state taxonomy.</summary>
    [Fact]
    public void ClassifySetsBlockedRobotsDisallowed()
    {
        var record = new UrlRecordDto(
            Id: 11,
            AuditId: 1,
            CanonicalUrl: "http://127.0.0.1:47314/private/blocked.html",
            Host: "127.0.0.1",
            Path: "/private/blocked.html",
            Depth: 1,
            DiscoverySource: DiscoverySource.Link,
            StatusClass: CrawlStatusClass.NotCrawled,
            FinalStatusCode: null,
            RedirectTargetUrl: null,
            ContentType: null,
            SizeBytes: null,
            TimingJson: null,
            RobotsState: RobotsState.Unknown,
            IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: true);

        var classified = RobotsBlockFindingFactory.Classify(record);
        Assert.Equal(CrawlStatusClass.Blocked, classified.StatusClass);
        Assert.Equal("robots", classified.BlockReason);
        Assert.Equal(RobotsState.Disallowed, classified.RobotsState);
        Assert.True(classified.IndexabilityFlags.HasFlag(IndexabilityFlags.RobotsBlocked));
        Assert.False(classified.IsIndexable);
        // Other flags are preserved.
        var withFlags = RobotsBlockFindingFactory.Classify(record with { IndexabilityFlags = IndexabilityFlags.Noindex });
        Assert.True(withFlags.IndexabilityFlags.HasFlag(IndexabilityFlags.Noindex));
        Assert.True(withFlags.IndexabilityFlags.HasFlag(IndexabilityFlags.RobotsBlocked));
    }
}
