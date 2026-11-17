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

        LinkEdgeDto edge = new(1, 1, 1, "https://example.test/", null, null, Enums.LinkRelationFlags.None, true);
        Assert.Null(edge.DomZone);
        Assert.Null(edge.ZoneEvidence);
        Assert.Null(edge.WeightedEquity);
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
}
