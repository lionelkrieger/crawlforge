using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Enum vocabulary locks derived from the specification surface: parent
/// severity vocabulary, additive categories, list discovery source, the
/// six-zone DOM classification, and WAF provider/challenge labels.
/// </summary>
public class EnumVocabularyTests
{
    [Fact]
    public void SeverityUsesTheParentVocabulary()
    {
        string[] names = Enum.GetNames<FindingSeverity>();
        Assert.Equal(4, names.Length);
        Assert.Contains("Error", names);
        Assert.Contains("Warning", names);
        Assert.Contains("Notice", names);
        Assert.Contains("Pass", names);
    }

    [Fact]
    public void CategoriesIncludeTheThreeAdditiveCategories()
    {
        string[] names = Enum.GetNames<AuditCategory>();
        Assert.Contains("LogIntelligence", names);
        Assert.Contains("AiSearchGeo", names);
        Assert.Contains("AnalyticsTagging", names);
        Assert.Contains("Indexability", names);
        Assert.Contains("Content", names);
        Assert.Contains("Linking", names);
    }

    [Fact]
    public void CategoriesAppendTheThreeSpecTaxonomyCategoriesWithoutMovingParentValues()
    {
        // Amendment WP-10-A1 (D-021): FS-CRAWL-001 §7.4 names Images,
        // Security & protocol and Internationalization; the locked enum gains
        // exactly those three members, appended after AnalyticsTagging = 10.
        Assert.Equal(0, (int)AuditCategory.Indexability);
        Assert.Equal(1, (int)AuditCategory.Content);
        Assert.Equal(2, (int)AuditCategory.Linking);
        Assert.Equal(3, (int)AuditCategory.StructuredData);
        Assert.Equal(4, (int)AuditCategory.Accessibility);
        Assert.Equal(5, (int)AuditCategory.Performance);
        Assert.Equal(6, (int)AuditCategory.Crawl);
        Assert.Equal(7, (int)AuditCategory.Custom);
        Assert.Equal(8, (int)AuditCategory.LogIntelligence);
        Assert.Equal(9, (int)AuditCategory.AiSearchGeo);
        Assert.Equal(10, (int)AuditCategory.AnalyticsTagging);
        Assert.Equal(11, (int)AuditCategory.Images);
        Assert.Equal(12, (int)AuditCategory.SecurityAndProtocol);
        Assert.Equal(13, (int)AuditCategory.Internationalization);
        Assert.Equal(14, Enum.GetValues<AuditCategory>().Length);
    }

    [Fact]
    public void CategoryWireNamesFollowTheLowercaseConvention()
    {
        // The Api converter lowercases member names (frozen convention; the
        // frontend IssueExplorer/score surfaces key on these exact tokens).
        Assert.Equal("indexability", EnumWireNames.ToWireValue(AuditCategory.Indexability));
        Assert.Equal("structureddata", EnumWireNames.ToWireValue(AuditCategory.StructuredData));
        Assert.Equal("logintelligence", EnumWireNames.ToWireValue(AuditCategory.LogIntelligence));
        Assert.Equal("aisearchgeo", EnumWireNames.ToWireValue(AuditCategory.AiSearchGeo));
        Assert.Equal("analyticstagging", EnumWireNames.ToWireValue(AuditCategory.AnalyticsTagging));
        Assert.Equal("images", EnumWireNames.ToWireValue(AuditCategory.Images));
        Assert.Equal("securityandprotocol", EnumWireNames.ToWireValue(AuditCategory.SecurityAndProtocol));
        Assert.Equal("internationalization", EnumWireNames.ToWireValue(AuditCategory.Internationalization));
        foreach (AuditCategory category in Enum.GetValues<AuditCategory>())
        {
            Assert.True(EnumWireNames.TryParse<AuditCategory>(EnumWireNames.ToWireValue(category), out var parsed));
            Assert.Equal(category, parsed);
        }
    }

