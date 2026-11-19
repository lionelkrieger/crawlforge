using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-09-06 D4: the REST URL-detail redirect chain maps losslessly from the
/// persisted redirect-chain element (WP-09-01 handoff recipe), keeping the
/// legacy single-hop RedirectTargetUrl fallback; the redirect-matrix verdict
/// vocabulary is the frozen metric-dimension vocabulary (MatrixVerdictNames)
/// on BOTH the REST results surface and the matrix-row-validated hub shape —
/// REST == hub == metric dimensions.
/// </summary>
public sealed class RedirectChainAndMatrixVocabularyRouteTests
{
    private static async Task<(long AuditId, UrlRecordDto Url)> SeedUrlWithLegacyRedirectAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://old.example.com/a", CrawlStatusClass.Redirect, 301);
        url = await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(url with
        {
            RedirectTargetUrl = "https://legacy.example.com/target",
            FinalStatusCode = 301,
        });
        return (audit.Id, url);
    }

    [Fact]
    public async Task UrlDetail_PersistedChain_MapsLosslessly_InOrdinalOrder()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedUrlWithLegacyRedirectAsync(host);

        // Hops stored OUT of ordinal order — the mapper must order them.
        var payload = RedirectChainJson.SerializePayload(new RedirectChainPayload(
            new[]
            {
                new RedirectChainHopPayload(1, "https://mid.example.com/a", "https://new.example.com/final", 302),
                new RedirectChainHopPayload(0, "https://old.example.com/a", "https://mid.example.com/a", 301),
            },
            "https://new.example.com/final", 200, false, false));
        await host.Get<IWriteRepository<UrlElementDto>>().CreateAsync(new UrlElementDto(
            0, url.Id, RedirectChainElementGroup.GroupName, payload, 0));

        var detail = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls/{url.Id}")).ReadDataAsync<UrlDetailWire>();

        Assert.Equal(2, detail.RedirectChain.Count);
        Assert.Equal("https://old.example.com/a", detail.RedirectChain[0].Url);
        Assert.Equal(301, detail.RedirectChain[0].StatusCode);
        Assert.Equal("https://mid.example.com/a", detail.RedirectChain[0].Location);
        Assert.Equal("https://mid.example.com/a", detail.RedirectChain[1].Url);
        Assert.Equal(302, detail.RedirectChain[1].StatusCode);
        Assert.Equal("https://new.example.com/final", detail.RedirectChain[1].Location);
    }

    [Fact]
    public async Task UrlDetail_NoChainRow_KeepsLegacySingleHopFallback()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedUrlWithLegacyRedirectAsync(host);

        var detail = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls/{url.Id}")).ReadDataAsync<UrlDetailWire>();

        var hop = Assert.Single(detail.RedirectChain);
        Assert.Equal(url.CanonicalUrl, hop.Url);
        Assert.Equal(301, hop.StatusCode);
        Assert.Equal("https://legacy.example.com/target", hop.Location);
    }

    [Fact]
    public async Task UrlDetail_MalformedChainPayload_FallsBackToLegacy()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedUrlWithLegacyRedirectAsync(host);
        await host.Get<IWriteRepository<UrlElementDto>>().CreateAsync(new UrlElementDto(
            0, url.Id, RedirectChainElementGroup.GroupName, "{not-json", 0));

        var detail = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls/{url.Id}")).ReadDataAsync<UrlDetailWire>();

        var hop = Assert.Single(detail.RedirectChain); // tolerant deserialize → legacy fallback
        Assert.Equal("https://legacy.example.com/target", hop.Location);
    }

    [Fact]
    public async Task UrlDetail_NoRedirectAtAll_EmptyChain()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/plain");

        var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/urls/{url.Id}")).ReadDataAsync<UrlDetailWire>();

        Assert.Empty(detail.RedirectChain);
    }

    [Theory]
    [InlineData(RedirectMatrixVerdict.Pass, "pass")]
    [InlineData(RedirectMatrixVerdict.TargetMismatch, "target-mismatch")]
    [InlineData(RedirectMatrixVerdict.ChainExceeded, "chain-exceeded")]
    [InlineData(RedirectMatrixVerdict.FinalNon200, "final-non-200")]
    [InlineData(RedirectMatrixVerdict.CanonicalMismatch, "canonical-mismatch")]
    public async Task Matrix_VerdictVocabulary_RestEqualsHubEqualsMetricDimensions(RedirectMatrixVerdict verdict, string dimension)
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://old.example.com/a");
        await host.Get<IWriteRepository<RedirectMappingDto>>().CreateAsync(new RedirectMappingDto(
            0, audit.Id, "https://old.example.com/a", "https://new.example.com/a", 0));
        host.Fakes.RedirectMatrix.RowResult = m => new RedirectMatrixRowResult(
            m, verdict, "https://new.example.com/a", 1, [], "https://new.example.com/a");

        var items = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/redirect-matrix/results")).ReadDataAsync<List<RedirectMatrixResultWire>>();
        var row = Assert.Single(items);

        // REST verdict == metric dimension name == hub entry verdict (the
        // Crawler producer builds MatrixRowValidatedItem with MatrixVerdictNames.Of).
        Assert.Equal(dimension, row.Verdict);
        Assert.Equal(MatrixVerdictNames.Of(verdict), row.Verdict);
        var hubItem = new MatrixRowValidatedItem(url.Id.ToString(), "https://old.example.com/a", MatrixVerdictNames.Of(verdict));
        Assert.Equal(hubItem.Verdict, row.Verdict);

        // The frozen name filters (case-insensitive input, canonical output).
        var filtered = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/redirect-matrix/results?verdict={dimension.ToUpperInvariant()}")).ReadDataAsync<List<RedirectMatrixResultWire>>();
        Assert.Single(filtered);
        var mismatched = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/redirect-matrix/results?verdict=pass")).ReadDataAsync<List<RedirectMatrixResultWire>>();
        if (verdict == RedirectMatrixVerdict.Pass)
        {
            Assert.Single(mismatched);
        }
        else
        {
            Assert.Empty(mismatched);
        }
    }

    [Fact]
    public async Task Matrix_UnknownVerdictFilter_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.GetAsync($"/api/audits/{audit.Id}/redirect-matrix/results?verdict=targetmismatch");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("verdict", out _));
    }
}
