using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// Content-quality facade evidence (FS-CRAWL-001 §7.8/§7.12, FS-CRAWL-002
/// §4/§15.3): scoped visible text consumption with baseline-identical behavior
/// when unconfigured, pixel-truncation findings under the locked rule keys,
/// serp_metrics inventory, and metric contributions.
/// </summary>
public class ContentQualityAnalyzerTests
{
    private static readonly HunspellSpellingChecker SharedSpelling = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static ContentQualityAnalyzer CreateAnalyzer(
        IContentScopeResolver? resolver = null,
        ContentQualityOptions? options = null,
        IReadOnlyCollection<string>? userDictionary = null) =>
        new(readability: null, spelling: SharedSpelling, serp: null, scopeResolver: resolver,
            options: options, userDictionary: userDictionary);

    private static ContentMetricsEvidence ReadMetricsEvidence(IReadOnlyList<FindingDraft> findings)
    {
        var metricsFinding = findings.Single(f => f.RuleKey == ContentQualityRuleKeys.Metrics);
        return JsonSerializer.Deserialize<ContentMetricsEvidence>(metricsFinding.EvidenceJson, JsonOptions)!;
    }

    /// <summary>With no scope configured, metrics equal direct computation over
    /// the full visible text — baseline-identical (§4.2 precedence rule 4).</summary>
    [Fact]
    public async Task Unconfigured_Scope_Is_Baseline_Identical()
    {
        var document = DomBuilder.Page("Title", "Description", "The cat sat. The dog ran.");
        var context = ContextFactory.Create(document, html: "<html><body><p>The cat sat. The dog ran.</p></body></html>");
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var evidence = ReadMetricsEvidence(report.Findings);
        var expected = new ReadabilityCalculator().Calculate("The cat sat. The dog ran.");
        Assert.False(evidence.Scoped);
        Assert.Equal(expected.WordCount, evidence.WordCount);
        Assert.Equal(expected.SentenceCount, evidence.SentenceCount);
        Assert.Equal(expected.FleschReadingEase, evidence.FleschReadingEase, 6);
        Assert.DoesNotContain(report.Metrics, m => m.MetricKey == MetricKeys.ScopedWordCount);
    }

    /// <summary>Resolved scoped text on the context is consumed by every text metric.</summary>
    [Fact]
    public async Task Scoped_Text_On_Context_Is_Consumed()
    {
        var document = DomBuilder.Page("Title", "Description", "Full body text with many words that should not be counted");
        var scoped = new ScopedVisibleText("Scoped text here", 3, ["matched main"]);
        var context = ContextFactory.Create(document, html: new string('x', 500), scopedText: scoped);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var evidence = ReadMetricsEvidence(report.Findings);
        Assert.True(evidence.Scoped);
        Assert.Equal(3, evidence.WordCount);
        Assert.Equal(3.0 / 200, evidence.ReadingTimeMinutes, 6);
        Assert.Contains(report.Metrics, m => m.MetricKey == MetricKeys.ScopedWordCount && m.Value == 3);
    }

    /// <summary>When the configuration defines a scope but the context carries no
    /// resolved text, the analyzer resolves it through IContentScopeResolver.</summary>
    [Fact]
    public async Task Configuration_Scope_Resolves_Through_Resolver()
    {
        var document = DomBuilder.Page("Title", "Description", "Full body text");
        var scope = new ContentScopeDefinition(["main"], ["nav"]);
        var resolver = new RecordingScopeResolver(new ScopedVisibleText("Resolved scoped text", 3, []));
        var context = ContextFactory.Create(document, scope: scope);
        var analyzer = CreateAnalyzer(resolver);

        var report = await analyzer.AnalyzePageAsync(context);

        var call = Assert.Single(resolver.Calls);
        Assert.Same(scope, call.Scope);
        Assert.True(ReadMetricsEvidence(report.Findings).Scoped);
    }

