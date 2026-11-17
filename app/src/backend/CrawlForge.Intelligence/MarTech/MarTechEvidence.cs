using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Structured Analytics &amp; Tagging evidence persisted with each finding
/// (FindingDraft.EvidenceJson): container IDs, bounded excerpts, network
/// evidence from the captured waterfall, and consent-timeline markers
/// (FS-CRAWL-002 §14.3).
/// </summary>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="Vendor">Vendor label, when vendor-scoped.</param>
/// <param name="ContainerId">Container/pixel identifier, when known.</param>
/// <param name="Message">Local description.</param>
/// <param name="Details">Structured details (bounded).</param>
public sealed record MarTechEvidence(
    string RuleKey,
    string? Vendor,
    string? ContainerId,
    string Message,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>Shared evidence serialization and finding helpers.</summary>
internal static class MarTechFindingFactory
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Serializes evidence with the wire camelCase convention.</summary>
    public static string Serialize(MarTechEvidence evidence) =>
        JsonSerializer.Serialize(evidence, EvidenceJsonOptions);

    /// <summary>Creates an Analytics &amp; Tagging finding draft.</summary>
    public static FindingDraft Create(
        string ruleKey,
        FindingSeverity severity,
        long urlId,
        MarTechEvidence evidence) =>
        new(ruleKey, AuditCategory.AnalyticsTagging, severity, urlId, Serialize(evidence), RecommendationKey: ruleKey);

    /// <summary>Creates the rendering-dependent degradation notice (§14.2).</summary>
    public static FindingDraft RequiresRenderingNotice(long urlId, string pass, string detail) =>
        Create(
            MarTechRuleKeys.RequiresRendering,
            FindingSeverity.Notice,
            urlId,
            new MarTechEvidence(
                MarTechRuleKeys.RequiresRendering,
                Vendor: null,
                ContainerId: null,
                $"The '{pass}' pass requires rendering mode and was not evaluated: {detail} "
                    + "Static inventory signals remain available.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pass"] = pass,
                    ["requiresRendering"] = "true",
                }));
}
