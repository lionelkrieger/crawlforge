using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Scope-aware on-the-fly rewrite rules (FS-CRAWL-002 §10.4): seeds-only vs
/// all-discovered scope, deterministic ordered application, exact per-rule
/// counters, scheme invariant, configuration-time validation, and the §10.7
/// live rewrite preview. Purely local — no endpoint is contacted.
/// </summary>
public class ScopedRewritePolicyTests
{
    private static UrlRewriteRule Rule(string pattern, string replacement, bool enabled = true) =>
        new(pattern, replacement, enabled);

    [Fact]
    public void SeedsOnlyRule_AppliesToSeeds_NotDiscovered()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("^https://staging\\.", "https://www."), RewriteRuleScope.SeedsOnly),
        ]);
        var engine = new TestRewriteEngine();

        Assert.Equal("https://www.example.com/a", policy.ApplyToSeed("https://staging.example.com/a", engine));
        Assert.Equal("https://staging.example.com/a", policy.ApplyToDiscovered("https://staging.example.com/a", engine));
    }

    [Fact]
    public void AllDiscoveredRule_AppliesToBoth()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("\\?utm_[^&]*", string.Empty), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        Assert.Equal("https://h.example/a", policy.ApplyToSeed("https://h.example/a?utm_source=x", engine));
        Assert.Equal("https://h.example/a", policy.ApplyToDiscovered("https://h.example/a?utm_source=x", engine));
    }

    [Fact]
    public void RulesApply_InOrder_WithExactCounters()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("^https://old\\.", "https://new."), RewriteRuleScope.AllDiscovered),
            new ScopedRewriteRule(Rule("/legacy/", "/modern/"), RewriteRuleScope.AllDiscovered),
            new ScopedRewriteRule(Rule("^https://never\\.", "https://x."), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        var result = policy.ApplyToDiscovered("https://old.example/legacy/page", engine);

        Assert.Equal("https://new.example/modern/page", result);
        var counters = policy.Counters();
        Assert.Equal(3, counters.Count);
        Assert.Equal(1, counters[0].RewriteCount);
        Assert.Equal(1, counters[1].RewriteCount);
        Assert.Equal(0, counters[2].RewriteCount);
    }

    [Fact]
    public void SchemeInvariant_NonHttpResult_IsRejected()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("^https://", "ftp://"), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        Assert.Equal("https://h.example/a", policy.ApplyToDiscovered("https://h.example/a", engine));
        Assert.Equal(0, policy.Counters()[0].RewriteCount);
    }

    [Fact]
    public void InvalidPattern_IsRejectedAtConfigurationTime()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("(unclosed", "x"), RewriteRuleScope.AllDiscovered),
        ]));
        Assert.Contains("rule 0", ex.Message, StringComparison.Ordinal);

        // Disabled rules are validated too — enabling later cannot introduce a bad pattern.
        Assert.Throws<ArgumentException>(() => new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("(unclosed", "x", enabled: false), RewriteRuleScope.SeedsOnly),
        ]));
    }

    [Fact]
    public void DisabledRule_IsSkipped()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("^https://a\\.", "https://b.", enabled: false), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        Assert.Equal("https://a.example/x", policy.ApplyToSeed("https://a.example/x", engine));
        Assert.Equal(0, policy.Counters()[0].RewriteCount);
    }

    [Fact]
    public void Preview_ShowsPerRuleBeforeAfter_IncludingSkippedSteps()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("^https://staging\\.", "https://www."), RewriteRuleScope.SeedsOnly),
            new ScopedRewriteRule(Rule("/old/", "/new/"), RewriteRuleScope.AllDiscovered),
            new ScopedRewriteRule(Rule("^https://never\\.", "https://x."), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        var seedPreview = policy.Preview("https://staging.example/old/page", engine, isSeed: true);
        Assert.Equal("https://www.example/new/page", seedPreview.OutputUrl);
        Assert.Equal(3, seedPreview.Steps.Count);
        Assert.True(seedPreview.Steps[0].Changed);
        Assert.Equal("https://staging.example/old/page", seedPreview.Steps[0].Before);
        Assert.Equal("https://www.example/old/page", seedPreview.Steps[0].After);
        Assert.True(seedPreview.Steps[1].Changed);
        Assert.False(seedPreview.Steps[2].Changed);

        var discoveredPreview = policy.Preview("https://staging.example/old/page", engine, isSeed: false);
        Assert.Equal("https://staging.example/new/page", discoveredPreview.OutputUrl);
        Assert.False(discoveredPreview.Steps[0].Changed); // seeds-only rule skipped
        Assert.True(discoveredPreview.Steps[1].Changed);
    }

    [Fact]
    public void Preview_DoesNotMutateCounters()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("/a", "/b"), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        policy.Preview("https://h.example/a", engine, isSeed: true);

        Assert.Equal(0, policy.Counters()[0].RewriteCount);
    }

    [Fact]
    public void FromPlainRules_DefaultsToSeedsOnly()
    {
        var scoped = ScopedRewritePolicy.FromPlainRules([Rule("/a", "/b")]);
        Assert.Equal(RewriteRuleScope.SeedsOnly, Assert.Single(scoped).Scope);

        var all = ScopedRewritePolicy.FromPlainRules([Rule("/a", "/b")], RewriteRuleScope.AllDiscovered);
        Assert.Equal(RewriteRuleScope.AllDiscovered, Assert.Single(all).Scope);
    }

    [Fact]
    public void Counters_AreThreadSafe()
    {
        var policy = new ScopedRewritePolicy([
            new ScopedRewriteRule(Rule("/a", "/b"), RewriteRuleScope.AllDiscovered),
        ]);
        var engine = new TestRewriteEngine();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                policy.ApplyToDiscovered("https://h.example/a", engine);
            }
        });

        Assert.Equal(1_600, policy.Counters()[0].RewriteCount);
    }
}
