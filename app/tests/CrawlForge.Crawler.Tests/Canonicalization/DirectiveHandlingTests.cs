using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Canonicalization;

/// <summary>
/// Directive-handling toggles (FS-CRAWL-001 §5.12): the specified defaults
/// and the discovery decisions each toggle governs. Detection/reporting of
/// directives is unaffected — these tests cover crawl/discovery behavior only.
/// </summary>
public class DirectiveHandlingTests
{
    /// <summary>The defaults match the §5.12 table exactly.</summary>
    [Fact]
    public void DefaultsMatchSpecification()
    {
        var options = new DirectiveHandlingOptions();
        Assert.False(options.FollowPageNofollow);
        Assert.False(options.FollowLinkNofollow);
        Assert.True(options.CrawlNoindexPages);
        Assert.True(options.UseCanonicalDiscovery);
        Assert.True(options.UseHreflangAndPaginationDiscovery);
        Assert.True(options.FollowMetaRefresh);
        Assert.False(options.FollowIframes);
    }

    /// <summary>Page nofollow suppresses all links unless its toggle is on.</summary>
    [Fact]
    public void PageNofollowSuppressesLinks()
    {
        var defaults = new DirectiveHandlingOptions();
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowLink(defaults, pageNofollow: true, linkNofollow: false));
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowLink(defaults, pageNofollow: true, linkNofollow: true));

        var enabled = new DirectiveHandlingOptions { FollowPageNofollow = true };
        Assert.True(DirectiveHandlingEvaluator.ShouldFollowLink(enabled, pageNofollow: true, linkNofollow: false));
        // Link-level nofollow still applies independently.
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowLink(enabled, pageNofollow: true, linkNofollow: true));
    }

    /// <summary>Link nofollow suppresses the individual link unless its toggle is on.</summary>
    [Fact]
    public void LinkNofollowSuppressesIndividualLinks()
    {
        var defaults = new DirectiveHandlingOptions();
        Assert.True(DirectiveHandlingEvaluator.ShouldFollowLink(defaults, pageNofollow: false, linkNofollow: false));
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowLink(defaults, pageNofollow: false, linkNofollow: true));

        var enabled = new DirectiveHandlingOptions { FollowLinkNofollow = true };
        Assert.True(DirectiveHandlingEvaluator.ShouldFollowLink(enabled, pageNofollow: false, linkNofollow: true));
    }

    /// <summary>Noindex pages are crawled by default so they can be reported.</summary>
    [Fact]
    public void NoindexPagesAreCrawledByDefault()
    {
        Assert.True(DirectiveHandlingEvaluator.ShouldCrawlPage(new DirectiveHandlingOptions(), noindex: true));
        Assert.False(DirectiveHandlingEvaluator.ShouldCrawlPage(new DirectiveHandlingOptions { CrawlNoindexPages = false }, noindex: true));
        Assert.True(DirectiveHandlingEvaluator.ShouldCrawlPage(new DirectiveHandlingOptions { CrawlNoindexPages = false }, noindex: false));
    }

    /// <summary>Discovery toggles govern canonical/alternate/meta-refresh/iframe targets.</summary>
    [Fact]
    public void DiscoveryTogglesAreHonored()
    {
        var defaults = new DirectiveHandlingOptions();
        Assert.True(DirectiveHandlingEvaluator.ShouldEnqueueCanonical(defaults));
        Assert.True(DirectiveHandlingEvaluator.ShouldEnqueueAlternates(defaults));
        Assert.True(DirectiveHandlingEvaluator.ShouldFollowMetaRefresh(defaults));
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowIframes(defaults));

        var off = new DirectiveHandlingOptions
        {
            UseCanonicalDiscovery = false,
            UseHreflangAndPaginationDiscovery = false,
            FollowMetaRefresh = false,
            FollowIframes = true
        };
        Assert.False(DirectiveHandlingEvaluator.ShouldEnqueueCanonical(off));
        Assert.False(DirectiveHandlingEvaluator.ShouldEnqueueAlternates(off));
        Assert.False(DirectiveHandlingEvaluator.ShouldFollowMetaRefresh(off));
        Assert.True(DirectiveHandlingEvaluator.ShouldFollowIframes(off));
    }
}
