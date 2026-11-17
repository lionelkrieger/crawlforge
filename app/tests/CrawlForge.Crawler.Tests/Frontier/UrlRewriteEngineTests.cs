using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-002 §10.4 on-the-fly regex rewriting: deterministic ordered
/// application, configuration-time validation, bounded evaluation, scheme guard,
/// per-rule diagnostics, and the rewrite-preview contract.
/// </summary>
public class UrlRewriteEngineTests
{
    private readonly UrlRewriteEngine _engine = new();

    [Fact]
    public void AppliesRulesInOrderDeterministically()
    {
        var rules = new[]
        {
            new UrlRewriteRule("/a", "/b"),
            new UrlRewriteRule("/b", "/c")
        };
        Assert.Equal("https://example.com/c", _engine.Rewrite("https://example.com/a", rules));
        Assert.Equal("https://example.com/c", _engine.Rewrite("https://example.com/a", rules));
    }

    [Fact]
    public void SupportsCaptureGroupReplacements()
    {
        var rules = new[] { new UrlRewriteRule(@"/item/(\d+)", "/product/$1") };
        Assert.Equal("https://example.com/product/42", _engine.Rewrite("https://example.com/item/42", rules));
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        var rules = new[]
        {
            new UrlRewriteRule("example", "changed", Enabled: false),
            new UrlRewriteRule("https", "http")
        };
        Assert.Equal("http://example.com/", _engine.Rewrite("https://example.com/", rules));
    }

    [Fact]
    public void InvalidPatternIsRejectedAtConfiguration()
    {
        var rules = new[] { new UrlRewriteRule("(unclosed", "x") };
        Assert.Throws<ArgumentException>(() => _engine.ValidateRules(rules));
        Assert.Throws<ArgumentException>(() => _engine.Rewrite("https://example.com/", rules));
    }

    [Fact]
    public void CatastrophicPatternIsTimeBounded()
    {
        var engine = new UrlRewriteEngine(TimeSpan.FromMilliseconds(50));
        var rules = new[] { new UrlRewriteRule("(a+)+$", "x") };
        Assert.Throws<TimeoutException>(() => engine.Rewrite("https://example.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!", rules));
    }

    [Fact]
    public void RewriteProducingNonHttpSchemeIsRejected()
    {
        var rules = new[] { new UrlRewriteRule("^https://example\\.com", "ftp://example.com") };
        Assert.Throws<InvalidOperationException>(() => _engine.Rewrite("https://example.com/page", rules));
    }

    [Fact]
    public void RelativeRewriteResultIsAllowed()
    {
        var rules = new[] { new UrlRewriteRule(@"^https://example\.com(/.*)$", "$1") };
        Assert.Equal("/page", _engine.Rewrite("https://example.com/page", rules));
    }

    [Fact]
    public void ReportsPerRuleMatchCounters()
    {
        var rules = new[]
        {
            new UrlRewriteRule("/a", "/b"),
            new UrlRewriteRule("zzz", "q")
        };
        var result = _engine.RewriteWithDiagnostics("https://example.com/a", rules);
        Assert.Equal("https://example.com/b", result.Url);
        Assert.Equal(1, result.RuleMatchCounts["/a"]);
        Assert.False(result.RuleMatchCounts.ContainsKey("zzz"));
    }

    [Fact]
    public void PreviewShowsBeforeAfterPerRule()
    {
        var rules = new[]
        {
            new UrlRewriteRule("old", "new"),
            new UrlRewriteRule("nomatch", "x"),
            new UrlRewriteRule("skip", "y", Enabled: false)
        };
        var preview = _engine.Preview(["https://old.example.com/"], rules);
        var item = Assert.Single(preview);
        Assert.Equal("https://old.example.com/", item.Original);
        Assert.Equal("https://new.example.com/", item.Final);
        Assert.Equal(2, item.Steps.Count); // disabled rule excluded
        Assert.True(item.Steps[0].Matched);
        Assert.Equal("https://old.example.com/", item.Steps[0].Before);
        Assert.Equal("https://new.example.com/", item.Steps[0].After);
        Assert.False(item.Steps[1].Matched);
    }

    [Fact]
    public void ScopedRulesApplyOnlyToSeedsWhenSeedsOnly()
    {
        var rules = new[]
        {
            new ScopedUrlRewriteRule(new UrlRewriteRule("seedmark", "rewritten"), RewriteScope.SeedsOnly),
            new ScopedUrlRewriteRule(new UrlRewriteRule("all", "global"))
        };
        Assert.Equal("https://example.com/rewritten-global", _engine.Rewrite("https://example.com/seedmark-all", rules, isSeed: true));
        Assert.Equal("https://example.com/seedmark-global", _engine.Rewrite("https://example.com/seedmark-all", rules, isSeed: false));
    }

    [Fact]
    public void EmptyRuleSetReturnsInputUnchanged()
    {
        Assert.Equal("https://example.com/x", _engine.Rewrite("https://example.com/x", []));
    }
}
