using System.Text.Json;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Structured performance evidence persisted with each finding
/// (FindingDraft.EvidenceJson) for the report surface and offline
/// re-reporting: measured value, threshold/band, and local details.
/// </summary>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="Metric">Metric key, when metric-scoped.</param>
/// <param name="Value">Measured value, when available.</param>
/// <param name="Threshold">Applied threshold, when any.</param>
/// <param name="Band">CWV band: good / needs-improvement / poor.</param>
/// <param name="Message">Local description.</param>
/// <param name="Details">Structured details.</param>
public sealed record PerformanceEvidence(
    string RuleKey,
    string? Metric,
    double? Value,
    double? Threshold,
    string? Band,
    string Message,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>Shared evidence serialization and finding helpers.</summary>
internal static class PerformanceFindingFactory
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Serializes evidence with the wire camelCase convention.</summary>
    public static string SerializeEvidence(PerformanceEvidence evidence) =>
        JsonSerializer.Serialize(evidence, EvidenceJsonOptions);

    /// <summary>Creates a performance finding draft.</summary>
    public static FindingDraft Create(
        string ruleKey,
        Contracts.Enums.FindingSeverity severity,
        long urlId,
        PerformanceEvidence evidence) =>
        new(ruleKey, Contracts.Enums.AuditCategory.Performance, severity, urlId,
            SerializeEvidence(evidence), RecommendationKey: ruleKey);

    /// <summary>Creates the render-dependent skip notice.</summary>
    public static FindingDraft RequiresRenderingNotice(long urlId, string measurement, bool renderingEnabled) =>
        Create(
            PerformanceRuleKeys.RequiresRendering,
            Contracts.Enums.FindingSeverity.Notice,
            urlId,
            new PerformanceEvidence(
                PerformanceRuleKeys.RequiresRendering,
                Metric: null,
                Value: null,
                Threshold: null,
                Band: null,
                $"Measurement '{measurement}' requires rendering and was not evaluated: "
                    + (renderingEnabled
                        ? "no rendered measurement is available for this page."
                        : "rendering mode is disabled for this audit."),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["measurement"] = measurement,
                    ["requiresRendering"] = "true",
                    ["renderingEnabled"] = renderingEnabled ? "true" : "false",
                }));

    /// <summary>Creates the local probe-failure notice.</summary>
    public static FindingDraft ProbeUnavailableNotice(long urlId, string measurement, string reason) =>
        Create(
            PerformanceRuleKeys.ProbeUnavailable,
            Contracts.Enums.FindingSeverity.Notice,
            urlId,
            new PerformanceEvidence(
                PerformanceRuleKeys.ProbeUnavailable,
                Metric: null,
                Value: null,
                Threshold: null,
                Band: null,
                $"Measurement '{measurement}' could not be collected: {reason}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["measurement"] = measurement,
                    ["reason"] = reason,
                }));
}
