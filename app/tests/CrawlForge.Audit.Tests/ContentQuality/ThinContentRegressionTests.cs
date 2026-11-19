using System.Text;
using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// WP-09-05 thin-content defect regression (probe-2: thin.html counted 6 words
/// but NO content.thin-text was emitted — the analyzer only checked the
/// text-to-HTML ratio, and thin.html's ratio ≈0.12 is above the 0.10 ratio
/// threshold). Spec: FS-CRAWL-001 §7.4 content-quality row ("thin content"),
/// §7.12; fixture trap "thin-page" expects "visible word count below
/// threshold; reported as thin content" with the counted text as evidence.
/// Runs the REAL fixture markup (pages.mjs line 109) through the REAL tolerant
/// parser and the REAL analyzer.
/// </summary>
public class ThinContentRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly AngleSharpDomParser Parser = new();

    private static ContentQualityAnalyzer CreateAnalyzer(ContentQualityOptions? options = null) =>
        new(options: options ?? new ContentQualityOptions(SpellingEnabled: false));

    private static PageAuditContext FixtureContext(string html, ScopedVisibleText? scopedText = null)
    {
        var document = Parser.Parse(html, FixtureSitePages.BaseUrl + "/pages/thin.html");
        return ContextFactory.Create(document, html: html, scopedText: scopedText);
    }

    private static ThinTextEvidence ThinEvidence(IReadOnlyList<FindingDraft> findings)
    {
        var finding = findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText);
        return JsonSerializer.Deserialize<ThinTextEvidence>(finding.EvidenceJson, JsonOptions)!;
    }

    /// <summary>
    /// THE probe-2 regression: the real thin.html fixture (6 visible words,
    /// ratio above the ratio threshold) MUST emit content.thin-text on the URL
    /// with the counted text as evidence.
    /// </summary>
    [Fact]
    public async Task RealFixtureThinPage_EmitsThinText_WithCountedTextEvidence()
    {
        var context = FixtureContext(FixtureSitePages.ThinHtml);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var finding = report.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText);
        Assert.Equal(AuditCategory.Content, finding.Category);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Equal(context.Url.Id, finding.UrlId);

        var evidence = ThinEvidence(report.Findings);
        Assert.Equal(6, evidence.WordCount);
        Assert.Equal(200, evidence.WordThreshold);
        Assert.Equal("Thin Only a few words here.", evidence.Text);
        Assert.False(evidence.TextTruncated);
        Assert.False(evidence.Scoped);
        Assert.Equal(["word-count"], evidence.Reasons);
        Assert.Equal(Encoding.UTF8.GetByteCount(FixtureSitePages.ThinHtml), evidence.MarkupLength);
        Assert.Equal("Thin Only a few words here.".Length, evidence.VisibleTextLength);

        // The ratio signal alone would NOT have fired on this fixture — the
        // word-count signal is what fixes the probe-2 defect.
        Assert.True(evidence.TextToHtmlRatio >= evidence.RatioThreshold);

        // The metrics annotation stays consistent with the thin finding.
        var metrics = JsonSerializer.Deserialize<ContentMetricsEvidence>(
            report.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.Metrics).EvidenceJson, JsonOptions)!;
        Assert.Equal(6, metrics.WordCount);
    }

    /// <summary>The §7.12 text-to-HTML ratio signal still fires on its own
    /// (large markup, word count above the word threshold).</summary>
    [Fact]
    public async Task RatioOnlySignal_StillEmitsThinText()
    {
        var html = FixtureSitePages.Page(
            "Ratio page",
            body: $"<h1>Ratio page</h1>{FixtureSitePages.Para(250, "ratio")}<!--{new string('x', 50_000)}-->");
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html));

        var evidence = ThinEvidence(report.Findings);
        Assert.True(evidence.WordCount >= 200);
        Assert.True(evidence.TextToHtmlRatio < evidence.RatioThreshold);
        Assert.Equal(["text-to-html-ratio"], evidence.Reasons);
    }

    /// <summary>Both signals firing produce ONE finding listing both reasons
    /// (per-URL dedup of the thin-content rule).</summary>
    [Fact]
    public async Task BothSignals_EmitSingleFinding_WithBothReasons()
    {
        var html = FixtureSitePages.Page(
            "Both signals",
            body: $"<h1>Both</h1><p>few words here</p><!--{new string('x', 50_000)}-->");
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html));

        Assert.Single(report.Findings, f => f.RuleKey == ContentQualityRuleKeys.ThinText);
        var evidence = ThinEvidence(report.Findings);
        Assert.Equal(["word-count", "text-to-html-ratio"], evidence.Reasons);
    }

    /// <summary>A healthy page (word count at/above threshold, ratio above
    /// threshold) emits no thin-content finding.</summary>
    [Fact]
    public async Task HealthyPage_EmitsNoThinText()
    {
        var html = FixtureSitePages.Page(
            "Healthy page",
            body: $"<h1>Healthy page</h1>{FixtureSitePages.Para(250, "healthy")}");
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html));

        Assert.DoesNotContain(report.Findings, f => f.RuleKey == ContentQualityRuleKeys.ThinText);
    }

    /// <summary>The word threshold is strict: word count == threshold is not
    /// thin; threshold - 1 is thin. The threshold is locally configurable.</summary>
    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public async Task WordThresholdBoundary_IsStrictAndConfigurable(int words, bool expectThin)
    {
        var text = string.Join(" ", Enumerable.Range(0, words).Select(i => $"word{i}"));
        var html = FixtureSitePages.Page("Boundary", body: $"<p>{text}</p>");
        var analyzer = CreateAnalyzer(new ContentQualityOptions(SpellingEnabled: false, ThinContentWordThreshold: 10));

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html));

        if (expectThin)
        {
            var evidence = ThinEvidence(report.Findings);
            Assert.Equal(words, evidence.WordCount);
            Assert.Equal(10, evidence.WordThreshold);
            Assert.Equal(["word-count"], evidence.Reasons);
        }
        else
        {
            Assert.DoesNotContain(report.Findings, f => f.RuleKey == ContentQualityRuleKeys.ThinText);
        }
    }

    /// <summary>Raising the configured threshold above the fixture's word count
    /// suppresses the word-count signal (configuration governs the rule).</summary>
    [Fact]
    public async Task ConfiguredThreshold_SuppressesWordCountSignal()
    {
        var analyzer = CreateAnalyzer(new ContentQualityOptions(SpellingEnabled: false, ThinContentWordThreshold: 5));

        var report = await analyzer.AnalyzePageAsync(FixtureContext(FixtureSitePages.ThinHtml));

        // 6 words >= 5-word threshold and ratio ≈0.12 >= 0.10: no signal fires.
        Assert.DoesNotContain(report.Findings, f => f.RuleKey == ContentQualityRuleKeys.ThinText);
    }

    /// <summary>With a content scope configured, the COUNTED text is the scoped
    /// visible text (FS-CRAWL-002 §4.3): a healthy full page whose scoped
    /// content is thin emits the finding with the scoped text as evidence.</summary>
    [Fact]
    public async Task ScopedText_DrivesThinRule_AndIsTheEvidenceText()
    {
        var html = FixtureSitePages.Page(
            "Scoped page",
            body: $"<main><p>Scoped thin text</p></main>{FixtureSitePages.Para(250, "boiler")}");
        var scoped = new ScopedVisibleText("Scoped thin text", 3, ["matched main"]);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html, scopedText: scoped));

        var evidence = ThinEvidence(report.Findings);
        Assert.True(evidence.Scoped);
        Assert.Equal(3, evidence.WordCount);
        Assert.Equal("Scoped thin text", evidence.Text);
    }

    /// <summary>When only the configuration defines a scope, the analyzer
    /// resolves it and the thin rule consumes the resolved scoped text.</summary>
    [Fact]
    public async Task ConfigurationScope_ResolvesAndFeedsThinRule()
    {
        var html = FixtureSitePages.Page(
            "Scoped page",
            body: $"<main><p>Scoped thin text</p></main>{FixtureSitePages.Para(250, "boiler")}");
        var document = Parser.Parse(html, FixtureSitePages.BaseUrl + "/pages/scoped.html");
        var scope = new ContentScopeDefinition(["main"], []);
        var resolver = new RecordingScopeResolver(new ScopedVisibleText("Scoped thin text", 3, []));
        var context = ContextFactory.Create(document, html: html, scope: scope);
        var analyzer = new ContentQualityAnalyzer(
            scopeResolver: resolver, options: new ContentQualityOptions(SpellingEnabled: false));

        var report = await analyzer.AnalyzePageAsync(context);

        var evidence = ThinEvidence(report.Findings);
        Assert.True(evidence.Scoped);
        Assert.Equal("Scoped thin text", evidence.Text);
        Assert.Single(resolver.Calls);
    }

    /// <summary>The evidence text is bounded for pathological long-token pages;
    /// the truncation is explicit and the full length still rides evidence.</summary>
    [Fact]
    public async Task PathologicalLongToken_BoundsEvidenceText()
    {
        var longToken = new string('a', 9000);
        var html = FixtureSitePages.Page("Long token", body: $"<p>{longToken}</p>");
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(FixtureContext(html));

        var evidence = ThinEvidence(report.Findings);
        Assert.Equal(1, evidence.WordCount);
        Assert.True(evidence.TextTruncated);
        Assert.Equal(ThinTextEvidence.TextBound, evidence.Text.Length);
        Assert.Equal(9000, evidence.VisibleTextLength);
    }

    /// <summary>Deterministic offline re-run: identical evidence on repeated
    /// analysis of the stored fixture (§9.6 re-audit).</summary>
    [Fact]
    public async Task RepeatedAnalysis_IsDeterministic()
    {
        var analyzer = CreateAnalyzer();
        var context = FixtureContext(FixtureSitePages.ThinHtml);

        var first = await analyzer.AnalyzePageAsync(context);
        var second = await analyzer.AnalyzePageAsync(context);

        Assert.Equal(
            first.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText).EvidenceJson,
            second.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText).EvidenceJson);
    }

    /// <summary>Thread-safe under concurrent page analysis (pipeline
    /// MaxConcurrency=2): one shared analyzer instance, identical verdicts.</summary>
    [Fact]
    public async Task ConcurrentAnalysis_ProducesIdenticalFindings()
    {
        var analyzer = CreateAnalyzer();
        var context = FixtureContext(FixtureSitePages.ThinHtml);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => analyzer.AnalyzePageAsync(context))));

        var expected = results[0].Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText).EvidenceJson;
        Assert.All(results, report =>
        {
            var finding = report.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText);
            Assert.Equal(expected, finding.EvidenceJson);
        });
    }
}
