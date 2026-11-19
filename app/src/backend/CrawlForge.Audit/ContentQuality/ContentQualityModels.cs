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
    /// <summary>
    /// Thin content (FS-CRAWL-001 §7.4 content-quality row, §7.12; fixture trap
    /// "thin-page"): visible word count below the configured threshold, or a
    /// visible-text-to-markup ratio below the ratio threshold. One finding per
    /// URL carrying the counted (scoped) text as evidence.
    /// </summary>
    public const string ThinText = "content.thin-text";

    /// <summary>Misspelled tokens detected against the bundled dictionary.</summary>
    public const string Spelling = "content.spelling";

    /// <summary>Notice annotation carrying the page content-metric payload.</summary>
    public const string Metrics = "content.metrics";

    /// <summary>
    /// Duplicate page titles (FS-CRAWL-001 §7.4 content-quality row, §7.6
    /// aggregation pass; CAP-B-040): one site-wide finding per duplicated title
    /// grouping ALL affected URLs.
    /// </summary>
    public const string DuplicateTitle = "content.duplicate-title";

    /// <summary>
    /// Duplicate meta descriptions (FS-CRAWL-001 §7.4 content-quality row;
    /// CAP-B-042): one site-wide finding per duplicated description grouping
    /// ALL affected URLs.
    /// </summary>
    public const string DuplicateDescription = "content.duplicate-description";

    /// <summary>
    /// Exact duplicate content (FS-CRAWL-001 §7.5; CAP-B-062/CAP-F-139): one
    /// site-wide finding per group of URLs whose normalized visible text is
    /// identical (SHA-256 equality over the whitespace-normalized text).
    /// </summary>
    public const string DuplicateContent = "content.duplicate-content";

    /// <summary>
    /// Near-duplicate content (FS-CRAWL-001 §7.4 content-quality row, §7.5
    /// shingle similarity; CAP-B-063/CAP-F-140): one site-wide finding per
    /// URL pair above the similarity threshold that is not an exact duplicate.
    /// </summary>
    public const string NearDuplicateContent = "content.near-duplicate-content";
}

/// <summary>
/// Content-quality configuration (FS-CRAWL-001 §7.12): reading-time rate,
/// thin-content thresholds, spelling behavior, and default SERP budgets.
/// Thresholds are locally configurable with application defaults taken from
/// the rule definitions (§7.5); no remote catalogue is consulted (E2).
/// </summary>
/// <param name="WordsPerMinute">Reading-time words-per-minute rate (configurable).</param>
/// <param name="ThinTextRatioThreshold">Text-to-HTML ratio below which content is thin (§7.12 thin-content signal).</param>
/// <param name="SpellingEnabled">Whether spelling checks run.</param>
/// <param name="MaxSpellingIssues">Bound on spelling issues reported per page.</param>
/// <param name="PreferredCheckingLanguage">Fallback checking language when the page declares none.</param>
/// <param name="DefaultSerpBudgets">SERP budgets used when the configuration supplies none.</param>
/// <param name="ThinContentWordThreshold">
/// Visible word count below which content is thin (FS-CRAWL-001 §7.4
/// content-quality row; fixture trap "thin-page"). Application default 200
/// words — the industry-standard thin-content threshold; configurable per
/// audit through this options surface. A page strictly below this count emits
/// <see cref="ContentQualityRuleKeys.ThinText"/> regardless of its markup ratio.
/// </param>
public sealed record ContentQualityOptions(
    int WordsPerMinute = 200,
    double ThinTextRatioThreshold = 0.10,
    bool SpellingEnabled = true,
    int MaxSpellingIssues = 100,
    string PreferredCheckingLanguage = "en-US",
    SerpBudgetSettings? DefaultSerpBudgets = null,
    int ThinContentWordThreshold = 200)
{
    /// <summary>Default options (§15.3 desktop budgets: title ≈580px, description ≈920px).</summary>
    public static ContentQualityOptions Default { get; } = new(
        DefaultSerpBudgets: new SerpBudgetSettings(580, 920, "arial-desktop"));
}

/// <summary>
/// Structured evidence payload of the <c>content.thin-text</c> finding
/// (FS-CRAWL-001 §7.4/§7.12; WP-09-05 thin-content defect fix). Carries the
/// COUNTED text itself — the scoped visible text when a content scope is
/// configured (FS-CRAWL-002 §4.3), the full visible text otherwise — plus both
/// thin-content signals and the thresholds that fired. Serialized camelCase
/// into findings.evidence_json.
/// </summary>
/// <param name="WordCount">Visible word count of the counted text.</param>
/// <param name="WordThreshold">Configured thin-content word threshold.</param>
/// <param name="TextToHtmlRatio">Visible-text length over full markup size.</param>
/// <param name="RatioThreshold">Configured text-to-HTML ratio threshold.</param>
/// <param name="VisibleTextLength">Character length of the counted text.</param>
/// <param name="MarkupLength">Full markup size in bytes; 0 when unknown.</param>
/// <param name="Text">The counted (possibly scoped) visible text, bounded by <see cref="TextBound"/>.</param>
/// <param name="TextTruncated">Whether <paramref name="Text"/> was truncated at the evidence bound.</param>
/// <param name="Scoped">Whether the counted text was scope-filtered.</param>
/// <param name="Reasons">Which signals fired: "word-count" and/or "text-to-html-ratio".</param>
public sealed record ThinTextEvidence(
    int WordCount,
    int WordThreshold,
    double TextToHtmlRatio,
    double RatioThreshold,
    int VisibleTextLength,
    int MarkupLength,
    string Text,
    bool TextTruncated,
    bool Scoped,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Evidence bound on the counted text (characters). Thin pages are
    /// far below this bound; it only guards pathological long-token pages.</summary>
    public const int TextBound = 8192;
}

/// <summary>
/// Cross-URL duplicate-content pass configuration (FS-CRAWL-001 §7.5/§7.6).
/// </summary>
/// <param name="NearDuplicateSimilarityThreshold">Minimum shingle Jaccard similarity reported as near-duplicate (0–1).</param>
/// <param name="MaxReportedPairs">Bound on near-duplicate pair findings emitted in one pass (scale invariance, §7.6).</param>
/// <param name="MaxGroupMembers">Bound on URL members listed in one exact-duplicate group's evidence; the full count always rides <c>urlCount</c>.</param>
public sealed record DuplicateContentOptions(
    double NearDuplicateSimilarityThreshold = 0.9,
    int MaxReportedPairs = 1000,
    int MaxGroupMembers = 500)
{
    /// <summary>Default options.</summary>
    public static DuplicateContentOptions Default { get; } = new();
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
