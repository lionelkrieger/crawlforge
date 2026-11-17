using System.Text.Json;
using CrawlForge.Audit.Accessibility;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Analyzer behavior tests: category/rule toggles, severity mapping,
/// rendered-tree preference, evidence shape, report aggregation, and
/// cancellation (FS-CRAWL-001 §7.10).
/// </summary>
public sealed class AccessibilityAnalyzerTests
{
    private const string ViolatingHtml = """
        <html><body>
        <img src="/hero.png">
        <input type="text" name="q">
        <a href="/x"></a>
        </body></html>
        """;

    private static readonly JsonSerializerOptions EvidenceOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task CategoryDisabled_ProducesNoFindings()
    {
        var analyzer = new AccessibilityAnalyzer(new AccessibilityAnalyzerOptions { Enabled = false });
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task RuleDisabled_SkipsOnlyThatRule()
    {
        var options = new AccessibilityAnalyzerOptions
        {
            RuleOverrides = new Dictionary<string, AccessibilityRuleOverride>
            {
                [AccessibilityRuleKeys.ImageAlt] = new(Enabled: false),
            },
        };
        var analyzer = new AccessibilityAnalyzer(options);
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml));
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.FormLabel);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LinkPurpose);
    }

    [Fact]
    public async Task SeverityMapping_OverridesBundledDefault()
    {
        var options = new AccessibilityAnalyzerOptions
        {
            RuleOverrides = new Dictionary<string, AccessibilityRuleOverride>
            {
                [AccessibilityRuleKeys.ImageAlt] = new(Severity: FindingSeverity.Notice),
            },
        };
        var analyzer = new AccessibilityAnalyzer(options);
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml));
        var finding = Assert.Single(findings, f => f.RuleKey == AccessibilityRuleKeys.ImageAlt);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
    }

    [Fact]
    public void FromAuditTogglesJson_ParsesCategoryToggle()
    {
        Assert.False(AccessibilityAnalyzerOptions.FromAuditTogglesJson("{\"accessibility\":false}").Enabled);
        Assert.True(AccessibilityAnalyzerOptions.FromAuditTogglesJson("{\"Accessibility\":true}").Enabled);
        Assert.True(AccessibilityAnalyzerOptions.FromAuditTogglesJson("{\"performance\":false}").Enabled);
        Assert.True(AccessibilityAnalyzerOptions.FromAuditTogglesJson(null).Enabled);
        Assert.True(AccessibilityAnalyzerOptions.FromAuditTogglesJson("not-json").Enabled);
    }

    [Fact]
    public async Task RenderedTree_IsPreferred_OverParsedTree()
    {
        // Parsed tree lacks alt; the rendered tree adds it (JS-injected).
        var parsed = """<html lang="en"><body><main><h1>T</h1><img src="/x.png"></main></body></html>""";
        var rendered = """<html lang="en"><body><main><h1>T</h1><img src="/x.png" alt="Injected"></main></body></html>""";
        var analyzer = new AccessibilityAnalyzer();
        var findings = await analyzer.AnalyzeAsync(
            AuditContexts.Create(html: parsed, renderedHtml: rendered, renderingMode: RenderMode.Always));
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt);
    }

    [Fact]
    public async Task Evidence_CarriesLocatorXPathSnippetRemediationAndVersion()
    {
        var analyzer = new AccessibilityAnalyzer();
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml));
        var finding = Assert.Single(findings, f => f.RuleKey == AccessibilityRuleKeys.ImageAlt);

        var evidence = JsonSerializer.Deserialize<AccessibilityEvidence>(finding.EvidenceJson, EvidenceOptions);
        Assert.NotNull(evidence);
        Assert.Equal(AccessibilityRuleKeys.ImageAlt, evidence!.RuleKey);
        Assert.Equal(AccessibilityRuleSet.Version, evidence.RuleSetVersion);
        Assert.Equal(AccessibilityRuleSet.Families.Images, evidence.Family);
        Assert.Contains("img", evidence.Locator);
        Assert.StartsWith("/html", evidence.XPath);
        Assert.Contains("<img", evidence.ElementSnippet);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Remediation));
        Assert.False(evidence.RequiresRendering);
        Assert.Equal(finding.RuleKey, finding.RecommendationKey);
    }

    [Fact]
    public async Task NoDocument_ProducesNoFindings()
    {
        var analyzer = new AccessibilityAnalyzer();
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create());
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var analyzer = new AccessibilityAnalyzer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml), cts.Token));
    }

    [Fact]
    public async Task Summary_AggregatesCountsFamiliesAndRenderSkips()
    {
        var analyzer = new AccessibilityAnalyzer();
        var findings = await analyzer.AnalyzeAsync(AuditContexts.Create(html: ViolatingHtml));
        var summary = AccessibilitySummaryBuilder.FromFindings(findings);

        Assert.Equal(AccessibilityRuleSet.Version, summary.RuleSetVersion);
        Assert.Equal(findings.Count, summary.TotalFindings);
        Assert.Equal(findings.Count(f => f.Severity == FindingSeverity.Error), summary.ErrorCount);
        Assert.Equal(findings.Count(f => f.Severity == FindingSeverity.Warning), summary.WarningCount);
        Assert.Equal(findings.Count(f => f.Severity == FindingSeverity.Notice), summary.NoticeCount);
        Assert.Equal(1, summary.RenderDependentSkippedCount); // contrast notice, rendering off
        Assert.True(summary.CountByFamily.ContainsKey(AccessibilityRuleSet.Families.Images));
        Assert.True(summary.CountByRule.ContainsKey(AccessibilityRuleKeys.ImageAlt));
    }

    [Fact]
    public void Summary_IgnoresOtherCategories()
    {
        var other = new FindingDraft(
            "content.keyword-cannibalization", AuditCategory.Content, FindingSeverity.Warning, 1, "{}", null);
        var summary = AccessibilitySummaryBuilder.FromFindings([other]);
        Assert.Equal(0, summary.TotalFindings);
    }

    [Fact]
    public void RuleSet_IsVersionedAndCoversAllKeys()
    {
        Assert.False(string.IsNullOrWhiteSpace(AccessibilityRuleSet.Version));
        Assert.Equal(17, AccessibilityRuleSet.Rules.Count);
        Assert.All(AccessibilityRuleSet.Rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Remediation));
            Assert.NotNull(AccessibilityRuleSet.Find(rule.Key));
        });
        Assert.Null(AccessibilityRuleSet.Find("accessibility.does-not-exist"));
        Assert.Single(AccessibilityRuleSet.Rules, rule => rule.RequiresRendering);
    }
}
