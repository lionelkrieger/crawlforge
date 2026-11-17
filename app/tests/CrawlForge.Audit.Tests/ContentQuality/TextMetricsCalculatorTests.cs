using CrawlForge.Audit.ContentQuality;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// FS-CRAWL-001 §7.12 / FS-CRAWL-002 §4.3 evidence: reading time from a
/// configurable WPM and text-to-HTML ratio keeping the FULL markup denominator.
/// </summary>
public class TextMetricsCalculatorTests
{
    /// <summary>Reading time is word count over the configured rate.</summary>
    [Fact]
    public void Reading_Time_Uses_Configured_Wpm()
    {
        var metrics = TextMetricsCalculator.Compute("one two three four", 4, markupLength: 100, wordsPerMinute: 200);

        Assert.Equal(0.02, metrics.ReadingTimeMinutes, 6);
        Assert.Equal(4, metrics.WordCount);
    }

    /// <summary>The ratio divides visible-text length by full markup size.</summary>
    [Fact]
    public void Ratio_Uses_Full_Markup_Denominator()
    {
        const string text = "hello world";
        var metrics = TextMetricsCalculator.Compute(text, 2, markupLength: 110, wordsPerMinute: 200);

        Assert.Equal(11, metrics.VisibleTextLength);
        Assert.Equal(110, metrics.MarkupLength);
        Assert.Equal(0.1, metrics.TextToHtmlRatio, 6);
    }

    /// <summary>Scoped (shorter) text keeps the same full-markup denominator.</summary>
    [Fact]
    public void Scoped_Text_Keeps_Full_Markup_Denominator()
    {
        var full = TextMetricsCalculator.Compute("aaaa bbbb cccc", 3, markupLength: 1000, wordsPerMinute: 200);
        var scoped = TextMetricsCalculator.Compute("aaaa", 1, markupLength: 1000, wordsPerMinute: 200);

        Assert.Equal(full.MarkupLength, scoped.MarkupLength);
        Assert.True(scoped.TextToHtmlRatio < full.TextToHtmlRatio);
    }

    /// <summary>Unknown markup size yields ratio zero, never a divide-by-zero.</summary>
    [Fact]
    public void Unknown_Markup_Size_Yields_Zero_Ratio()
    {
        var metrics = TextMetricsCalculator.Compute("text", 1, markupLength: 0, wordsPerMinute: 200);

        Assert.Equal(0, metrics.TextToHtmlRatio);
        Assert.Equal(0, metrics.MarkupLength);
    }

    /// <summary>Invalid rates and sizes are rejected.</summary>
    [Fact]
    public void Invalid_Arguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextMetricsCalculator.Compute("t", 1, 10, wordsPerMinute: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextMetricsCalculator.Compute("t", 1, markupLength: -1, wordsPerMinute: 200));
    }
}
