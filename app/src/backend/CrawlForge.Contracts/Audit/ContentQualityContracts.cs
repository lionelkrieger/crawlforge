namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Content quality analyzer family (FS-CRAWL-001 §7.5/§7.12): content metrics
/// and quality findings over the scoped visible text.
/// </summary>
public interface IContentQualityAnalyzer
{
    /// <summary>Analyzes content quality of a page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Content quality findings.</returns>
    Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dictionary-based spelling checker (FS-CRAWL-001 §7.8). Dictionaries are
/// bundled assets; user dictionary additions are local and suppress findings.
/// </summary>
public interface ISpellingChecker
{
    /// <summary>Checks text for misspellings in a language.</summary>
    /// <param name="input">Text, language, and accepted words.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Spelling issues with suggestions.</returns>
    Task<SpellingCheckResult> CheckAsync(SpellingCheckInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Spelling check input.
/// </summary>
/// <param name="Text">Text to check.</param>
/// <param name="Language">Language tag selecting the bundled dictionary.</param>
/// <param name="AcceptedWords">User-accepted spellings that suppress findings.</param>
public sealed record SpellingCheckInput(
    string Text,
    string Language,
    IReadOnlyCollection<string> AcceptedWords);

/// <summary>
/// Spelling check result.
/// </summary>
/// <param name="Issues">Misspelling issues in text order.</param>
public sealed record SpellingCheckResult(IReadOnlyList<SpellingIssue> Issues);

/// <summary>
/// One misspelling issue.
/// </summary>
/// <param name="Word">Misspelled word.</param>
/// <param name="Offset">Character offset in the checked text.</param>
/// <param name="Suggestions">Dictionary suggestions.</param>
public sealed record SpellingIssue(string Word, int Offset, IReadOnlyList<string> Suggestions);

/// <summary>
/// Readability calculator (FS-CRAWL-001 v1.4): Flesch reading ease and
/// Flesch–Kincaid grade level over visible text.
/// </summary>
public interface IReadabilityCalculator
{
    /// <summary>Computes readability metrics for text.</summary>
    /// <param name="text">Visible text.</param>
    /// <returns>Readability scores and counters.</returns>
    ReadabilityScore Calculate(string text);
}

/// <summary>
/// Readability metrics.
/// </summary>
/// <param name="FleschReadingEase">Flesch reading-ease score.</param>
/// <param name="FleschKincaidGradeLevel">Flesch–Kincaid grade level.</param>
/// <param name="WordCount">Words counted.</param>
/// <param name="SentenceCount">Sentences counted.</param>
/// <param name="SyllableCount">Syllables counted.</param>
public sealed record ReadabilityScore(
    double FleschReadingEase,
    double FleschKincaidGradeLevel,
    int WordCount,
    int SentenceCount,
    int SyllableCount);

/// <summary>
/// Shingle-based near-duplicate detector (FS-CRAWL-001 §7.5): site-wide
/// pairwise similarity over content shingles.
/// </summary>
public interface IShingleNearDuplicateDetector
{
    /// <summary>Finds near-duplicate URL pairs within an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="similarityThreshold">Minimum similarity to report (0–1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Near-duplicate pairs above the threshold.</returns>
    Task<IReadOnlyList<NearDuplicatePair>> FindNearDuplicatesAsync(
        long auditId,
        double similarityThreshold,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One near-duplicate URL pair.
/// </summary>
/// <param name="UrlIdA">First URL record identifier.</param>
/// <param name="UrlIdB">Second URL record identifier.</param>
/// <param name="Similarity">Shingle similarity (0–1).</param>
public sealed record NearDuplicatePair(long UrlIdA, long UrlIdB, double Similarity);

/// <summary>
/// Pixel-width SERP measurement (FS-CRAWL-002 §15): measures rendered pixel
/// width of titles/descriptions against bundled font-metric tables and SERP
/// budgets. Font metrics are packaged assets (never fetched at runtime).
/// </summary>
public interface ISerpPixelMeasurer
{
    /// <summary>Measures the pixel width of a text under a font profile.</summary>
    /// <param name="text">Text to measure.</param>
    /// <param name="options">Font profile and pixel budget.</param>
    /// <returns>Pixel measurement with truncation verdict.</returns>
    SerpPixelMeasurement Measure(string text, SerpMeasurementOptions options);
}

/// <summary>
/// SERP measurement options.
/// </summary>
/// <param name="FontProfileKey">Bundled font-metric profile key.</param>
/// <param name="BudgetPixels">Pixel-width budget before truncation.</param>
public sealed record SerpMeasurementOptions(string FontProfileKey, double BudgetPixels);

/// <summary>
/// SERP pixel measurement result.
/// </summary>
/// <param name="WidthPixels">Measured pixel width.</param>
/// <param name="IsTruncated">Whether the text exceeds the budget.</param>
/// <param name="TruncatedText">Text visible within the budget, when truncated.</param>
public sealed record SerpPixelMeasurement(double WidthPixels, bool IsTruncated, string? TruncatedText);

/// <summary>
/// SERP pixel budgets (additive configuration fields, FS-CRAWL-002 §15.3/§15.5).
/// </summary>
/// <param name="TitleBudgetPixels">Pixel budget for titles.</param>
/// <param name="DescriptionBudgetPixels">Pixel budget for meta descriptions.</param>
/// <param name="FontProfileKey">Bundled font-metric profile key.</param>
public sealed record SerpBudgetSettings(
    double TitleBudgetPixels,
    double DescriptionBudgetPixels,
    string FontProfileKey);
