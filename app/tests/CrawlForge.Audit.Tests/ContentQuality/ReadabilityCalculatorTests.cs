using CrawlForge.Audit.ContentQuality;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// FS-CRAWL-001 §7.12 readability evidence: Flesch reading ease and
/// Flesch–Kincaid grade level over sentence/word/syllable counts, with
/// hand-computed fixture values.
/// </summary>
public class ReadabilityCalculatorTests
{
    private readonly ReadabilityCalculator _calculator = new();

    /// <summary>Empty and whitespace text score zero with zero counters.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \t\n ")]
    public void Empty_Text_Scores_Zero(string text)
    {
        var score = _calculator.Calculate(text);

        Assert.Equal(0, score.FleschReadingEase);
        Assert.Equal(0, score.FleschKincaidGradeLevel);
        Assert.Equal(0, score.WordCount);
        Assert.Equal(0, score.SentenceCount);
        Assert.Equal(0, score.SyllableCount);
    }

    /// <summary>Hand-computed two-sentence fixture: 6 words, 2 sentences, 6 syllables.</summary>
    [Fact]
    public void Flesch_Formulas_Match_Hand_Computed_Values()
    {
        var score = _calculator.Calculate("The cat sat. The dog ran.");

        Assert.Equal(6, score.WordCount);
        Assert.Equal(2, score.SentenceCount);
        Assert.Equal(6, score.SyllableCount);
        // 206.835 - 1.015*(6/2) - 84.6*(6/6) = 119.19
        Assert.Equal(119.19, score.FleschReadingEase, 3);
        // 0.39*(6/2) + 11.8*(6/6) - 15.59 = -2.62
        Assert.Equal(-2.62, score.FleschKincaidGradeLevel, 3);
    }

    /// <summary>Text without terminal punctuation counts as one sentence.</summary>
    [Fact]
    public void Unterminated_Text_Is_One_Sentence()
    {
        var score = _calculator.Calculate("Hello world");

        Assert.Equal(1, score.SentenceCount);
        Assert.Equal(2, score.WordCount);
    }

    /// <summary>Punctuation runs delimit sentences without double counting.</summary>
    [Fact]
    public void Punctuation_Runs_Count_Once()
    {
        var score = _calculator.Calculate("Wait... what?! Really.");

        Assert.Equal(3, score.SentenceCount);
    }

    /// <summary>Vowel-group syllable heuristic with silent-e handling.</summary>
    [Theory]
    [InlineData("beautiful", 3)]
    [InlineData("music", 2)]
    [InlineData("cat", 1)]
    [InlineData("the", 1)]
    [InlineData("table", 2)]
    [InlineData("rhythm", 1)]
    public void Syllable_Heuristic_Counts(string word, int expected)
    {
        var score = _calculator.Calculate(word);

        Assert.Equal(expected, score.SyllableCount);
    }

    /// <summary>Longer sentences and polysyllabic words lower reading ease.</summary>
    [Fact]
    public void Complex_Text_Scores_Lower_Than_Simple_Text()
    {
        var simple = _calculator.Calculate("The cat sat. The dog ran.");
        var complex = _calculator.Calculate(
            "The organization systematically evaluated comprehensive infrastructure alternatives. " +
            "Subsequently, administrators implemented unprecedented configuration modifications.");

        Assert.True(complex.FleschReadingEase < simple.FleschReadingEase);
        Assert.True(complex.FleschKincaidGradeLevel > simple.FleschKincaidGradeLevel);
    }

    /// <summary>Hyphenated and apostrophe tokens count as single words.</summary>
    [Fact]
    public void Hyphenated_And_Contracted_Tokens_Are_Single_Words()
    {
        var score = _calculator.Calculate("It's a well-known fact.");

        Assert.Equal(4, score.WordCount);
    }
}
