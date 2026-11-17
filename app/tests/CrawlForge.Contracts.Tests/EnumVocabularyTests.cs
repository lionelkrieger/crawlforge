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
}