    [Fact]
    public void DiscoverySourceIncludesList()
    {
        Assert.Contains("List", Enum.GetNames<DiscoverySource>());
    }

    [Fact]
    public void DomZoneAppendsBreadcrumbWithoutMovingParentValues()
    {
        // Amendment WP-03-A1: FS-CRAWL-002 §15.1/§15.2 require a distinct breadcrumb
        // zone (default weight 0.6); parent values keep their locked numbers and
        // Other remains the residual region.
        Assert.Equal(0, (int)DomZone.Header);
        Assert.Equal(1, (int)DomZone.Navigation);
        Assert.Equal(2, (int)DomZone.MainContent);
        Assert.Equal(3, (int)DomZone.Sidebar);
        Assert.Equal(4, (int)DomZone.Footer);
        Assert.Equal(5, (int)DomZone.Other);
        Assert.Equal(6, (int)DomZone.Breadcrumb);
        Assert.Equal(7, Enum.GetValues<DomZone>().Length);
    }

    [Fact]
    public void TextZoneHasExactlyFourZones()
    {
        Assert.Equal(4, Enum.GetValues<TextZone>().Length);
    }

    [Fact]
    public void WafProviderIncludesThePublicClassificationLabels()
    {
        string[] names = Enum.GetNames<WafProvider>();
        Assert.Contains("Cloudflare", names);
        Assert.Contains("DataDome", names);
        Assert.Contains("Akamai", names);
        Assert.Contains("Imperva", names);
        Assert.Contains("PerimeterX", names);
    }

    [Fact]
    public void WafChallengeClassCoversCaptchaInterstitialBotBlock()
    {
        string[] names = Enum.GetNames<WafChallengeClass>();
        Assert.Contains("Captcha", names);
        Assert.Contains("Interstitial", names);
        Assert.Contains("BotBlock", names);
    }

    [Fact]
    public void WafChallengeClassAppendsManagedChallengeAndRateLimitSoftBlock()
    {
        // Amendment WP-10-A1 (D-021): FS-CRAWL-002 §2.3.1 lists five challenge
        // classes; the locked enum gains the two missing members, appended
        // after BotBlock = 3. Existing numbers and wire tokens stay frozen.
        Assert.Equal(0, (int)WafChallengeClass.Unknown);
        Assert.Equal(1, (int)WafChallengeClass.Captcha);
        Assert.Equal(2, (int)WafChallengeClass.Interstitial);
        Assert.Equal(3, (int)WafChallengeClass.BotBlock);
        Assert.Equal(4, (int)WafChallengeClass.ManagedChallenge);
        Assert.Equal(5, (int)WafChallengeClass.RateLimitSoftBlock);
        Assert.Equal(6, Enum.GetValues<WafChallengeClass>().Length);
    }

    [Fact]
    public void WafChallengeClassWireNamesUseTheSpecTokens()
    {
        // Baseline wire tokens are frozen lowercase names; the two new classes
        // use the hyphenated §2.3.1 tokens (catch-up precedent).
        Assert.Equal("unknown", EnumWireNames.ToWireValue(WafChallengeClass.Unknown));
        Assert.Equal("captcha", EnumWireNames.ToWireValue(WafChallengeClass.Captcha));
        Assert.Equal("interstitial", EnumWireNames.ToWireValue(WafChallengeClass.Interstitial));
        Assert.Equal("botblock", EnumWireNames.ToWireValue(WafChallengeClass.BotBlock));
        Assert.Equal("managed-challenge", EnumWireNames.ToWireValue(WafChallengeClass.ManagedChallenge));
        Assert.Equal("rate-limit-soft-block", EnumWireNames.ToWireValue(WafChallengeClass.RateLimitSoftBlock));
        Assert.True(EnumWireNames.TryParse<WafChallengeClass>("managed-challenge", out var managed));
        Assert.Equal(WafChallengeClass.ManagedChallenge, managed);
        Assert.True(EnumWireNames.TryParse<WafChallengeClass>("RATE-LIMIT-SOFT-BLOCK", out var rateLimit));
        Assert.Equal(WafChallengeClass.RateLimitSoftBlock, rateLimit);
        Assert.False(EnumWireNames.TryParse<WafChallengeClass>("managedchallenge", out _));
    }

