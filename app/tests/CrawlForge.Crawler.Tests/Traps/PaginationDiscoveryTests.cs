using System.Text;
using CrawlForge.Crawler.Traps;
using Xunit;

namespace CrawlForge.Crawler.Tests.Traps;

/// <summary>
/// Pagination handling (FS-CRAWL-001 §5.5 link discovery; §5.12 directive
/// toggles): rel=next/prev from the Link header, HTML link elements and
/// anchors, numbered pagination discovery, scope limits, dedup and bounds.
/// Purely local — no network is touched.
/// </summary>
public class PaginationDiscoveryTests
{
    private const string PageUrl = "https://fixture.example/products?color=red";

    private static ReadOnlyMemory<byte> Body(string html) => Encoding.UTF8.GetBytes(html);

    private static IReadOnlyDictionary<string, string> NoHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void LinkHeader_NextAndPrev_AreDiscovered()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Link"] = "<https://fixture.example/products?color=red&page=2>; rel=\"next\", <https://fixture.example/products?color=red&page=1>; rel=\"prev\"",
        };

        var result = new PaginationDiscovery().Discover(PageUrl, headers, ReadOnlyMemory<byte>.Empty);

        Assert.Equal(2, result.Links.Count);
        Assert.Contains(result.Links, l => l.Relation == PaginationRelation.Next && l.Url.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(result.Links, l => l.Relation == PaginationRelation.Prev && l.Url.EndsWith("page=1", StringComparison.Ordinal));
        Assert.Equal(0, result.TruncatedCount);
    }

    [Fact]
    public void HtmlLinkElements_NextAndPrev_AreDiscovered()
    {
        var html = "<html><head>" +
                   "<link rel=\"next\" href=\"/products?color=red&page=2\">" +
                   "<link rel=\"prev\" href=\"/products?color=red&page=1\">" +
                   "<link rel=\"canonical\" href=\"/products\">" +
                   "</head><body></body></html>";

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        Assert.Equal(2, result.Links.Count);
        Assert.Equal(PaginationRelation.Next, result.Links[0].Relation);
        Assert.Equal(PaginationRelation.Prev, result.Links[1].Relation);
    }

    [Fact]
    public void AnchorRel_NextAndPrevious_AreDiscoveredWithAnchorText()
    {
        var html = "<a rel=\"next\" href=\"?color=red&page=2\">Next »</a> " +
                   "<a rel='previous' href='?color=red&page=1'>« Previous</a>";

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        Assert.Equal(2, result.Links.Count);
        Assert.Equal("Next »", result.Links[0].AnchorText);
        Assert.Equal(PaginationRelation.Next, result.Links[0].Relation);
        Assert.Equal(PaginationRelation.Prev, result.Links[1].Relation);
    }

    [Fact]
    public void NumberedPagination_QueryShape_IsDiscovered()
    {
        var html = "<a href=\"/products?color=red&page=2\">2</a>" +
                   "<a href=\"/products?color=red&page=3\">3</a>" +
                   "<a href=\"/products?color=red&page=1\">1</a>" +
                   "<a href=\"/about\">2</a>" +                    // different path → not pagination
                   "<a href=\"/products?color=blue\">2</a>";        // non-page param change → not pagination

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        Assert.Equal(3, result.Links.Count);
        Assert.All(result.Links, l => Assert.Equal(PaginationRelation.Numbered, l.Relation));
        Assert.Contains(result.Links, l => l.Url.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(result.Links, l => l.Url.EndsWith("page=3", StringComparison.Ordinal));
        Assert.Contains(result.Links, l => l.Url.EndsWith("page=1", StringComparison.Ordinal));
    }

    [Fact]
    public void NumberedPagination_PathShape_IsDiscovered()
    {
        const string current = "https://fixture.example/products/page/2";
        var html = "<a href=\"/products/page/1\">1</a>" +
                   "<a href=\"/products/page/3\">3</a>" +
                   "<a href=\"/products/page/2\">2</a>" +          // current page → deduped
                   "<a href=\"/other/page/3\">3</a>";               // different base path → excluded

        var result = new PaginationDiscovery().Discover(current, NoHeaders, Body(html));

        Assert.Equal(2, result.Links.Count);
        Assert.Contains(result.Links, l => l.Url.EndsWith("/products/page/1", StringComparison.Ordinal));
        Assert.Contains(result.Links, l => l.Url.EndsWith("/products/page/3", StringComparison.Ordinal));
    }

    [Fact]
    public void NumberedPagination_FromPageOneBasePath_IsDiscovered()
    {
        const string current = "https://fixture.example/products";
        var html = "<a href=\"/products/page/2\">2</a><a href=\"/products/page/3\">3</a>";

        var result = new PaginationDiscovery().Discover(current, NoHeaders, Body(html));

        Assert.Equal(2, result.Links.Count);
        Assert.All(result.Links, l => Assert.Equal(PaginationRelation.Numbered, l.Relation));
    }

    [Fact]
    public void NumericAnchor_NonPageTarget_IsNotPagination()
    {
        var html = "<a href=\"/products/info\">2</a>" +             // trailing segment is not numeric
                   "<a href=\"/products?sort=price\">2</a>" +       // sort is not a page parameter
                   "<a href=\"/other\">2</a>";                      // different base path, no page segment

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        Assert.Empty(result.Links);
    }

    [Fact]
    public void OutOfScopeHosts_AreExcluded_ByDefaultSameHostScope()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Link"] = "<https://other.example/products?page=2>; rel=\"next\"",
        };
        var html = "<a rel=\"next\" href=\"https://other.example/products?page=3\">Next</a>" +
                   "<a href=\"/products?color=red&page=2\">2</a>";

        var result = new PaginationDiscovery().Discover(PageUrl, headers, Body(html));

        var link = Assert.Single(result.Links);
        Assert.StartsWith("https://fixture.example/", link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomScopePredicate_IsHonored()
    {
        var html = "<a rel=\"next\" href=\"https://allowed.example/p/2\">Next</a>" +
                   "<a rel=\"next\" href=\"https://blocked.example/p/2\">Next</a>";

        var result = new PaginationDiscovery().Discover(
            PageUrl, NoHeaders, Body(html),
            inScope: url => url.Contains("allowed.example", StringComparison.Ordinal));

        var link = Assert.Single(result.Links);
        Assert.Contains("allowed.example", link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateTargets_AndCurrentPage_AreDeduplicated()
    {
        var html = "<a rel=\"next\" href=\"?color=red&page=2\">Next</a>" +
                   "<a href=\"/products?color=red&page=2\">2</a>" +
                   "<a href=\"/products?color=red\">1</a>";         // resolves to the current page

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        var link = Assert.Single(result.Links);
        Assert.Equal(PaginationRelation.Next, link.Relation);
    }

    [Fact]
    public void PerPageBound_TruncatesAndCounts()
    {
        var html = string.Concat(Enumerable.Range(2, 10).Select(i => $"<a href=\"/products?color=red&page={i}\">{i}</a>"));

        var result = new PaginationDiscovery(new PaginationOptions(MaxLinksPerPage: 3)).Discover(PageUrl, NoHeaders, Body(html));

        Assert.Equal(3, result.Links.Count);
        Assert.Equal(7, result.TruncatedCount);
    }

    [Fact]
    public void NumberedPageBound_TruncatesNumberedLinksOnly()
    {
        var html = "<a rel=\"next\" href=\"?color=red&page=2\">Next</a>" +
                   string.Concat(Enumerable.Range(3, 6).Select(i => $"<a href=\"/products?color=red&page={i}\">{i}</a>"));

        var result = new PaginationDiscovery(new PaginationOptions(MaxNumberedPages: 2)).Discover(PageUrl, NoHeaders, Body(html));

        Assert.Equal(3, result.Links.Count); // next + 2 numbered
        Assert.Equal(4, result.TruncatedCount);
        Assert.Equal(PaginationRelation.Next, result.Links[0].Relation);
    }

    [Fact]
    public void EmptyBody_HeaderOnlyDiscovery_Works()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Link"] = "</products?color=red&page=2>; rel=next",
        };

        var result = new PaginationDiscovery().Discover(PageUrl, headers, ReadOnlyMemory<byte>.Empty);

        Assert.Single(result.Links);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://fixture.example/products")]
    public void InvalidPageUrl_ReturnsEmpty(string pageUrl)
    {
        var result = new PaginationDiscovery().Discover(pageUrl, NoHeaders, Body("<a rel='next' href='/x'>Next</a>"));
        Assert.Empty(result.Links);
    }

    [Fact]
    public void MalformedMarkup_IsBoundedAndDoesNotThrow()
    {
        var html = "<a rel=\"next\" href=\"?color=red&page=2\">Next</a>" +
                   "<a rel=\"next\" href=\"?page=9\">unclosed " + new string('<', 5000) +
                   "<link rel=\"next\" href=\"?page=3\"";

        var result = new PaginationDiscovery().Discover(PageUrl, NoHeaders, Body(html));

        // The well-formed prefix still yields discoveries; nothing throws.
        var link = Assert.Single(result.Links);
        Assert.EndsWith("page=2", link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_InvalidBounds_ThrowAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaginationDiscovery(new PaginationOptions(MaxLinksPerPage: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaginationDiscovery(new PaginationOptions(MaxNumberedPages: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaginationDiscovery(new PaginationOptions(MaxBodyScanBytes: 0)));
    }

    [Fact]
    public void DeclarationOrder_HeaderThenLinkThenAnchors_IsPreserved()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Link"] = "<https://fixture.example/products?color=red&page=9>; rel=\"next\"",
        };
        var html = "<link rel=\"prev\" href=\"?color=red&page=1\">" +
                   "<a href=\"/products?color=red&page=2\">2</a>";

        var result = new PaginationDiscovery().Discover(PageUrl, headers, Body(html));

        Assert.Equal(3, result.Links.Count);
        Assert.EndsWith("page=9", result.Links[0].Url, StringComparison.Ordinal);
        Assert.Equal(PaginationRelation.Prev, result.Links[1].Relation);
        Assert.Equal(PaginationRelation.Numbered, result.Links[2].Relation);
    }
}
