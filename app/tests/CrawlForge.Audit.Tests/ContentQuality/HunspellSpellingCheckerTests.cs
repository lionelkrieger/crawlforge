using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Audit;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// FS-CRAWL-001 §7.8 spelling evidence over the REAL bundled en_US Hunspell
/// dictionary (offline, no runtime fetch): token-level evidence with offsets,
/// page-language honoring with fallback, user-dictionary suppression, and
/// proper-noun/code-like heuristics.
/// </summary>
public class HunspellSpellingCheckerTests : IDisposable
{
    private readonly HunspellSpellingChecker _checker = new();

    /// <inheritdoc />
    public void Dispose() => _checker.Dispose();

    /// <summary>Correct English text produces no issues.</summary>
    [Fact]
    public async Task Correct_Text_Has_No_Issues()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "The quick brown fox jumps over the lazy dog.", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>Misspellings carry the offending token and its character offset.</summary>
    [Fact]
    public async Task Misspellings_Carry_Token_And_Offset()
    {
        const string text = "Ths qck brwn fx";
        var result = await _checker.CheckAsync(new SpellingCheckInput(text, "en-US", []));

        Assert.Equal(4, result.Issues.Count);
        Assert.Equal("Ths", result.Issues[0].Word);
        Assert.Equal(0, result.Issues[0].Offset);
        Assert.Equal("qck", result.Issues[1].Word);
        Assert.Equal(4, result.Issues[1].Offset);
        Assert.Equal("brwn", result.Issues[2].Word);
        Assert.Equal(8, result.Issues[2].Offset);
        Assert.Equal("fx", result.Issues[3].Word);
        Assert.Equal(13, result.Issues[3].Offset);
        Assert.Contains(result.Issues, i => i.Suggestions.Count > 0);
    }

    /// <summary>User-dictionary entries suppress findings (§7.8).</summary>
    [Fact]
    public async Task User_Dictionary_Suppresses_Findings()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "Ths qck brwn fx", "en-US", ["Ths", "qck", "brwn", "fx"]));

        Assert.Empty(result.Issues);
    }

    /// <summary>CamelCase identifiers are excluded by the code-like heuristic.</summary>
    [Fact]
    public async Task CamelCase_Tokens_Are_Skipped()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "MyVariableName is valid", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>ALL-CAPS acronyms are excluded.</summary>
    [Fact]
    public async Task Acronyms_Are_Skipped()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "NASA launched it", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>Tokens with digits or underscores are code-like and excluded.</summary>
    [Fact]
    public async Task Code_Like_Tokens_Are_Skipped()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "var_name1 and foo2bar are valid", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>URL and email spans are excluded from checking.</summary>
    [Fact]
    public async Task Urls_And_Emails_Are_Skipped()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "Visit https://example.com or mail test@example.com today", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>Regional page language resolves to the bundled base dictionary.</summary>
    [Fact]
    public async Task Regional_Language_Resolves_To_Bundled_Dictionary()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "The color is red", "en-GB", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>Unknown page language falls back to the preferred checking language.</summary>
    [Fact]
    public async Task Unknown_Language_Falls_Back_To_Preferred()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput(
            "Ths is wrong", "xx-YY", []));

        Assert.Contains(result.Issues, i => i.Word == "Ths");
    }

    /// <summary>Issue count is bounded by the configured maximum.</summary>
    [Fact]
    public async Task Issue_Count_Is_Bounded()
    {
        using var bounded = new HunspellSpellingChecker(new SpellingCheckerOptions(MaxIssues: 2));
        var result = await bounded.CheckAsync(new SpellingCheckInput(
            "zzzqqq xxxvvv wwwuuu vvvttt", "en-US", []));

        Assert.Equal(2, result.Issues.Count);
    }

    /// <summary>Empty text yields no issues.</summary>
    [Fact]
    public async Task Empty_Text_Yields_No_Issues()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput("   ", "en-US", []));

        Assert.Empty(result.Issues);
    }

    /// <summary>Suggestions are bounded per issue.</summary>
    [Fact]
    public async Task Suggestions_Are_Bounded()
    {
        var result = await _checker.CheckAsync(new SpellingCheckInput("speling", "en-US", []));

        var issue = Assert.Single(result.Issues);
        Assert.True(issue.Suggestions.Count <= 5);
        Assert.Contains("spelling", issue.Suggestions);
    }
}