    /// <summary>Over-budget titles emit title-pixel-truncation (Warning, Content)
    /// with pixel evidence, plus serp_metrics inventory and serp-truncation metric.</summary>
    [Fact]
    public async Task Over_Budget_Title_Emits_Truncation_Finding()
    {
        var longTitle = string.Join(" ", Enumerable.Repeat("WideWord", 40));
        var document = DomBuilder.Page(longTitle, "Short description", "Body text");
        var context = ContextFactory.Create(document);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var finding = report.Findings.Single(f => f.RuleKey == FindingRuleKeys.TitlePixelTruncation);
        Assert.Equal(AuditCategory.Content, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(7, finding.UrlId);
        var measurement = JsonSerializer.Deserialize<SerpElementMeasurement>(finding.EvidenceJson, JsonOptions)!;
        Assert.Equal("title", measurement.Role);
        Assert.True(measurement.WidthPixels > 580);
        Assert.True(measurement.OverflowPixels > 0);
        Assert.True(measurement.IsTruncated);
        Assert.EndsWith("…", measurement.TruncatedText);

        var inventory = Assert.Single(report.Inventory);
        Assert.Equal(ElementGroups.SerpMetrics, inventory.Group);
        Assert.Contains(report.Metrics, m => m.MetricKey == MetricKeys.SerpTruncation && m.Value == 1
            && m.DimensionsJson.Contains("title"));
    }

    /// <summary>Over-budget descriptions emit description-pixel-truncation.</summary>
    [Fact]
    public async Task Over_Budget_Description_Emits_Truncation_Finding()
    {
        var longDescription = string.Join(" ", Enumerable.Repeat("DescriptionWord", 80));
        var document = DomBuilder.Page("Short title", longDescription, "Body text");
        var context = ContextFactory.Create(document);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var finding = report.Findings.Single(f => f.RuleKey == FindingRuleKeys.DescriptionPixelTruncation);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.DoesNotContain(report.Findings, f => f.RuleKey == FindingRuleKeys.TitlePixelTruncation);
    }

    /// <summary>Within-budget elements produce no truncation findings; the
    /// serp-truncation metric still records zero per element.</summary>
    [Fact]
    public async Task Within_Budget_Elements_Do_Not_Truncate()
    {
        var document = DomBuilder.Page("Short title", "Short description", "Body text");
        var context = ContextFactory.Create(document);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        Assert.DoesNotContain(report.Findings, f => f.RuleKey is FindingRuleKeys.TitlePixelTruncation or FindingRuleKeys.DescriptionPixelTruncation);
        Assert.Equal(2, report.Metrics.Count(m => m.MetricKey == MetricKeys.SerpTruncation && m.Value == 0));
    }

    /// <summary>Configuration SERP budgets override the defaults.</summary>
    [Fact]
    public async Task Configuration_Budgets_Override_Defaults()
    {
        var document = DomBuilder.Page("A modest title", "Description", "Body");
        var budgets = new SerpBudgetSettings(TitleBudgetPixels: 10, DescriptionBudgetPixels: 920, FontProfileKey: "arial-desktop");
        var context = ContextFactory.Create(document, budgets: budgets);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        Assert.Contains(report.Findings, f => f.RuleKey == FindingRuleKeys.TitlePixelTruncation);
    }

    /// <summary>Thin visible-text-to-markup ratio emits the thin-content Notice.</summary>
    [Fact]
    public async Task Thin_Content_Emits_Notice()
    {
        var document = DomBuilder.Page("Title", "Description", "Tiny");
        var context = ContextFactory.Create(document, html: new string('x', 10_000));
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        var finding = report.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.ThinText);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Equal(AuditCategory.Content, finding.Category);
    }

    /// <summary>Misspellings emit content.spelling with token evidence; the user
    /// dictionary suppresses the finding entirely.</summary>
    [Fact]
    public async Task Spelling_Findings_Honor_User_Dictionary()
    {
        var document = DomBuilder.Page("Title", "Description", "Ths qck brwn fx");
        var context = ContextFactory.Create(document);

        var withIssues = await CreateAnalyzer().AnalyzePageAsync(context);
        var spelling = withIssues.Findings.Single(f => f.RuleKey == ContentQualityRuleKeys.Spelling);
        Assert.Equal(FindingSeverity.Warning, spelling.Severity);
        Assert.Contains("Ths", spelling.EvidenceJson);

        var suppressed = await CreateAnalyzer(userDictionary: ["Ths", "qck", "brwn", "fx"]).AnalyzePageAsync(context);
        Assert.DoesNotContain(suppressed.Findings, f => f.RuleKey == ContentQualityRuleKeys.Spelling);
        Assert.Equal(0, ReadMetricsEvidence(suppressed.Findings).SpellingIssueCount);
    }

    /// <summary>The locked interface returns the same findings as the rich report.</summary>
    [Fact]
    public async Task Locked_Interface_Returns_Findings()
    {
        var document = DomBuilder.Page("Title", "Description", "The cat sat.");
        var context = ContextFactory.Create(document);
        IContentQualityAnalyzer analyzer = CreateAnalyzer();

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == ContentQualityRuleKeys.Metrics);
        Assert.All(findings, f => Assert.Equal(AuditCategory.Content, f.Category));
    }

    /// <summary>A context without any document yields an empty report.</summary>
    [Fact]
    public async Task Missing_Document_Yields_Empty_Report()
    {
        var context = ContextFactory.Create(document: null);
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Inventory);
        Assert.Empty(report.Metrics);
    }

    /// <summary>Readability, reading-time, and ratio metrics are always contributed.</summary>
    [Fact]
    public async Task Core_Metrics_Are_Contributed()
    {
        var document = DomBuilder.Page("Title", "Description", "The cat sat. The dog ran.");
        var context = ContextFactory.Create(document, html: new string('x', 200));
        var analyzer = CreateAnalyzer();

        var report = await analyzer.AnalyzePageAsync(context);

        Assert.Contains(report.Metrics, m => m.MetricKey == "content.flesch-reading-ease");
        Assert.Contains(report.Metrics, m => m.MetricKey == "content.flesch-kincaid-grade");
        Assert.Contains(report.Metrics, m => m.MetricKey == "content.reading-time-minutes" && m.Value == 6.0 / 200);
        Assert.Contains(report.Metrics, m => m.MetricKey == "content.text-to-html-ratio");
    }
}
