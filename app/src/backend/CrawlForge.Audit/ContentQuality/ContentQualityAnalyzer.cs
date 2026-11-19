using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Content-quality analyzer facade (FS-CRAWL-001 §7.8/§7.12, FS-CRAWL-002
/// §15.3): thin content (word count + text-to-HTML ratio), readability,
/// reading time, spelling, and SERP pixel measurement over the Scoped Visible
/// Text Tree when configured — baseline-identical full visible text when
/// unconfigured (FS-CRAWL-002 §4).
/// The locked interface returns findings; <see cref="AnalyzePageAsync"/>
/// additionally surfaces the serp_metrics inventory and metric contributions.
/// </summary>
public sealed class ContentQualityAnalyzer : IContentQualityAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadabilityCalculator _readability;
    private readonly ISpellingChecker? _spelling;
    private readonly ISerpPixelMeasurer? _serp;
    private readonly IContentScopeResolver? _scopeResolver;
    private readonly ContentQualityOptions _options;
    private IReadOnlyCollection<string> _userDictionary;

    /// <summary>Creates the analyzer; null dependencies use the bundled defaults.</summary>
    /// <param name="readability">Readability calculator; null uses <see cref="ReadabilityCalculator"/>.</param>
    /// <param name="spelling">Spelling checker; null uses the bundled Hunspell checker when spelling is enabled.</param>
    /// <param name="serp">SERP pixel measurer; null uses the bundled font-metric measurer.</param>
    /// <param name="scopeResolver">Scope resolver used when the context carries no resolved scoped text but the configuration defines a scope.</param>
    /// <param name="options">Content-quality options.</param>
    /// <param name="userDictionary">Local user-dictionary entries suppressing spelling findings (§7.8).</param>
    public ContentQualityAnalyzer(
        IReadabilityCalculator? readability = null,
        ISpellingChecker? spelling = null,
        ISerpPixelMeasurer? serp = null,
        IContentScopeResolver? scopeResolver = null,
        ContentQualityOptions? options = null,
        IReadOnlyCollection<string>? userDictionary = null)
    {
        _options = options ?? ContentQualityOptions.Default;
        _readability = readability ?? new ReadabilityCalculator();
        _spelling = spelling ?? (_options.SpellingEnabled ? new HunspellSpellingChecker() : null);
        _serp = serp ?? new SerpPixelMeasurer();
        _scopeResolver = scopeResolver;
        _userDictionary = userDictionary ?? [];
    }

    /// <summary>
    /// Replaces the user-dictionary snapshot used for spelling suppression
    /// (D13 live wiring: the Host feeds persisted <c>user_dictionary</c> entries
    /// at startup and after every REST mutation, so accepted spellings apply
    /// without rebuilding the singleton analyzer). Thread-safe reference swap;
    /// in-flight checks keep the snapshot they started with.
    /// </summary>
    /// <param name="entries">Accepted spellings; null clears the dictionary.</param>
    public void UpdateUserDictionary(IReadOnlyCollection<string>? entries) =>
        Volatile.Write(ref _userDictionary, entries ?? []);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        var report = await AnalyzePageAsync(context, cancellationToken).ConfigureAwait(false);
        return report.Findings;
    }

    /// <summary>
    /// Produces the full content-quality report: findings, serp_metrics element
    /// inventory, and metric contributions (readability, reading time,
    /// text-to-HTML, scoped word count, serp-truncation).
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content-quality report.</returns>
    public async Task<ContentQualityReport> AnalyzePageAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var document = context.Document ?? context.RenderedDocument;
        if (document is null)
        {
            return new ContentQualityReport([], [], []);
        }

        var (text, wordCount, scoped) = ResolveText(context, document);
        var markupLength = context.Fetch?.Body.Length ?? 0;
        var readability = _readability.Calculate(text);
        var textMetrics = TextMetricsCalculator.Compute(text, wordCount, markupLength, _options.WordsPerMinute);

        var spellingIssues = await CheckSpellingAsync(context, document, text, cancellationToken).ConfigureAwait(false);
        var serpMeasurements = MeasureSerp(context, document);

        var findings = BuildFindings(context, text, readability, textMetrics, scoped, spellingIssues, serpMeasurements);
        var inventory = BuildInventory(serpMeasurements);
        var metrics = BuildMetrics(readability, textMetrics, scoped, serpMeasurements);
        return new ContentQualityReport(findings, inventory, metrics);
    }

    private (string Text, int WordCount, bool Scoped) ResolveText(PageAuditContext context, DomDocument document)
    {
        if (context.ScopedText is not null)
        {
            return (context.ScopedText.Text, context.ScopedText.WordCount, true);
        }

        var scope = context.Configuration.ContentScope;
        if (scope is not null && _scopeResolver is not null)
        {
            var resolved = _scopeResolver.Resolve(document, scope);
            return (resolved.Text, resolved.WordCount, true);
        }

        var text = VisibleTextExtractor.ExtractVisibleText(document);
        return (text, VisibleTextExtractor.CountWords(text), false);
    }

    private async Task<IReadOnlyList<SpellingIssue>> CheckSpellingAsync(
        PageAuditContext context, DomDocument document, string text, CancellationToken cancellationToken)
    {
        if (_spelling is null || !_options.SpellingEnabled || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var language = VisibleTextExtractor.ExtractLanguage(document) ?? _options.PreferredCheckingLanguage;
        var result = await _spelling.CheckAsync(
            new SpellingCheckInput(text, language, Volatile.Read(ref _userDictionary)), cancellationToken).ConfigureAwait(false);
        return result.Issues.Count > _options.MaxSpellingIssues
            ? result.Issues.Take(_options.MaxSpellingIssues).ToList()
            : result.Issues;
    }

    private List<SerpElementMeasurement> MeasureSerp(PageAuditContext context, DomDocument document)
    {
        var measurements = new List<SerpElementMeasurement>();
        if (_serp is null)
        {
            return measurements;
        }

        var budgets = context.Configuration.SerpBudgets ?? _options.DefaultSerpBudgets
            ?? new SerpBudgetSettings(580, 920, "arial-desktop");

        var title = VisibleTextExtractor.ExtractTitle(document);
        if (!string.IsNullOrEmpty(title))
        {
            measurements.Add(MeasureElement("title", title, budgets.FontProfileKey + "-title", budgets.TitleBudgetPixels));
        }

        var description = VisibleTextExtractor.ExtractMetaDescription(document);
        if (!string.IsNullOrEmpty(description))
        {
            measurements.Add(MeasureElement("description", description, budgets.FontProfileKey + "-description", budgets.DescriptionBudgetPixels));
        }

        return measurements;
    }

    private SerpElementMeasurement MeasureElement(string role, string text, string profileKey, double budget)
    {
        var measurement = _serp!.Measure(text, new SerpMeasurementOptions(profileKey, budget));
        var overflow = Math.Max(0, measurement.WidthPixels - budget);
        return new SerpElementMeasurement(
            role, text, profileKey, measurement.WidthPixels, budget, overflow,
            measurement.IsTruncated, measurement.TruncatedText);
    }

    private List<FindingDraft> BuildFindings(
        PageAuditContext context,
        string text,
        ReadabilityScore readability,
        TextMetricsResult textMetrics,
        bool scoped,
        IReadOnlyList<SpellingIssue> spellingIssues,
        List<SerpElementMeasurement> serpMeasurements)
    {
        var findings = new List<FindingDraft>();
        var urlId = context.Url.Id;

        foreach (var measurement in serpMeasurements)
        {
            if (!measurement.IsTruncated)
            {
                continue;
            }

            var ruleKey = measurement.Role == "title"
                ? FindingRuleKeys.TitlePixelTruncation
                : FindingRuleKeys.DescriptionPixelTruncation;
            findings.Add(new FindingDraft(
                ruleKey, AuditCategory.Content, FindingSeverity.Warning, urlId,
                JsonSerializer.Serialize(measurement, JsonOptions), RecommendationKey: null));
        }

        if (spellingIssues.Count > 0)
        {
            var evidence = new
            {
                issueCount = spellingIssues.Count,
                issues = spellingIssues.Select(i => new { word = i.Word, offset = i.Offset, suggestions = i.Suggestions }),
            };
            findings.Add(new FindingDraft(
                ContentQualityRuleKeys.Spelling, AuditCategory.Content, FindingSeverity.Warning, urlId,
                JsonSerializer.Serialize(evidence, JsonOptions), RecommendationKey: null));
        }

        // Thin content (FS-CRAWL-001 §7.4 content-quality row, §7.12; WP-09-05
        // defect fix): the visible WORD COUNT below threshold is the primary
        // signal (fixture trap "thin-page": 6 visible words). The text-to-HTML
        // ratio remains the secondary §7.12 signal. Either fires ONE finding
        // per URL, and the evidence carries the counted text itself — scoped
        // when a content scope is configured (FS-CRAWL-002 §4.3).
        var thinByWordCount = textMetrics.WordCount < _options.ThinContentWordThreshold;
        var thinByRatio = textMetrics.MarkupLength > 0 && textMetrics.TextToHtmlRatio < _options.ThinTextRatioThreshold;
        if (thinByWordCount || thinByRatio)
        {
            var reasons = new List<string>(2);
            if (thinByWordCount)
            {
                reasons.Add("word-count");
            }

            if (thinByRatio)
            {
                reasons.Add("text-to-html-ratio");
            }

            var truncated = text.Length > ThinTextEvidence.TextBound;
            var evidence = new ThinTextEvidence(
                textMetrics.WordCount,
                _options.ThinContentWordThreshold,
                textMetrics.TextToHtmlRatio,
                _options.ThinTextRatioThreshold,
                textMetrics.VisibleTextLength,
                textMetrics.MarkupLength,
                truncated ? text[..ThinTextEvidence.TextBound] : text,
                truncated,
                scoped,
                reasons);
            findings.Add(new FindingDraft(
                ContentQualityRuleKeys.ThinText, AuditCategory.Content, FindingSeverity.Notice, urlId,
                JsonSerializer.Serialize(evidence, JsonOptions), RecommendationKey: null));
        }

        var metricsEvidence = new ContentMetricsEvidence(
            readability.FleschReadingEase,
            readability.FleschKincaidGradeLevel,
            readability.WordCount,
            readability.SentenceCount,
            readability.SyllableCount,
            textMetrics.ReadingTimeMinutes,
            textMetrics.TextToHtmlRatio,
            scoped,
            spellingIssues.Count,
            serpMeasurements);
        findings.Add(new FindingDraft(
            ContentQualityRuleKeys.Metrics, AuditCategory.Content, FindingSeverity.Notice, urlId,
            JsonSerializer.Serialize(metricsEvidence, JsonOptions), RecommendationKey: null));

        return findings;
    }

    private static List<ElementInventoryItem> BuildInventory(List<SerpElementMeasurement> measurements)
    {
        if (measurements.Count == 0)
        {
            return [];
        }

        var payload = new
        {
            title = measurements.FirstOrDefault(m => m.Role == "title"),
            description = measurements.FirstOrDefault(m => m.Role == "description"),
        };
        return [new ElementInventoryItem(ElementGroups.SerpMetrics, JsonSerializer.Serialize(payload, JsonOptions), 0)];
    }

    private List<MetricContribution> BuildMetrics(
        ReadabilityScore readability,
        TextMetricsResult textMetrics,
        bool scoped,
        List<SerpElementMeasurement> measurements)
    {
        var metrics = new List<MetricContribution>
        {
            new("content.flesch-reading-ease", "{}", readability.FleschReadingEase),
            new("content.flesch-kincaid-grade", "{}", readability.FleschKincaidGradeLevel),
            new("content.reading-time-minutes", "{}", textMetrics.ReadingTimeMinutes),
            new("content.text-to-html-ratio", "{}", textMetrics.TextToHtmlRatio),
        };

        if (scoped)
        {
            metrics.Add(new MetricContribution(MetricKeys.ScopedWordCount, "{}", textMetrics.WordCount));
        }

        foreach (var measurement in measurements)
        {
            var dimensions = JsonSerializer.Serialize(new { element = measurement.Role }, JsonOptions);
            metrics.Add(new MetricContribution(MetricKeys.SerpTruncation, dimensions, measurement.IsTruncated ? 1 : 0));
        }

        return metrics;
    }
}
