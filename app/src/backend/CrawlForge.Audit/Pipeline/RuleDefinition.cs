using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Declarative rule definition (FS-CRAWL-001 §7.6): rules are data, not engine
/// code. Severity is assigned by the rule definition with locally configurable
/// thresholds; priority is a local importance ranking independent of severity
/// (§7.5); remediation difficulty is an optional effort estimate independent of
/// severity and priority.
/// </summary>
/// <param name="RuleKey">Stable rule key from the product taxonomy.</param>
/// <param name="Category">Taxonomy category.</param>
/// <param name="DefaultSeverity">Severity assigned by the rule definition.</param>
/// <param name="RecommendationKey">Generic remediation guidance key.</param>
/// <param name="Priority">Local importance ranking (higher = earlier remediation).</param>
/// <param name="RemediationDifficulty">Optional effort estimate (low/medium/high).</param>
public sealed record RuleDefinition(
    string RuleKey,
    AuditCategory Category,
    FindingSeverity DefaultSeverity,
    string? RecommendationKey,
    int Priority = 0,
    string? RemediationDifficulty = null);

/// <summary>
/// Recommendation catalog keys (FS-CRAWL-001 §7.5: generic remediation
/// guidance). Keys are open strings; the convention is "rec." + rule key.
/// </summary>
public static class RecommendationKeys
{
    /// <summary>Derives the recommendation key for a rule key.</summary>
    /// <param name="ruleKey">Stable rule key.</param>
    /// <returns>The recommendation catalog key.</returns>
    public static string ForRule(string ruleKey) => "rec." + ruleKey;
}

/// <summary>
/// Registry of declarative rule definitions (FS-CRAWL-001 §7.4–7.6). Seeded
/// with the locked extension rule keys (Constants.FindingRuleKeys); analyzer
/// families register their own definitions additively at composition time —
/// adding a rule is a data change, not an engine change.
/// </summary>
public sealed class AuditRuleCatalog
{
    private readonly Dictionary<string, RuleDefinition> _rules = new(StringComparer.Ordinal);

