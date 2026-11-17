using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Standalone single-page analysis (FS-CRAWL-001 §7.7, criterion #10):
/// findings for one URL without a project or crawl, with the additive WAF
/// verdict and scope diagnostics.
/// </summary>
public class SinglePageAnalyzerTests
{
    private static SinglePageAnalyzer CreateAnalyzer(
        FakeHttpFetcher fetcher,
        IAuditRuleAnalyzer? host = null,
        FakeWafEvaluator? waf = null,
        FakeRenderingPool? rendering = null,
        SinglePageAnalyzerOptions? options = null)
    {
        var parser = new AngleSharpDomParser();
        var pipeline = new AuditPipeline(
            parser,
            new ElementInventoryExtractor(),
            new ContentScopeResolver(),
            host ?? new AuditRuleHost([]));
        return new SinglePageAnalyzer(
            fetcher,
            parser,
            pipeline,
            waf,
            rendering,
            new ContentScopeResolver(),
            options);
    }

    [Fact]
    public async Task AnalyzesOneUrlWithoutProjectOrCrawl()
    {
        // Criterion #10: fetch + audit a single URL ad hoc; no audit/project
        // records are involved (synthetic context ids are zero).
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var finding = new FindingDraft("content.single", AuditCategory.Content, FindingSeverity.Notice, 0, "{}", null);
        var host = new AuditRuleHost([new RecordingRuleAnalyzer(["content.single"], [finding])]);
        var analyzer = CreateAnalyzer(fetcher, host);

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/page"));

        Assert.Equal(1, fetcher.FetchCount);
        Assert.Contains(result.Findings, f => f.RuleKey == "content.single");
        Assert.NotEmpty(result.Inventory);
        Assert.Contains(result.Inventory, i => i.Group == "document_head");
    }

    [Fact]
    public async Task WafVerdictIsCarriedWhenEvaluatorIsInjected()
    {
        var fetcher = new FakeHttpFetcher("<html><body>Challenge page</body></html>");
        var analyzer = CreateAnalyzer(fetcher, waf: new FakeWafEvaluator(isChallenge: true));

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/challenge"));

        Assert.NotNull(result.WafVerdict);
        Assert.True(result.WafVerdict!.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.WafVerdict.Provider);
    }

    [Fact]
    public async Task WafVerdictIsNullWithoutEvaluator()
    {
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var analyzer = CreateAnalyzer(fetcher);

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/page"));
        Assert.Null(result.WafVerdict);
    }

    [Fact]
    public async Task RuleKeysFilterRestrictsFindings()
    {
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var host = new AuditRuleHost([new RecordingRuleAnalyzer(["rule.keep", "rule.drop"], [
            new FindingDraft("rule.keep", AuditCategory.Content, FindingSeverity.Notice, 0, "{}", null),
            new FindingDraft("rule.drop", AuditCategory.Content, FindingSeverity.Notice, 0, "{}", null)])]);
        var analyzer = CreateAnalyzer(fetcher, host);

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest(
            "https://example.test/page", RuleKeys: ["rule.keep"]));

        Assert.Contains(result.Findings, f => f.RuleKey == "rule.keep");
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == "rule.drop");
    }

    [Fact]
    public async Task ScopeDiagnosticsAreIncludedWhenScopeIsSupplied()
    {
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var analyzer = CreateAnalyzer(fetcher, options: new SinglePageAnalyzerOptions
        {
            DefaultScope = new ContentScopeDefinition(["main"], ["nav"]),
        });

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/page"));

        Assert.NotNull(result.ScopeDiagnostics);
        Assert.Contains("Alpha beta gamma", result.ScopeDiagnostics!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nav one", result.ScopeDiagnostics.Text, StringComparison.Ordinal);
        Assert.True(result.ScopeDiagnostics.WordCount > 0);
    }

    [Fact]
    public async Task RenderRequestUsesTheRenderingPoolWhenAvailable()
    {
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var rendering = new FakeRenderingPool("<html><body><main>Rendered words here.</main></body></html>");
        var analyzer = CreateAnalyzer(fetcher, rendering: rendering, options: new SinglePageAnalyzerOptions
        {
            DefaultScope = new ContentScopeDefinition(["main"], []),
        });

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/page", Render: true));

        Assert.Equal(1, rendering.RenderCount);
        Assert.NotNull(result.ScopeDiagnostics);
        Assert.Equal("Rendered words here.", result.ScopeDiagnostics!.Text);
    }

    [Fact]
    public async Task RenderWithoutPoolFallsBackToResponseDocument()
    {
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var analyzer = CreateAnalyzer(fetcher);

        var result = await analyzer.AnalyzeAsync(new SinglePageAnalysisRequest("https://example.test/page", Render: true));

        Assert.NotEmpty(result.Inventory);
        Assert.Null(result.ScopeDiagnostics); // no scope supplied
    }
}
