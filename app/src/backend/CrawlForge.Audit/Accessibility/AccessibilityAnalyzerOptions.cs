using System.Text.Json;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// Per-rule user override (FS-CRAWL-001 §7.10): rules are individually
/// enable-able and severity-mappable. Null fields keep the bundled default.
/// </summary>
/// <param name="Enabled">Rule enablement; null keeps the default (enabled).</param>
/// <param name="Severity">Severity mapping; null keeps the bundled default.</param>
public sealed record AccessibilityRuleOverride(bool? Enabled = null, FindingSeverity? Severity = null);

/// <summary>
/// Local accessibility analyzer options. The category toggle mirrors the
/// per-category audit enablement payload (application settings
/// AuditTogglesJson); everything is resolved locally — no remote resolution.
/// </summary>
public sealed class AccessibilityAnalyzerOptions
{
    private static readonly JsonSerializerOptions ToggleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Category enablement (optional category, FS-CRAWL-001 §7.10). When false
    /// the analyzer produces no findings.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Per-rule overrides keyed by stable rule key.</summary>
    public IReadOnlyDictionary<string, AccessibilityRuleOverride> RuleOverrides { get; init; } =
        new Dictionary<string, AccessibilityRuleOverride>(StringComparer.Ordinal);

    /// <summary>Tests whether a rule is enabled under these options.</summary>
    /// <param name="ruleKey">Stable rule key.</param>
    /// <returns>True when the category and the rule are enabled.</returns>
    public bool IsRuleEnabled(string ruleKey) =>
        Enabled && (!RuleOverrides.TryGetValue(ruleKey, out var ruleOverride) || ruleOverride.Enabled != false);

    /// <summary>Resolves the effective severity for a rule.</summary>
    /// <param name="rule">Bundled rule descriptor.</param>
    /// <returns>The user-mapped severity, or the bundled default.</returns>
    public FindingSeverity ResolveSeverity(AccessibilityRuleDescriptor rule) =>
        RuleOverrides.TryGetValue(rule.Key, out var ruleOverride) && ruleOverride.Severity is { } severity
            ? severity
            : rule.DefaultSeverity;

    /// <summary>
    /// Builds options from the per-category audit enablement payload
    /// (AuditTogglesJson). The payload is a JSON object mapping category names
    /// (case-insensitive, e.g. "accessibility") to booleans; missing or
    /// malformed payloads keep the baseline default: all categories enabled.
    /// </summary>
    /// <param name="auditTogglesJson">The enablement payload, when present.</param>
    /// <param name="ruleOverrides">Optional per-rule overrides.</param>
    /// <returns>Options reflecting the local category toggle.</returns>
    public static AccessibilityAnalyzerOptions FromAuditTogglesJson(
        string? auditTogglesJson,
        IReadOnlyDictionary<string, AccessibilityRuleOverride>? ruleOverrides = null)
    {
        var enabled = true;
        if (!string.IsNullOrWhiteSpace(auditTogglesJson))
        {
            try
            {
                using var document = JsonDocument.Parse(auditTogglesJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, nameof(AuditCategory.Accessibility), StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            enabled = property.Value.GetBoolean();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed local payload: keep the baseline default (enabled).
            }
        }

        return new AccessibilityAnalyzerOptions
        {
            Enabled = enabled,
            RuleOverrides = ruleOverrides ?? new Dictionary<string, AccessibilityRuleOverride>(StringComparer.Ordinal),
        };
    }
}
