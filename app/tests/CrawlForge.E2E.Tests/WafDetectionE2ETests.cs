using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.E2E.Tests.Fixtures;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WAF challenge detection over real loopback origins (WP-07-01, criterion
/// #35): HTTP 200-OK challenge pages with provider markers are classified
/// Blocked with correct provider attribution, never re-fetched, and never
/// produce content-quality findings. Includes the corroboration-guard
/// negative (single-facet hits stay unblocked).
/// </summary>
public sealed class WafChallengeClassificationE2ETests
{
    /// <summary>One corroborated 200-OK challenge fixture per named provider.</summary>
    public static TheoryData<string, WafProvider, WafChallengeClass, string, string, string> ProviderFixtures => new()
    {
        // provider label, expected enum, expected class, header name, header value, body marker
        { "Cloudflare", WafProvider.Cloudflare, WafChallengeClass.Interstitial, "cf-mitigated", "challenge", "challenge-platform" },
        { "DataDome", WafProvider.DataDome, WafChallengeClass.Interstitial, "x-datadome", "1", "captcha-delivery" },
        { "Akamai", WafProvider.Akamai, WafChallengeClass.BotBlock, "server", "AkamaiGHOST", "Reference #18.deadbeef" },
        { "Imperva", WafProvider.Imperva, WafChallengeClass.Interstitial, "x-iinfo", "12-34567890", "Incapsula incident ID: 999000111" },
        { "PerimeterX", WafProvider.PerimeterX, WafChallengeClass.Captcha, "x-px-block", "1", "px-captcha" },
    };

    [Theory]
    [MemberData(nameof(ProviderFixtures))]
    public async Task ChallengePage200Ok_IsBlocked_WithProviderAttribution(
        string providerLabel, WafProvider expectedProvider, WafChallengeClass expectedClass,
        string headerName, string headerValue, string bodyMarker)
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Challenge", $"<div>{bodyMarker}</div><p>Please wait while we verify your browser.</p>"))
                .WithHeader(headerName, headerValue));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(
            origin.BaseUrl, name: $"WAF {providerLabel}");

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var blocked = Assert.Single(urls);
        Assert.Equal(CrawlStatusClass.Blocked, blocked.StatusClass);
        Assert.Equal("waf-challenge", blocked.BlockReason);
        Assert.Equal(expectedProvider, blocked.WafProvider);
        Assert.Equal(expectedClass, blocked.ChallengeClass);
        Assert.Equal(200, blocked.FinalStatusCode);

        // Never re-fetched: exactly one request for the challenge page.
        Assert.Equal(1, origin.CountFor("/"));

        // Blocked URLs skip content analysis: no content-quality findings
        // attach to them (in particular no thin-content/soft-404 judgment on
        // challenge text). The WAF detection finding itself is expected.
        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        Assert.DoesNotContain(findings, f => f.UrlId == blocked.Id
            && f.RuleKey != FindingRuleKeys.WafChallengeDetected
            && f.RuleKey != FindingRuleKeys.SuspectedChallenge);
        Assert.Contains(findings, f => f.UrlId == blocked.Id
            && f.RuleKey == FindingRuleKeys.WafChallengeDetected);
    }

    [Fact]
    public async Task SingleFacetHit_CorroborationGuard_LeavesPageUnblocked()
    {
        // Negative case: a 200-OK page with ONLY a body marker (one facet,
        // confidence below threshold) must NOT be classified as a challenge.
        // The guard's documented output is a suspected-challenge Notice.
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Friendly", "<p>Just a moment of your attention: this is a normal article page " +
                "with plenty of words so the analyzer treats it as real content, not a challenge.</p>")));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var page = Assert.Single(urls);
        Assert.Equal(CrawlStatusClass.Success, page.StatusClass);
        Assert.Null(page.BlockReason);
        Assert.Null(page.WafProvider);

        // Single-facet hit → Notice annotation, never a block.
        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.SuspectedChallenge
            && f.Severity == FindingSeverity.Notice && f.UrlId == page.Id);
        Assert.DoesNotContain(findings, f => f.RuleKey == FindingRuleKeys.WafChallengeDetected);
    }

    [Fact]
    public async Task GenericProvider_BodyOnlyMarkers_ProducesSuspectedNotice_NeverBlocks()
    {
        // Sixth provider ('Other', generic-challenge) per orchestrator ruling
        // (option a, spec-correct as designed): the bundled catalogue gives it
        // body markers only (single Body facet), so the corroboration guard
        // yields a Notice-level suspected-challenge annotation — never a block,
        // never a silently clean fetch. Catalogue-fidelity note for sign-off:
        // the spec's five facet types (response-header/cookie/title/dom-marker/
        // script, 02-frontier-fetching-and-waf.md §2.3.1) collapse to three in
        // the evaluator (WafFacet Header/Cookie/Body); challenge-form structure
        // is NOT a distinct facet, so generic-with-form-structure cannot
        // corroborate under the current model — title-only→suspected stands.
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Generic", "<p>verify you are human — we have detected unusual traffic from your network.</p>")));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var page = Assert.Single(urls);
        Assert.Equal(CrawlStatusClass.Success, page.StatusClass);
        Assert.Null(page.BlockReason);
        Assert.Null(page.WafProvider);

        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.SuspectedChallenge
            && f.Severity == FindingSeverity.Notice && f.UrlId == page.Id);
        Assert.DoesNotContain(findings, f => f.RuleKey == FindingRuleKeys.WafChallengeDetected);
    }
}

