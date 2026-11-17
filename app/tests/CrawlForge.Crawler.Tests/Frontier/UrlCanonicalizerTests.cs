using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-001 §5.2 canonicalization: normalization rules, query policies,
/// relative resolution with base overrides, rewrite integration, idempotence.
/// </summary>
public class UrlCanonicalizerTests
{
    private readonly UrlCanonicalizer _canonicalizer = new();

    [Theory]
    [InlineData("HTTP://Example.COM/Path", "http://example.com/Path")]
    [InlineData("https://EXAMPLE.com/", "https://example.com/")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("https://example.com/page#section", "https://example.com/page")]
    [InlineData("https://example.com", "https://example.com/")]
    public void NormalizesSchemeHostPortAndFragment(string input, string expected)
    {
        Assert.Equal(expected, _canonicalizer.Canonicalize(input));
    }

    [Fact]
    public void NormalizesPercentEncoding()
    {
        // %7E is '~' (unreserved → decoded); %2f stays escaped but uppercased.
        Assert.Equal("https://example.com/~user/%2F", _canonicalizer.Canonicalize("https://example.com/%7Euser/%2f"));
    }

    [Fact]
    public void SortsQueryParameters()
    {
        Assert.Equal("https://example.com/?a=1&b=2&c=3", _canonicalizer.Canonicalize("https://example.com/?c=3&a=1&b=2"));
    }

    [Fact]
    public void StripsKnownSessionTrackingParameters()
    {
        Assert.Equal(
            "https://example.com/?id=5",
            _canonicalizer.Canonicalize("https://example.com/?utm_source=news&id=5&fbclid=abc&utm_medium=email"));
    }

    [Fact]
    public void StripsAdditionalSessionParameters()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions
        {
            AdditionalSessionParameters = ["custom_track"]
        });
        Assert.Equal("https://example.com/?id=5", canonicalizer.Canonicalize("https://example.com/?custom_track=x&id=5"));
    }

    [Fact]
    public void KeepWhitelistPolicyRetainsOnlyWhitelistedParameters()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions
        {
            QueryPolicy = QueryParameterPolicy.KeepWhitelist,
            QueryWhitelist = ["id", "page"]
        });
        Assert.Equal("https://example.com/?id=5&page=2", canonicalizer.Canonicalize("https://example.com/?page=2&sort=asc&id=5"));
    }

    [Fact]
    public void StripAllPolicyRemovesEveryParameter()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { QueryPolicy = QueryParameterPolicy.StripAll });
        Assert.Equal("https://example.com/path", canonicalizer.Canonicalize("https://example.com/path?a=1&b=2"));
    }

    [Theory]
    [InlineData(TrailingSlashPolicy.Append, "https://example.com/page", "https://example.com/page/")]
    [InlineData(TrailingSlashPolicy.Strip, "https://example.com/page/", "https://example.com/page")]
    [InlineData(TrailingSlashPolicy.Preserve, "https://example.com/page/", "https://example.com/page/")]
    [InlineData(TrailingSlashPolicy.Append, "https://example.com/", "https://example.com/")]
    [InlineData(TrailingSlashPolicy.Strip, "https://example.com/", "https://example.com/")]
    public void AppliesTrailingSlashPolicyConsistently(TrailingSlashPolicy policy, string input, string expected)
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { TrailingSlash = policy });
        Assert.Equal(expected, canonicalizer.Canonicalize(input));
    }

    [Fact]
    public void ResolvesRelativeReferencesAgainstDocumentBase()
    {
        Assert.Equal(
            "https://example.com/docs/page",
            _canonicalizer.Canonicalize("page", "https://example.com/docs/index.html"));
        Assert.Equal(
            "https://example.com/root",
            _canonicalizer.Canonicalize("/root", "https://example.com/docs/index.html"));
    }

    [Fact]
    public void ExplicitBaseOverridesConfiguredDefault()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { DefaultBase = "https://default.example.com/" });
        Assert.Equal(
            "https://override.example.com/page",
            canonicalizer.Canonicalize("/page", "https://override.example.com/base/"));
        Assert.Equal("https://default.example.com/page", canonicalizer.Canonicalize("/page"));
    }

    [Fact]
    public void RelativeUrlWithoutAnyBaseIsRejected()
    {
        Assert.Throws<ArgumentException>(() => _canonicalizer.Canonicalize("/page"));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsNonHttpSchemesAndEmptyInput(string input)
    {
        Assert.Throws<ArgumentException>(() => _canonicalizer.Canonicalize(input));
    }

    [Fact]
    public void AppliesRewriteRulesDuringCanonicalizationBeforeKeying()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions
        {
            RewriteRules = [new UrlRewriteRule(@"^https?://old\.example\.com", "https://new.example.com")]
        });
        Assert.Equal("https://new.example.com/page", canonicalizer.Canonicalize("http://old.example.com/page"));
    }

    [Theory]
    [InlineData("https://example.com/a?b=2&a=1#frag")]
    [InlineData("http://example.com:80/%7Euser/../x")]
    [InlineData("https://example.com/path?utm_source=x&id=7")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/p/%2f?q=%7E")]
    public void CanonicalizationIsIdempotent(string input)
    {
        var once = _canonicalizer.Canonicalize(input);
        Assert.Equal(once, _canonicalizer.Canonicalize(once));
    }

    [Fact]
    public void DuplicateQueryNamesAreSortedStably()
    {
        Assert.Equal(
            "https://example.com/?a=1&a=2&b=3",
            _canonicalizer.Canonicalize("https://example.com/?b=3&a=2&a=1"));
    }
}
