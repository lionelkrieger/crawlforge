using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Canonicalization;

/// <summary>
/// Configuration parsing for the crawl-scope payload: baseline defaults,
/// full-shape round trip, total fallbacks for invalid values, and hard
/// rejection of invalid regex patterns (§4.3/§10.4).
/// </summary>
public class CrawlScopeConfigParserTests
{
    /// <summary>Absent payload reproduces the baseline exactly.</summary>
    [Fact]
    public void AbsentPayloadIsBaseline()
    {
        foreach (var json in new string?[] { null, "", "   " })
        {
            var result = CrawlScopeConfigParser.Parse(json);
            Assert.Empty(result.Fallbacks);
            Assert.Empty(result.Errors);
            Assert.True(result.Config.CookieJar.Enabled);
            Assert.True(result.Config.Canonicalization.StripCookielessSessionSegments);
            Assert.Equal(QueryParameterPolicy.KeepAll, result.Config.Canonicalization.QueryPolicy);
            Assert.Equal(CrawlScopeMode.HostOnly, result.Config.Scope.Mode);
            Assert.False(result.Config.UserAgentComparison.Enabled);
            Assert.True(result.Config.Directives.CrawlNoindexPages);
        }
    }

    /// <summary>Invalid JSON falls back to baseline with a note (never throws).</summary>
    [Fact]
    public void InvalidJsonFallsBack()
    {
        var result = CrawlScopeConfigParser.Parse("{not json");
        Assert.NotEmpty(result.Fallbacks);
        Assert.Empty(result.Errors);
        Assert.True(result.Config.CookieJar.Enabled);
    }

    /// <summary>The full payload shape maps onto every options record.</summary>
    [Fact]
    public void FullPayloadRoundTrips()
    {
        const string json = """
        {
          "canonicalization": {
            "queryPolicy": "keepWhitelist",
            "queryWhitelist": ["page", "id"],
            "trailingSlash": "strip",
            "stripCookielessSessionSegments": false,
            "additionalSessionParameters": ["sid"]
          },
          "scope": {
            "mode": "includeSubdomains",
            "includePatterns": ["^https://example\\.com/"],
            "excludePatterns": ["/private/"],
            "pathPrefixes": ["/docs/"],
            "assetDomains": ["cdn.example.net"],
            "checkExternalLinks": true
          },
          "directives": {
            "followPageNofollow": true,
            "followLinkNofollow": true,
            "crawlNoindexPages": false,
            "useCanonicalDiscovery": false,
            "useHreflangAndPaginationDiscovery": false,
            "followMetaRefresh": false,
            "followIframes": true
          },
          "resources": { "fetchScripts": true, "fetchStylesheets": true, "fetchImages": true, "fetchFonts": true, "fetchOtherFiles": true },
          "cookieJar": { "enabled": false },
          "userAgent": { "custom": "MyBot/1.0", "simulateGooglebot": true },
          "userAgentComparison": { "enabled": true, "sampleSize": 10 },
          "headers": { "X-Token": "abc" },
          "cookies": { "session": "s1" }
        }
        """;
        var result = CrawlScopeConfigParser.Parse(json);
        Assert.Empty(result.Fallbacks);
        Assert.Empty(result.Errors);

        var config = result.Config;
        Assert.Equal(QueryParameterPolicy.KeepWhitelist, config.Canonicalization.QueryPolicy);
        Assert.Equal(["page", "id"], config.Canonicalization.QueryWhitelist);
        Assert.Equal(TrailingSlashPolicy.Strip, config.Canonicalization.TrailingSlash);
        Assert.False(config.Canonicalization.StripCookielessSessionSegments);
        Assert.Equal(["sid"], config.Canonicalization.AdditionalSessionParameters);

        Assert.Equal(CrawlScopeMode.IncludeSubdomains, config.Scope.Mode);
        Assert.Single(config.Scope.IncludePatterns);
        Assert.Equal(["/private/"], config.Scope.ExcludePatterns);
        Assert.Equal(["/docs/"], config.Scope.PathPrefixes);
        Assert.Equal(["cdn.example.net"], config.Scope.AssetDomains);
        Assert.True(config.Scope.CheckExternalLinks);

        Assert.True(config.Directives.FollowPageNofollow);
        Assert.True(config.Directives.FollowLinkNofollow);
        Assert.False(config.Directives.CrawlNoindexPages);
        Assert.False(config.Directives.UseCanonicalDiscovery);
        Assert.False(config.Directives.UseHreflangAndPaginationDiscovery);
        Assert.False(config.Directives.FollowMetaRefresh);
        Assert.True(config.Directives.FollowIframes);

        Assert.True(config.Resources.FetchScripts);
        Assert.True(config.Resources.FetchOtherFiles);
        Assert.False(config.CookieJar.Enabled);
        Assert.Equal("MyBot/1.0", config.UserAgent.Resolve());
        Assert.True(config.UserAgentComparison.Enabled);
        Assert.Equal(10, config.UserAgentComparison.SampleSize);
        Assert.Equal("abc", config.Headers["X-Token"]);
        Assert.Equal("s1", config.Cookies["session"]);
    }

