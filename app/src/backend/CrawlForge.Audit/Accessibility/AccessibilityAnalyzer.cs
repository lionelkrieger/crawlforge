using System.Text.Json;
using CrawlForge.Audit.Accessibility.Rules;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// Accessibility rule engine (FS-CRAWL-001 §7.10, locked
/// <see cref="IAccessibilityAnalyzer"/>): evaluates the bundled, versioned
/// rule set over the page's document tree — the rendered tree when rendering
/// is enabled, otherwise the parsed tree. Each violation emits a finding with
/// severity, an element locator, and remediation guidance; render-dependent
/// checks are flagged with a notice when rendering is off. Findings carry
/// Category=Accessibility and feed the accessibility report surface, the URL
/// detail view, and audit scoring (§8.1). All computation is local.
/// </summary>
public sealed class AccessibilityAnalyzer : IAccessibilityAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AccessibilityAnalyzerOptions _options;

    /// <summary>Creates the analyzer with local options.</summary>
    /// <param name="options">
    /// Category toggle and per-rule overrides; null uses the baseline defaults
    /// (category enabled, bundled severities, all rules enabled).
    /// </param>
    public AccessibilityAnalyzer(AccessibilityAnalyzerOptions? options = null)
    {
        _options = options ?? new AccessibilityAnalyzerOptions();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        // Rendered tree when available; parsed tree otherwise (§7.10).
        var document = context.RenderedDocument ?? context.Document;
        if (document is null)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        var renderingEnabled = context.Configuration.RenderingMode != RenderMode.None;
        var ruleContext = new AccessibilityRuleContext(
            document,
            renderingEnabled,
            renderedTreeAvailable: context.RenderedDocument is not null,
            _options);

        var enabledKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in AccessibilityRuleSet.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_options.IsRuleEnabled(rule.Key))
            {
                continue;
            }

            enabledKeys.Add(rule.Key);
            if (rule.RequiresRendering && !(renderingEnabled && ruleContext.RenderedTreeAvailable))
            {
                // Render-dependent check flagged when rendering is off.
                ruleContext.AddRequiresRenderingNotice(rule);
            }
        }

        // Each family pass runs once; individual rule keys are filtered at
        // materialization so per-rule disablement stays effective.
        var contrastRunnable = enabledKeys.Contains(AccessibilityRuleKeys.ColorContrast)
            && renderingEnabled
            && ruleContext.RenderedTreeAvailable;
        if (enabledKeys.Contains(AccessibilityRuleKeys.ImageAlt))
        {
            ImageAltRule.Evaluate(ruleContext);
        }

        if (enabledKeys.Overlaps(AriaKeys))
        {
            AriaValidityRule.Evaluate(ruleContext);
        }

        if (enabledKeys.Overlaps(FormKeys))
        {
            FormLabelRule.Evaluate(ruleContext);
        }

        if (enabledKeys.Overlaps(StructureKeys))
        {
            StructureRule.Evaluate(ruleContext);
        }

        if (enabledKeys.Overlaps(LinkKeys))
        {
            LinkPurposeRule.Evaluate(ruleContext);
        }

        if (enabledKeys.Overlaps(LanguageKeys))
        {
            DocumentLanguageRule.Evaluate(ruleContext);
        }

        if (contrastRunnable)
        {
            ColorContrastRule.Evaluate(ruleContext);
        }

        var findings = new List<FindingDraft>(ruleContext.Violations.Count);
        foreach (var violation in ruleContext.Violations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabledKeys.Contains(violation.RuleKey))
            {
                findings.Add(MaterializeFinding(violation, context.Url.Id));
            }
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    private static readonly string[] AriaKeys =
    [
        AccessibilityRuleKeys.AriaValidRole,
        AccessibilityRuleKeys.AriaValidAttr,
        AccessibilityRuleKeys.AriaRequiredAttr,
        AccessibilityRuleKeys.AriaIdRef,
    ];

    private static readonly string[] FormKeys =
    [
        AccessibilityRuleKeys.FormLabel,
        AccessibilityRuleKeys.FieldsetLegend,
    ];

    private static readonly string[] StructureKeys =
    [
        AccessibilityRuleKeys.LandmarkMain,
        AccessibilityRuleKeys.LandmarkUnique,
        AccessibilityRuleKeys.HeadingOrder,
        AccessibilityRuleKeys.HeadingEmpty,
        AccessibilityRuleKeys.HeadingH1,
    ];

    private static readonly string[] LinkKeys =
    [
        AccessibilityRuleKeys.LinkPurpose,
        AccessibilityRuleKeys.LinkGenericText,
    ];

    private static readonly string[] LanguageKeys =
    [
        AccessibilityRuleKeys.DocumentLanguage,
        AccessibilityRuleKeys.DocumentLanguageValid,
    ];

    private FindingDraft MaterializeFinding(AccessibilityViolation violation, long urlId)
    {
        var descriptor = AccessibilityRuleSet.Find(violation.RuleKey)
            ?? throw new InvalidOperationException($"Violation references unknown rule '{violation.RuleKey}'.");

        // Requires-rendering notices always carry Notice severity.
        var isRequiresRenderingNotice = violation.Details is not null
            && violation.Details.TryGetValue("requiresRendering", out var flag)
            && flag == "true";
        var severity = isRequiresRenderingNotice
            ? FindingSeverity.Notice
            : _options.ResolveSeverity(descriptor);

        var evidence = new AccessibilityEvidence(
            violation.RuleKey,
            AccessibilityRuleSet.Version,
            descriptor.Family,
            violation.Locator,
            violation.XPath,
            violation.ElementSnippet,
            violation.Message,
            descriptor.Remediation,
            isRequiresRenderingNotice,
            violation.Details);

        return new FindingDraft(
            violation.RuleKey,
            AuditCategory.Accessibility,
            severity,
            urlId,
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions),
            RecommendationKey: violation.RuleKey);
    }
}
