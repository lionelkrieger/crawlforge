using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>Tokenizer tests (FS-CRAWL-002 §5.2): Unicode-aware tokenization,
/// apostrophe/hyphen joining, case folding, bounds, sentence segmentation.</summary>
public sealed class NGramTokenizerTests
{
    /// <summary>Tokens are case-folded letter/digit sequences.</summary>
    [Fact]
    public void Tokenize_CaseFolds_Letter_Sequences()
    {
        var sentences = NGramTokenizer.TokenizeSentences("Hello WORLD foo");

        var sentence = Assert.Single(sentences);
        Assert.Equal(["hello", "world", "foo"], sentence);
    }

    /// <summary>Apostrophes and hyphens join word parts.</summary>
    [Fact]
    public void Tokenize_Joins_Apostrophes_And_Hyphens()
    {
        var sentences = NGramTokenizer.TokenizeSentences("don't well-known state-of-the-art");

        var sentence = Assert.Single(sentences);
        Assert.Equal(["don't", "well-known", "state-of-the-art"], sentence);
    }

    /// <summary>Minimum token length drops short tokens (default 2).</summary>
    [Fact]
    public void Tokenize_Applies_Minimum_Token_Length()
    {
        var sentences = NGramTokenizer.TokenizeSentences("a bb ccc", minTokenLength: 2);

        var sentence = Assert.Single(sentences);
        Assert.Equal(["bb", "ccc"], sentence);
    }

    /// <summary>Numeric-token exclusion drops purely numeric tokens only.</summary>
    [Fact]
    public void Tokenize_Excludes_Purely_Numeric_Tokens_When_Configured()
    {
        var sentences = NGramTokenizer.TokenizeSentences("abc 123 def456", excludeNumericTokens: true);

        var sentence = Assert.Single(sentences);
        Assert.Equal(["abc", "def456"], sentence);
    }

    /// <summary>N-grams never cross sentence boundaries (§5.2 step 3).</summary>
    [Fact]
    public void Ngrams_Do_Not_Cross_Sentence_Boundaries()
    {
        var sentences = NGramTokenizer.TokenizeSentences("one two. three four");

        Assert.Equal(2, sentences.Count);
        var bigrams = NGramTokenizer.BuildNgrams(sentences, [2])[2];
        Assert.True(bigrams.ContainsKey("one two"));
        Assert.True(bigrams.ContainsKey("three four"));
        Assert.False(bigrams.ContainsKey("two three"));
    }

    /// <summary>Contiguous n-gram counts per order are exact.</summary>
    [Fact]
    public void BuildNgrams_Counts_Contiguous_Terms()
    {
        var sentences = NGramTokenizer.TokenizeSentences("the cat sat the cat");

        var ngrams = NGramTokenizer.BuildNgrams(sentences, [1, 2]);
        Assert.Equal(2, ngrams[1]["the"]);
        Assert.Equal(2, ngrams[1]["cat"]);
        Assert.Equal(1, ngrams[1]["sat"]);
        Assert.Equal(2, ngrams[2]["the cat"]);
        Assert.Equal(1, ngrams[2]["cat sat"]);
        Assert.Equal(1, ngrams[2]["sat the"]);
    }

    /// <summary>Unicode letters tokenize (locale-aware, not ASCII-only).</summary>
    [Fact]
    public void Tokenize_Handles_Unicode_Letters()
    {
        var sentences = NGramTokenizer.TokenizeSentences("café naïve 日本語");

        var sentence = Assert.Single(sentences);
        Assert.Equal(["café", "naïve", "日本語"], sentence);
    }

    /// <summary>Empty text yields no sentences.</summary>
    [Fact]
    public void Tokenize_Empty_Text_Yields_No_Sentences()
    {
        Assert.Empty(NGramTokenizer.TokenizeSentences(string.Empty));
        Assert.Empty(NGramTokenizer.TokenizeSentences("...!!!"));
    }

    /// <summary>Ellipsis and question marks also end sentences.</summary>
    [Fact]
    public void Tokenize_Splits_On_All_Terminal_Punctuation()
    {
        var sentences = NGramTokenizer.TokenizeSentences("alpha beta? gamma delta… epsilon zeta");

        Assert.Equal(3, sentences.Count);
    }
}
