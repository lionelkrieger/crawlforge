using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Maps MarTech audit outputs onto <see cref="MetricContribution"/> rows for
/// metric_summaries (FS-CRAWL-002 §14.4 + WP-04-04 dispatch keys). Locked
/// keys: analytics-coverage (dimension: vendor), consent-compliance.
/// Additive local keys (approved, open-keyed pattern): martech-stack,
/// tag-integrity-issues, consent-violations. Phase-05 aggregates these for
/// the Analytics &amp; Tagging Explorer and the /api/audits/{id}/analytics
/// + MCP analytics-coverage surfaces.
/// </summary>
public static class MarTechMetricContributions
{
    private static readonly JsonSerializerOptions DimensionsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Builds url-scoped dimensions.</summary>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Serialized dimensions JSON.</returns>
    public static string Dimensions(long urlId) =>
        JsonSerializer.Serialize(new { urlId }, DimensionsOptions);

    /// <summary>Builds url+vendor-scoped dimensions.</summary>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <param name="vendor">Vendor label.</param>
    /// <returns>Serialized dimensions JSON.</returns>
    public static string VendorDimensions(long urlId, string vendor) =>
        JsonSerializer.Serialize(new { urlId, vendor }, DimensionsOptions);

    /// <summary>
    /// Maps the tag inventory and integrity findings: martech-stack size,
    /// tag-integrity issue count, and per-vendor analytics-coverage.
    /// </summary>
    /// <param name="result">MarTech audit result.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Metric contributions.</returns>
    public static IReadOnlyList<MetricContribution> FromAudit(MarTechAuditResult result, long urlId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var metrics = new List<MetricContribution>
        {
            new(MarTechMetricKeys.MarTechStack, Dimensions(urlId), result.Detections.Count),
            new(MarTechMetricKeys.TagIntegrityIssues, Dimensions(urlId), CountIntegrityIssues(result.Findings)),
        };

        foreach (var group in result.Detections.GroupBy(detection => detection.Vendor, StringComparer.Ordinal))
        {
            metrics.Add(new MetricContribution(
                MarTechMetricKeys.AnalyticsCoverage,
                VendorDimensions(urlId, group.Key),
                group.Count()));
        }

        return metrics;
    }

    /// <summary>
    /// Maps consent analysis: consent-violations count and the locked
    /// consent-compliance indicator (1 compliant / 0 violating).
    /// </summary>
    /// <param name="result">Consent analysis result.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Metric contributions.</returns>
    public static IReadOnlyList<MetricContribution> FromConsent(ConsentModeAnalysisResult result, long urlId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var violations = result.Findings.Count(finding => finding.RuleKey == MarTechRuleKeys.ConsentModeMissing);
        return
        [
            new(MarTechMetricKeys.ConsentViolations, Dimensions(urlId), violations),
            new(MarTechMetricKeys.ConsentCompliance, Dimensions(urlId), violations == 0 ? 1 : 0),
        ];
    }

    /// <summary>
    /// Maps dataLayer inspection: malformed-payload findings add to the
    /// tag-integrity issue count.
    /// </summary>
    /// <param name="result">dataLayer inspection result.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Metric contributions.</returns>
    public static IReadOnlyList<MetricContribution> FromDataLayer(DataLayerInspectionResult result, long urlId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var issues = result.Findings.Count(finding => finding.RuleKey == MarTechRuleKeys.DataLayerMalformed);
        return [new(MarTechMetricKeys.TagIntegrityIssues, Dimensions(urlId), issues)];
    }

    private static int CountIntegrityIssues(IReadOnlyList<FindingDraft> findings) =>
        findings.Count(finding => finding.RuleKey is MarTechRuleKeys.DuplicateContainer
            or MarTechRuleKeys.MissingContainer
            or MarTechRuleKeys.NoHitsObserved);
}
