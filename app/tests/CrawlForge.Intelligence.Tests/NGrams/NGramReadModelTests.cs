using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>
/// Read-model tests (FS-CRAWL-002 §5.7–§5.8): explorer list filters/sort/
/// pagination over the materialized summaries, per-URL scope, cluster/gap
/// reads from persisted findings, and the bounded MCP resource projection.
/// </summary>
public sealed class NGramReadModelTests
{
    /// <summary>Site list filters by zone/order/minDf, sorts, and paginates.</summary>
    [Fact]
    public async Task List_Site_Filters_Sorts_And_Paginates()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await SeedTwoUrlAuditAsync(stack);
        var readModel = new NGramReadModel(stack.Provider);

        var all = await readModel.ListAsync(auditId, new NgramListQuery());
        Assert.True(all.TotalCount >= 2);
        Assert.Equal(all.TotalCount, all.SiteItems.Count);

        var filtered = await readModel.ListAsync(auditId, new NgramListQuery(Zone: TextZone.Title, Order: 2, MinDf: 2));
        Assert.All(filtered.SiteItems, item =>
        {
            Assert.Equal(TextZone.Title, item.Zone);
            Assert.Equal(2, item.Order);
            Assert.True(item.DocumentFrequency >= 2);
        });

        var page1 = await readModel.ListAsync(auditId, new NgramListQuery(SortBy: "count", Limit: 1, Offset: 0));
        var page2 = await readModel.ListAsync(auditId, new NgramListQuery(SortBy: "count", Limit: 1, Offset: 1));
        Assert.Single(page1.SiteItems);
        Assert.Single(page2.SiteItems);
        Assert.True(page1.SiteItems[0].TotalFrequency >= page2.SiteItems[0].TotalFrequency);
        Assert.Equal(all.TotalCount, page1.TotalCount);

        var search = await readModel.ListAsync(auditId, new NgramListQuery(Query: "red"));
        Assert.All(search.SiteItems, item => Assert.Contains("red", item.Term));
    }

    /// <summary>Url scope returns per-URL frequency rows for the URL filter.</summary>
    [Fact]
    public async Task List_Url_Scope_Returns_PerUrl_Rows()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes" }, [1, 2]));
        var readModel = new NGramReadModel(stack.Provider);

        var response = await readModel.ListAsync(auditId, new NgramListQuery(Scope: "url", UrlId: urlId, SortBy: "tf"));

        Assert.Empty(response.SiteItems);
        Assert.Equal(3, response.UrlItems.Count);
        Assert.All(response.UrlItems, item => Assert.Equal(urlId, item.UrlId));
        Assert.True(response.UrlItems[0].TermFrequency >= response.UrlItems[^1].TermFrequency);
    }

    /// <summary>Clusters read from persisted findings with filters.</summary>
    [Fact]
    public async Task ListClusters_Reads_Persisted_Findings_With_Filters()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedFindingAsync(auditId, FindingRuleKeys.ContentKeywordCannibalization,
            """{"term":"alpha beta","order":2,"zone":"title","siteDf":2,"sameCanonicalTarget":true,"members":[{"urlId":1,"url":"https://example.com/a","tf":1.0,"canonicalTarget":"https://example.com/c"},{"urlId":2,"url":"https://example.com/b","tf":1.0,"canonicalTarget":"https://example.com/c"}]}""");
        var readModel = new NGramReadModel(stack.Provider);

        var clusters = await readModel.ListClustersAsync(auditId);

        var cluster = Assert.Single(clusters);
        Assert.Equal("alpha beta", cluster.Term);
        Assert.True(cluster.SameCanonicalTarget);
        Assert.Equal(2, cluster.Members.Count);
        Assert.Equal("https://example.com/c", cluster.Members[0].DeclaredCanonicalTarget);
        Assert.Empty(await readModel.ListClustersAsync(auditId, order: 3));
        Assert.Empty(await readModel.ListClustersAsync(auditId, minClusterSize: 3));
    }

    /// <summary>Gaps read from persisted findings with the anchor-DF override.</summary>
    [Fact]
    public async Task ListGaps_Reads_Persisted_Findings_With_MinAnchorDf()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedFindingAsync(auditId, FindingRuleKeys.ContentTopicalGap,
            """{"term":"green widgets","order":2,"anchorDf":3,"threshold":3,"sampleSourceUrls":["https://example.com/source"]}""");
        var readModel = new NGramReadModel(stack.Provider);

        var gaps = await readModel.ListGapsAsync(auditId, new NgramGapsQuery());

        var gap = Assert.Single(gaps);
        Assert.Equal("green widgets", gap.Term);
        Assert.Equal(3, gap.AnchorDocumentFrequency);
        Assert.Single(gap.SampleSourceUrls);
        Assert.Empty(await readModel.ListGapsAsync(auditId, new NgramGapsQuery(MinAnchorDf: 4)));
    }

    /// <summary>The MCP resource is a bounded projection of summaries, URL rows, and clusters.</summary>
    [Fact]
    public async Task AnalysisResource_Projects_Summaries_UrlRows_And_Clusters()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await SeedTwoUrlAuditAsync(stack);
        await stack.SeedFindingAsync(auditId, FindingRuleKeys.ContentKeywordCannibalization,
            """{"term":"red shoes","order":2,"zone":"title","siteDf":2,"sameCanonicalTarget":false,"members":[{"urlId":1,"url":"https://example.com/a","tf":0.5,"canonicalTarget":null},{"urlId":2,"url":"https://example.com/b","tf":1.0,"canonicalTarget":null}]}""");
        var readModel = new NGramReadModel(stack.Provider);
        var urlId = (long)(await stack.ScalarAsync("SELECT `id` FROM `url_records` WHERE `audit_id` = @a ORDER BY `id` LIMIT 1", ("@a", auditId)) ?? 0L);

        var resource = await readModel.GetAnalysisResourceAsync(auditId, urlId: urlId, limit: 5);

        Assert.Equal(auditId, resource.AuditId);
        Assert.NotEmpty(resource.TopTerms);
        Assert.True(resource.TopTerms.Count <= 5);
        Assert.NotEmpty(resource.UrlFrequencies);
        Assert.All(resource.UrlFrequencies, row => Assert.Equal(urlId, row.UrlId));
        Assert.Single(resource.Clusters);
    }

    private static async Task<long> SeedTwoUrlAuditAsync(NGramTestStack stack)
    {
        var auditId = await stack.SeedAuditAsync();
        var url1 = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var url2 = await stack.SeedUrlAsync(auditId, "https://example.com/b");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url1, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes red" }, [1, 2]));
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url2, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes" }, [1, 2]));
        await analyzer.SummarizeSiteAsync(auditId);
        return auditId;
    }
}
