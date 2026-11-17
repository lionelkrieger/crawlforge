using CrawlForge.Audit.Performance;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Performance analyzer unit tests against the fake lab probe (FS-CRAWL-001
/// §7.11): CWV bands, TTFB, budgets, coverage, composition, metric mapping,
/// toggles, render-dependent notices, and shared-cache behavior.
/// </summary>
public sealed class PerformanceAnalyzerTests
{
    // ── Core Web Vitals ───────────────────────────────────────────────────

    [Fact]
    public async Task Vitals_GoodSample_MapsVitalsAndPassBands()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var analyzer = new CoreWebVitalsAnalyzer(probe: probe);
        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            renderingMode: RenderMode.Always,
            fetch: AuditContexts.Fetch(ttfbMs: 120));

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Equal(1200, result.LcpMilliseconds);
        Assert.Equal(0.02, result.Cls);
        Assert.Equal(80, result.InpMilliseconds);
        Assert.Equal(120, result.TtfbMilliseconds);
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, finding => Assert.Equal(FindingSeverity.Pass, finding.Severity));
        Assert.Equal("https://target.test/", Assert.Single(probe.RequestedUrls));
    }

    [Fact]
    public async Task Vitals_PoorSample_EmitsWarningBands()
    {
        var analyzer = new CoreWebVitalsAnalyzer(probe: new FakeLabVitalsProbe(FakeLabVitalsProbe.PoorSample()));
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Equal(6000, result.LcpMilliseconds);
        Assert.Equal(0.4, result.Cls);
        Assert.Equal(900, result.InpMilliseconds);
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, finding => Assert.Equal(FindingSeverity.Warning, finding.Severity));
        Assert.Contains(result.Findings, finding => finding.EvidenceJson.Contains("poor"));
    }

    [Fact]
    public async Task Vitals_MiddleBand_EmitsNeedsImprovementNotice()
    {
        var sample = new LabVitalsSample(3000, 0.15, 300, 100, 900, null, null);
        var analyzer = new CoreWebVitalsAnalyzer(probe: new FakeLabVitalsProbe(sample));
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.All(result.Findings, finding => Assert.Equal(FindingSeverity.Notice, finding.Severity));
        Assert.Contains(result.Findings, finding => finding.EvidenceJson.Contains("needs-improvement"));
    }

    [Fact]
    public async Task Vitals_RenderingOff_EmitsRequiresRenderingNotice_AndNullVitals()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var analyzer = new CoreWebVitalsAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.None);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Null(result.LcpMilliseconds);
        Assert.Null(result.Cls);
        Assert.Null(result.InpMilliseconds);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(PerformanceRuleKeys.RequiresRendering, finding.RuleKey);
        Assert.Equal(0, probe.CallCount); // idle-lazy: no measurement work when rendering off
    }

    [Fact]
    public async Task Vitals_CategoryDisabled_ProducesNothing()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var options = new PerformanceAnalyzerOptions { Enabled = false };
        var analyzer = new CoreWebVitalsAnalyzer(options, probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Null(result.LcpMilliseconds);
        Assert.Empty(result.Findings);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Vitals_ProbeFailure_EmitsNotice_AndNullVitals()
    {
        var probe = new FakeLabVitalsProbe(new LabProbeException(LabProbeFailureKind.BrowserUnavailable, "no local browser"));
        var analyzer = new CoreWebVitalsAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Null(result.LcpMilliseconds);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(PerformanceRuleKeys.ProbeUnavailable, finding.RuleKey);
        Assert.Contains("BrowserUnavailable", finding.EvidenceJson);
    }

    [Fact]
    public async Task Vitals_NoProbeConfigured_EmitsNotice()
    {
        var analyzer = new CoreWebVitalsAnalyzer();
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);
        var result = await analyzer.AnalyzeAsync(context);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(PerformanceRuleKeys.ProbeUnavailable, finding.RuleKey);
    }

    [Fact]
    public void Options_FromAuditTogglesJson_ParsesPerformanceToggle()
    {
        Assert.False(PerformanceAnalyzerOptions.FromAuditTogglesJson("{\"performance\":false}").Enabled);
        Assert.True(PerformanceAnalyzerOptions.FromAuditTogglesJson("{\"Performance\":true}").Enabled);
        Assert.True(PerformanceAnalyzerOptions.FromAuditTogglesJson("{\"accessibility\":false}").Enabled);
        Assert.True(PerformanceAnalyzerOptions.FromAuditTogglesJson(null).Enabled);
        Assert.True(PerformanceAnalyzerOptions.FromAuditTogglesJson("{broken").Enabled);
    }

    // ── Shared cache ──────────────────────────────────────────────────────

    [Fact]
    public async Task SharedCache_MeasuresPageOnce_AcrossAnalyzers()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var cache = new LabVitalsSampleCache();
        var vitals = new CoreWebVitalsAnalyzer(probe: probe, cache: cache);
        var coverage = new CodeCoverageAnalyzer(probe: probe, cache: cache);
        var budgets = new PerformanceBudgetEvaluator(
            probe: probe, cache: cache);
        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            renderingMode: RenderMode.Always,
            fetch: AuditContexts.Fetch());

        await vitals.AnalyzeAsync(context);
        await coverage.AnalyzeAsync(context);
        await budgets.EvaluateAsync(context, new PerformanceBudget(MaxLcpMilliseconds: 2500));

        Assert.Equal(1, probe.CallCount);
    }

    // ── Performance budgets ───────────────────────────────────────────────

    [Fact]
    public async Task Budget_ByteAndRequestExceedance_EmitsFindings()
    {
        var analyzer = new PerformanceBudgetEvaluator();
        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            fetch: AuditContexts.Fetch(bodyBytes: 5000),
            capturedRequests:
            [
                AuditContexts.Captured("https://target.test/", "text/html"),
                AuditContexts.Captured("https://target.test/app.js", "application/javascript"),
                AuditContexts.Captured("https://target.test/site.css", "text/css"),
            ]);

        var findings = await analyzer.EvaluateAsync(
            context, new PerformanceBudget(MaxTotalBytes: 1000, MaxRequests: 2));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(PerformanceRuleKeys.BudgetExceeded, finding.RuleKey));
        Assert.Contains(findings, finding => finding.EvidenceJson.Contains(PerformanceMetricKeys.TotalBytes));
        Assert.Contains(findings, finding => finding.EvidenceJson.Contains(PerformanceMetricKeys.RequestCount));
    }

    [Fact]
    public async Task Budget_WithinLimits_EmitsNothing()
    {
        var analyzer = new PerformanceBudgetEvaluator();
        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            fetch: AuditContexts.Fetch(bodyBytes: 500),
            capturedRequests: [AuditContexts.Captured("https://target.test/", "text/html")]);

        var findings = await analyzer.EvaluateAsync(
            context, new PerformanceBudget(MaxTotalBytes: 1000, MaxRequests: 5));

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Budget_VitalExceedance_UsesLabSample()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.PoorSample());
        var analyzer = new PerformanceBudgetEvaluator(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var findings = await analyzer.EvaluateAsync(
            context, new PerformanceBudget(MaxLcpMilliseconds: 2500, MaxCls: 0.1, MaxInpMilliseconds: 200));

        Assert.Equal(3, findings.Count);
        Assert.All(findings, finding => Assert.Equal(PerformanceRuleKeys.BudgetExceeded, finding.RuleKey));
    }

    [Fact]
    public async Task Budget_VitalsWithoutRendering_EmitNotEvaluatedNotices()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var analyzer = new PerformanceBudgetEvaluator(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.None);

        var findings = await analyzer.EvaluateAsync(
            context, new PerformanceBudget(MaxLcpMilliseconds: 2500, MaxCls: 0.1));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(PerformanceRuleKeys.BudgetNotEvaluated, finding.RuleKey));
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Budget_MissingFetchAndCapture_EmitNotEvaluatedNotices()
    {
        var analyzer = new PerformanceBudgetEvaluator();
        var context = AuditContexts.Create(html: "<html><body>x</body></html>");

        var findings = await analyzer.EvaluateAsync(
            context, new PerformanceBudget(MaxTotalBytes: 1000, MaxRequests: 5));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(PerformanceRuleKeys.BudgetNotEvaluated, finding.RuleKey));
    }

    [Fact]
    public async Task Budget_NullFields_AreNotEvaluated()
    {
        var analyzer = new PerformanceBudgetEvaluator();
        var context = AuditContexts.Create(html: "<html><body>x</body></html>");
        var findings = await analyzer.EvaluateAsync(context, new PerformanceBudget());
        Assert.Empty(findings);
    }

    // ── Code coverage ─────────────────────────────────────────────────────

    [Fact]
    public async Task Coverage_ComputesUsedTotalAndUnusedPercent()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var analyzer = new CodeCoverageAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var result = await analyzer.AnalyzeAsync(context);

        Assert.Equal(700, result.UsedBytes);   // 400 js + 300 css
        Assert.Equal(1500, result.TotalBytes); // 1000 js + 500 css
        Assert.Equal(800.0 / 1500 * 100, result.UnusedPercent, 3);
    }

    [Fact]
    public async Task Coverage_LargeUnusedPayload_EmitsFinding_WithCaptureCorrelation()
    {
        var sample = FakeLabVitalsProbe.GoodSample() with
        {
            Coverage = new CodeCoverageSample(
                JsUsedBytes: 10 * 1024,
                JsTotalBytes: 200 * 1024,
                CssUsedBytes: 5 * 1024,
                CssTotalBytes: 100 * 1024,
                Entries:
                [
                    new CoverageEntry("https://target.test/app.js", "js", 10 * 1024, 200 * 1024),
                    new CoverageEntry("https://target.test/site.css", "css", 5 * 1024, 100 * 1024),
                ]),
        };
        var analyzer = new CodeCoverageAnalyzer(probe: new FakeLabVitalsProbe(sample));
        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            renderingMode: RenderMode.Always,
            capturedRequests:
            [
                AuditContexts.Captured("https://target.test/app.js", "application/javascript"),
                AuditContexts.Captured("https://target.test/site.css", "text/css"),
                AuditContexts.Captured("https://target.test/logo.png", "image/png"),
            ]);

        var analysis = await analyzer.AnalyzeDetailedAsync(context);

        var finding = Assert.Single(analysis.Findings);
        Assert.Equal(PerformanceRuleKeys.UnusedCode, finding.RuleKey);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("capturedCodeRequests", finding.EvidenceJson);
        Assert.Contains("app.js", finding.EvidenceJson);
    }

    [Fact]
    public async Task Coverage_RenderingOff_ReturnsZero_AndRequiresRenderingNotice()
    {
        var probe = new FakeLabVitalsProbe(FakeLabVitalsProbe.GoodSample());
        var analyzer = new CodeCoverageAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.None);

        var analysis = await analyzer.AnalyzeDetailedAsync(context);

        Assert.Equal(0, analysis.Result.TotalBytes);
        var finding = Assert.Single(analysis.Findings);
        Assert.Equal(PerformanceRuleKeys.RequiresRendering, finding.RuleKey);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Coverage_ProbeFailure_ReturnsZero_AndNotice()
    {
        var probe = new FakeLabVitalsProbe(new LabProbeException(LabProbeFailureKind.Timeout, "timed out"));
        var analyzer = new CodeCoverageAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);

        var analysis = await analyzer.AnalyzeDetailedAsync(context);

        Assert.Equal(0, analysis.Result.UsedBytes);
        var finding = Assert.Single(analysis.Findings);
        Assert.Equal(PerformanceRuleKeys.ProbeUnavailable, finding.RuleKey);
    }

    // ── Page composition ──────────────────────────────────────────────────

    [Fact]
    public void Composition_FromDocument_ComputesNodesDepthWidthImagesAndFont()
    {
        var document = TestDom.Parse("""
            <html lang="en"><body><main>
            <h1 style="font-size:28px">Title</h1>
            <p style="font-size:9px">tiny</p>
            <img src="/a.png"><img src="/b.png">
            <div><span>deep</span></div>
            </main></body></html>
            """);

        var sample = PageCompositionAnalyzer.FromDocument(document);

        Assert.True(sample.DomNodes >= 10);
        Assert.True(sample.MaxTreeDepth >= 5); // html > body > main > div > span
        Assert.True(sample.MaxTreeWidth >= 4); // main children
        Assert.Equal(2, sample.ImageCount);
        Assert.Equal(9, sample.SmallestFontSizePx);
        Assert.Equal(0, sample.ImageBytes); // DOM-only: bytes need the probe
    }

    [Fact]
    public void Composition_Merge_ProbeValuesWin()
    {
        var dom = new PageCompositionSample(100, 5, 8, 2, 0, 12);
        var probe = new PageCompositionSample(120, 6, 9, 2, 4096, 10.5);
        var merged = PageCompositionAnalyzer.Merge(dom, probe);
        Assert.Equal(120, merged.DomNodes);
        Assert.Equal(4096, merged.ImageBytes);
        Assert.Equal(10.5, merged.SmallestFontSizePx);

        var unchanged = PageCompositionAnalyzer.Merge(dom, null);
        Assert.Equal(dom, unchanged);
    }

    // ── Metric contributions (metric_summaries) ───────────────────────────

    [Fact]
    public void MetricContributions_MapVitalsCoverageCompositionAndFacts()
    {
        var vitals = new CoreWebVitalsResult(1200, 0.02, 80, 120, []);
        var vitalMetrics = PerformanceMetricContributions.FromVitals(vitals, urlId: 7);
        Assert.Contains(vitalMetrics, metric => metric.MetricKey == PerformanceMetricKeys.Lcp && metric.Value == 1200);
        Assert.Contains(vitalMetrics, metric => metric.MetricKey == PerformanceMetricKeys.Ttfb && metric.Value == 120);
        Assert.All(vitalMetrics, metric => Assert.Contains("\"urlId\":7", metric.DimensionsJson));

        var coverageMetrics = PerformanceMetricContributions.FromCoverage(new CodeCoverageResult(700, 1500, 53.3), 7);
        Assert.Equal(3, coverageMetrics.Count);
        Assert.Contains(coverageMetrics, metric => metric.MetricKey == PerformanceMetricKeys.CoverageUnusedPercent);

        var compositionMetrics = PerformanceMetricContributions.FromComposition(
            new PageCompositionSample(100, 5, 8, 2, 4096, 11), 7);
        Assert.Equal(6, compositionMetrics.Count);

        var context = AuditContexts.Create(
            html: "<html><body>x</body></html>",
            fetch: AuditContexts.Fetch(bodyBytes: 2048),
            capturedRequests: [AuditContexts.Captured("https://target.test/", "text/html")]);
        var factMetrics = PerformanceMetricContributions.FromPageFacts(context);
        Assert.Contains(factMetrics, metric => metric.MetricKey == PerformanceMetricKeys.TotalBytes && metric.Value == 2048);
        Assert.Contains(factMetrics, metric => metric.MetricKey == PerformanceMetricKeys.RequestCount && metric.Value == 1);
    }

    [Fact]
    public void MetricContributions_NullVitals_AreSkipped()
    {
        var vitals = new CoreWebVitalsResult(null, null, null, 50, []);
        var metrics = PerformanceMetricContributions.FromVitals(vitals, 1);
        var metric = Assert.Single(metrics);
        Assert.Equal(PerformanceMetricKeys.Ttfb, metric.MetricKey);
    }
}
