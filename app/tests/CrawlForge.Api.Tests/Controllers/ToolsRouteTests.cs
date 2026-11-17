using System.Net;
using System.Net.Http.Json;
using System.Text;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the standalone tools and single-page
/// analysis: local robots/structured-data/scope/rewrite logic, fetch-boundary
/// delegation, render capture, sitemap export delegation, and llms generation.
/// </summary>
public sealed class ToolsRouteTests
{
    [Fact]
    public async Task RobotsTest_Body_EvaluatesAllowDisallow()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var body = "User-agent: *\nDisallow: /private/\nAllow: /private/public\n";

        var response = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new
        {
            robotsBody = body,
            testUrls = new[] { "https://example.com/private/secret", "https://example.com/private/public/page", "https://example.com/open" },
            userAgent = "*",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<RobotsTestResponse>();
        Assert.Equal(3, verdict.Results.Count);
        Assert.False(verdict.Results[0].Allowed);
        Assert.Equal("/private/", verdict.Results[0].MatchedRule);
        Assert.True(verdict.Results[1].Allowed);
        Assert.True(verdict.Results[2].Allowed);
        Assert.Null(verdict.Results[2].MatchedRule);
    }

    [Fact]
    public async Task RobotsTest_Url_FetchesThroughBoundary()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Fetcher.Result = new HttpFetchResult(
            "https://example.com/robots.txt", 200, new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /admin\n"), TimeSpan.Zero, TimeSpan.Zero);

        var response = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new
        {
            robotsUrl = "https://example.com/robots.txt",
            testUrls = new[] { "https://example.com/admin" },
            userAgent = "*",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<RobotsTestResponse>();
        Assert.False(verdict.Results[0].Allowed);
        Assert.True(host.Fakes.Fetcher.Requests.TryDequeue(out var fetch));
        Assert.Equal("https://example.com/robots.txt", fetch.Url);
    }

    [Fact]
    public async Task RobotsTest_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var emptyUrls = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new { robotsBody = "x", testUrls = Array.Empty<string>(), userAgent = "*" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyUrls.StatusCode);

        var both = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new { robotsBody = "x", robotsUrl = "https://e.com/robots.txt", testUrls = new[] { "https://e.com/" }, userAgent = "*" });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(both, "validation");

