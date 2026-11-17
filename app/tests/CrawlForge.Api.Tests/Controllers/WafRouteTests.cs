using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the WAF explorer and local signature
/// catalogue: block list/summary and signature list/create/update/delete.
/// </summary>
public sealed class WafRouteTests
{
    private static async Task<(long AuditId, UrlRecordDto Url)> SeedAuditWithUrlAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, countersJson: "{\"fetched\":10}");
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/blocked", CrawlStatusClass.Blocked);
        return (audit.Id, url);
    }

    private static Task<WafDetectionDto> SeedDetectionAsync(
        ControllerTestHost host,
        long auditId,
        long urlId,
        WafProvider provider = WafProvider.Cloudflare,
        WafChallengeClass challengeClass = WafChallengeClass.Captcha) =>
        host.Get<IWriteRepository<WafDetectionDto>>().CreateAsync(new WafDetectionDto(
            0, auditId, urlId, provider, challengeClass,
            "{\"confidence\":0.9,\"snippet\":\"challenge\",\"headers\":{\"server\":\"cloudflare\"}}",
            "cf-captcha", false, DateTimeOffset.UtcNow));

    [Fact]
    public async Task WafBlocks_List_ProjectsRows_WithCanonicalUrlAndEvidence()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        await SeedDetectionAsync(host, auditId, url.Id);

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/waf-blocks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<WafBlockWire>>();
        var row = Assert.Single(items);
        Assert.Equal(url.Id.ToString(), row.UrlId);
        Assert.Equal("https://example.com/blocked", row.CanonicalUrl);
        Assert.Equal("cloudflare", row.Provider);
        Assert.Equal("captcha", row.ChallengeClass);
        Assert.Equal(0.9, row.Confidence);
        Assert.Equal("cf-captcha", row.SignatureKey);
        Assert.Equal("challenge", row.Evidence!.Snippet);
        Assert.Equal("cloudflare", row.Evidence.Headers!["server"]);
    }

    [Fact]
    public async Task WafBlocks_List_FiltersByProviderAndChallengeClass()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var url2 = await host.SeedUrlAsync(auditId, "https://example.com/blocked-2", CrawlStatusClass.Blocked);
        await SeedDetectionAsync(host, auditId, url.Id);
        await SeedDetectionAsync(host, auditId, url2.Id, WafProvider.DataDome, WafChallengeClass.BotBlock);

        var byProvider = await (await host.Client.GetAsync($"/api/audits/{auditId}/waf-blocks?provider=datadome")).ReadDataAsync<List<WafBlockWire>>();
        Assert.Single(byProvider);
        Assert.Equal("datadome", byProvider[0].Provider);

        var byClass = await (await host.Client.GetAsync($"/api/audits/{auditId}/waf-blocks?challengeClass=botblock")).ReadDataAsync<List<WafBlockWire>>();
        Assert.Single(byClass);

        var badProvider = await host.Client.GetAsync($"/api/audits/{auditId}/waf-blocks?provider=nope");
        await ControllerTestHelpers.AssertErrorShape(badProvider, "validation");
    }

    [Fact]
    public async Task WafBlocks_Summary_ComputesCountsAndBlockRate()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var url2 = await host.SeedUrlAsync(auditId, "https://example.com/blocked-2", CrawlStatusClass.Blocked);
        await SeedDetectionAsync(host, auditId, url.Id);
        await SeedDetectionAsync(host, auditId, url2.Id, WafProvider.DataDome, WafChallengeClass.BotBlock);

        var summary = await (await host.Client.GetAsync($"/api/audits/{auditId}/waf-blocks/summary")).ReadDataAsync<WafBlocksSummaryWire>();

        Assert.Equal(2, summary.TotalBlocked);
        Assert.Equal(0.2, summary.BlockRate);
        Assert.Equal(1, summary.ByProvider["cloudflare"]);
        Assert.Equal(1, summary.ByProvider["datadome"]);
        Assert.Equal(1, summary.ByChallengeClass["captcha"]);
        Assert.Equal(1, summary.ByChallengeClass["botblock"]);
    }

    [Fact]
    public async Task WafBlocks_UnknownAudit_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.GetAsync("/api/audits/999/waf-blocks");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "not-found");
    }

    [Fact]
    public async Task Signatures_List_IncludesBundledAndUserEntries()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.WafCatalogue.Bundled.Add(new WafSignature(
            "cf-bundled", WafProvider.Cloudflare, WafChallengeClass.Captcha, ["server~cloudflare|0.5"], ["cf-chl|0.6"], true));
        await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "user-sig", "datadome", "botblock", "dd-marker", null));

        var response = await host.Client.GetAsync("/api/waf-signatures");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 2);
        var catalogue = await response.ReadDataAsync<WafSignatureCatalogueWire>();
        Assert.Equal("bundled-test", catalogue.BundledVersion);
        var bundled = catalogue.Signatures.Single(s => s.Key == "cf-bundled");
        Assert.Equal("bundled", bundled.Source);
        Assert.True(bundled.Enabled);
        Assert.Contains("cf-chl|0.6", bundled.Pattern);
        var user = catalogue.Signatures.Single(s => s.Key == "user-sig");
        Assert.Equal("user", user.Source);
        Assert.Equal("dd-marker", user.Pattern);
        Assert.True(user.Enabled);
    }

    [Fact]
    public async Task Signatures_Create_PersistsUserEntry_AndRejectsDuplicate()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "user-sig", "cloudflare", "interstitial", "marker-x", "desc"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<WafSignatureWire>();
        Assert.Equal("user-sig", created.Key);
        Assert.Equal("cloudflare", created.Provider);
        Assert.Equal("interstitial", created.ChallengeClass);
        Assert.Equal("marker-x", created.Pattern);
        Assert.Equal("user", created.Source);

        var duplicate = await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "user-sig", "cloudflare", "interstitial", "marker-x", null));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(duplicate, "conflict");
    }

    [Fact]
    public async Task Signatures_Create_InvalidProvider_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "user-sig", "not-a-provider", "captcha", "marker", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "validation");
    }

    [Fact]
    public async Task Signatures_Update_MergesSubset()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "user-sig", "cloudflare", "captcha", "marker-x", null));

        var response = await host.Client.PutJsonAsync("/api/waf-signatures/user-sig", new WafSignatureUpdateRequest(
            null, null, "marker-y", false, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadDataAsync<WafSignatureWire>();
        Assert.Equal("marker-y", updated.Pattern);
        Assert.False(updated.Enabled);
        Assert.Equal("cloudflare", updated.Provider);

        var missing = await host.Client.PutJsonAsync("/api/waf-signatures/absent", new WafSignatureUpdateRequest(null, null, null, true, null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Signatures_Delete_RemovesUserEntry_AndReportsBundledRestore()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.WafCatalogue.Bundled.Add(new WafSignature(
            "cf-bundled", WafProvider.Cloudflare, WafChallengeClass.Captcha, [], [], true));
        await host.Client.PostJsonAsync("/api/waf-signatures", new WafSignatureCreateRequest(
            "cf-bundled", "cloudflare", "captcha", "override", null));

        var response = await host.Client.DeleteAsync("/api/waf-signatures/cf-bundled");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<WafSignatureDeleteResponse>();
        Assert.True(result.Deleted);
        Assert.True(result.BundledDefaultRestored);

        var missing = await host.Client.DeleteAsync("/api/waf-signatures/cf-bundled");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
