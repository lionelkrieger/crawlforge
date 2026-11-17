using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.MarTech;
using Xunit;

namespace CrawlForge.Intelligence.Tests.MarTech;

/// <summary>
/// MarTech auditor unit tests (FS-CRAWL-002 §14.1/§14.3, criterion #47):
/// catalogue detection across vendors, DOM+proxy inventory merge, tag
/// integrity findings, toggles, custom beacons, and metric mapping.
/// </summary>
public sealed class MarTechAuditorTests
{
    private const string GtmGa4Page = """
        <html><head>
        <script async src="https://www.googletagmanager.com/gtm.js?id=GTM-ABC123"></script>
        <script>window.dataLayer = window.dataLayer || [];</script>
        </head><body>
        <script src="https://www.googletagmanager.com/gtag/js?id=G-XYZ789"></script>
        <script>gtag('config', 'G-XYZ789');</script>
        </body></html>
        """;

    private static Task<MarTechAuditResult> Audit(
        string? html,
        MarTechOptions? options = null,
        IReadOnlyList<Contracts.Crawl.ProxyCapturedRequest>? captures = null,
        RenderMode mode = RenderMode.None,
        string? renderedHtml = null,
        Contracts.Entities.CrawlConfigurationDto? configuration = null,
        bool isIndexable = true,
        string? primaryContainer = null)
    {
        var auditor = new MarTechAuditor(options, primaryContainer);
        var context = AuditContexts.Create(
            html: html,
            renderedHtml: renderedHtml,
            renderingMode: mode,
            capturedRequests: captures,
            configuration: configuration,
            isIndexable: isIndexable);
        return auditor.AuditAsync(context);
    }

    // ── Catalogue detection ───────────────────────────────────────────────

    [Fact]
    public async Task Inventory_DetectsGtmAndGa4_WithIdsPlacementAndLoadFlags()
    {
        var result = await Audit(GtmGa4Page);

        var gtm = Assert.Single(result.Detections, d => d.Vendor == "google-tag-manager");
        Assert.Equal("GTM-ABC123", gtm.ContainerId);
        Assert.Equal("dom", gtm.DetectionSource);
        Assert.Equal("head", gtm.Placement);

        var ga4 = Assert.Single(result.Detections, d => d.Vendor == "google-analytics-4");
        Assert.Equal("G-XYZ789", ga4.ContainerId);
        Assert.Equal("body", ga4.Placement);
        Assert.All(result.Findings, f => Assert.Equal(AuditCategory.AnalyticsTagging, f.Category));
    }

    [Fact]
    public async Task Inventory_DetectsMetaLinkedInClarityAdsAndUniversal()
    {
        const string html = """
            <html><head>
            <script src="https://connect.facebook.net/en_US/fbevents.js"></script>
            <script>fbq('init', '123456789012345');</script>
            <script src="https://snap.licdn.com/li.lms-analytics/insight.min.js"></script>
            <script>_linkedin_partner_id = '987654';</script>
            <script src="https://www.clarity.ms/tag/abcd1234"></script>
            <script src="https://www.google-analytics.com/analytics.js"></script>
            <script>ga('create', 'UA-12345678-1', 'auto');</script>
            </head><body>
            <script>var google_conversion_id = 'AW-123456789';</script>
            </body></html>
            """;
        var result = await Audit(html);

        Assert.Contains(result.Detections, d => d.Vendor == "meta-pixel" && d.ContainerId == "123456789012345");
        Assert.Contains(result.Detections, d => d.Vendor == "linkedin-insight" && d.ContainerId == "987654");
        Assert.Contains(result.Detections, d => d.Vendor == "microsoft-clarity" && d.ContainerId == "abcd1234");
        Assert.Contains(result.Detections, d => d.Vendor == "google-analytics-universal" && d.ContainerId == "UA-12345678-1");
        Assert.Contains(result.Detections, d => d.Vendor == "google-ads" && d.ContainerId == "AW-123456789");
    }