        var neither = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new { testUrls = new[] { "https://e.com/" }, userAgent = "*" });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var noAgent = await host.Client.PostAsJsonAsync("/api/tools/robots-test", new { robotsBody = "x", testUrls = new[] { "https://e.com/" }, userAgent = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noAgent.StatusCode);
    }

    [Fact]
    public async Task UrlCheck_ProjectsStatusHeadersChainAndIndexability()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Fetcher.Result = new HttpFetchResult(
            "https://example.com/final", 200,
            new Dictionary<string, string> { ["content-type"] = "text/html" },
            Encoding.UTF8.GetBytes("<html></html>"), TimeSpan.Zero, TimeSpan.Zero);

        var response = await host.Client.PostAsJsonAsync("/api/tools/url-check", new { url = "https://example.com/start" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<UrlCheckResponse>();
        Assert.Equal(200, verdict.StatusCode);
        Assert.Equal("2xx", verdict.StatusClass);
        Assert.True(verdict.Indexable);
        Assert.Equal("text/html", verdict.Headers["content-type"]);
        Assert.Single(verdict.RedirectChain);
        Assert.Equal("https://example.com/start", verdict.RedirectChain[0].Url);
        Assert.Equal("https://example.com/final", verdict.RedirectChain[0].Location);
        Assert.Null(verdict.WafVerdict);
        Assert.Null(verdict.ScopeDiagnostics);
    }

    [Fact]
    public async Task UrlCheck_NoindexHeader_NotIndexable()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Fetcher.Result = new HttpFetchResult(
            "https://example.com/", 200,
            new Dictionary<string, string> { ["x-robots-tag"] = "noindex, nofollow" },
            ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero);

        var verdict = await (await host.Client.PostAsJsonAsync("/api/tools/url-check", new { url = "https://example.com/" }))
            .ReadDataAsync<UrlCheckResponse>();

        Assert.False(verdict.Indexable);
        Assert.Empty(verdict.RedirectChain);
    }

    [Fact]
    public async Task UrlCheck_DetectsWafChallenge_FromCatalogue()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.WafCatalogue.Bundled.Add(new WafSignature(
            "test-cf", WafProvider.Cloudflare, WafChallengeClass.Interstitial, ["cf-ray"], ["challenge-platform"], IsBundled: true));
        host.Fakes.Fetcher.Result = new HttpFetchResult(
            "https://example.com/", 403,
            new Dictionary<string, string> { ["cf-ray"] = "abc123" },
            Encoding.UTF8.GetBytes("<html>challenge-platform</html>"), TimeSpan.Zero, TimeSpan.Zero);

        var verdict = await (await host.Client.PostAsJsonAsync("/api/tools/url-check", new { url = "https://example.com/" }))
            .ReadDataAsync<UrlCheckResponse>();

        Assert.Equal("4xx", verdict.StatusClass);
        Assert.False(verdict.Indexable);
        Assert.NotNull(verdict.WafVerdict);
        Assert.Equal("cloudflare", verdict.WafVerdict!.Provider);
        Assert.Equal("interstitial", verdict.WafVerdict.ChallengeClass);
        Assert.Equal("test-cf", verdict.WafVerdict.SignatureKey);
        Assert.True(verdict.WafVerdict.Confidence >= 0.8);
    }

    [Fact]
    public async Task UrlCheck_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var bad = await host.Client.PostAsJsonAsync("/api/tools/url-check", new { url = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(bad, "validation");
    }

    [Fact]
    public async Task StructuredDataCheck_Markup_ParsesJsonLd()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var markup = "<html><head><script type=\"application/ld+json\">{\"@context\":\"https://schema.org\",\"@type\":\"WebPage\",\"name\":\"Test\"}</script></head><body></body></html>";

        var response = await host.Client.PostAsJsonAsync("/api/tools/structured-data-check", new { markup });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<StructuredDataCheckResponse>();
        var block = Assert.Single(result.Blocks);
        Assert.Equal("json-ld", block.Format);
        Assert.Equal("WebPage", block.Type);
        Assert.NotNull(block.Properties);
        Assert.NotNull(block.Issues);
    }

    [Fact]
    public async Task StructuredDataCheck_MalformedJsonLd_ReportsInvalid()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var markup = "<script type=\"application/ld+json\">{not json}</script>";

        var result = await (await host.Client.PostAsJsonAsync("/api/tools/structured-data-check", new { markup }))
            .ReadDataAsync<StructuredDataCheckResponse>();

        var block = Assert.Single(result.Blocks);
        Assert.False(block.Valid);
        Assert.NotEmpty(block.Issues);
    }

    [Fact]
    public async Task StructuredDataCheck_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var both = await host.Client.PostAsJsonAsync("/api/tools/structured-data-check", new { markup = "<html></html>", url = "https://e.com/" });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        var neither = await host.Client.PostAsJsonAsync("/api/tools/structured-data-check", new { });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
    }

    [Fact]
    public async Task RenderTest_ReturnsScreenshotConsoleAndPicker()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Rendering.Page = new RenderedPage(
            "https://example.com/rendered",
            "<html><body>rendered</body></html>",
            new byte[] { 9, 8, 7 },
            [new ConsoleMessage("error", "boom", DateTimeOffset.UtcNow)],
            null);

        var response = await host.Client.PostAsJsonAsync("/api/tools/render-test", new { url = "https://example.com/", deviceProfile = "mobile", picker = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<RenderTestResponse>();
        Assert.Equal(Convert.ToBase64String(new byte[] { 9, 8, 7 }), result.ScreenshotBase64);
        Assert.Equal("<html><body>rendered</body></html>", result.RenderedHtml);
        Assert.Equal("https://example.com/rendered", result.FinalUrl);
        Assert.True(result.PickerEnabled);
        var message = Assert.Single(result.ConsoleMessages);
        Assert.Equal("error", message.Level);
        Assert.Equal("boom", message.Text);
        Assert.NotNull(message.Timestamp);
        Assert.True(host.Fakes.Rendering.Requests.TryDequeue(out var request));
        Assert.True(request.Device.IsMobile);
        Assert.Equal("mobile", request.Device.Name);
    }

    [Fact]
    public async Task RenderTest_UnknownProfile_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var bad = await host.Client.PostAsJsonAsync("/api/tools/render-test", new { url = "https://example.com/", deviceProfile = "watch" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(bad, "validation");
    }

    [Fact]
    public async Task SitemapGenerate_DelegatesToExportEngine_WithEligibleCount()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        await host.SeedUrlAsync(audit.Id, "https://example.com/");
        await host.SeedUrlAsync(audit.Id, "https://example.com/a");
        await host.SeedUrlAsync(audit.Id, "https://example.com/blocked", CrawlStatusClass.Blocked, 403);

        var response = await host.Client.PostAsJsonAsync("/api/tools/sitemap-generate", new { auditId = audit.Id.ToString(), includeIndex = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<SitemapGenerateResponse>();
        Assert.Equal(2, result.UrlCount);
        // The fake mirrors the locked ExportEngine naming: XML sitemap → .xml.
        Assert.EndsWith(".xml", result.FileName);
        Assert.True(host.Fakes.Exports.Requests.TryDequeue(out var export));
        Assert.Equal(ExportFormat.XmlSitemap, export.Format);
        Assert.Equal(audit.Id, export.AuditId);
    }

    [Fact]
    public async Task SitemapGenerate_UnknownAuditAndIncompleteJob()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var unknown = await host.Client.PostAsJsonAsync("/api/tools/sitemap-generate", new { auditId = "999" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        host.Fakes.Exports.CreateStatus = ExportJobStatus.Running;
        var incomplete = await host.Client.PostAsJsonAsync("/api/tools/sitemap-generate", new { auditId = audit.Id.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(incomplete, "invalid-state");
    }

    [Fact]
    public async Task ScopePreview_ReturnsRegionsCountsAndSample()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var html = "<html><body><main>Alpha beta gamma delta epsilon</main><footer>Boilerplate footer text here</footer></body></html>";

        var response = await host.Client.PostAsJsonAsync("/api/tools/scope-preview", new
        {
            sampleHtml = html,
            contentScope = new { includeSelectors = new[] { "main" }, excludeSelectors = new[] { "footer" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<ScopePreviewResponse>();
        Assert.True(result.FullWordCount > result.ScopedWordCount);
        Assert.Equal(result.FullWordCount - result.ScopedWordCount, result.RemovedWordCount);
        Assert.Contains("Alpha", result.SampleScopedText);
        Assert.DoesNotContain("Boilerplate", result.SampleScopedText);
        Assert.NotEmpty(result.MatchedRegions);
        Assert.Equal("css", result.MatchedRegions[0].Language);
        Assert.NotEmpty(result.PerSelectorCounts);
    }

    [Fact]
    public async Task ScopePreview_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var bad = await host.Client.PostAsJsonAsync("/api/tools/scope-preview", new { sampleHtml = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task RewritePreview_AppliesRulesAndHostMappings()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostAsJsonAsync("/api/tools/rewrite-preview", new
        {
            url = "https://old.example.com/path",
            rules = new object[]
            {
                new { pattern = "old\\.example\\.com", replacement = "new.example.com", enabled = true },
                new { pattern = "never-matches", replacement = "x", enabled = false },
            },
            hostMappings = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<RewritePreviewResponse>();
        Assert.Equal("https://old.example.com/path", result.OriginalUrl);
        Assert.Contains("new.example.com", result.RewrittenUrl);
        Assert.Single(result.AppliedRules);
        Assert.Equal("old\\.example\\.com", result.AppliedRules[0]);

        var mapped = await (await host.Client.PostAsJsonAsync("/api/tools/rewrite-preview", new
        {
            url = "https://legacy.example.com/page",
            rules = Array.Empty<object>(),
            hostMappings = new object[] { new { fromHost = "legacy.example.com", toHost = "www.example.com" } },
        })).ReadDataAsync<RewritePreviewResponse>();
        Assert.Contains("www.example.com", mapped.RewrittenUrl);
        Assert.Empty(mapped.AppliedRules);
    }

    [Fact]
    public async Task LlmsGenerate_WritesLocalFile_PerVariant()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var filePath = Path.Combine(Path.GetTempPath(), "llms.txt");
        host.Fakes.LlmsGenerator.Result = new Contracts.Intelligence.LlmsGenerationResult([filePath]);

        var response = await host.Client.PostAsJsonAsync("/api/tools/llms-generate", new { auditId = audit.Id.ToString(), variant = "llms-txt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<LlmsGenerateResponse>();
        Assert.Equal("llms-txt", result.Variant);
        Assert.Equal("llms.txt", result.FileName);
        Assert.Equal(filePath, result.FilePath);
        Assert.True(host.Fakes.LlmsGenerator.Requests.TryDequeue(out var request));
        Assert.False(request.IncludeFullVersion);

        host.Fakes.LlmsGenerator.Result = new Contracts.Intelligence.LlmsGenerationResult([Path.Combine(Path.GetTempPath(), "llms-full.txt")]);
        var full = await (await host.Client.PostAsJsonAsync("/api/tools/llms-generate", new { auditId = audit.Id.ToString(), variant = "llms-full-txt" }))
            .ReadDataAsync<LlmsGenerateResponse>();
        Assert.Equal("llms-full.txt", full.FileName);
        Assert.True(host.Fakes.LlmsGenerator.Requests.TryDequeue(out var fullRequest));
        Assert.True(fullRequest.IncludeFullVersion);
    }

    [Fact]
    public async Task LlmsGenerate_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var unknown = await host.Client.PostAsJsonAsync("/api/tools/llms-generate", new { auditId = "999", variant = "llms-txt" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var badVariant = await host.Client.PostAsJsonAsync("/api/tools/llms-generate", new { auditId = audit.Id.ToString(), variant = "llms-json" });
        Assert.Equal(HttpStatusCode.BadRequest, badVariant.StatusCode);
    }

    [Fact]
    public async Task LlmsValidate_BodyAndUrl()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Geo.Validation = new Contracts.Intelligence.LlmsTxtValidation("example.com", true, true, [], []);

        var valid = await (await host.Client.PostAsJsonAsync("/api/tools/llms-validate", new { body = "# Example\n> Summary\n" }))
            .ReadDataAsync<LlmsValidateResponse>();
        Assert.True(valid.Valid);
        Assert.Empty(valid.Issues);

        host.Fakes.Geo.Validation = new Contracts.Intelligence.LlmsTxtValidation("example.com", true, false, ["https://example.com/missing"], ["Missing title."]);
        host.Fakes.Fetcher.Result = new HttpFetchResult(
            "https://example.com/llms.txt", 200, new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("bad"), TimeSpan.Zero, TimeSpan.Zero);
        var invalid = await (await host.Client.PostAsJsonAsync("/api/tools/llms-validate", new { url = "https://example.com/llms.txt" }))
            .ReadDataAsync<LlmsValidateResponse>();
        Assert.False(invalid.Valid);
        Assert.Contains("Missing title.", invalid.Issues);
        Assert.Contains(invalid.Issues, i => i.Contains("Broken link"));

        var both = await host.Client.PostAsJsonAsync("/api/tools/llms-validate", new { body = "x", url = "https://e.com/llms.txt" });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task SinglePage_ReturnsFindingsInventoryAndDiagnostics()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.SinglePage.Result = new SinglePageAnalysisResult(
            [new FindingDraft("title.missing", AuditCategory.Content, FindingSeverity.Warning, null, "{\"message\":\"Title is missing.\"}", null)],
            [new ElementInventoryItem("document_head", "{}", 0), new ElementInventoryItem("headings", "{}", 1), new ElementInventoryItem("headings", "{}", 2)],
            new WafEvaluationResult(true, WafProvider.Cloudflare, WafChallengeClass.Captcha, ["sig-1"], "{\"confidence\":0.9}"),
            new ScopedVisibleText("scoped text", 2, []));

        var response = await host.Client.PostAsJsonAsync("/api/analysis/single-page", new { url = "https://example.com/", render = true, ruleKeys = new[] { "title.missing" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<SinglePageAnalysisResponse>();
        var finding = Assert.Single(result.Findings);
        Assert.Equal(string.Empty, finding.Id);
        Assert.Equal("title.missing", finding.RuleKey);
        Assert.Equal("Title is missing.", finding.Message);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal(3, result.ElementInventory.TotalElements);
        Assert.Equal(2, result.ElementInventory.Counts["headings"]);
        Assert.NotNull(result.WafVerdict);
        Assert.Equal("captcha", result.WafVerdict!.ChallengeClass);
        Assert.NotNull(result.ScopeDiagnostics);
        Assert.Equal(2, result.ScopeDiagnostics!.ScopedWordCount);
        Assert.True(host.Fakes.SinglePage.Requests.TryDequeue(out var request));
        Assert.True(request.Render);
        Assert.Equal(new[] { "title.missing" }, request.RuleKeys);
    }

    [Fact]
    public async Task SinglePage_Validation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var bad = await host.Client.PostAsJsonAsync("/api/analysis/single-page", new { url = "ftp://example.com/" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(bad, "validation");
    }
}
