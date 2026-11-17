using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.E2E.Tests.Fixtures;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 criterion #10 — standalone single-page analysis (FS-CRAWL-001 §7.7):
/// POST /api/analysis/single-page fetches and audits ONE url through the shared
/// pipeline without creating a project, configuration, or audit. Proven here:
/// findings + element inventory come back, nothing is persisted, the ad-hoc
/// fetch happens exactly once, the rule-key filter narrows findings, invalid
/// input yields the field-level validation envelope, and a WAF challenge page
/// surfaces the additive verdict.
/// </summary>
public sealed class SinglePageAnalysisE2ETests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task SinglePage_AnalyzesOneUrl_WithoutCreatingProjectOrAudit()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/page", OriginResponse.Html(FixturePages.Page(
                "Standalone page",
                "<h1>Heading</h1><p>Standalone single page analysis content.</p>" +
                FixturePages.Links(["/other"])))));
        await using var backend = await E2EBackend.StartAsync();

        var response = await backend.Http.PostAsJsonAsync("/api/analysis/single-page",
            new { url = origin.BaseUrl + "page" }, WireOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Single-page analysis failed: {(int)response.StatusCode} {body}");
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");

        // Findings and element inventory come from the shared pipeline.
        Assert.True(data.GetProperty("findings").GetArrayLength() > 0, "No findings produced.");
        var inventory = data.GetProperty("elementInventory");
        Assert.True(inventory.GetProperty("totalElements").GetInt32() > 0, "Empty element inventory.");
        Assert.True(inventory.GetProperty("counts").EnumerateObject().Any(), "No inventory groups.");

        // §7.7: the ad-hoc fetch happened exactly once — no crawl.
        Assert.Equal(1, origin.CountFor("/page"));

        // Criterion #10: no project, configuration, or audit was created.
        var projects = await backend.Service<IReadRepository<ProjectDto>>().ListAsync(new ListQuery(0, 10));
        var audits = await backend.Service<IReadRepository<AuditDto>>().ListAsync(new ListQuery(0, 10));
        var urls = await backend.Service<IReadRepository<UrlRecordDto>>().ListAsync(new ListQuery(0, 10));
        Assert.Empty(projects.Items);
        Assert.Empty(audits.Items);
        Assert.Empty(urls.Items);
    }

    [Fact]
    public async Task SinglePage_InvalidUrl_ReturnsFieldLevelValidation()
    {
        await using var backend = await E2EBackend.StartAsync();

        var response = await backend.Http.PostAsJsonAsync("/api/analysis/single-page",
            new { url = "not-an-absolute-url" }, WireOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await WireJson.EnvelopeAsync(response);
        var error = envelope.GetProperty("error");
        Assert.Equal("validation", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("fieldErrors").TryGetProperty("url", out _));
    }

    [Fact]
    public async Task SinglePage_RuleKeysFilter_RestrictsFindings()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/page", OriginResponse.Html(FixturePages.Page(
                "Filtered page",
                "<p>Content for the rule filter proof.</p>"))));
        await using var backend = await E2EBackend.StartAsync();
        var url = origin.BaseUrl + "page";

        var unfiltered = await AnalyzeAsync(backend, url, ruleKeys: null);
        var allKeys = unfiltered.GetProperty("findings").EnumerateArray()
            .Select(f => f.GetProperty("ruleKey").GetString()!)
            .Distinct()
            .ToList();
        Assert.NotEmpty(allKeys);
        var chosen = allKeys[0];

        var filtered = await AnalyzeAsync(backend, url, ruleKeys: [chosen]);
        var filteredFindings = filtered.GetProperty("findings").EnumerateArray().ToList();
        Assert.NotEmpty(filteredFindings);
        Assert.All(filteredFindings, f => Assert.Equal(chosen, f.GetProperty("ruleKey").GetString()));
        Assert.True(filteredFindings.Count <= unfiltered.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task SinglePage_WafChallengePage_SurfacesAdditiveVerdict()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/challenge", OriginResponse.Html(
                    "<html><body><div id='challenge-platform'>Please wait while we verify your browser.</div></body></html>")
                .WithHeader("cf-mitigated", "challenge")));
        await using var backend = await E2EBackend.StartAsync();

        var data = await AnalyzeAsync(backend, origin.BaseUrl + "challenge", ruleKeys: null);
        var verdict = data.GetProperty("wafVerdict");
        Assert.Equal(JsonValueKind.Object, verdict.ValueKind);
        Assert.Equal("cloudflare", verdict.GetProperty("provider").GetString());
        Assert.True(verdict.GetProperty("confidence").GetDouble() > 0);
        Assert.False(string.IsNullOrEmpty(verdict.GetProperty("challengeClass").GetString()));
    }

    [Fact]
    public async Task SinglePage_NotFoundPage_AnalyzesWithoutPersisting()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/missing", OriginResponse.Status(404, "<html><body><p>Gone.</p></body></html>")));
        await using var backend = await E2EBackend.StartAsync();

        var data = await AnalyzeAsync(backend, origin.BaseUrl + "missing", ruleKeys: null);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("findings").ValueKind);

        var audits = await backend.Service<IReadRepository<AuditDto>>().ListAsync(new ListQuery(0, 10));
        Assert.Empty(audits.Items);
        Assert.Equal(1, origin.CountFor("/missing"));
    }

    private static async Task<JsonElement> AnalyzeAsync(E2EBackend backend, string url, IReadOnlyList<string>? ruleKeys)
    {
        var response = await backend.Http.PostAsJsonAsync("/api/analysis/single-page",
            new { url, ruleKeys }, WireOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Single-page analysis failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }
}