    [Fact]
    public void IndexabilityFlagsAppendCanonicalElsewhereWithoutMovingParentBits()
    {
        // Amendment WP-10-A1 (D-021; CR-024): a canonical pointing elsewhere
        // makes the URL non-search-indexable. The new bit is 16; the parent
        // bits keep their locked values and the enum stays [Flags].
        Assert.Equal(0, (int)IndexabilityFlags.None);
        Assert.Equal(1, (int)IndexabilityFlags.Noindex);
        Assert.Equal(2, (int)IndexabilityFlags.Nofollow);
        Assert.Equal(4, (int)IndexabilityFlags.RobotsBlocked);
        Assert.Equal(8, (int)IndexabilityFlags.NonIndexableStatus);
        Assert.Equal(16, (int)IndexabilityFlags.CanonicalElsewhere);
        Assert.Equal(6, Enum.GetValues<IndexabilityFlags>().Length);
        Assert.NotNull(typeof(IndexabilityFlags).GetCustomAttributes(typeof(FlagsAttribute), false).FirstOrDefault());

        IndexabilityFlags combined = IndexabilityFlags.Noindex | IndexabilityFlags.CanonicalElsewhere;
        Assert.True(combined.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.True(combined.HasFlag(IndexabilityFlags.Noindex));
        Assert.False(combined.HasFlag(IndexabilityFlags.Nofollow));
        Assert.Equal(17, (int)combined);
    }

    [Fact]
    public void CaptureModeLocksTheFourSpecModesAndWireTokens()
    {
        // Amendment WP-10-A1 (D-021): FS-CRAWL-001 §5.6.1 capture modes.
        // NonJs = 0 is the default for new configurations; the non-JS wire
        // token is hyphenated per the catch-up convention.
        Assert.Equal(0, (int)CaptureMode.NonJs);
        Assert.Equal(1, (int)CaptureMode.Js);
        Assert.Equal(2, (int)CaptureMode.Dual);
        Assert.Equal(3, (int)CaptureMode.Adaptive);
        Assert.Equal(4, Enum.GetValues<CaptureMode>().Length);
        Assert.Equal("non-js", EnumWireNames.ToWireValue(CaptureMode.NonJs));
        Assert.Equal("js", EnumWireNames.ToWireValue(CaptureMode.Js));
        Assert.Equal("dual", EnumWireNames.ToWireValue(CaptureMode.Dual));
        Assert.Equal("adaptive", EnumWireNames.ToWireValue(CaptureMode.Adaptive));
        Assert.True(EnumWireNames.TryParse<CaptureMode>("non-js", out var nonJs));
        Assert.Equal(CaptureMode.NonJs, nonJs);
        Assert.False(EnumWireNames.TryParse<CaptureMode>("nonJs", out _));
    }

    [Fact]
    public void PatternCaptureModeLocksRenderedAndNonRendered()
    {
        // FS-CRAWL-001 §5.6.1 manual override: rendered or non-rendered.
        Assert.Equal(0, (int)PatternCaptureMode.NonRendered);
        Assert.Equal(1, (int)PatternCaptureMode.Rendered);
        Assert.Equal(2, Enum.GetValues<PatternCaptureMode>().Length);
        Assert.Equal("non-rendered", EnumWireNames.ToWireValue(PatternCaptureMode.NonRendered));
        Assert.Equal("rendered", EnumWireNames.ToWireValue(PatternCaptureMode.Rendered));
    }

    [Fact]
    public void CaptureSourceLocksRawRenderedBoth()
    {
        // FS-CRAWL-001 §5.6.1: every URL record states which capture produced it.
        Assert.Equal(0, (int)CaptureSource.Raw);
        Assert.Equal(1, (int)CaptureSource.Rendered);
        Assert.Equal(2, (int)CaptureSource.Both);
        Assert.Equal(3, Enum.GetValues<CaptureSource>().Length);
        Assert.Equal("raw", EnumWireNames.ToWireValue(CaptureSource.Raw));
        Assert.Equal("rendered", EnumWireNames.ToWireValue(CaptureSource.Rendered));
        Assert.Equal("both", EnumWireNames.ToWireValue(CaptureSource.Both));
    }

    [Fact]
    public void DiscoverySourceAppendsRenderWithoutMovingParentValues()
    {
        // Amendment WP-10-A1 (D-021): JS-only links discovered from the rendered
        // DOM are recorded as discovered-by-render (§5.6.1). List = 4 stays.
        Assert.Equal(0, (int)DiscoverySource.Seed);
        Assert.Equal(1, (int)DiscoverySource.Link);
        Assert.Equal(2, (int)DiscoverySource.Sitemap);
        Assert.Equal(3, (int)DiscoverySource.Redirect);
        Assert.Equal(4, (int)DiscoverySource.List);
        Assert.Equal(5, (int)DiscoverySource.Render);
        Assert.Equal(6, Enum.GetValues<DiscoverySource>().Length);
        Assert.Equal("render", EnumWireNames.ToWireValue(DiscoverySource.Render));
    }

    [Fact]
    public void ContentArtifactKindAppendsNetworkWaterfallWithoutMovingParentValues()
    {
        // Amendment WP-10-A1 (D-021): the rendered-capture network waterfall is
        // stored as its own artifact kind (§5.6.2; WP-10-01 render result).
        Assert.Equal(0, (int)ContentArtifactKind.Body);
        Assert.Equal(1, (int)ContentArtifactKind.Screenshot);
        Assert.Equal(2, (int)ContentArtifactKind.RenderedDom);
        Assert.Equal(3, (int)ContentArtifactKind.NetworkWaterfall);
        Assert.Equal(4, Enum.GetValues<ContentArtifactKind>().Length);
        Assert.Equal("networkwaterfall", EnumWireNames.ToWireValue(ContentArtifactKind.NetworkWaterfall));
    }

    [Fact]
    public void EnumWireNamesPreserveEveryLockedLegacyWireValue()
    {
        // The catch-up exception is frozen; every other pre-amendment member
        // keeps its lowercase wire name byte for byte.
        Assert.Equal("catch-up", EnumWireNames.ToWireValue(ScheduleTriggerType.CatchUp));
        Assert.Equal("scheduled", EnumWireNames.ToWireValue(ScheduleTriggerType.Scheduled));
        Assert.Equal("manual", EnumWireNames.ToWireValue(ScheduleTriggerType.Manual));
        Assert.Equal("none", EnumWireNames.ToWireValue(RenderMode.None));
        Assert.Equal("auto", EnumWireNames.ToWireValue(RenderMode.Auto));
        Assert.Equal("always", EnumWireNames.ToWireValue(RenderMode.Always));
        Assert.Equal("other", EnumWireNames.ToWireValue(WafProvider.Other));
        Assert.Equal("cloudflare", EnumWireNames.ToWireValue(WafProvider.Cloudflare));
        Assert.Equal("error", EnumWireNames.ToWireValue(FindingSeverity.Error));
        Assert.Equal("pass", EnumWireNames.ToWireValue(FindingSeverity.Pass));
    }

    [Fact]
    public void StatusClassCoversHttpClassesAndBlocked()
    {
        string[] names = Enum.GetNames<CrawlStatusClass>();
        Assert.Contains("Success", names);
        Assert.Contains("Redirect", names);
        Assert.Contains("ClientError", names);
        Assert.Contains("ServerError", names);
        Assert.Contains("Blocked", names);
        Assert.Contains("FetchError", names);
    }

    [Fact]
    public void EmbeddingProvidersAreLocalOnlyKinds()
    {
        string[] names = Enum.GetNames<EmbeddingProviderKind>();
        Assert.Equal(2, names.Length);
        Assert.Contains("BundledOnnx", names);
        Assert.Contains("LoopbackDaemon", names);
    }

    [Fact]
    public void GovernancePurposesCoverSearchRagTraining()
    {
        string[] names = Enum.GetNames<AiAgentPurpose>();
        Assert.Equal(3, names.Length);
        Assert.Contains("Search", names);
        Assert.Contains("Rag", names);
        Assert.Contains("Training", names);
    }

    [Fact]
    public void PluginPermissionsAreReadOnlyByConstruction()
    {
        // Exclusion E5: plugins get read-only data + UI contribution rights only.
        string[] names = Enum.GetNames<PluginPermission>();
        Assert.Contains("None", names);
        Assert.Contains("ReadData", names);
        Assert.Contains("ContributeUi", names);
        Assert.Equal(3, names.Length);
    }

    [Fact]
    public void ScheduleTriggerTypeAppendsCatchUpWithoutMovingParentValues()
    {
        // Amendment WP-02-A1: run history distinguishes catch-up firings
        // (FS-CRAWL-002 §6.2/§6.3); parent values keep their locked numbers.
        Assert.Equal(0, (int)ScheduleTriggerType.Scheduled);
        Assert.Equal(1, (int)ScheduleTriggerType.Manual);
        Assert.Equal(2, (int)ScheduleTriggerType.CatchUp);
        Assert.Equal(3, Enum.GetValues<ScheduleTriggerType>().Length);
    }

    [Fact]
    public void FindingRuleKeysPromoteThePhase09BuilderLocalKeysVerbatim()
    {
        // Amendment WP-10-A1 (D-021; WIRING-NOTES notation gap 6): the stable
        // strings defined locally by the phase-09 builders join the locked
        // catalogue byte for byte — never repurposed.
        Assert.Equal("crawler-trap.detected", Constants.FindingRuleKeys.CrawlerTrapDetected);
        Assert.Equal("canonicalization.session-segment-removed", Constants.FindingRuleKeys.CanonicalizationSessionSegmentRemoved);
        Assert.Equal("crawl.cookieless-redirect-loop", Constants.FindingRuleKeys.CrawlCookielessRedirectLoop);
        Assert.Equal("indexability.robots-blocked", Constants.FindingRuleKeys.IndexabilityRobotsBlocked);
        Assert.Equal("ua-comparison.material-difference", Constants.FindingRuleKeys.UaComparisonMaterialDifference);
        Assert.Equal("redirects.loop", Constants.FindingRuleKeys.RedirectsLoop);
        Assert.Equal("redirects.inappropriate", Constants.FindingRuleKeys.RedirectsInappropriate);
        Assert.Equal("redirects.chain", Constants.FindingRuleKeys.RedirectsChain);
        Assert.Equal("content.duplicate-title", Constants.FindingRuleKeys.ContentDuplicateTitle);
        Assert.Equal("content.duplicate-description", Constants.FindingRuleKeys.ContentDuplicateDescription);
        Assert.Equal("content.duplicate-content", Constants.FindingRuleKeys.ContentDuplicateContent);
        Assert.Equal("url.session-parameters", Constants.FindingRuleKeys.UrlSessionParameters);
        Assert.Equal("url.tracking-parameters", Constants.FindingRuleKeys.UrlTrackingParameters);
        Assert.Equal("url.trailing-slash-inconsistency", Constants.FindingRuleKeys.UrlTrailingSlashInconsistency);
        Assert.Equal("url.duplicate-urls", Constants.FindingRuleKeys.UrlDuplicateUrls);
        Assert.Equal("image.oversized", Constants.FindingRuleKeys.ImageOversized);
        Assert.Equal("image.format", Constants.FindingRuleKeys.ImageFormat);
        Assert.Equal("structured-data.missing-recommended", Constants.FindingRuleKeys.StructuredDataMissingRecommended);
    }

    [Fact]
    public void FindingRuleKeysLockTheCr029CasesAndCr030DeprecatedTerm()
    {
        // CR-029 (applied): four independently toggleable inappropriate-redirect
        // cases; CR-030 (applied): deprecated structured-data terms.
        Assert.Equal("redirects.inappropriate-cross-host", Constants.FindingRuleKeys.RedirectsInappropriateCrossHost);
        Assert.Equal("redirects.inappropriate-temporary-status", Constants.FindingRuleKeys.RedirectsInappropriateTemporaryStatus);
        Assert.Equal("redirects.inappropriate-non-indexable-target", Constants.FindingRuleKeys.RedirectsInappropriateNonIndexableTarget);
        Assert.Equal("redirects.inappropriate-https-downgrade", Constants.FindingRuleKeys.RedirectsInappropriateHttpsDowngrade);
        Assert.Equal("structured-data.deprecated-term", Constants.FindingRuleKeys.StructuredDataDeprecatedTerm);
    }

    [Fact]
    public void FindingRuleKeysLockThePhase10CaptureAndComparisonKeys()
    {
        // §5.6.1 adaptive switch finding; §7.18 response-vs-rendered deltas
        // (the seven phase-09 keys promoted verbatim plus the canonical /
        // meta-robots / noindex directive deltas); §7.19 service-worker notice.
        Assert.Equal("content.javascript-dependent-template", Constants.FindingRuleKeys.ContentJavascriptDependentTemplate);
        Assert.Equal("render.status-delta", Constants.FindingRuleKeys.RenderStatusDelta);
        Assert.Equal("render.title-delta", Constants.FindingRuleKeys.RenderTitleDelta);
        Assert.Equal("render.metadata-delta", Constants.FindingRuleKeys.RenderMetadataDelta);
        Assert.Equal("render.text-delta", Constants.FindingRuleKeys.RenderTextDelta);
        Assert.Equal("render.links-delta", Constants.FindingRuleKeys.RenderLinksDelta);
        Assert.Equal("render.structured-data-delta", Constants.FindingRuleKeys.RenderStructuredDataDelta);
        Assert.Equal("render.resources-delta", Constants.FindingRuleKeys.RenderResourcesDelta);
        Assert.Equal("render.canonical-delta", Constants.FindingRuleKeys.RenderCanonicalDelta);
        Assert.Equal("render.meta-robots-delta", Constants.FindingRuleKeys.RenderMetaRobotsDelta);
        Assert.Equal("render.noindex-delta", Constants.FindingRuleKeys.RenderNoindexDelta);
        Assert.Equal("render.service-worker-registered", Constants.FindingRuleKeys.RenderServiceWorkerRegistered);
    }

    [Fact]
    public void FindingRuleKeysStayUniqueAndKeepTheBaselineVocabulary()
    {
        // Additive-only evolution: baseline keys are frozen, the catalog grows
        // to 65 keys (30 baseline + 35 amendment) and no value repeats.
        Assert.Equal("waf-challenge-detected", Constants.FindingRuleKeys.WafChallengeDetected);
        Assert.Equal("waf-block-rate", Constants.FindingRuleKeys.WafBlockRate);
        Assert.Equal("redirect-matrix.pass", Constants.FindingRuleKeys.RedirectMatrixPass);
        Assert.Equal("hreflang.header-html-conflict", Constants.FindingRuleKeys.HreflangHeaderHtmlConflict);

        string[] values = typeof(Constants.FindingRuleKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.Equal(65, values.Length);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }
}
