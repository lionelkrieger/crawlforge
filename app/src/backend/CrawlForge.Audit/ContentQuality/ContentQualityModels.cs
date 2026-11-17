using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Stable rule keys owned by the content-quality analyzer family. Extension
/// rule keys (title/description pixel truncation) live in
/// <see cref="Contracts.Constants.FindingRuleKeys"/>; these are the base
/// content-quality keys (FS-CRAWL-001 §7.8/§7.12).
/// </summary>
public static class ContentQualityRuleKeys
{
    /// <summary>Thin-content signal: visible-text-to-markup ratio below threshold.</summary>
    public const string ThinText = "content.thin-text";

    /// <summary>Misspelled tokens detected against the bundled dictionary.</summary>
    public const string Spelling = "content.spelling";

    /// <summary>Notice annotation carrying the page content-metric payload.</summary>
    public const string Metrics = "content.metrics";
}

/// <summary>
/// Content-quality configuration (FS-CRAWL-001 §7.12): reading-time rate,
/// thin-content threshold, spelling behavior, and default SERP budgets.
/// </summary>
/// <param name="WordsPerMinute">Reading-time words-per-minute rate (configurable).</param>
/// <param name="ThinTextRatioThreshold">Text-to-HTML ratio below which content is thin.</param>
/// <param name="SpellingEnabled">Whether spelling checks run.</param>
/// <param name="MaxSpellingIssues">Bound on spelling issues reported per page.</param>
/// <param name="PreferredCheckingLanguage">Fallback checking language when the page declares none.</param>
/// <param name="DefaultSerpBudgets">SERP budgets used when the configuration supplies none.</param>
public sealed record ContentQualityOptions(
    int WordsPerMinute = 200,
    double ThinTextRatioThreshold = 0.10,
    bool SpellingEnabled = true,
    int MaxSpellingIssues = 100,
    string PreferredCheckingLanguage = "en-US",
    SerpBudgetSettings? DefaultSerpBudgets = null)
{
    /// <summary>Default options (§15.3 desktop budgets: title ≈580px, description ≈920px).</summary>
    public static ContentQualityOptions Default { get; } = new(
        DefaultSerpBudgets: new SerpBudgetSettings(580, 920, "arial-desktop"));
}

/// <summary>
/// Reading-time and text-to-HTML metrics (FS-CRAWL-001 §7.12). The ratio keeps
/// the FULL markup size as denominator even when text is scope-filtered
/// (FS-CRAWL-002 §4.3).
/// </summary>
/// <param name="ReadingTimeMinutes">Estimated reading time at the configured WPM.</param>
/// <param name="TextToHtmlRatio">Visible-text length over full markup size (0 when markup size is unknown).</param>
/// <param name="VisibleTextLength">Character length of the (possibly scoped) visible text.</param>
/// <param name="MarkupLength">Full markup size in bytes; 0 when unknown.</param>
/// <param name="WordCount">Visible word count.</param>
public sealed record TextMetricsResult(
    double ReadingTimeMinutes,
    double TextToHtmlRatio,
    int VisibleTextLength,
    int MarkupLength,
    int WordCount);

/// <summary>
/// One SERP element measurement (FS-CRAWL-002 §15.3): computed pixel width,
/// budget, overflow, and truncation verdict for a title or description.
/// </summary>
/// <param name="Role">Element role: "title" or "description".</param>
/// <param name="Text">Measured text.</param>
/// <param name="FontProfileKey">Bundled font-metric profile used.</param>
/// <param name="WidthPixels">Computed pixel width.</param>
/// <param name="BudgetPixels">Pixel budget applied.</param>
/// <param name="OverflowPixels">Width beyond the budget (0 when within budget).</param>
/// <param name="IsTruncated">Whether the element exceeds its budget.</param>
/// <param name="TruncatedText">Text visible within the budget, when truncated.</param>
public sealed record SerpElementMeasurement(
    string Role,
    string Text,
    string FontProfileKey,
    double WidthPixels,
    double BudgetPixels,
    double OverflowPixels,
    bool IsTruncated,
    string? TruncatedText);

/// <summary>
/// Structured evidence payload of the <c>content.metrics</c> annotation finding:
/// readability, reading time, text-to-HTML, scope state, and SERP measurements.
/// Serialized camelCase into findings.evidence_json.
/// </summary>
/// <param name="FleschReadingEase">Flesch reading-ease score.</param>
/// <param name="FleschKincaidGradeLevel">Flesch–Kincaid grade level.</param>
/// <param name="WordCount">Visible word count.</param>
/// <param name="SentenceCount">Sentence count.</param>
/// <param name="SyllableCount">Syllable count.</param>
/// <param name="ReadingTimeMinutes">Estimated reading time.</param>
/// <param name="TextToHtmlRatio">Visible-text-to-full-markup ratio.</param>
/// <param name="Scoped">Whether the metrics consumed scoped visible text.</param>
/// <param name="SpellingIssueCount">Misspelling issues detected (bounded).</param>
/// <param name="Serp">SERP element measurements (serp_metrics payload).</param>
public sealed record ContentMetricsEvidence(
    double FleschReadingEase,
    double FleschKincaidGradeLevel,
    int WordCount,
    int SentenceCount,
    int SyllableCount,
    double ReadingTimeMinutes,
    double TextToHtmlRatio,
    bool Scoped,
    int SpellingIssueCount,
    IReadOnlyList<SerpElementMeasurement> Serp);

/// <summary>
/// Rich content-quality output: findings plus the additive element inventory
/// (serp_metrics group) and metric contributions. The locked
/// <see cref="IContentQualityAnalyzer"/> surface returns the findings; the
/// pipeline/Host composes the full report.
/// </summary>
/// <param name="Findings">Content-quality findings for the page.</param>
/// <param name="Inventory">Element inventory items (serp_metrics).</param>
/// <param name="Metrics">Metric contributions (readability, reading time, ratio, serp-truncation).</param>
public sealed record ContentQualityReport(
    IReadOnlyList<FindingDraft> Findings,
    IReadOnlyList<ElementInventoryItem> Inventory,
    IReadOnlyList<MetricContribution> Metrics);

/// <summary>
/// Shingle near-duplicate detection options (FS-CRAWL-001 §7.5).
/// </summary>
/// <param name="ShingleSize">Overlapping token shingle size (k).</param>
/// <param name="MaxUrls">Bound on URLs compared in one pass.</param>
/// <param name="MaxCharacters">Bound on visible characters consumed per URL.</param>
public sealed record ShingleOptions(
    int ShingleSize = 5,
    int MaxUrls = 5000,
    int MaxCharacters = 200_000)
{
    /// <summary>Default options.</summary>
    public static ShingleOptions Default { get; } = new();
}