    /// <summary>Invalid regex patterns are hard errors, not silent defaults.</summary>
    [Fact]
    public void InvalidRegexIsHardError()
    {
        var result = CrawlScopeConfigParser.Parse("""{"scope":{"excludePatterns":["("]}}""");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("excludePatterns"));
    }

    /// <summary>Invalid enum strings fall back with notes.</summary>
    [Fact]
    public void InvalidEnumsFallBack()
    {
        var result = CrawlScopeConfigParser.Parse("""
        {"canonicalization":{"queryPolicy":"bogus","trailingSlash":"sideways"},"scope":{"mode":"everywhere"}}
        """);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Fallbacks.Count);
        Assert.Equal(QueryParameterPolicy.KeepAll, result.Config.Canonicalization.QueryPolicy);
        Assert.Equal(TrailingSlashPolicy.Preserve, result.Config.Canonicalization.TrailingSlash);
        Assert.Equal(CrawlScopeMode.HostOnly, result.Config.Scope.Mode);
    }

    /// <summary>Non-boolean toggles fall back to the specified defaults.</summary>
    [Fact]
    public void NonBooleanTogglesFallBack()
    {
        var result = CrawlScopeConfigParser.Parse("""{"cookieJar":{"enabled":"yes"},"directives":{"followIframes":1}}""");
        Assert.Equal(2, result.Fallbacks.Count);
        Assert.True(result.Config.CookieJar.Enabled);
        Assert.False(result.Config.Directives.FollowIframes);
    }

    /// <summary>Invalid comparison sample sizes are hard errors; null means all.</summary>
    [Fact]
    public void ComparisonSampleSizeValidation()
    {
        var invalid = CrawlScopeConfigParser.Parse("""{"userAgentComparison":{"enabled":true,"sampleSize":-3}}""");
        Assert.NotEmpty(invalid.Errors);

        var all = CrawlScopeConfigParser.Parse("""{"userAgentComparison":{"enabled":true,"sampleSize":null}}""");
        Assert.Empty(all.Errors);
        Assert.Null(all.Config.UserAgentComparison.SampleSize);
    }

    /// <summary>The parsed scope options drive the evaluator end to end.</summary>
    [Fact]
    public void ParsedOptionsDriveEvaluator()
    {
        var result = CrawlScopeConfigParser.Parse("""
        {"scope":{"excludePatterns":["/private/"]},"resources":{"fetchImages":true}}
        """);
        Assert.Empty(result.Errors);
        var evaluator = new CrawlScopeEvaluator(result.Config.Scope, ["example.com"], result.Config.Resources);
        Assert.Equal(CrawlScopeDisposition.SkippedExcluded, evaluator.Evaluate("https://example.com/private/x").Disposition);
        Assert.Equal(CrawlScopeDisposition.Enqueue, evaluator.Evaluate("https://example.com/logo.png").Disposition);
    }
}
