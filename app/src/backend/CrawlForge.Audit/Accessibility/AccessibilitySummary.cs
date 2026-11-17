using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// Aggregated accessibility surface for one page or audit (FS-CRAWL-001
/// §7.10): severity counts, per-family/per-rule distribution, and the
/// render-dependent skip count. Severity counts align with the scoring
/// service inputs (§8.1): findings flow to scoring through their category.
/// </summary>
/// <param name="RuleSetVersion">Bundled rule-set version that produced the findings.</param>
/// <param name="TotalFindings">Total accessibility findings aggregated.</param>
/// <param name="ErrorCount">Error-severity count.</param>
/// <param name="WarningCount">Warning-severity count.</param>
/// <param name="NoticeCount">Notice-severity count (includes render-dependent skips).</param>
/// <param name="PassCount">Pass-severity count.</param>
/// <param name="RenderDependentSkippedCount">Checks flagged as requiring rendering.</param>
/// <param name="CountByFamily">Finding count per rule family.</param>
/// <param name="CountByRule">Finding count per stable rule key.</param>
public sealed record AccessibilitySummary(
    string RuleSetVersion,
    int TotalFindings,
    int ErrorCount,
    int WarningCount,
    int NoticeCount,
    int PassCount,
    int RenderDependentSkippedCount,
    IReadOnlyDictionary<string, int> CountByFamily,
    IReadOnlyDictionary<string, int> CountByRule);

/// <summary>
/// Builds the accessibility report aggregation from persisted or in-flight
/// findings. Pure local computation over finding evidence.
/// </summary>
public static class AccessibilitySummaryBuilder
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Aggregates accessibility findings into the report surface.</summary>
    /// <param name="findings">Findings of any category; non-accessibility findings are ignored.</param>
    /// <returns>The aggregated accessibility summary.</returns>
    public static AccessibilitySummary FromFindings(IEnumerable<FindingDraft> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var total = 0;
        var errors = 0;
        var warnings = 0;
        var notices = 0;
        var passes = 0;
        var renderSkipped = 0;
        var byFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            if (finding.Category != AuditCategory.Accessibility)
            {
                continue;
            }

            total++;
            switch (finding.Severity)
            {
                case FindingSeverity.Error:
                    errors++;
                    break;
                case FindingSeverity.Warning:
                    warnings++;
                    break;
                case FindingSeverity.Notice:
                    notices++;
                    break;
                case FindingSeverity.Pass:
                    passes++;
                    break;
            }

            var family = ResolveFamily(finding);
            byFamily.TryGetValue(family, out var familyCount);
            byFamily[family] = familyCount + 1;

            byRule.TryGetValue(finding.RuleKey, out var ruleCount);
            byRule[finding.RuleKey] = ruleCount + 1;

            if (IsRenderDependentSkip(finding))
            {
                renderSkipped++;
            }
        }

        return new AccessibilitySummary(
            AccessibilityRuleSet.Version,
            total,
            errors,
            warnings,
            notices,
            passes,
            renderSkipped,
            byFamily,
            byRule);
    }

    private static string ResolveFamily(FindingDraft finding)
    {
        var descriptor = AccessibilityRuleSet.Find(finding.RuleKey);
        if (descriptor is not null)
        {
            return descriptor.Family;
        }

        // Evidence carries the family for forward-compatible rule keys.
        try
        {
            using var document = JsonDocument.Parse(finding.EvidenceJson);
            if (document.RootElement.TryGetProperty("family", out var family)
                && family.ValueKind == JsonValueKind.String)
            {
                return family.GetString() ?? "unknown";
            }
        }
        catch (JsonException)
        {
            // Malformed evidence: fall through to the unknown family.
        }

        return "unknown";
    }

    private static bool IsRenderDependentSkip(FindingDraft finding)
    {
        try
        {
            var evidence = JsonSerializer.Deserialize<AccessibilityEvidence>(finding.EvidenceJson, EvidenceJsonOptions);
            return evidence?.RequiresRendering == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
