using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the redirect migration matrix
/// (upload/results) and the semantic explorer (run/similar/link-opportunities/
/// clusters).
/// </summary>
public sealed class RedirectMatrixSemanticsRouteTests
{
    private static async Task<long> SeedAuditAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        return audit.Id;
    }

    [Fact]
    public async Task Matrix_Upload_PersistsRows_AndAutoSeedsListModeAudit()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);

        var response = await host.Client.PostJsonAsync($"/api/audits/{auditId}/redirect-matrix", new RedirectMatrixUploadRequest(
            [new RedirectMatrixRowWire("https://old.example.com/a", "https://new.example.com/a")], true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<RedirectMatrixUploadResponse>();
        Assert.Equal(auditId.ToString(), result.MatrixId);
        Assert.Equal(1, result.RowCount);
        Assert.NotNull(result.AuditId);
        var persisted = await host.Get<IReadRepository<RedirectMappingDto>>().ListAsync(
            new ListQuery(0, 100, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        var mapping = Assert.Single(persisted.Items);
        Assert.Equal("https://old.example.com/a", mapping.SourceUrl);
        Assert.Equal(0, mapping.Ordinal);
        Assert.True(host.Fakes.ListMode.Calls.TryDequeue(out var call));
        Assert.False(call.AllowDiscovery);
    }

    [Fact]
    public async Task Matrix_Upload_RejectsEmptyAndInvalidRows()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var empty = await host.Client.PostJsonAsync($"/api/audits/{auditId}/redirect-matrix", new RedirectMatrixUploadRequest([], null));
        await ControllerTestHelpers.AssertErrorShape(empty, "validation");
        var invalid = await host.Client.PostJsonAsync($"/api/audits/{auditId}/redirect-matrix", new RedirectMatrixUploadRequest(
            [new RedirectMatrixRowWire("not-a-url", "https://new.example.com/a")], null));
        await ControllerTestHelpers.AssertErrorShape(invalid, "validation");
    }

    [Fact]
    public async Task Matrix_Results_ProjectsVerdictsChainsAndCanonical()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://old.example.com/a");
        await host.Get<IWriteRepository<RedirectMappingDto>>().CreateAsync(new RedirectMappingDto(
            0, auditId, "https://old.example.com/a", "https://new.example.com/a", 0));
        host.Fakes.RedirectMatrix.RowResult = m => new RedirectMatrixRowResult(
            m, RedirectMatrixVerdict.TargetMismatch, "https://new.example.com/final", 2,
            [new RedirectHop(0, m.SourceUrl, "https://mid.example.com/a", 301), new RedirectHop(1, "https://mid.example.com/a", "https://new.example.com/final", 302)],
            "https://new.example.com/canonical");

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/redirect-matrix/results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<RedirectMatrixResultWire>>();
        var row = Assert.Single(items);
        Assert.Equal(url.Id.ToString(), row.UrlId);
        Assert.Equal("targetmismatch", row.Verdict);
        Assert.Equal(2, row.HopChain.Count);
        Assert.Equal(301, row.HopChain[0].StatusCode);
        Assert.Equal("https://new.example.com/canonical", row.CanonicalUrl);
        Assert.Equal("https://new.example.com/final", row.Evidence);

        var filtered = await (await host.Client.GetAsync($"/api/audits/{auditId}/redirect-matrix/results?verdict=pass")).ReadDataAsync<List<RedirectMatrixResultWire>>();
        Assert.Empty(filtered);
    }

    [Fact]
    public async Task Semantics_Run_ReturnsCompletedJob()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);

        var response = await host.Client.PostJsonAsync($"/api/audits/{auditId}/semantics/run", new SemanticsRunRequest(false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<SemanticsRunResponse>();
        Assert.Equal(auditId.ToString(), result.AuditId);
        Assert.Equal(auditId.ToString(), result.JobId);
        Assert.Equal("completed", result.State);
        Assert.True(host.Fakes.Semantics.Runs.TryDequeue(out var run));
        Assert.Equal(auditId, run.AuditId);
    }

    [Fact]
    public async Task Semantics_Similar_ReturnsTopKNeighbours()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var source = await host.SeedUrlAsync(auditId, "https://example.com/source");
        var targetA = await host.SeedUrlAsync(auditId, "https://example.com/a");
        var targetB = await host.SeedUrlAsync(auditId, "https://example.com/b");
        var writes = host.Get<IWriteRepository<SemanticLinkOpportunityDto>>();
        await writes.CreateAsync(new SemanticLinkOpportunityDto(0, auditId, source.Id, targetA.Id, 0.9, null));
        await writes.CreateAsync(new SemanticLinkOpportunityDto(0, auditId, source.Id, targetB.Id, 0.6, null));

        var items = await (await host.Client.GetAsync($"/api/audits/{auditId}/semantics/similar?urlId={source.Id}&k=1&minSimilarity=0.5")).ReadDataAsync<List<SimilarUrlWire>>();
        var row = Assert.Single(items);
        Assert.Equal(targetA.Id.ToString(), row.UrlId);
        Assert.Equal("https://example.com/a", row.CanonicalUrl);
        Assert.Equal(0.9, row.Similarity);
    }

    [Fact]
    public async Task Semantics_LinkOpportunities_ProjectsAnchorSuggestion()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var source = await host.SeedUrlAsync(auditId, "https://example.com/source");
        var target = await host.SeedUrlAsync(auditId, "https://example.com/target");
        await host.Get<IWriteRepository<SemanticLinkOpportunityDto>>().CreateAsync(new SemanticLinkOpportunityDto(
            0, auditId, source.Id, target.Id, 0.8, "[\"related topic\"]"));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/semantics/link-opportunities");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<LinkOpportunityWire>>();
        var row = Assert.Single(items);
        Assert.Equal(source.Id.ToString(), row.SourceUrlId);
        Assert.Equal(target.Id.ToString(), row.TargetUrlId);
        Assert.Equal("https://example.com/target", row.TargetCanonicalUrl);
        Assert.Equal("related topic", row.AnchorSuggestion);
    }

    [Fact]
    public async Task Semantics_Clusters_GroupsMembersWithCentroid()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var urlA = await host.SeedUrlAsync(auditId, "https://example.com/a");
        var urlB = await host.SeedUrlAsync(auditId, "https://example.com/b");
        var writes = host.Get<IWriteRepository<SemanticClusterDto>>();
        await writes.CreateAsync(new SemanticClusterDto(0, auditId, 1, urlA.Id, 0.95));
        await writes.CreateAsync(new SemanticClusterDto(0, auditId, 1, urlB.Id, 0.8));

        var items = await (await host.Client.GetAsync($"/api/audits/{auditId}/semantics/clusters")).ReadDataAsync<List<SemanticClusterWire>>();
        var cluster = Assert.Single(items);
        Assert.Equal("1", cluster.ClusterId);
        Assert.Equal(urlA.Id.ToString(), cluster.CentroidUrlId);
        Assert.Equal(2, cluster.Members.Count);
        Assert.Equal(0.95, cluster.Members[0].Similarity);
    }
}
