using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// FS-CRAWL-002 §10.4/§10.5 evidence: rewrite rules apply deterministically
/// through the locked <see cref="IUrlRewriteEngine"/> contract, invalid
/// patterns are rejected at configuration time, a rewrite never produces a
/// non-http(s) scheme, and staging↔production host mapping aligns URLs by path.
/// </summary>
public class RewriteAndHostMappingTests
{
    /// <summary>Invalid regex patterns are rejected at configuration time.</summary>
    [Fact]
    public void Invalid_Pattern_Rejected_At_Configuration()
    {
        var rules = new[] { new UrlRewriteRule("(unclosed", "replacement") };

        var ex = Assert.Throws<ArgumentException>(() => new RewriteRuleGuard(rules));
        Assert.Contains("rule 0", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Validation reports one diagnostic per rule without throwing.</summary>
    [Fact]
    public void Validate_Reports_Per_Rule_Diagnostics()
    {
        var rules = new[]
        {
            new UrlRewriteRule("/old/", "/new/"),
            new UrlRewriteRule("[bad", "x"),
        };

        var diagnostics = RewriteRuleGuard.Validate(rules);

        Assert.Equal(2, diagnostics.Count);
        Assert.True(diagnostics[0].IsValid);
        Assert.False(diagnostics[1].IsValid);
        Assert.NotNull(diagnostics[1].Reason);
    }

    /// <summary>Rules apply deterministically in order through the engine.</summary>
    [Fact]
    public void Rules_Apply_In_Order()
    {
        var guard = new RewriteRuleGuard([
            new UrlRewriteRule("https://example.com/a", "https://example.com/b"),
            new UrlRewriteRule("https://example.com/b", "https://example.com/c"),
        ]);

        var result = guard.Apply("https://example.com/a", new TestRewriteEngine());

        Assert.Equal("https://example.com/c", result);
        Assert.Equal(1, guard.Counters[0].RewriteCount);
        Assert.Equal(1, guard.Counters[1].RewriteCount);
    }

    /// <summary>Disabled rules never participate.</summary>
    [Fact]
    public void Disabled_Rules_Are_Skipped()
    {
        var guard = new RewriteRuleGuard([
            new UrlRewriteRule("/a", "/b", Enabled: false),
        ]);

        var result = guard.Apply("https://example.com/a", new TestRewriteEngine());

        Assert.Equal("https://example.com/a", result);
        Assert.Equal(0, guard.Counters[0].RewriteCount);
    }

    /// <summary>A rewrite producing a non-http(s) scheme is ignored (§10.4 invariant).</summary>
    [Fact]
    public void Non_Http_Scheme_Rewrite_Is_Ignored()
    {
        var guard = new RewriteRuleGuard([
            new UrlRewriteRule("^https://", "ftp://"),
        ]);

        var result = guard.Apply("https://example.com/a", new TestRewriteEngine());

        Assert.Equal("https://example.com/a", result);
        Assert.Equal(0, guard.Counters[0].RewriteCount);
    }

    /// <summary>Per-rule counters track only rules that actually changed a URL.</summary>
    [Fact]
    public void Counters_Trigger_Only_On_Change()
    {
        var guard = new RewriteRuleGuard([
            new UrlRewriteRule("/missing/", "/x/"),
            new UrlRewriteRule("/a", "/b"),
        ]);

        guard.Apply("https://example.com/a", new TestRewriteEngine());
        guard.Apply("https://example.com/a", new TestRewriteEngine());

        Assert.Equal(0, guard.Counters[0].RewriteCount);
        Assert.Equal(2, guard.Counters[1].RewriteCount);
    }

    /// <summary>Host mapping replaces the host and preserves path and query.</summary>
    [Fact]
    public void Host_Mapping_Replaces_Host()
    {
        var mapper = new HostMapper([new HostMappingRule("staging.example.com", "www.example.com")]);

        var mapped = mapper.MapUrl("https://staging.example.com/path?q=1");

        Assert.Equal("https://www.example.com/path?q=1", mapped);
    }

    /// <summary>The optional path-prefix transform applies with the host mapping.</summary>
    [Fact]
    public void Host_Mapping_Applies_Path_Prefix_Transform()
    {
        var mapper = new HostMapper([
            new HostMappingRule("staging.example.com", "www.example.com", "/beta", "/v2"),
        ]);

        var mapped = mapper.MapUrl("https://staging.example.com/beta/page");

        Assert.Equal("https://www.example.com/v2/page", mapped);
    }

    /// <summary>Without a matching rule the URL passes through unchanged.</summary>
    [Fact]
    public void Unmapped_Host_Passes_Through()
    {
        var mapper = new HostMapper([new HostMappingRule("staging.example.com", "www.example.com")]);

        const string url = "https://other.example.com/path";
        Assert.Equal(url, mapper.MapUrl(url));
    }

    /// <summary>Comparison aligns staging and production URLs by path plus query.</summary>
    [Fact]
    public void Path_Alignment_Maps_Both_Sides()
    {
        var mapper = new HostMapper([new HostMappingRule("staging.example.com", "www.example.com")]);

        Assert.True(mapper.AlignByPath("https://staging.example.com/a?q=1", "https://www.example.com/a?q=1"));
        Assert.False(mapper.AlignByPath("https://staging.example.com/a", "https://www.example.com/b"));
        Assert.False(mapper.AlignByPath("https://staging.example.com/a?q=1", "https://www.example.com/a?q=2"));
    }

    /// <summary>Normalized comparison is scheme-insensitive after host mapping.</summary>
    [Fact]
    public void Normalized_Comparison_Ignores_Scheme()
    {
        var mapper = new HostMapper([new HostMappingRule("staging.example.com", "www.example.com")]);

        Assert.True(mapper.NormalizedEquals("http://staging.example.com/a", "https://www.example.com/a"));
        Assert.False(mapper.NormalizedEquals("http://staging.example.com/a", "https://www.example.com/other"));
    }

    /// <summary>Mapping labels describe the configured host pairs for surfaces.</summary>
    [Fact]
    public void Mapping_Labels_Describe_Rules()
    {
        var mapper = new HostMapper([new HostMappingRule("staging.example.com", "www.example.com")]);

        var label = Assert.Single(mapper.DescribeMappings());
        Assert.Contains("staging.example.com", label, StringComparison.Ordinal);
        Assert.Contains("www.example.com", label, StringComparison.Ordinal);
    }
}