    [Fact]
    public async Task Inventory_DetectsConsentPlatforms()
    {
        const string html = """
            <html><head>
            <script src="https://cdn.cookielaw.org/scripttemplates/otSDKStub.js"></script>
            <script src="https://consent.cookiebot.com/uc.js"></script>
            </head><body><div id="cookie-consent">We use cookies</div></body></html>
            """;
        var result = await Audit(html);
        Assert.Contains(result.Detections, d => d.Vendor == "onetrust");
        Assert.Contains(result.Detections, d => d.Vendor == "cookiebot");
    }

    [Fact]
    public async Task Inventory_NetworkOnlyDetection_FromProxyCapture()
    {
        var result = await Audit(
            "<html><body>no tags here</body></html>",
            captures:
            [
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-NET123&en=page_view", 1),
                AuditContexts.Captured("https://www.facebook.com/tr?id=5555555555&ev=PageView", 2),
            ]);

        var ga4 = Assert.Single(result.Detections, d => d.Vendor == "google-analytics-4");
        Assert.Equal("proxy", ga4.DetectionSource);
        Assert.Equal("G-NET123", ga4.ContainerId);
        Assert.Contains(result.Detections, d => d.Vendor == "meta-pixel" && d.ContainerId == "5555555555");
    }

    [Fact]
    public async Task Inventory_MergesDomAndNetwork_IntoBothSource()
    {
        var result = await Audit(
            GtmGa4Page,
            captures: [AuditContexts.Captured("https://www.googletagmanager.com/gtm.js?id=GTM-ABC123", 1)]);

        var gtm = Assert.Single(result.Detections, d => d.Vendor == "google-tag-manager");
        Assert.Equal("both", gtm.DetectionSource);
        Assert.Equal("GTM-ABC123", gtm.ContainerId);
    }

    [Fact]
    public async Task Inventory_RenderedTreePreferred_OverStatic()
    {
        const string rendered = """
            <html><head>
            <script src="https://www.googletagmanager.com/gtm.js?id=GTM-RENDER1"></script>
            </head><body></body></html>
            """;
        var result = await Audit(
            "<html><body>static only</body></html>",
            mode: RenderMode.Always,
            renderedHtml: rendered);
        var gtm = Assert.Single(result.Detections, d => d.Vendor == "google-tag-manager");
        Assert.Equal("GTM-RENDER1", gtm.ContainerId);
    }

    // ── Tag integrity findings ────────────────────────────────────────────

