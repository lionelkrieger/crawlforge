using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.Tests.ContentQuality;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// WP-09-05 soft-404 single-emission regression (probe-2: soft-404.html
/// produced TWO indexability.soft-404 findings — one from IndexabilityAnalyzer
/// step 5 and one from the pipeline's dedicated ISoft404Detector step, both
/// keyed identically). Contract after the fix: the pipeline's dedicated
/// detector step is the SOLE emitter; the analyzer emits only under the
/// explicit standalone opt-in. Runs the REAL AuditPipeline composition (the
/// exact EngineServicesModule wiring) over the REAL fixture soft-404.html
/// markup (pages.mjs line 81) parsed by the REAL tolerant parser.
/// </summary>
public class Soft404DedupTests
{
    private const string Soft404Url = FixtureSitePages.BaseUrl + "/pages/soft-404.html";

    private static PageAuditContext FixtureContext(string url, string html) =>
        IndexabilityContextFactory.Context(
            url: IndexabilityContextFactory.UrlRecord(canonicalUrl: url),
            fetch: IndexabilityContextFactory.Fetch(url: url, body: html));

    /// <summary>The production composition: analyzer WITH injected detector
    /// (default options) PLUS the pipeline's dedicated detector step — exactly
    /// how EngineServicesModule wires both today.</summary>
    private static AuditPipeline CreateProductionPipeline() =>
        new(
            new AngleSharpDomParser(),
            new ElementInventoryExtractor(),
            new ContentScopeResolver(),
            new AuditRuleHost([]),
            indexability: new IndexabilityAnalyzer(new Soft404Detector()),
            soft404Detector: new Soft404Detector());

    /// <summary>
    /// THE probe-2 regression: the real soft-404 fixture through the real
    /// pipeline composition yields EXACTLY ONE indexability.soft-404 finding.
    /// </summary>
    [Fact]
    public async Task ProductionComposition_RealSoft404Fixture_EmitsExactlyOneFinding()
    {
        var pipeline = CreateProductionPipeline();
        var context = FixtureContext(Soft404Url, FixtureSitePages.Soft404Html);

        var result = await pipeline.RunAsync(context);

        var soft404 = Assert.Single(result.Findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
        Assert.Equal(FindingSeverity.Warning, soft404.Severity);
        Assert.Equal(AuditCategory.Indexability, soft404.Category);
        Assert.Equal(context.Url.Id, soft404.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(soft404.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("confidence").GetDouble() >= 0.6);

        // The pipeline step's key and the analyzer's key are the same string —
        // the dedup guarantee is structural, not key-renaming.
        Assert.Equal(PipelineDiagnostics.Soft404RuleKey, IndexabilityRuleKeys.Soft404);
    }

    /// <summary>A healthy fixture page emits no soft-404 finding at all.</summary>
    [Fact]
    public async Task ProductionComposition_HealthyFixture_EmitsNoSoft404()
    {
        var pipeline = CreateProductionPipeline();
        var context = FixtureContext(
            FixtureSitePages.BaseUrl + "/pages/redirect-target.html",
            FixtureSitePages.RedirectTargetHtml);

        var result = await pipeline.RunAsync(context);

        Assert.DoesNotContain(result.Findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
    }

    /// <summary>Standalone analyzer, DEFAULT mode, real fixture: no emission
    /// (the pipeline step owns emission).</summary>
    [Fact]
    public async Task AnalyzerAlone_DefaultMode_RealFixture_EmitsNoSoft404()
    {
        var parser = new AngleSharpDomParser();
        var context = IndexabilityContextFactory.Context(
            document: parser.Parse(FixtureSitePages.Soft404Html, Soft404Url),
            fetch: IndexabilityContextFactory.Fetch(url: Soft404Url, body: FixtureSitePages.Soft404Html));
        var analyzer = new IndexabilityAnalyzer(new Soft404Detector());

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
    }

    /// <summary>Standalone analyzer, OPT-IN mode, real fixture: exactly one
    /// emission for compositions without the pipeline detector step.</summary>
    [Fact]
    public async Task AnalyzerAlone_OptInMode_RealFixture_EmitsOneSoft404()
    {
        var parser = new AngleSharpDomParser();
        var context = IndexabilityContextFactory.Context(
            document: parser.Parse(FixtureSitePages.Soft404Html, Soft404Url),
            fetch: IndexabilityContextFactory.Fetch(url: Soft404Url, body: FixtureSitePages.Soft404Html));
        var analyzer = new IndexabilityAnalyzer(
            new Soft404Detector(), new IndexabilityAnalyzerOptions(EmitSoft404Findings: true));

        var findings = await analyzer.AnalyzeAsync(context);

        var soft404 = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
        Assert.Equal(FindingSeverity.Warning, soft404.Severity);
    }

    /// <summary>Offline re-audit determinism (§9.6): repeated pipeline runs on
    /// the stored fixture produce byte-identical soft-404 evidence.</summary>
    [Fact]
    public async Task RepeatedPipelineRuns_AreDeterministic()
    {
        var pipeline = CreateProductionPipeline();
        var context = FixtureContext(Soft404Url, FixtureSitePages.Soft404Html);

        var first = await pipeline.RunAsync(context);
        var second = await pipeline.RunAsync(context);

        Assert.Equal(
            first.Findings.Single(f => f.RuleKey == IndexabilityRuleKeys.Soft404).EvidenceJson,
            second.Findings.Single(f => f.RuleKey == IndexabilityRuleKeys.Soft404).EvidenceJson);
    }

    /// <summary>Concurrent pipeline runs (MaxConcurrency=2) each emit exactly
    /// one finding — no shared-state duplication.</summary>
    [Fact]
    public async Task ConcurrentPipelineRuns_EachEmitExactlyOne()
    {
        var pipeline = CreateProductionPipeline();
        var context = FixtureContext(Soft404Url, FixtureSitePages.Soft404Html);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => pipeline.RunAsync(context))));

        Assert.All(results, result =>
            Assert.Single(result.Findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404));
    }
}
