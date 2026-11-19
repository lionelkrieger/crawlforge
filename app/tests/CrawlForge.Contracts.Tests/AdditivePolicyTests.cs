using System.Reflection;
using CrawlForge.Contracts.Entities;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Additive-only field policy (precedence rule 3): entity DTOs are immutable
/// records; every extension member marked <see cref="AdditiveMemberAttribute"/>
/// is optional (nullable); parent members are never renamed or repurposed.
/// </summary>
public class AdditivePolicyTests
{
    [Fact]
    public void AllEntityDtosAreSealedRecords()
    {
        List<string> violations = new();
        foreach (Type type in ContractAssembly.EntityTypes)
        {
            if (!type.IsSealed || !ContractAssembly.IsRecord(type))
            {
                violations.Add(type.Name);
            }
        }

        Assert.True(violations.Count == 0, "Entity DTOs must be sealed records: " + string.Join("; ", violations));
    }

    [Fact]
    public void AllEntityDtoPropertiesAreInitOnly()
    {
        List<string> violations = new();
        foreach (Type type in ContractAssembly.EntityTypes)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (PropertyInfo property in properties)
            {
                if (!ContractAssembly.IsInitOnly(property))
                {
                    violations.Add(type.Name + "." + property.Name);
                }
            }
        }

        Assert.True(violations.Count == 0, "Mutable contract properties: " + string.Join("; ", violations));
    }

    [Fact]
    public void EveryAdditiveMemberIsOptional()
    {
        NullabilityInfoContext nullability = new();
        List<string> violations = new();
        foreach (Type type in ContractAssembly.PublicTypes)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (PropertyInfo property in properties)
            {
                if (property.GetCustomAttribute<AdditiveMemberAttribute>() is null)
                {
                    continue;
                }

                NullabilityInfo info = nullability.Create(property);
                if (info.WriteState != NullabilityState.Nullable)
                {
                    violations.Add(type.Name + "." + property.Name);
                }
            }
        }

        Assert.True(violations.Count == 0, "Additive members must be nullable/optional: " + string.Join("; ", violations));
    }

    [Fact]
    public void KnownAdditiveMembersExistOnParentDtos()
    {
        // FS-CRAWL-002 Appendix A additive columns/fields on parent tables.
        Assert.NotNull(typeof(UrlRecordDto).GetProperty(nameof(UrlRecordDto.BlockReason)));
        Assert.NotNull(typeof(UrlRecordDto).GetProperty(nameof(UrlRecordDto.WafProvider)));
        Assert.NotNull(typeof(UrlRecordDto).GetProperty(nameof(UrlRecordDto.ChallengeClass)));
        Assert.NotNull(typeof(LinkEdgeDto).GetProperty(nameof(LinkEdgeDto.DomZone)));
        Assert.NotNull(typeof(LinkEdgeDto).GetProperty(nameof(LinkEdgeDto.ZoneEvidence)));
        Assert.NotNull(typeof(LinkEdgeDto).GetProperty(nameof(LinkEdgeDto.WeightedEquity)));
        Assert.NotNull(typeof(CrawlCheckpointDto).GetProperty(nameof(CrawlCheckpointDto.AimdStateJson)));
        Assert.NotNull(typeof(AuditDto).GetProperty(nameof(AuditDto.WafCatalogueVersion)));
        Assert.NotNull(typeof(AuditDto).GetProperty(nameof(AuditDto.ResolvedContentScopeJson)));
        Assert.NotNull(typeof(AuditDto).GetProperty(nameof(AuditDto.EmbeddingModelTag)));
        Assert.NotNull(typeof(AuditDto).GetProperty(nameof(AuditDto.AgentCatalogueVersion)));
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.ContentScope)));
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.IsListMode)));
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.SerpBudgets)));
        // WP-10-A1 (D-021): capture-mode surface + CR-029 redirect toggles.
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.CaptureMode)));
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.CaptureSettings)));
        Assert.NotNull(typeof(CrawlConfigurationDto).GetProperty(nameof(CrawlConfigurationDto.RedirectChecks)));
        Assert.NotNull(typeof(UrlRecordDto).GetProperty(nameof(UrlRecordDto.CaptureSource)));
        Assert.NotNull(typeof(ApplicationSettingsDto).GetProperty(nameof(ApplicationSettingsDto.AimdDefaults)));
        Assert.NotNull(typeof(ApplicationSettingsDto).GetProperty(nameof(ApplicationSettingsDto.EmbeddingProvider)));
    }

    [Fact]
    public void AdditiveMembersDefaultToNullOnBaselineConstruction()
    {
        UrlRecordDto baseline = NewBaselineUrlRecord();
        Assert.Null(baseline.BlockReason);
        Assert.Null(baseline.WafProvider);
        Assert.Null(baseline.ChallengeClass);
        Assert.Null(baseline.CaptureSource);

        LinkEdgeDto edge = new(1, 1, 1, "https://example.test/", null, null, Enums.LinkRelationFlags.None, true);
        Assert.Null(edge.DomZone);
        Assert.Null(edge.ZoneEvidence);
        Assert.Null(edge.WeightedEquity);

        // WP-10-A1: baseline configurations keep non-JS behaviour (null capture
        // mode), engine-default capture settings and all CR-029 checks enabled.
        CrawlConfigurationDto configuration = NewBaselineConfiguration();
        Assert.Null(configuration.CaptureMode);
        Assert.Null(configuration.CaptureSettings);
        Assert.Null(configuration.RedirectChecks);
    }

    [Fact]
    public void CaptureSettingsCarryTheSpecDefaults()
    {
        // FS-CRAWL-001 §5.6.1/§5.6.2 defaults (WP-10-A1 handoff): adaptive
        // sample 4 (spec range 3–5), material word-count change 20%, dual
        // storage warning 1 GiB (configurable, no hard cap), wait rule
        // 500 ms quiet / 20000 ms hard cap, pool half the logical processors
        // with minimum 2.
        Crawl.CaptureSettings settings = new();
        Assert.Equal(4, settings.AdaptiveSampleSizePerGroup);
        Assert.InRange(settings.AdaptiveSampleSizePerGroup, 3, 5);
        Assert.Equal(0.20, settings.MaterialChangeWordCountThreshold);
        Assert.Equal(1_073_741_824, settings.DualStorageWarningThresholdBytes);
        Assert.Equal(500, settings.RenderWaitQuietMs);
        Assert.Equal(20_000, settings.RenderWaitHardCapMs);
        Assert.Null(settings.RenderingPoolSize);
        Assert.Null(settings.PatternOverrides);
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), Crawl.CaptureSettings.DefaultRenderingPoolSize);
        Assert.InRange(Crawl.CaptureSettings.DefaultRenderingPoolSize, 2, int.MaxValue);
        Assert.Equal(settings, Crawl.CaptureSettings.Default);
    }

    [Fact]
    public void RedirectCheckSettingsDefaultToAllFourCasesEnabled()
    {
        // CR-029 (applied): cross-host, temporary 302/303/307, non-indexable
        // final target (CR-024), HTTPS-to-HTTP downgrade — all on by default.
        Crawl.RedirectCheckSettings settings = new();
        Assert.True(settings.CrossHost);
        Assert.True(settings.TemporaryStatus);
        Assert.True(settings.NonIndexableTarget);
        Assert.True(settings.HttpsDowngrade);
        Assert.Equal(settings, Crawl.RedirectCheckSettings.AllEnabled);

        Crawl.RedirectCheckSettings disabled = Crawl.RedirectCheckSettings.AllDisabled;
        Assert.False(disabled.CrossHost);
        Assert.False(disabled.TemporaryStatus);
        Assert.False(disabled.NonIndexableTarget);
        Assert.False(disabled.HttpsDowngrade);

        // Each case toggles independently.
        Crawl.RedirectCheckSettings partial = settings with { HttpsDowngrade = false };
        Assert.True(partial.CrossHost);
        Assert.False(partial.HttpsDowngrade);
    }

    [Fact]
    public void CaptureAndRedirectDocumentsExtendConfigurationsWithoutMutatingParents()
    {
        CrawlConfigurationDto baseline = NewBaselineConfiguration();
        CrawlConfigurationDto extended = baseline with
        {
            CaptureMode = Enums.CaptureMode.Adaptive,
            CaptureSettings = new Crawl.CaptureSettings(
                PatternOverrides: new[] { new Crawl.CapturePatternOverride("/blog/*", Enums.PatternCaptureMode.Rendered) }),
            RedirectChecks = new Crawl.RedirectCheckSettings(NonIndexableTarget: false),
        };

        Assert.Null(baseline.CaptureMode);
        Assert.Null(baseline.CaptureSettings);
        Assert.Null(baseline.RedirectChecks);
        Assert.Equal(Enums.CaptureMode.Adaptive, extended.CaptureMode);
        Assert.Equal(4, extended.CaptureSettings!.AdaptiveSampleSizePerGroup);
        Assert.Equal("/blog/*", extended.CaptureSettings.PatternOverrides![0].Pattern);
        Assert.Equal(Enums.PatternCaptureMode.Rendered, extended.CaptureSettings.PatternOverrides[0].Capture);
        Assert.True(extended.RedirectChecks!.CrossHost);
        Assert.False(extended.RedirectChecks.NonIndexableTarget);

        // URL records state their capture provenance additively (§5.6.1).
        UrlRecordDto url = NewBaselineUrlRecord() with { CaptureSource = Enums.CaptureSource.Both };
        Assert.Equal(Enums.CaptureSource.Both, url.CaptureSource);
        Assert.True(url.IsIndexable);
    }

    [Fact]
    public void RenderedPageCarriesTheNetworkWaterfallAdditively()
    {
        // WP-10-A1 (D-021): baseline renders keep the positional shape and carry
        // no waterfall; the additive member is nullable and init-only.
        Crawl.RenderedPage baseline = new(
            "https://example.test/", "<html></html>", ReadOnlyMemory<byte>.Empty,
            Array.Empty<Crawl.ConsoleMessage>(), null);
        Assert.Null(baseline.NetworkWaterfall);

        Crawl.RenderedNetworkEntry entry = new(
            Url: "https://example.test/app.js",
            ResourceType: "script",
            Method: "GET",
            StatusCode: 200,
            MimeType: "text/javascript",
            TransferredBytes: 4096,
            EncodedDataLength: 1024,
            TimingJson: "{\"requestStart\":0}",
            FromCache: false,
            FromServiceWorker: false);
        Crawl.RenderedPage extended = baseline with { NetworkWaterfall = new[] { entry } };
        Assert.Null(baseline.NetworkWaterfall);
        Assert.Single(extended.NetworkWaterfall!);
        Assert.Equal(4096, extended.NetworkWaterfall![0].TransferredBytes);
        Assert.False(extended.NetworkWaterfall[0].FromServiceWorker);

        // Unobserved optional facts default safely (CR-031 reads transferredBytes).
        Crawl.RenderedNetworkEntry minimal = new("https://example.test/", "document", "GET", 200);
        Assert.Null(minimal.MimeType);
        Assert.Equal(0, minimal.TransferredBytes);
        Assert.Equal(0, minimal.EncodedDataLength);
        Assert.Null(minimal.TimingJson);
        Assert.False(minimal.FromCache);
        Assert.False(minimal.FromServiceWorker);
    }

    [Fact]
    public void WithExpressionsExtendDtosWithoutMutatingParents()
    {
        UrlRecordDto baseline = NewBaselineUrlRecord();
        UrlRecordDto extended = baseline with
        {
            BlockReason = "waf-challenge",
            WafProvider = Enums.WafProvider.Cloudflare,
            ChallengeClass = Enums.WafChallengeClass.Interstitial,
        };

        // Parent instance is untouched; extension is additive.
        Assert.Null(baseline.BlockReason);
        Assert.Null(baseline.WafProvider);
        Assert.Equal(baseline.CanonicalUrl, extended.CanonicalUrl);
        Assert.Equal("waf-challenge", extended.BlockReason);
        Assert.Equal(Enums.WafProvider.Cloudflare, extended.WafProvider);
        Assert.Equal(Enums.WafChallengeClass.Interstitial, extended.ChallengeClass);
    }

    [Fact]
    public void AllEnumsUseInt32UnderlyingType()
    {
        // int-backed enums keep member addition cheap and serialization stable.
        List<string> violations = new();
        foreach (Type type in ContractAssembly.PublicTypes)
        {
            if (type.IsEnum && Enum.GetUnderlyingType(type) != typeof(int))
            {
                violations.Add(type.Name);
            }
        }

        Assert.True(violations.Count == 0, "Non-int enums: " + string.Join("; ", violations));
    }

    private static UrlRecordDto NewBaselineUrlRecord() => new(
        Id: 1,
        AuditId: 1,
        CanonicalUrl: "https://example.test/",
        Host: "example.test",
        Path: "/",
        Depth: 0,
        DiscoverySource: Enums.DiscoverySource.Seed,
        StatusClass: Enums.CrawlStatusClass.Success,
        FinalStatusCode: 200,
        RedirectTargetUrl: null,
        ContentType: "text/html",
        SizeBytes: 1024,
        TimingJson: null,
        RobotsState: Enums.RobotsState.Allowed,
        IndexabilityFlags: Enums.IndexabilityFlags.None,
        IsIndexable: true);

    private static CrawlConfigurationDto NewBaselineConfiguration() => new(
        Id: 1,
        ProjectId: 1,
        Name: "Baseline configuration",
        SeedsJson: "[\"https://example.test/\"]",
        ScopeRulesJson: null,
        Depth: null,
        LimitOverrides: null,
        RenderingMode: Enums.RenderMode.None,
        PolitenessJson: null,
        ProxyRoutingJson: null,
        CustomExtractionRulesJson: null,
        HeadersJson: null,
        CookiesJson: null,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