    [Fact]
    public async Task DuplicateContainer_SameIdInjectedTwice_EmitsWarning()
    {
        const string html = """
            <html><head>
            <script src="https://www.googletagmanager.com/gtm.js?id=GTM-DUP123"></script>
            <script src="https://www.googletagmanager.com/gtm.js?id=GTM-DUP123"></script>
            </head><body></body></html>
            """;
        var result = await Audit(html);
        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.DuplicateContainer);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("GTM-DUP123", finding.EvidenceJson);
        Assert.Contains("dom-injection", finding.EvidenceJson);
    }

    [Fact]
    public async Task DuplicateContainer_DuplicatePageViewHits_EmitsWarning_WithNetworkEvidence()
    {
        var result = await Audit(
            GtmGa4Page,
            captures:
            [
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 1),
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 2),
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=scroll", 3),
            ]);

        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.DuplicateContainer);
        Assert.Contains("network-pageview", finding.EvidenceJson);
        Assert.Contains("G-XYZ789", finding.EvidenceJson);
        Assert.Contains("capturedAt", finding.EvidenceJson);
    }

    [Fact]
    public async Task SinglePageView_NoDuplicateFinding()
    {
        var result = await Audit(
            GtmGa4Page,
            captures: [AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 1)]);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.DuplicateContainer);
    }

    [Fact]
    public async Task NoHitsObserved_ContainerInDomZeroHits_EmitsNotice()
    {
        var result = await Audit(
            GtmGa4Page,
            captures: [AuditContexts.Captured("https://target.test/style.css", 1, "text/css")]);

        var notices = result.Findings.Where(f => f.RuleKey == MarTechRuleKeys.NoHitsObserved).ToList();
        Assert.NotEmpty(notices);
        Assert.All(notices, notice => Assert.Equal(FindingSeverity.Notice, notice.Severity));
    }

    [Fact]
    public async Task NoHitsObserved_NotEmitted_WhenCaptureDisabled()
    {
        var result = await Audit(GtmGa4Page, captures: null);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.NoHitsObserved);
    }

    [Fact]
    public async Task NoHitsObserved_NotEmitted_WhenVendorHitsPresent()
    {
        var result = await Audit(
            GtmGa4Page,
            captures:
            [
                AuditContexts.Captured("https://www.googletagmanager.com/gtm.js?id=GTM-ABC123", 1),
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 2),
            ]);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.NoHitsObserved);
    }

    [Fact]
    public async Task MissingPrimary_UserOverride_EmitsWarning_OnIndexablePage()
    {
        var configuration = AuditContexts.Configuration(primaryContainerOverride: "GTM-MAIN1");
        var result = await Audit(GtmGa4Page, configuration: configuration);
        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.MissingContainer);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("GTM-MAIN1", finding.EvidenceJson);
        Assert.Contains("user-override", finding.EvidenceJson);
    }

    [Fact]
    public async Task MissingPrimary_NotIndexable_Skipped()
    {
        var configuration = AuditContexts.Configuration(primaryContainerOverride: "GTM-MAIN1");
        var result = await Audit(GtmGa4Page, configuration: configuration, isIndexable: false);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.MissingContainer);
    }

    [Fact]
    public async Task MissingPrimary_PresentOnPage_NoFinding()
    {
        var configuration = AuditContexts.Configuration(primaryContainerOverride: "GTM-ABC123");
        var result = await Audit(GtmGa4Page, configuration: configuration);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == MarTechRuleKeys.MissingContainer);
    }

    [Fact]
    public async Task MissingPrimary_AuditAggregate_UsedWhenNoOverride()
    {
        var result = await Audit(GtmGa4Page, primaryContainer: "GTM-AGG999");
        var finding = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.MissingContainer);
        Assert.Contains("audit-aggregate", finding.EvidenceJson);
    }

    [Fact]
    public void PrimaryResolver_PicksMostFrequentContainer()
    {
        var detections = new List<AnalyticsDetectionDraft>
        {
            new("google-tag-manager", "GTM-A", "dom", "head"),
            new("google-tag-manager", "GTM-B", "dom", "head"),
            new("google-tag-manager", "GTM-B", "dom", "head"),
            new("google-analytics-4", null, "proxy", null),
        };
        Assert.Equal("GTM-B", PrimaryContainerResolver.ResolvePrimary(detections));
        Assert.Null(PrimaryContainerResolver.ResolvePrimary([]));
    }

    // ── Toggles, beacons, bounds ──────────────────────────────────────────

    [Fact]
    public async Task CategoryDisabled_ProducesNothing()
    {
        var result = await Audit(GtmGa4Page, options: new MarTechOptions { Enabled = false });
        Assert.Empty(result.Detections);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Options_FromAuditTogglesJson_ParsesCategoryToggle()
    {
        Assert.False(MarTechOptions.FromAuditTogglesJson("{\"analytics\":false}").Enabled);
        Assert.False(MarTechOptions.FromAuditTogglesJson("{\"MarTech\":false}").Enabled);
        Assert.True(MarTechOptions.FromAuditTogglesJson("{\"performance\":false}").Enabled);
        Assert.True(MarTechOptions.FromAuditTogglesJson(null).Enabled);
        Assert.True(MarTechOptions.FromAuditTogglesJson("{broken").Enabled);
    }

    [Fact]
    public void Options_FromConfiguration_ConsumesAdditiveFields()
    {
        var configuration = AuditContexts.Configuration(
            consentModeEnabled: false,
            primaryContainerOverride: "GTM-OVR",
            dataLayerSchemaJson: "{\"required\":[\"event\"]}",
            beaconPatternsJson: "[{\"name\":\"pixel\",\"pattern\":\"beacon\\\\.test\",\"target\":\"network\"}]");
        var options = MarTechOptions.FromConfiguration(configuration);
        Assert.False(options.ConsentAuditingEnabled);
        Assert.Equal("GTM-OVR", options.PrimaryContainerOverride);
        Assert.Equal("{\"required\":[\"event\"]}", options.DataLayerSchemaJson);
        Assert.Single(options.CustomBeacons);
    }

    [Fact]
    public async Task CustomBeacon_NetworkPattern_DetectsLocalVendor()
    {
        var options = new MarTechOptions
        {
            CustomBeacons = [new CustomBeaconPattern("internal-pixel", @"beacon\.internal\.test/pixel", "network")],
        };
        var result = await Audit(
            "<html><body></body></html>",
            options: options,
            captures: [AuditContexts.Captured("https://beacon.internal.test/pixel?id=42", 1)]);
        Assert.Contains(result.Detections, d => d.Vendor == "custom:internal-pixel");
    }

    [Fact]
    public async Task CustomBeacon_InvalidPattern_IsolatedNotFatal()
    {
        var options = new MarTechOptions
        {
            CustomBeacons = [new CustomBeaconPattern("bad", "([unclosed", "network")],
        };
        var result = await Audit(GtmGa4Page, options: options);
        Assert.Contains(result.Detections, d => d.Vendor == "google-tag-manager");
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var auditor = new MarTechAuditor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            auditor.AuditAsync(AuditContexts.Create(html: GtmGa4Page), cts.Token));
    }

    // ── Metric contributions ──────────────────────────────────────────────

    [Fact]
    public async Task Metrics_MapStackIntegrityAndVendorCoverage()
    {
        var result = await Audit(
            GtmGa4Page,
            captures:
            [
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 1),
                AuditContexts.Captured("https://www.google-analytics.com/g/collect?tid=G-XYZ789&en=page_view", 2),
            ]);
        var metrics = MarTechMetricContributions.FromAudit(result, urlId: 7);

        Assert.Contains(metrics, m => m.MetricKey == MarTechMetricKeys.MarTechStack && m.Value == result.Detections.Count);
        Assert.Contains(metrics, m => m.MetricKey == MarTechMetricKeys.TagIntegrityIssues && m.Value >= 1);
        Assert.Contains(metrics, m => m.MetricKey == MarTechMetricKeys.AnalyticsCoverage
            && m.DimensionsJson.Contains("google-tag-manager"));
        Assert.All(metrics, m => Assert.Contains("\"urlId\":7", m.DimensionsJson));
    }

    [Fact]
    public void Metrics_ConsentAndDataLayerMaps()
    {
        var consentFinding = new FindingDraft(
            MarTechRuleKeys.ConsentModeMissing, AuditCategory.AnalyticsTagging, FindingSeverity.Warning, 7, "{}", null);
        var consent = new ConsentModeAnalysisResult("{}", true, [consentFinding]);
        var consentMetrics = MarTechMetricContributions.FromConsent(consent, 7);
        Assert.Contains(consentMetrics, m => m.MetricKey == MarTechMetricKeys.ConsentViolations && m.Value == 1);
        Assert.Contains(consentMetrics, m => m.MetricKey == MarTechMetricKeys.ConsentCompliance && m.Value == 0);

        var compliant = new ConsentModeAnalysisResult("{}", true, []);
        Assert.Contains(MarTechMetricContributions.FromConsent(compliant, 7),
            m => m.MetricKey == MarTechMetricKeys.ConsentCompliance && m.Value == 1);

        var malformed = new FindingDraft(
            MarTechRuleKeys.DataLayerMalformed, AuditCategory.AnalyticsTagging, FindingSeverity.Warning, 7, "{}", null);
        var dataLayer = new DataLayerInspectionResult("{}", false, ["issue"], [malformed]);
        Assert.Contains(MarTechMetricContributions.FromDataLayer(dataLayer, 7),
            m => m.MetricKey == MarTechMetricKeys.TagIntegrityIssues && m.Value == 1);
    }

    [Fact]
    public void Catalogue_IsVersionedAndCoversSpecVendors()
    {
        Assert.False(string.IsNullOrWhiteSpace(MarTechCatalogue.Version));
        foreach (var vendor in new[]
        {
            "google-tag-manager", "google-analytics-4", "google-ads",
            "meta-pixel", "linkedin-insight", "microsoft-clarity",
        })
        {
            Assert.NotNull(MarTechCatalogue.Find(vendor));
        }

        Assert.True(MarTechCatalogue.Signatures.Count(s => s.Kind == MarTechCatalogue.KindConsent) >= 7);
        Assert.Null(MarTechCatalogue.Find("no-such-vendor"));
    }
}
