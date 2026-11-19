using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Canonicalization;

/// <summary>
/// Cookieless session-segment removal (FS-CRAWL-001 §5.2, CR-009; fixture
/// trap cookieless-session) and the config-driven canonicalization options
/// (G27): query policy, trailing-slash policy, session-segment toggle.
/// </summary>
public class UrlCanonicalizerSessionSegmentTests
{
    /// <summary>The fixture trap URL canonicalizes to /pages/session.aspx by default.</summary>
    [Fact]
    public void FixtureTrapSessionSegmentIsRemovedByDefault()
    {
        var canonicalizer = new UrlCanonicalizer();
        var canonical = canonicalizer.Canonicalize("http://127.0.0.1:47314/(S(abc123xyz))/pages/session.aspx");
        Assert.Equal("http://127.0.0.1:47314/pages/session.aspx", canonical);
    }

    /// <summary>All three cookieless segment kinds are removed (S, A, F).</summary>
    /// <param name="segment">The session segment kind.</param>
    [Theory]
    [InlineData("S")]
    [InlineData("A")]
    [InlineData("F")]
    public void AllSessionSegmentKindsAreRemoved(string segment)
    {
        var canonicalizer = new UrlCanonicalizer();
        var canonical = canonicalizer.Canonicalize($"https://example.com/({segment}(token123))/pages/session.aspx");
        Assert.Equal("https://example.com/pages/session.aspx", canonical);
    }

    /// <summary>Mid-path and repeated segments are removed, collapsing slashes.</summary>
    [Fact]
    public void MidPathAndRepeatedSegmentsAreRemoved()
    {
        var canonicalizer = new UrlCanonicalizer();
        Assert.Equal(
            "https://example.com/a/b",
            canonicalizer.Canonicalize("https://example.com/a/(S(x))/b"));
        Assert.Equal(
            "https://example.com/pages/session.aspx",
            canonicalizer.Canonicalize("https://example.com/(S(a))/(A(b))/(F(c))/pages/session.aspx"));
    }

    /// <summary>A root-only session segment collapses to the root path.</summary>
    [Fact]
    public void RootOnlySegmentCollapsesToRoot()
    {
        var canonicalizer = new UrlCanonicalizer();
        Assert.Equal("https://example.com/", canonicalizer.Canonicalize("https://example.com/(S(x))"));
    }

    /// <summary>Percent-encoded segment forms are removed too.</summary>
    [Fact]
    public void PercentEncodedSegmentsAreRemoved()
    {
        var canonicalizer = new UrlCanonicalizer();
        Assert.Equal(
            "https://example.com/pages/session.aspx",
            canonicalizer.Canonicalize("https://example.com/%28S%28abc123xyz%29%29/pages/session.aspx"));
    }

