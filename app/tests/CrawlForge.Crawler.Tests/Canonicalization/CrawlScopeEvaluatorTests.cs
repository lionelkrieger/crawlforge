using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Canonicalization;

/// <summary>
/// Crawl inclusion/exclusion scope (FS-CRAWL-001 §5.1/§5.13): regex rules
/// evaluated inclusion-first, host scope modes, asset domains, external-link
/// toggle, resource fetch toggles, and bounded/rejected regex configuration.
/// </summary>
public class CrawlScopeEvaluatorTests
{
    private static CrawlScopeEvaluator Create(
        CrawlScopeOptions? options = null,
        ResourceScopeOptions? resources = null,
        params string[] seeds) =>
        new(options, seeds.Length == 0 ? ["example.com"] : seeds, resources);

    /// <summary>Same-host URLs are enqueued under the baseline host-only scope.</summary>
    [Fact]
    public void SameHostIsEnqueued()
    {
        var verdict = Create().Evaluate("https://example.com/page");
        Assert.Equal(CrawlScopeDisposition.Enqueue, verdict.Disposition);
        Assert.True(verdict.IsInternalHost);
        Assert.Equal(CrawlResourceType.Html, verdict.ResourceType);
    }

    /// <summary>External hosts are recorded only by default; the toggle status-checks them.</summary>
    [Fact]
    public void ExternalHostsAreRecordedOrStatusChecked()
    {
        var baseline = Create().Evaluate("https://other.org/page");
        Assert.Equal(CrawlScopeDisposition.RecordExternal, baseline.Disposition);
        Assert.False(baseline.IsInternalHost);

        var checking = Create(new CrawlScopeOptions { CheckExternalLinks = true }).Evaluate("https://other.org/page");
        Assert.Equal(CrawlScopeDisposition.CheckExternalStatus, checking.Disposition);
    }

    /// <summary>Subdomains are out of scope host-only, in scope with the toggle.</summary>
    [Fact]
    public void SubdomainScopeModeIsHonored()
    {
        Assert.Equal(CrawlScopeDisposition.RecordExternal, Create().Evaluate("https://blog.example.com/post").Disposition);
        Assert.Equal(
            CrawlScopeDisposition.Enqueue,
            Create(new CrawlScopeOptions { Mode = CrawlScopeMode.IncludeSubdomains }).Evaluate("https://blog.example.com/post").Disposition);
    }

    /// <summary>Whole-domain mode matches sibling hosts on the registered domain.</summary>
    [Fact]
    public void WholeDomainModeMatchesSiblingHosts()
    {
        var evaluator = Create(new CrawlScopeOptions { Mode = CrawlScopeMode.WholeDomain });
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://shop.example.com/item").Disposition);
        Assert.Equal(CrawlScopeDisposition.RecordExternal, evaluator.Evaluate("https://example.org/page").Disposition);
    }

    /// <summary>Inclusion patterns gate crawling; at least one must match.</summary>
    [Fact]
    public void InclusionPatternsGateCrawling()
    {
        var evaluator = Create(new CrawlScopeOptions { IncludePatterns = [@"^https://example\.com/docs/"] });
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/docs/intro").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedOutOfScope, evaluator.Evaluate("https://example.com/blog/post").Disposition);
    }

    /// <summary>Exclusion patterns are evaluated after inclusion and disqualify.</summary>
    [Fact]
    public void ExclusionPatternsDisqualifyAfterInclusion()
    {
        var evaluator = Create(new CrawlScopeOptions
        {
            IncludePatterns = [@"^https://example\.com/"],
            ExcludePatterns = [@"\?sessionid=", @"/private/"]
        });
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/docs/intro").Disposition);

        var excluded = evaluator.Evaluate("https://example.com/page?sessionid=abc");
        Assert.Equal(CrawlScopeDisposition.SkippedExcluded, excluded.Disposition);
        Assert.Contains("sessionid", excluded.Reason);

        Assert.Equal(CrawlScopeDisposition.SkippedExcluded, evaluator.Evaluate("https://example.com/private/x").Disposition);
    }

    /// <summary>Path-prefix rules restrict the crawl to the configured prefixes.</summary>
    [Fact]
    public void PathPrefixesRestrictScope()
    {
        var evaluator = Create(new CrawlScopeOptions { PathPrefixes = ["/docs/"] });
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/docs/a").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedOutOfScope, evaluator.Evaluate("https://example.com/blog/a").Disposition);
    }

