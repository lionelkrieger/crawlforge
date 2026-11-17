using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Self-contained WordPiece tokenizer over the bundled bert-base-uncased
/// vocabulary: framing, subwords, unknown fallback, truncation, case/accent
/// normalization — deterministic and offline.
/// </summary>
public class WordPieceTokenizerTests
{
    private static WordPieceTokenizer CreateTokenizer() =>
        new(Path.Combine(IntelligenceSqliteStackFixture.FindAssetDirectory(), "vocab.txt"));

    [Fact]
    public void KnownWordsTokenizeWithClsSepFraming()
    {
        var tokenizer = CreateTokenizer();
        var encoded = tokenizer.Encode("hello world");

        Assert.Equal(101, encoded.InputIds[0]);            // [CLS]
        Assert.Equal(102, encoded.InputIds[^1]);           // [SEP]
        Assert.Equal(7592, encoded.InputIds[1]);           // hello
        Assert.Equal(2088, encoded.InputIds[2]);           // world
        Assert.Equal(4, encoded.InputIds.Length);
        Assert.All(encoded.AttentionMask, m => Assert.Equal(1, m));
        Assert.All(encoded.TokenTypeIds, t => Assert.Equal(0, t));
    }

    [Fact]
    public void CaseAndAccentsAreNormalized()
    {
        var tokenizer = CreateTokenizer();
        var lower = tokenizer.Encode("hello");
        var upper = tokenizer.Encode("HELLO");
        var accented = tokenizer.Encode("café");
        var plain = tokenizer.Encode("cafe");

        Assert.Equal(lower.InputIds, upper.InputIds);
        Assert.Equal(plain.InputIds, accented.InputIds);
    }

    [Fact]
    public void LongWordsSplitIntoWordPieces()
    {
        var tokenizer = CreateTokenizer();
        // Not a vocabulary word: greedy longest-match must emit several pieces.
        var encoded = tokenizer.Encode("aaaaabbbbbccccc");

        Assert.True(encoded.InputIds.Length >= 4);
        Assert.Equal(101, encoded.InputIds[0]);
        Assert.Equal(102, encoded.InputIds[^1]);
    }

    [Fact]
    public void UnknownTokensFallBackToUnk()
    {
        var tokenizer = CreateTokenizer();
        // Words beyond the 100-character WordPiece limit map to [UNK].
        var encoded = tokenizer.Encode(new string('q', 150));
        Assert.Contains(100, encoded.InputIds); // [UNK]
    }

    [Fact]
    public void TruncationRespectsMaxLength()
    {
        var tokenizer = CreateTokenizer();
        var longText = string.Join(' ', Enumerable.Repeat("word", 500));
        var encoded = tokenizer.Encode(longText, maxLength: 16);

        Assert.Equal(16, encoded.InputIds.Length);
        Assert.Equal(101, encoded.InputIds[0]);
        Assert.Equal(102, encoded.InputIds[^1]);
    }

    [Fact]
    public void PunctuationSplitsIntoSeparateTokens()
    {
        var tokenizer = CreateTokenizer();
        var encoded = tokenizer.Encode("hi,there");
        // [CLS] hi , there [SEP]
        Assert.Equal(5, encoded.InputIds.Length);
    }

    [Fact]
    public void EmptyInputYieldsOnlyFraming()
    {
        var tokenizer = CreateTokenizer();
        var encoded = tokenizer.Encode("");
        Assert.Equal(new long[] { 101, 102 }, encoded.InputIds);
    }

    [Fact]
    public void MissingVocabularyRaisesConfigurationError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cf-missing-vocab-" + Guid.NewGuid().ToString("N"), "vocab.txt");
        Assert.Throws<EmbeddingConfigurationException>(() => new WordPieceTokenizer(missing));
    }

    [Fact]
    public void VocabSizeMatchesBundledAsset()
    {
        var tokenizer = CreateTokenizer();
        Assert.Equal(30522, tokenizer.VocabSize);
        Assert.Equal(0, tokenizer.PadId);
        Assert.Equal(100, tokenizer.UnkId);
    }
}
