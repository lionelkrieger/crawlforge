using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Generic rule host (FS-CRAWL-001 §7.4/§7.6): composition-time injection,
/// per-rule failure isolation, category/rule filtering.
/// </summary>
public class RuleHostTests
{
    private static FindingDraft Draft(string ruleKey, AuditCategory category = AuditCategory.Content) =>
        new(ruleKey, category, FindingSeverity.Warning, 1, "{}", null);

    [Fact]
    public async Task EmptyAnalyzerSetIsTolerated()
    {
        var host = new AuditRuleHost([]);
        Assert.Empty(host.SupportedRuleKeys);
        var findings = await host.AnalyzeAsync(PipelineTestContext.Create());
        Assert.Empty(findings);
    }

    [Fact]
    public async Task SupportedKeysAreTheUnionOfInjectedAnalyzers()
    {
        var a = new RecordingRuleAnalyzer(["rule.a1", "rule.a2"]);
        var b = new RecordingRuleAnalyzer(["rule.b1", "rule.a1"]);
        var host = new AuditRuleHost([a, b]);

        Assert.Equal(3, host.SupportedRuleKeys.Count);
        Assert.Contains("rule.a1", host.SupportedRuleKeys);
        Assert.Contains("rule.b1", host.SupportedRuleKeys);
    }

    [Fact]
    public async Task ThrowingAnalyzerIsIsolatedAndOthersStillRun()
    {
        var throwing = new ThrowingRuleAnalyzer();
        var healthy = new RecordingRuleAnalyzer(["rule.ok"], [Draft("rule.ok")]);
        var host = new AuditRuleHost([throwing, healthy]);

        var findings = await host.AnalyzeAsync(PipelineTestContext.Create());

        Assert.Contains(findings, f => f.RuleKey == "rule.ok");
        var diagnostic = findings.Single(f => f.RuleKey == PipelineDiagnostics.AnalyzerFailureRuleKey);
        Assert.Equal(FindingSeverity.Notice, diagnostic.Severity);
        Assert.Contains("analyzer boom", diagnostic.EvidenceJson, StringComparison.Ordinal);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task CategoryFilterRestrictsEmission()
    {
        var analyzer = new RecordingRuleAnalyzer(
            ["rule.content", "rule.linking"],
            [Draft("rule.content"), Draft("rule.linking", AuditCategory.Linking)]);
        var host = new AuditRuleHost([analyzer], new RuleHostOptions
        {
            EnabledCategories = new HashSet<AuditCategory> { AuditCategory.Linking },
        });

        var findings = await host.AnalyzeAsync(PipelineTestContext.Create());
        Assert.Single(findings);
        Assert.Equal("rule.linking", findings[0].RuleKey);
    }

    [Fact]
    public async Task RuleKeyFilterRestrictsKeysAndEmission()
    {
        var analyzer = new RecordingRuleAnalyzer(
            ["rule.keep", "rule.drop"],
            [Draft("rule.keep"), Draft("rule.drop")]);
        var host = new AuditRuleHost([analyzer], new RuleHostOptions
        {
            RuleKeyFilter = ["rule.keep"],
        });

        Assert.Equal(["rule.keep"], host.SupportedRuleKeys);
        var findings = await host.AnalyzeAsync(PipelineTestContext.Create());
        Assert.Single(findings);
        Assert.Equal("rule.keep", findings[0].RuleKey);
    }

    [Fact]
    public async Task ContextFlowsThroughToAnalyzers()
    {
        var analyzer = new RecordingRuleAnalyzer(["rule.x"]);
        var host = new AuditRuleHost([analyzer]);
        var context = PipelineTestContext.Create(urlId: 42, auditId: 9);

        await host.AnalyzeAsync(context);

        Assert.NotNull(analyzer.LastContext);
        Assert.Equal(42, analyzer.LastContext!.Url.Id);
        Assert.Equal(9, analyzer.LastContext.AuditId);
    }
}
