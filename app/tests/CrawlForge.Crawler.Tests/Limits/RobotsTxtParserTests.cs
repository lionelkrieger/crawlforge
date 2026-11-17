using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Robots parsing and evaluation (FS-CRAWL-001 §4.4): groups, rules,
/// longest-match precedence, wildcards, crawl-delay, sitemaps, and tolerant
/// handling of malformed input.
/// </summary>
public sealed class RobotsTxtParserTests
{
    private readonly RobotsTxtParser _parser = new();

    [Fact]
    public void ParsesGroupsRulesCrawlDelayAndSitemaps()
    {
        const string body = """
            # comment line
            User-agent: CrawlForge
            Disallow: /private
            Allow: /private/public
            Crawl-delay: 2.5

            User-agent: *
            Disallow: /tmp

            Sitemap: https://example.com/sitemap.xml
            """;

        var document = _parser.Parse(body);

        Assert.Equal(2, document.Groups.Count);
        Assert.Single(document.Sitemaps);
        Assert.Equal("https://example.com/sitemap.xml", document.Sitemaps[0]);
        Assert.Equal(2.5, document.Groups[0].CrawlDelaySeconds);
        Assert.Equal(2, document.Groups[0].Rules.Count);
    }

    [Fact]
    public void EmptyBodyAllowsEverything()
    {
        var document = _parser.Parse(string.Empty);

        var verdict = _parser.Evaluate(document, "https://example.com/anything", "CrawlForge");

        Assert.True(verdict.Allowed);
        Assert.Null(verdict.MatchedRule);
        Assert.Empty(document.Groups);
    }

    [Fact]
    public void DisallowRootBlocksEverything()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /");

        var verdict = _parser.Evaluate(document, "https://example.com/page", "CrawlForge");

        Assert.False(verdict.Allowed);
        Assert.NotNull(verdict.MatchedRule);
    }

    [Fact]
    public void EmptyDisallowImposesNoRestriction()
    {
        var document = _parser.Parse("User-agent: *\nDisallow:");

        var verdict = _parser.Evaluate(document, "https://example.com/page", "CrawlForge");

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void LongestPatternWins()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /a\nAllow: /a/b");

        var allowed = _parser.Evaluate(document, "https://example.com/a/b/c", "CrawlForge");
        var blocked = _parser.Evaluate(document, "https://example.com/a/c", "CrawlForge");

        Assert.True(allowed.Allowed);
        Assert.False(blocked.Allowed);
    }

    [Fact]
    public void AllowBreaksLengthTies()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /x\nAllow: /x");

        var verdict = _parser.Evaluate(document, "https://example.com/x", "CrawlForge");

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void WildcardPatternMatches()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /private/*.pdf");

        var blocked = _parser.Evaluate(document, "https://example.com/private/doc.pdf", "CrawlForge");
        var allowed = _parser.Evaluate(document, "https://example.com/private/doc.html", "CrawlForge");

        Assert.False(blocked.Allowed);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void EndAnchorRequiresExactEnd()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /exact$");

        var blocked = _parser.Evaluate(document, "https://example.com/exact", "CrawlForge");
        var allowed = _parser.Evaluate(document, "https://example.com/exact/child", "CrawlForge");

        Assert.False(blocked.Allowed);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void SpecificUserAgentGroupBeatsWildcard()
    {
        const string body = """
            User-agent: *
            Disallow: /

            User-agent: CrawlForge
            Disallow: /secret
            """;
        var document = _parser.Parse(body);

        var allowed = _parser.Evaluate(document, "https://example.com/page", "CrawlForge");
        var blocked = _parser.Evaluate(document, "https://example.com/secret", "CrawlForge");
        var otherBlocked = _parser.Evaluate(document, "https://example.com/page", "OtherBot");

        Assert.True(allowed.Allowed);
        Assert.False(blocked.Allowed);
        Assert.False(otherBlocked.Allowed);
    }

    [Fact]
    public void UserAgentMatchingIsCaseInsensitive()
    {
        var document = _parser.Parse("User-agent: crawlforge\nDisallow: /no");

        var verdict = _parser.Evaluate(document, "https://example.com/no", "CrawlForge/1.0");

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void MalformedLinesAreIgnoredTolerantly()
    {
        const string body = """
            this line has no colon
            : empty field
            User-agent: *
            Unknown-Field: value
            Disallow: /ok
            Crawl-delay: not-a-number
            """;

        var document = _parser.Parse(body);

        var verdict = _parser.Evaluate(document, "https://example.com/ok", "CrawlForge");
        Assert.False(verdict.Allowed);
        Assert.Null(document.Groups[0].CrawlDelaySeconds);
    }

    [Fact]
    public void RulesBeforeAnyUserAgentApplyToWildcardGroup()
    {
        var document = _parser.Parse("Disallow: /early");

        var verdict = _parser.Evaluate(document, "https://example.com/early", "CrawlForge");

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void UserAgentAfterRulesStartsANewGroup()
    {
        const string body = """
            User-agent: A
            Disallow: /a
            User-agent: B
            Disallow: /b
            """;
        var document = _parser.Parse(body);

        Assert.Equal(2, document.Groups.Count);
        Assert.False(_parser.Evaluate(document, "https://example.com/a", "A").Allowed);
        Assert.True(_parser.Evaluate(document, "https://example.com/b", "A").Allowed);
        Assert.False(_parser.Evaluate(document, "https://example.com/b", "B").Allowed);
    }

    [Fact]
    public void QueryStringIsPartOfTheMatchedPath()
    {
        var document = _parser.Parse("User-agent: *\nDisallow: /search?q=blocked");

        var blocked = _parser.Evaluate(document, "https://example.com/search?q=blocked", "CrawlForge");
        var allowed = _parser.Evaluate(document, "https://example.com/search?q=ok", "CrawlForge");

        Assert.False(blocked.Allowed);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void SelectGroupReturnsNullWhenNoGroupMatches()
    {
        var document = _parser.Parse("User-agent: OtherBot\nDisallow: /");

        var group = RobotsTxtParser.SelectGroup(document, "CrawlForge");

        Assert.Null(group);
    }
}
