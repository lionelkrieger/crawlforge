using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Canonicalization;

/// <summary>
/// Configurable user agents incl. Googlebot simulation (§4.4/§5.1), custom
/// request headers (§5.1), and the optional user-agent comparison (§5.15):
/// deterministic sampling, material-difference detection, finding emission.
/// </summary>
public class UserAgentTests
{
    /// <summary>The default identity is the identifiable crawler UA.</summary>
    [Fact]
    public void DefaultIdentityIsCrawlForge()
    {
        Assert.Equal(UserAgentProfiles.CrawlForgeDefault, new UserAgentOptions().Resolve());
        Assert.Contains("CrawlForge/", UserAgentProfiles.CrawlForgeDefault);
    }

    /// <summary>Googlebot simulation resolves the documented smartphone identity.</summary>
    [Fact]
    public void GooglebotSimulationResolvesSmartphoneIdentity()
    {
        var resolved = new UserAgentOptions { SimulateGooglebot = true }.Resolve();
        Assert.Equal(UserAgentProfiles.GooglebotSmartphone, resolved);
        Assert.Contains("Googlebot/2.1", resolved);
        Assert.Contains("Mobile", resolved);
    }

    /// <summary>A custom user-agent wins over simulation.</summary>
    [Fact]
    public void CustomUserAgentWins()
    {
        var resolved = new UserAgentOptions { CustomUserAgent = "MyBot/1.0", SimulateGooglebot = true }.Resolve();
        Assert.Equal("MyBot/1.0", resolved);
    }

    /// <summary>The request profile applies UA, custom headers and user cookies.</summary>
    [Fact]
    public void RequestProfileAppliesHeadersAndCookies()
    {
        var profile = CrawlRequestProfile.Create(
            new UserAgentOptions { SimulateGooglebot = true },
            new Dictionary<string, string> { ["X-Token"] = "abc" },
            new Dictionary<string, string> { ["session"] = "s1" });
        var request = profile.Apply(new HttpFetchRequest("https://example.com/page"));
        Assert.Equal(UserAgentProfiles.GooglebotSmartphone, request.Headers!["User-Agent"]);
        Assert.Equal("abc", request.Headers["X-Token"]);
        Assert.Equal("s1", request.Cookies!["session"]);
    }

    /// <summary>Explicit request values win over profile defaults; user UA header wins.</summary>
    [Fact]
    public void ExplicitRequestValuesWin()
    {
        var profile = CrawlRequestProfile.Create(
            customHeaders: new Dictionary<string, string> { ["X-Token"] = "profile", ["User-Agent"] = "HeaderBot/2.0" },
            userCookies: new Dictionary<string, string> { ["session"] = "profile" });
        var request = profile.Apply(new HttpFetchRequest(
            "https://example.com/page",
            Headers: new Dictionary<string, string> { ["X-Token"] = "request" },
            Cookies: new Dictionary<string, string> { ["session"] = "request" }));
        Assert.Equal("request", request.Headers!["X-Token"]);
        Assert.Equal("HeaderBot/2.0", request.Headers["User-Agent"]);
        Assert.Equal("request", request.Cookies!["session"]);
    }

    /// <summary>Comparison sampling is deterministic and bounded; disabled yields empty.</summary>
    [Fact]
    public void ComparisonSamplingIsDeterministic()
    {
        var urls = new[] { "https://example.com/c", "https://example.com/a", "https://example.com/b", "https://example.com/a" };
        var options = new UserAgentComparisonOptions { Enabled = true, SampleSize = 2 };
        var sample = UserAgentComparisonEvaluator.SelectSample(urls, options);
        Assert.Equal(["https://example.com/a", "https://example.com/b"], sample);
        Assert.Equal(sample, UserAgentComparisonEvaluator.SelectSample(urls.Reverse(), options));

        Assert.Empty(UserAgentComparisonEvaluator.SelectSample(urls, new UserAgentComparisonOptions()));
        Assert.Equal(3, UserAgentComparisonEvaluator.SelectSample(urls, new UserAgentComparisonOptions { Enabled = true, SampleSize = null }).Count);
    }

    /// <summary>Invalid sample sizes are rejected (§4.3).</summary>
    [Fact]
    public void InvalidSampleSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UserAgentComparisonEvaluator.SelectSample([], new UserAgentComparisonOptions { Enabled = true, SampleSize = 0 }));
    }

    /// <summary>Identical observations produce no material difference.</summary>
    [Fact]
    public void IdenticalObservationsAreNotMaterial()
    {
        var observation = Observation("https://example.com/page");
        var result = UserAgentComparisonEvaluator.Compare(observation, observation with { });
        Assert.False(result.IsMaterial);
        Assert.Empty(result.Differences);
        Assert.Null(UserAgentComparisonFindingFactory.Create(1, result));
    }

    /// <summary>Status, redirect, robots, canonical, content and link differences are material.</summary>
    [Fact]
    public void DifferencesAreDetectedPerField()
    {
        var googlebot = Observation("https://example.com/page");
        var chrome = googlebot with
        {
            StatusCode = 403,
            FinalUrl = "https://example.com/blocked",
            MetaRobotsDirectives = ["noindex"],
            CanonicalUrl = "https://example.com/other",
            ContentHash = "different-hash",
            DiscoveredLinks = ["https://example.com/extra"]
        };
        var result = UserAgentComparisonEvaluator.Compare(googlebot, chrome);
        Assert.True(result.IsMaterial);
        Assert.Equal(
            ["status", "redirect", "meta-robots", "canonical", "content-hash", "links"],
            result.Differences.Select(d => d.Field));
    }

    /// <summary>Link ordering alone is not a difference (sets are compared).</summary>
    [Fact]
    public void LinkOrderingIsNotADifference()
    {
        var googlebot = Observation("https://example.com/page") with { DiscoveredLinks = ["https://example.com/a", "https://example.com/b"] };
        var chrome = googlebot with { DiscoveredLinks = ["https://example.com/b", "https://example.com/a"] };
        Assert.False(UserAgentComparisonEvaluator.Compare(googlebot, chrome).IsMaterial);
    }

    /// <summary>The comparison finding is a Warning with per-field evidence.</summary>
    [Fact]
    public void ComparisonFindingCarriesEvidence()
    {
        var googlebot = Observation("https://example.com/page");
        var chrome = googlebot with { StatusCode = 403 };
        var result = UserAgentComparisonEvaluator.Compare(googlebot, chrome);
        var finding = UserAgentComparisonFindingFactory.Create(7, result);
        Assert.NotNull(finding);
        Assert.Equal("ua-comparison.material-difference", finding!.RuleKey);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(7, finding.UrlId);
        Assert.Contains("cloaking", finding.EvidenceJson);
        Assert.Contains("403", finding.EvidenceJson);
    }

    /// <summary>Mismatched observation URLs are rejected.</summary>
    [Fact]
    public void MismatchedUrlsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => UserAgentComparisonEvaluator.Compare(
            Observation("https://example.com/a"),
            Observation("https://example.com/b")));
    }

    private static UaFetchObservation Observation(string url) => new(
        url,
        200,
        url,
        [],
        CanonicalUrl: null,
        ContentHash: "stable-hash",
        DiscoveredLinks: []);
}
