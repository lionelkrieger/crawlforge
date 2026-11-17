using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Audit;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// FS-CRAWL-002 §15.3 pixel-facet evidence over the REAL bundled font-metric
/// tables: sum-of-advances widths, budget truncation with ellipsis behavior,
/// profile validation, and unknown-codepoint fallback. No runtime fetching.
/// </summary>
public class SerpPixelMeasurerTests
{
    private readonly SerpPixelMeasurer _measurer = new();

    /// <summary>"Hello" in Arial 20px: H=722 e=556 l=222 l=222 o=556 → 2278/1000*20 = 45.56px.</summary>
    [Fact]
    public void Width_Is_Sum_Of_Advances_Scaled_By_Font_Size()
    {
        var measurement = _measurer.Measure("Hello", new SerpMeasurementOptions("arial-desktop-title", 580));

        Assert.Equal(45.56, measurement.WidthPixels, 2);
        Assert.False(measurement.IsTruncated);
        Assert.Null(measurement.TruncatedText);
    }

    /// <summary>The description profile scales the same advances at 14px.</summary>
    [Fact]
    public void Description_Profile_Uses_Its_Own_Font_Size()
    {
        var measurement = _measurer.Measure("Hello", new SerpMeasurementOptions("arial-desktop-description", 920));

        Assert.Equal(31.892, measurement.WidthPixels, 3);
    }

    /// <summary>Over-budget text truncates to the longest prefix plus ellipsis within budget.</summary>
    [Fact]
    public void Over_Budget_Text_Truncates_With_Ellipsis()
    {
        var longTitle = string.Join(" ", Enumerable.Repeat("WideWord", 40));
        var measurement = _measurer.Measure(longTitle, new SerpMeasurementOptions("arial-desktop-title", 580));

        Assert.True(measurement.IsTruncated);
        Assert.NotNull(measurement.TruncatedText);
        Assert.EndsWith("…", measurement.TruncatedText);
        Assert.True(measurement.TruncatedText!.Length < longTitle.Length);

        var truncatedWidth = _measurer.Measure(measurement.TruncatedText, new SerpMeasurementOptions("arial-desktop-title", 580));
        Assert.True(truncatedWidth.WidthPixels <= 580, "Truncated text plus ellipsis must fit the budget.");
    }

    /// <summary>Exactly-at-budget text does not truncate.</summary>
    [Fact]
    public void At_Budget_Text_Does_Not_Truncate()
    {
        const string text = "Hello";
        var width = _measurer.Measure(text, new SerpMeasurementOptions("arial-desktop-title", 1000)).WidthPixels;

        var measurement = _measurer.Measure(text, new SerpMeasurementOptions("arial-desktop-title", width));

        Assert.False(measurement.IsTruncated);
    }

    /// <summary>Empty text measures zero and never truncates.</summary>
    [Fact]
    public void Empty_Text_Measures_Zero()
    {
        var measurement = _measurer.Measure(string.Empty, new SerpMeasurementOptions("arial-desktop-title", 580));

        Assert.Equal(0, measurement.WidthPixels);
        Assert.False(measurement.IsTruncated);
    }

    /// <summary>Unknown profile keys are rejected (configuration-time validation).</summary>
    [Fact]
    public void Unknown_Profile_Key_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _measurer.Measure("text", new SerpMeasurementOptions("no-such-profile", 580)));
    }

    /// <summary>Codepoints outside the table fall back to the documented default width.</summary>
    [Fact]
    public void Unknown_Codepoint_Uses_Default_Width()
    {
        // CJK ideograph U+4E00 is outside the bundled Latin tables.
        var measurement = _measurer.Measure("一", new SerpMeasurementOptions("arial-desktop-title", 5000));

        Assert.Equal(500 * 20 / 1000d, measurement.WidthPixels, 6);
    }

    /// <summary>All four bundled role profiles are present.</summary>
    [Fact]
    public void Bundled_Profiles_Cover_Desktop_And_Mobile_Roles()
    {
        Assert.Contains("arial-desktop-title", _measurer.ProfileKeys);
        Assert.Contains("arial-desktop-description", _measurer.ProfileKeys);
        Assert.Contains("roboto-mobile-title", _measurer.ProfileKeys);
        Assert.Contains("roboto-mobile-description", _measurer.ProfileKeys);
    }

    /// <summary>Roboto mobile widths differ from Arial desktop (real per-family metrics).</summary>
    [Fact]
    public void Mobile_Profile_Measures_With_Robot_Advances()
    {
        // Roboto space=248, A=652 at 18px: "A A" = (652+248+652)/1000*18 = 27.936
        var measurement = _measurer.Measure("A A", new SerpMeasurementOptions("roboto-mobile-title", 920));

        Assert.Equal(27.936, measurement.WidthPixels, 3);
    }
}
