using CrawlForge.Crawler.Capture;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes;

/// <summary>
/// URL-path-pattern grouping and override matching (§5.6.1 adaptive grouping and
/// manual overrides). Deterministic, documented semantics.
/// </summary>
public sealed class UrlPathPatternGroupingTests
{
    [Theory]
    [InlineData("/blog/post-1", "/blog/*")]
    [InlineData("/blog/", "/blog/*")]
    [InlineData("/product/sku-123", "/product/*")]
    [InlineData("/a/b/c", "/a/b/*")]
    [InlineData("/", "/")]
    [InlineData("/blog/post-1?x=1#frag", "/blog/*")]
    public void GroupKey_ReplacesFinalSegment(string path, string expected) =>
        Assert.Equal(expected, UrlPathPatternGrouping.GroupKey(path));

    [Fact]
    public void GroupKey_SharedDirectoryAndChildren_GroupTogether() =>
        Assert.Equal(UrlPathPatternGrouping.GroupKey("/blog/"), UrlPathPatternGrouping.GroupKey("/blog/post-1"));

    [Theory]
    [InlineData("/blog/*", "/blog/post-1", true)]
    [InlineData("/blog/*", "/blog/2026/post-1", true)]
    [InlineData("/blog/*", "/blog/", true)]
    [InlineData("/blog/*", "/shop/post-1", false)]
    [InlineData("/blog", "/blog", true)]
    [InlineData("/blog", "/blog/post", false)]
    [InlineData("*", "/anything/at/all", true)]
    [InlineData("/blog/*", "/blog/post?x=1", true)]
    [InlineData("", "/blog", false)]
    public void Matches_GlobAndExact(string pattern, string path, bool expected) =>
        Assert.Equal(expected, UrlPathPatternGrouping.Matches(pattern, path));

    [Fact]
    public void Matches_IsCaseSensitive() =>
        Assert.False(UrlPathPatternGrouping.Matches("/Blog/*", "/blog/post"));
}
