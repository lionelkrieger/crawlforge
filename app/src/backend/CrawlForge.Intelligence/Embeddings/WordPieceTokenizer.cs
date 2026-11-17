using System.Globalization;
using System.Text;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Self-contained WordPiece tokenizer for the bundled BERT-family ONNX model
/// (bert-base-uncased vocabulary). Deterministic and fully offline: the
/// vocabulary is read once from the bundled vocab.txt asset; no tokenizer
/// package, no network, no runtime downloads. Implements lower-casing, accent
/// stripping, punctuation splitting, and greedy longest-match WordPiece with
/// [CLS]/[SEP] framing and [UNK] fallback.
/// </summary>
public sealed class WordPieceTokenizer
{
    private const int MaxWordChars = 100;

    private readonly Dictionary<string, int> _vocab;

    /// <summary>Padding token id.</summary>
    public int PadId { get; }

    /// <summary>Unknown token id.</summary>
    public int UnkId { get; }

    /// <summary>Sequence-start token id.</summary>
    public int ClsId { get; }

    /// <summary>Sequence-end token id.</summary>
    public int SepId { get; }

    /// <summary>Loads the vocabulary from a bundled vocab.txt asset.</summary>
    /// <param name="vocabPath">Path to vocab.txt (one token per line; line index = id).</param>
    /// <exception cref="EmbeddingConfigurationException">The vocabulary is missing or lacks special tokens.</exception>
    public WordPieceTokenizer(string vocabPath)
    {
        if (!File.Exists(vocabPath))
        {
            throw new EmbeddingConfigurationException($"Bundled tokenizer vocabulary not found at '{vocabPath}'. The model asset must be installed with the product; runtime downloads are forbidden.");
        }

        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(vocabPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var token = lines[i].TrimEnd('\r');
            if (!_vocab.ContainsKey(token))
            {
                _vocab[token] = i;
            }
        }

        PadId = RequireToken("[PAD]");
        UnkId = RequireToken("[UNK]");
        ClsId = RequireToken("[CLS]");
        SepId = RequireToken("[SEP]");
    }

    /// <summary>Vocabulary size.</summary>
    public int VocabSize => _vocab.Count;

    /// <summary>
    /// Tokenizes text into model inputs: [CLS] pieces [SEP], truncated to
    /// <paramref name="maxLength"/> total tokens, with attention mask and
    /// all-zero segment ids.
    /// </summary>
    /// <param name="text">Input text.</param>
    /// <param name="maxLength">Maximum total tokens including special tokens (minimum 3).</param>
    /// <returns>Model input arrays.</returns>
    public TokenizedInput Encode(string? text, int maxLength = 256)
    {
        if (maxLength < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must allow [CLS] + at least one piece + [SEP].");
        }

        var pieces = new List<int> { ClsId };
        foreach (var word in BasicTokenize(text ?? string.Empty))
        {
            foreach (var id in WordPiece(word))
            {
                if (pieces.Count >= maxLength - 1)
                {
                    break;
                }

                pieces.Add(id);
            }

            if (pieces.Count >= maxLength - 1)
            {
                break;
            }
        }

        pieces.Add(SepId);

        var length = pieces.Count;
        var inputIds = new long[length];
        var attentionMask = new long[length];
        var tokenTypeIds = new long[length];
        for (var i = 0; i < length; i++)
        {
            inputIds[i] = pieces[i];
            attentionMask[i] = 1;
        }

        return new TokenizedInput(inputIds, attentionMask, tokenTypeIds);
    }

    private int RequireToken(string token) =>
        _vocab.TryGetValue(token, out var id)
            ? id
            : throw new EmbeddingConfigurationException($"Bundled vocabulary is missing the required special token '{token}'.");

    private static IEnumerable<string> BasicTokenize(string text)
    {
        // bert-base-uncased basic tokenizer: control removal, NFD accent
        // stripping, lower-casing, whitespace + punctuation splitting.
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        var current = new StringBuilder();
        foreach (var ch in builder.ToString())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else if (IsPunctuation(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return ch.ToString();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool IsPunctuation(char ch)
    {
        // ASCII punctuation plus Unicode punctuation categories (BERT convention).
        if (ch is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private IEnumerable<int> WordPiece(string word)
    {
        if (word.Length > MaxWordChars)
        {
            yield return UnkId;
            yield break;
        }

        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            string? matched = null;
            while (start < end)
            {
                var candidate = (start > 0 ? "##" : string.Empty) + word[start..end];
                if (_vocab.ContainsKey(candidate))
                {
                    matched = candidate;
                    break;
                }

                end--;
            }

            if (matched is null)
            {
                yield return UnkId;
                yield break;
            }

            yield return _vocab[matched];
            start = end;
        }
    }
}

/// <summary>
/// Tokenized model inputs (int64 tensors, batch size 1).
/// </summary>
/// <param name="InputIds">Token ids including [CLS]/[SEP].</param>
/// <param name="AttentionMask">1 for real tokens, 0 for padding.</param>
/// <param name="TokenTypeIds">Segment ids (all zero for single-sequence input).</param>
public sealed record TokenizedInput(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);