    /// <summary>Asset domains are internal; their resources obey the fetch toggles.</summary>
    [Fact]
    public void AssetDomainsAreInternal()
    {
        var evaluator = Create(
            new CrawlScopeOptions { AssetDomains = ["cdn.example.net"] },
            new ResourceScopeOptions { FetchScripts = true });
        var verdict = evaluator.Evaluate("https://assets.cdn.example.net/app.js");
        Assert.True(verdict.IsInternalHost);
        Assert.Equal(CrawlScopeDisposition.Enqueue, verdict.Disposition);
        Assert.Equal(CrawlResourceType.Script, verdict.ResourceType);
    }

    /// <summary>Resource fetch toggles default off: resources are skipped, HTML always fetched.</summary>
    [Fact]
    public void ResourceTogglesDefaultOff()
    {
        var evaluator = Create();
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/page.html").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/app.js").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/site.css").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/logo.png").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/font.woff2").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/file.pdf").Disposition);
    }

    /// <summary>Each resource toggle enables exactly its type.</summary>
    [Fact]
    public void ResourceTogglesEnableTheirType()
    {
        var evaluator = Create(resources: new ResourceScopeOptions { FetchImages = true, FetchFonts = true });
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/logo.png").Disposition);
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/font.woff2").Disposition);
        Assert.Equal(CrawlScopeDisposition.SkippedResource, evaluator.Evaluate("https://example.com/app.js").Disposition);
    }

    /// <summary>Invalid regex patterns are rejected at construction (§4.3).</summary>
    [Fact]
    public void InvalidRegexIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => Create(new CrawlScopeOptions { IncludePatterns = ["("] }));
        Assert.Throws<ArgumentException>(() => Create(new CrawlScopeOptions { ExcludePatterns = ["[z-a]"] }));
    }

    /// <summary>Catastrophic patterns are time-bounded: no hang, treated as not matching.</summary>
    [Fact]
    public void CatastrophicPatternIsTimeBounded()
    {
        var evaluator = Create(new CrawlScopeOptions
        {
            IncludePatterns = [@"^https://example\.com/(a+)+$"],
            RegexTimeoutMs = 50
        });
        var verdict = evaluator.Evaluate("https://example.com/" + new string('a', 64) + "b");
        Assert.Equal(CrawlScopeDisposition.SkippedOutOfScope, verdict.Disposition);
    }

    /// <summary>Non-http(s) URLs are rejected (network boundary).</summary>
    [Fact]
    public void NonHttpUrlsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create().Evaluate("ftp://example.com/file"));
        Assert.Throws<ArgumentException>(() => Create().Evaluate("/relative/path"));
    }

    /// <summary>The evaluator is thread-safe for concurrent fetch workers.</summary>
    [Fact]
    public void ConcurrentEvaluationIsConsistent()
    {
        var evaluator = Create(new CrawlScopeOptions { ExcludePatterns = ["/private/"] });
        var urls = Enumerable.Range(0, 200)
            .Select(i => i % 2 == 0 ? $"https://example.com/page-{i}" : $"https://example.com/private/{i}")
            .ToArray();
        var results = new CrawlScopeVerdict[urls.Length];
        Parallel.For(0, urls.Length, i => results[i] = evaluator.Evaluate(urls[i]));
        for (var i = 0; i < urls.Length; i++)
        {
            Assert.Equal(
                i % 2 == 0 ? CrawlScopeDisposition.Enqueue : CrawlScopeDisposition.SkippedExcluded,
                results[i].Disposition);
        }
    }

    /// <summary>Content-type classification covers the resource families.</summary>
    [Fact]
    public void ContentTypeClassification()
    {
        Assert.Equal(CrawlResourceType.Html, CrawlResourceClassifier.FromContentType("text/html; charset=utf-8"));
        Assert.Equal(CrawlResourceType.Script, CrawlResourceClassifier.FromContentType("application/javascript"));
        Assert.Equal(CrawlResourceType.Stylesheet, CrawlResourceClassifier.FromContentType("text/css"));
        Assert.Equal(CrawlResourceType.Image, CrawlResourceClassifier.FromContentType("image/png"));
        Assert.Equal(CrawlResourceType.Font, CrawlResourceClassifier.FromContentType("font/woff2"));
        Assert.Equal(CrawlResourceType.Other, CrawlResourceClassifier.FromContentType("application/pdf"));
        Assert.Equal(CrawlResourceType.Other, CrawlResourceClassifier.FromContentType(null));
    }
}