/// <summary>
/// WAF detection REPORTING (criterion #35): persisted detection rows, the
/// REST waf-blocks surface, the site-wide WAF-Block alert, and block metrics —
/// exercising the accepted Crawler recording surfaces (WafDetectionRecorder /
/// WafBlockRateDiagnostic) through the composed engine (WP-06-01 reopen fix).
/// </summary>
public sealed class WafDetectionReportingE2ETests
{
    [Fact]
    public async Task ChallengeAudit_PersistsDetectionRow_AndRestSurfaceListsIt()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Challenge", "<div>challenge-platform</div><p>Verifying your browser.</p>"))
                .WithHeader("cf-mitigated", "challenge"));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Detection row persisted with provider + evidence.
        var detections = await backend.ListByAuditAsync<WafDetectionDto>(auditId);
        var detection = Assert.Single(detections);
        Assert.Equal(WafProvider.Cloudflare, detection.Provider);
        Assert.Equal(WafChallengeClass.Interstitial, detection.ChallengeClass);
        Assert.False(string.IsNullOrWhiteSpace(detection.EvidenceJson));

        // REST surface lists the block with provider attribution.
        using var response = await backend.Http.GetAsync($"/api/audits/{auditId}/waf-blocks");
        response.EnsureSuccessStatusCode();
        var envelope = await WireJson.EnvelopeAsync(response);
        var items = envelope.GetProperty("data");
        Assert.Equal(1, items.GetArrayLength());
        // Locked wire convention: enums serialize lowercase (no hyphens).
        Assert.Equal("cloudflare", items[0].GetProperty("provider").GetString());

        // Summary aggregates.
        using var summaryResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/waf-blocks/summary");
        summaryResponse.EnsureSuccessStatusCode();
        var summary = await WireJson.EnvelopeAsync(summaryResponse);
        Assert.Equal(1, summary.GetProperty("data").GetProperty("totalBlocked").GetInt64());
    }

    [Fact]
    public async Task ChallengeAudit_EmitsWafBlockAlert_AndBlockMetrics()
    {
        // The site-wide diagnostic requires a minimum sample of 20 URLs
        // (WafDetectionPolicy.DefaultMinimumUrlsForBlockRate): a clean index
        // linking to 19 challenge pages → 20 URLs, 95% block rate. (Blocked
        // pages never discover links, so the index must be a clean fetch.)
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Index", FixturePages.Links(Enumerable.Range(0, 19).Select(i => $"/p/{i}").ToArray()))));
            var challenge = OriginResponse.Html(FixturePages.Page(
                "Challenge", "<div>challenge-platform</div><p>Verifying your browser.</p>"))
                .WithHeader("cf-mitigated", "challenge");
            for (var i = 0; i < 19; i++)
            {
                routes.Map($"/p/{i}", challenge);
            }
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        // WP-08-A2: 300s harness patience under full-solution gate load (Scale
        // 500k seed + Acceptance live UI checks + conformance audit can starve
        // this 20-URL crawl past the old 90s budget; the audit was still
        // RUNNING at timeout, not failed). Not a product requirement; the
        // fail-fast-on-Failed behavior in WaitForAuditAsync is unchanged.
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(300));

        // Per-URL detection findings + site-wide WAF-Block alert.
        var findings = await backend.ListByAuditAsync<FindingDto>(auditId, limit: 200);
        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.WafChallengeDetected);
        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.WafBlockRate && f.UrlId is null);

        // Block metrics materialized.
        var metrics = await backend.ListByAuditAsync<MetricSummaryDto>(auditId);
        Assert.Contains(metrics, m => m.MetricKey == MetricKeys.WafBlocks && m.Value >= 19);
        Assert.Contains(metrics, m => m.MetricKey == MetricKeys.WafBlockRate && m.Value > 0.9);
    }
}
