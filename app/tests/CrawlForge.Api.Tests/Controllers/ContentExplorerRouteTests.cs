using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the content explorers: cross-audit content
/// comparison, custom extraction grid/summary, custom search, snippet outputs,
/// and technology detections.
/// </summary>
public sealed class ContentExplorerRouteTests
{
    private static async Task<long> SeedAuditAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        return audit.Id;
    }

    private static async Task SeedPageContentAsync(ControllerTestHost host, long urlId, string title, string description, string heading, long wordCount)
    {
        var writes = host.Get<IWriteRepository<UrlElementDto>>();
        await writes.CreateAsync(new UrlElementDto(0, urlId, "document_head", $"{{\"title\":\"{title}\",\"metaDescription\":\"{description}\"}}", 0));
        await writes.CreateAsync(new UrlElementDto(0, urlId, "headings", $"{{\"outline\":[{{\"level\":1,\"text\":\"{heading}\"}}]}}", 0));
        await writes.CreateAsync(new UrlElementDto(0, urlId, "text_statistics", $"{{\"visibleWordCount\":{wordCount}}}", 0));
    }

    [Fact]
    public async Task CompareContent_DiffsTitleHeadingsTextAndMetadata()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditA = await SeedAuditAsync(host);
        var auditB = await SeedAuditAsync(host);
        var urlA = await host.SeedUrlAsync(auditA, "https://example.com/page");
        var urlB = await host.SeedUrlAsync(auditB, "https://example.com/page");
        await SeedPageContentAsync(host, urlA.Id, "Old Title", "old desc", "Old Heading", 100);
        await SeedPageContentAsync(host, urlB.Id, "New Title", "new desc", "New Heading", 150);

        var response = await host.Client.GetAsync($"/api/audits/compare/content?auditIdA={auditA}&auditIdB={auditB}&canonicalUrl=https://example.com/page");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<ContentCompareResponseWire>();
        Assert.Equal("https://example.com/page", result.CanonicalUrl);
        Assert.True(result.Title.Changed);
        Assert.Equal("Old Title", result.Title.Before);
        Assert.Equal("New Title", result.Title.After);
        var heading = Assert.Single(result.Headings);
        Assert.True(heading.Changed);
        Assert.Equal("100", result.Text.Before);
        Assert.Equal("150", result.Text.After);
        Assert.Contains(result.MetadataChanges, m => m.Field == "metaDescription" && m.Before == "old desc" && m.After == "new desc");
    }

    [Fact]
    public async Task CompareContent_MissingParameters_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.GetAsync("/api/audits/compare/content?auditIdA=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "validation");
    }

    [Fact]
    public async Task ExtractedContent_GridAndSummary_ProjectDynamicColumns()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://example.com/page");
        var writes = host.Get<IWriteRepository<ExtractedContentDto>>();
        await writes.CreateAsync(new ExtractedContentDto(0, auditId, url.Id, "price", 0, "\"19.99\"", ExtractedValueType.Text, false, false));
        await writes.CreateAsync(new ExtractedContentDto(0, auditId, url.Id, "price", 1, "42", ExtractedValueType.Number, false, true));
        await writes.CreateAsync(new ExtractedContentDto(0, auditId, url.Id, "price", 2, null, ExtractedValueType.Text, true, false));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/extracted-content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var grid = await response.ReadDataAsync<ExtractedContentResponseWire>();
        Assert.Equal(new[] { "0", "1", "2" }, grid.Columns);
        var row = Assert.Single(grid.Rows);
        Assert.Equal(url.Id.ToString(), row.UrlId);
        Assert.Equal("price", row.RuleKey);
        Assert.Equal("19.99", row.Values["0"].GetString());
        Assert.Equal(42, row.Values["1"].GetInt32());

        var summary = await (await host.Client.GetAsync($"/api/audits/{auditId}/extracted-content/summary")).ReadDataAsync<ExtractionSummaryResponseWire>();
        var rule = Assert.Single(summary.Rules);
        Assert.Equal("price", rule.RuleKey);
        Assert.Equal(1, rule.PagesMatched);
        Assert.Equal(2, rule.ValuesCaptured);
        Assert.Equal(1, rule.Discards);
        Assert.Equal(1, rule.Truncations);
    }

    [Fact]
    public async Task SearchResults_ProjectsOverviewAndMatches()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://example.com/page");
        await host.Get<IWriteRepository<ContentSearchResultDto>>().CreateAsync(new ContentSearchResultDto(
            0, auditId, url.Id, "phone", 3, "[\"555-0100\"]"));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/search-results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<SearchResultsResponseWire>();
        var overview = Assert.Single(result.Overview);
        Assert.Equal("phone", overview.Item);
        Assert.Equal(3, overview.MatchCount);
        Assert.Equal(1, overview.UrlCount);
        var match = Assert.Single(result.Matches);
        Assert.Equal(url.Id.ToString(), match.UrlId);
        Assert.Equal("555-0100", match.Snippet);
        Assert.Equal(3, match.Occurrences);
    }

    [Fact]
    public async Task SnippetResults_ProjectsDynamicColumns()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://example.com/page");
        await host.Get<IWriteRepository<SnippetResultDto>>().CreateAsync(new SnippetResultDto(
            0, auditId, url.Id, "meta", "[{\"name\":\"title\"}]", "[{\"title\":\"Hello\"}]"));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/snippet-results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<SnippetResultsResponseWire>();
        Assert.Equal(new[] { "title" }, result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal("meta", row.SnippetKey);
        Assert.Equal("Hello", row.Values["title"].GetString());
    }

    [Fact]
    public async Task Technologies_ListsAndFiltersByCategory()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://example.com/page");
        var writes = host.Get<IWriteRepository<TechnologyDetectionDto>>();
        await writes.CreateAsync(new TechnologyDetectionDto(0, auditId, url.Id, "WordPress", "CMS", 0.9));
        await writes.CreateAsync(new TechnologyDetectionDto(0, auditId, url.Id, "jQuery", "JavaScriptLibrary", 0.8));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/technologies");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 2);
        var items = await response.ReadDataAsync<List<TechnologyDetectionWire>>();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, t => t.Name == "WordPress" && t.Confidence == 0.9 && t.UrlId == url.Id.ToString());

        var filtered = await (await host.Client.GetAsync($"/api/audits/{auditId}/technologies?category=CMS")).ReadDataAsync<List<TechnologyDetectionWire>>();
        Assert.Single(filtered);
        Assert.Equal("WordPress", filtered[0].Name);
    }
}
