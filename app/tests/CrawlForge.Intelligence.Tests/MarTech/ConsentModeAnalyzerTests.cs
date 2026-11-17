using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Intelligence.MarTech;
using Xunit;

namespace CrawlForge.Intelligence.Tests.MarTech;

/// <summary>
/// Consent Mode v2 analyzer unit tests (FS-CRAWL-002 §14.2/§14.3): ordered
/// consent timeline, gcs/gcd network correlation, consent-before-tags
/// sequencing from capture timestamps, toggles, and rendering-off notices.
/// </summary>
public sealed class ConsentModeAnalyzerTests
{
    private const string ConsentDefaultHtml = """
        <html><head><script>
        gtag('consent', 'default', {ad_storage: 'denied', analytics_storage: 'denied', ad_user_data: 'denied', ad_personalization: 'denied'});
        </script></head><body></body></html>
        """;

    private static ConsentModeAnalyzer CreateAnalyzer(MarTechOptions? options = null) =>
        new(new ElementInventoryExtractor(), options);

    private static Task<Contracts.Intelligence.ConsentModeAnalysisResult> Analyze(
        string? html,
        IReadOnlyList<ProxyCapturedRequest>? captures = null,
        RenderMode mode = RenderMode.Always,
        MarTechOptions? options = null,
        Contracts.Entities.CrawlConfigurationDto? configuration = null)
    {
        var analyzer = CreateAnalyzer(options);
        var context = AuditContexts.Create(
            html: html, renderingMode: mode, capturedRequests: captures, configuration: configuration);
        return analyzer.AnalyzeAsync(context);
    }

    private static ProxyCapturedRequest CollectHit(int seconds, string extra = "") =>
        AuditContexts.Captured($"https://www.google-analytics.com/g/collect?tid=G-TEST1&en=page_view{extra}", seconds);

    // ── Consent timeline ──────────────────────────────────────────────────

    [Fact]
    public async Task Timeline_DefaultsThenUpdates_WithSequenceMarkers()
    {
        const string html = """
            <html><head><script>
            gtag('consent', 'default', {ad_storage: 'denied', analytics_storage: 'denied'});
            gtag('consent', 'update', {analytics_storage: 'granted'});
            </script></head><body></body></html>
            """;
        var result = await Analyze(html);

        Assert.True(result.ConsentModePresent);
        Assert.NotNull(result.ConsentStateJson);
        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        var defaults = state.RootElement.GetProperty("defaults");
        var updates = state.RootElement.GetProperty("updates");
        Assert.Equal(1, defaults.GetArrayLength());
        Assert.Equal(1, updates.GetArrayLength());
        Assert.Equal("denied", defaults[0].GetProperty("states").GetProperty("ad_storage").GetString());
        Assert.Equal(0, defaults[0].GetProperty("sequence").GetInt32());
        Assert.Equal(1, updates[0].GetProperty("sequence").GetInt32());
        Assert.Equal("granted", updates[0].GetProperty("states").GetProperty("analytics_storage").GetString());
    }

    [Fact]
    public async Task Timeline_DataLayerPushForm_IsRecognized()
    {
        const string html = """
            <html><head><script>
            dataLayer.push(['consent', 'default', {ad_storage: 'denied'}]);
            dataLayer.push(['consent', 'update', {ad_storage: 'granted'}]);
            </script></head><body></body></html>
            """;
        var result = await Analyze(html);
        Assert.True(result.ConsentModePresent);
        Assert.Contains("\"ad_storage\":\"granted\"", result.ConsentStateJson);
    }

    [Fact]
    public async Task NoConsentCommands_PresentFalse()
    {
        var result = await Analyze("<html><body>plain page</body></html>");
        Assert.False(result.ConsentModePresent);
    }

    // ── Without-default and sequencing findings ───────────────────────────

    [Fact]
    public async Task TagsFired_WithoutDefaultDeclaration_EmitsWarning()
    {
        var result = await Analyze(
            "<html><body>no consent code</body></html>",
            captures: [CollectHit(5)]);

        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("without-default", finding.EvidenceJson);
        Assert.Contains("g/collect", finding.EvidenceJson);
    }

    [Fact]
    public async Task DefaultDeclared_TagsFired_NoFinding()
    {
        var result = await Analyze(ConsentDefaultHtml, captures: [CollectHit(5)]);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
    }

