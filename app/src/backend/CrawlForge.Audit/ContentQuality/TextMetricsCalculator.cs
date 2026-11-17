namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Reading-time and text-to-HTML metrics (FS-CRAWL-001 §7.12). Reading time is
/// word count over a configurable words-per-minute rate; the text-to-HTML ratio
/// keeps the FULL markup size as denominator even when the visible text is
/// scope-filtered (FS-CRAWL-002 §4.3).
/// </summary>
public static class TextMetricsCalculator
{
    /// <summary>Computes reading time and text-to-HTML ratio.</summary>
    /// <param name="visibleText">The (possibly scoped) visible text.</param>
    /// <param name="wordCount">Visible word count.</param>
    /// <param name="markupLength">Full markup size in bytes; 0 when unknown.</param>
    /// <param name="wordsPerMinute">Configurable reading rate (must be positive).</param>
    /// <returns>Reading-time and ratio metrics.</returns>
    public static TextMetricsResult Compute(string visibleText, int wordCount, int markupLength, int wordsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(visibleText);
        ArgumentOutOfRangeException.ThrowIfLessThan(wordsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(markupLength);

        var readingTime = (double)wordCount / wordsPerMinute;
        var ratio = markupLength > 0 ? (double)visibleText.Length / markupLength : 0d;
        return new TextMetricsResult(readingTime, ratio, visibleText.Length, markupLength, wordCount);
    }
}