    /// <summary>Creates a catalog seeded with the extension rule taxonomy.</summary>
    public AuditRuleCatalog()
    {
        // WP-09-11 (gap #21): the accepted WAF recording surface persists these
        // three rules under Indexability (FS-CRAWL-001 §2.4.5); the catalog seed
        // matches the producer. WafBlockRate stays Crawl (site-wide diagnostic).
        Seed(FindingRuleKeys.WafChallengeDetected, AuditCategory.Indexability, FindingSeverity.Warning, priority: 90, "medium");
        Seed(FindingRuleKeys.WafBlockRate, AuditCategory.Crawl, FindingSeverity.Warning, priority: 85, "medium");
        Seed(FindingRuleKeys.ChallengeResolvedByRender, AuditCategory.Indexability, FindingSeverity.Notice, priority: 10);
        Seed(FindingRuleKeys.SuspectedChallenge, AuditCategory.Indexability, FindingSeverity.Notice, priority: 20);
        Seed(FindingRuleKeys.ContentKeywordCannibalization, AuditCategory.Content, FindingSeverity.Warning, priority: 60, "medium");
        Seed(FindingRuleKeys.ContentTopicalGap, AuditCategory.Content, FindingSeverity.Notice, priority: 40, "medium");
        Seed(FindingRuleKeys.RedirectMatrixPass, AuditCategory.Indexability, FindingSeverity.Pass, priority: 0);
        Seed(FindingRuleKeys.RedirectMatrixTargetMismatch, AuditCategory.Indexability, FindingSeverity.Error, priority: 95, "medium");
        Seed(FindingRuleKeys.RedirectMatrixChainExceeded, AuditCategory.Indexability, FindingSeverity.Warning, priority: 70, "low");
        Seed(FindingRuleKeys.RedirectMatrixFinalNon200, AuditCategory.Indexability, FindingSeverity.Error, priority: 95, "medium");
        Seed(FindingRuleKeys.RedirectMatrixCanonicalMismatch, AuditCategory.Indexability, FindingSeverity.Warning, priority: 75, "low");
        Seed(FindingRuleKeys.LogsTrueOrphan, AuditCategory.LogIntelligence, FindingSeverity.Warning, priority: 65, "medium");
        Seed(FindingRuleKeys.LogsCrawlBudgetWaste, AuditCategory.LogIntelligence, FindingSeverity.Notice, priority: 45, "medium");
        Seed(FindingRuleKeys.LogsUncrawledIndexable, AuditCategory.LogIntelligence, FindingSeverity.Warning, priority: 60, "low");
        Seed(FindingRuleKeys.ContentSemanticCannibalization, AuditCategory.Content, FindingSeverity.Warning, priority: 55, "high");
        Seed(FindingRuleKeys.LinkingLowRelevanceLink, AuditCategory.Linking, FindingSeverity.Notice, priority: 30, "medium");
        Seed(FindingRuleKeys.GeoLlmsTxtMissing, AuditCategory.AiSearchGeo, FindingSeverity.Notice, priority: 35, "low");
        Seed(FindingRuleKeys.GeoLlmsTxtMalformed, AuditCategory.AiSearchGeo, FindingSeverity.Warning, priority: 50, "low");
        Seed(FindingRuleKeys.GeoLlmsTxtBrokenLink, AuditCategory.AiSearchGeo, FindingSeverity.Warning, priority: 50, "low");
        Seed(FindingRuleKeys.GeoGovernanceConflict, AuditCategory.AiSearchGeo, FindingSeverity.Warning, priority: 55, "medium");
        Seed(FindingRuleKeys.AnalyticsMissingContainer, AuditCategory.AnalyticsTagging, FindingSeverity.Warning, priority: 50, "medium");
        Seed(FindingRuleKeys.AnalyticsDuplicateContainer, AuditCategory.AnalyticsTagging, FindingSeverity.Notice, priority: 30, "low");
        Seed(FindingRuleKeys.AnalyticsDataLayerMalformed, AuditCategory.AnalyticsTagging, FindingSeverity.Warning, priority: 55, "medium");
        Seed(FindingRuleKeys.AnalyticsConsentModeMissing, AuditCategory.AnalyticsTagging, FindingSeverity.Notice, priority: 35, "medium");
        Seed(FindingRuleKeys.AnalyticsNoHitsObserved, AuditCategory.AnalyticsTagging, FindingSeverity.Notice, priority: 25, "medium");
        Seed(FindingRuleKeys.TitlePixelTruncation, AuditCategory.Content, FindingSeverity.Notice, priority: 40, "low");
        Seed(FindingRuleKeys.DescriptionPixelTruncation, AuditCategory.Content, FindingSeverity.Notice, priority: 40, "low");
        Seed(FindingRuleKeys.IndexabilityHeaderHtmlConflict, AuditCategory.Indexability, FindingSeverity.Warning, priority: 70, "low");
        Seed(FindingRuleKeys.CanonicalHeaderHtmlConflict, AuditCategory.Indexability, FindingSeverity.Warning, priority: 75, "low");
        Seed(FindingRuleKeys.HreflangHeaderHtmlConflict, AuditCategory.Indexability, FindingSeverity.Warning, priority: 65, "low");
    }

    /// <summary>Registers (or replaces) a rule definition additively.</summary>
    /// <param name="definition">The rule definition.</param>
    public void Register(RuleDefinition definition) => _rules[definition.RuleKey] = definition;

    /// <summary>Looks up a rule definition.</summary>
    /// <param name="ruleKey">Stable rule key.</param>
    /// <param name="definition">The definition when registered.</param>
    /// <returns>True when the rule is registered.</returns>
    public bool TryGet(string ruleKey, out RuleDefinition? definition) =>
        _rules.TryGetValue(ruleKey, out definition);

    /// <summary>All registered rule definitions.</summary>
    public IReadOnlyList<RuleDefinition> All => _rules.Values.ToArray();

    private void Seed(string ruleKey, AuditCategory category, FindingSeverity severity, int priority, string? difficulty = null) =>
        Register(new RuleDefinition(ruleKey, category, severity, RecommendationKeys.ForRule(ruleKey), priority, difficulty));
}