    /// <summary>The option off retains the segments verbatim (config-driven).</summary>
    [Fact]
    public void OptionOffRetainsSegments()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { StripCookielessSessionSegments = false });
        Assert.Equal(
            "https://example.com/(S(abc123xyz))/pages/session.aspx",
            canonicalizer.Canonicalize("https://example.com/(S(abc123xyz))/pages/session.aspx"));
    }

    /// <summary>Ordinary parenthesized path segments are NOT session segments and stay.</summary>
    [Fact]
    public void OrdinaryParenthesesArePreserved()
    {
        var canonicalizer = new UrlCanonicalizer();
        Assert.Equal(
            "https://example.com/(ordinary)/page",
            canonicalizer.Canonicalize("https://example.com/(ordinary)/page"));
        Assert.Equal(
            "https://example.com/(X(token))/page",
            canonicalizer.Canonicalize("https://example.com/(X(token))/page"));
    }

    /// <summary>Canonicalization stays idempotent with segment removal on.</summary>
    [Fact]
    public void CanonicalizationIsIdempotent()
    {
        var canonicalizer = new UrlCanonicalizer();
        var once = canonicalizer.Canonicalize("https://example.com/(S(x))/pages/session.aspx?utm_source=a&page=2#frag");
        Assert.Equal(once, canonicalizer.Canonicalize(once));
    }

    /// <summary>The detailed outcome reports the removed segments (trap: "and reported").</summary>
    [Fact]
    public void DetailedOutcomeReportsRemovedSegments()
    {
        var canonicalizer = new UrlCanonicalizer();
        var outcome = canonicalizer.CanonicalizeDetailed("https://example.com/(S(abc123xyz))/pages/session.aspx");
        Assert.Equal("https://example.com/pages/session.aspx", outcome.CanonicalUrl);
        Assert.Equal(["(S(abc123xyz))"], outcome.RemovedSessionSegments);
    }

    /// <summary>The finding factory emits the Notice with evidence; null when nothing removed.</summary>
    [Fact]
    public void SessionSegmentFindingIsEmittedWithEvidence()
    {
        var canonicalizer = new UrlCanonicalizer();
        var outcome = canonicalizer.CanonicalizeDetailed("https://example.com/(S(abc123xyz))/pages/session.aspx");
        var finding = CanonicalizationFindingFactory.CreateSessionSegmentFinding(42, outcome);
        Assert.NotNull(finding);
        Assert.Equal("canonicalization.session-segment-removed", finding!.RuleKey);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Equal(42, finding.UrlId);
        Assert.Contains("(S(abc123xyz))", finding.EvidenceJson);
        Assert.Contains("/pages/session.aspx", finding.EvidenceJson);

        var clean = canonicalizer.CanonicalizeDetailed("https://example.com/pages/session.aspx");
        Assert.Null(CanonicalizationFindingFactory.CreateSessionSegmentFinding(42, clean));
    }

    /// <summary>Query policy keep-all strips bundled session trackers (baseline).</summary>
    [Fact]
    public void KeepAllStripsSessionTrackers()
    {
        var canonicalizer = new UrlCanonicalizer();
        Assert.Equal(
            "https://example.com/page?page=2",
            canonicalizer.Canonicalize("https://example.com/page?utm_source=x&page=2&fbclid=y"));
    }

    /// <summary>Query policy keep-whitelist keeps only whitelisted parameters (G27 option honored).</summary>
    [Fact]
    public void KeepWhitelistHonorsWhitelist()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions
        {
            QueryPolicy = QueryParameterPolicy.KeepWhitelist,
            QueryWhitelist = ["page"]
        });
        Assert.Equal(
            "https://example.com/page?page=2",
            canonicalizer.Canonicalize("https://example.com/page?sort=asc&page=2&ref=home"));
    }

    /// <summary>Query policy strip-all removes every parameter (G27 option honored).</summary>
    [Fact]
    public void StripAllRemovesQuery()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { QueryPolicy = QueryParameterPolicy.StripAll });
        Assert.Equal("https://example.com/page", canonicalizer.Canonicalize("https://example.com/page?page=2&sort=asc"));
    }

    /// <summary>Trailing-slash append/strip policies are applied (G27 option honored).</summary>
    [Fact]
    public void TrailingSlashPoliciesAreApplied()
    {
        var append = new UrlCanonicalizer(new CanonicalizationOptions { TrailingSlash = TrailingSlashPolicy.Append });
        Assert.Equal("https://example.com/dir/", append.Canonicalize("https://example.com/dir"));
        Assert.Equal("https://example.com/", append.Canonicalize("https://example.com"));

        var strip = new UrlCanonicalizer(new CanonicalizationOptions { TrailingSlash = TrailingSlashPolicy.Strip });
        Assert.Equal("https://example.com/dir", strip.Canonicalize("https://example.com/dir/"));
        Assert.Equal("https://example.com/", strip.Canonicalize("https://example.com/"));
    }

    /// <summary>Session removal composes with the trailing-slash policy.</summary>
    [Fact]
    public void SessionRemovalComposesWithTrailingSlashPolicy()
    {
        var canonicalizer = new UrlCanonicalizer(new CanonicalizationOptions { TrailingSlash = TrailingSlashPolicy.Strip });
        Assert.Equal(
            "https://example.com/pages/session.aspx",
            canonicalizer.Canonicalize("https://example.com/(S(x))/pages/session.aspx/"));
    }
}