    [Fact]
    public async Task TagsBeforeConsentPlatformLoad_EmitsWarning_WithTimestamps()
    {
        var result = await Analyze(
            ConsentDefaultHtml,
            captures:
            [
                CollectHit(5),
                AuditContexts.Captured("https://cdn.cookielaw.org/scripttemplates/otSDKStub.js", 10),
            ]);

        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
        Assert.Contains("before-consent-load", finding.EvidenceJson);
        Assert.Contains("consentPlatformLoadAt", finding.EvidenceJson);
        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        Assert.Equal("tags-before-consent", state.RootElement.GetProperty("sequencingVerdict").GetString());
    }

    [Fact]
    public async Task ConsentBeforeTags_NoSequencingFinding()
    {
        var result = await Analyze(
            ConsentDefaultHtml,
            captures:
            [
                AuditContexts.Captured("https://cdn.cookielaw.org/scripttemplates/otSDKStub.js", 1),
                CollectHit(5),
            ]);

        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        Assert.Equal("consent-before-tags", state.RootElement.GetProperty("sequencingVerdict").GetString());
    }

    [Fact]
    public async Task NoAnalyticsHits_NoConsentFinding()
    {
        var result = await Analyze(
            "<html><body>no consent</body></html>",
            captures: [AuditContexts.Captured("https://target.test/style.css", 1, "text/css")]);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
    }

    // ── gcs/gcd correlation ───────────────────────────────────────────────

    [Fact]
    public async Task GcsGcdParameters_AreCorrelatedAndDecoded()
    {
        var result = await Analyze(
            ConsentDefaultHtml,
            captures: [CollectHit(5, "&gcs=G100&gcd=13t3t3")]);

        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        var observations = state.RootElement.GetProperty("gcsObservations");
        Assert.Equal(1, observations.GetArrayLength());
        Assert.Equal("G100", observations[0].GetProperty("gcs").GetString());
        Assert.Equal("13t3t3", observations[0].GetProperty("gcd").GetString());
        Assert.Equal("denied", observations[0].GetProperty("adStorage").GetString());
        Assert.Equal("denied", observations[0].GetProperty("analyticsStorage").GetString());
    }

    [Fact]
    public async Task GcsGranted_DecodesGranted()
    {
        var result = await Analyze(
            ConsentDefaultHtml,
            captures: [CollectHit(5, "&gcs=G111")]);
        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        var observation = state.RootElement.GetProperty("gcsObservations")[0];
        Assert.Equal("granted", observation.GetProperty("adStorage").GetString());
        Assert.Equal("granted", observation.GetProperty("analyticsStorage").GetString());
    }

    // ── Platform presence, toggles, degradation ───────────────────────────

    [Fact]
    public async Task ConsentPlatformPresence_FromKeystoneGroupAndCatalogue()
    {
        const string html = """
            <html><head>
            <script src="https://consent.cookiebot.com/uc.js"></script>
            </head><body><div id="cookie-banner">Cookies</div></body></html>
            """;
        var result = await Analyze(html);
        using var state = JsonDocument.Parse(result.ConsentStateJson!);
        var hints = state.RootElement.GetProperty("platformHints").EnumerateArray()
            .Select(item => item.GetString()).ToList();
        Assert.Contains("cookiebot", hints);
        Assert.True(state.RootElement.GetProperty("markers").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ConsentAuditingDisabled_SuppressesFindings()
    {
        var configuration = AuditContexts.Configuration(RenderMode.Always, consentModeEnabled: false);
        var result = await Analyze(
            "<html><body>no consent</body></html>",
            captures: [CollectHit(5)],
            configuration: configuration);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing);
    }

    [Fact]
    public async Task RenderingOff_EmitsNotice_ButStaticChecksStillRun()
    {
        var result = await Analyze(
            "<html><body>no consent</body></html>",
            captures: [CollectHit(5)],
            mode: RenderMode.None);

        Assert.Contains(result.Findings, f => f.RuleKey == MarTechRuleKeys.RequiresRendering);
        Assert.Contains(result.Findings, f => f.RuleKey == MarTechRuleKeys.ConsentModeMissing
            && f.EvidenceJson.Contains("without-default"));
    }

    [Fact]
    public async Task CategoryDisabled_ProducesNothing()
    {
        var result = await Analyze(
            ConsentDefaultHtml,
            captures: [CollectHit(5)],
            options: new MarTechOptions { Enabled = false });
        Assert.Null(result.ConsentStateJson);
        Assert.False(result.ConsentModePresent);
        Assert.Empty(result.Findings);
    }
}
