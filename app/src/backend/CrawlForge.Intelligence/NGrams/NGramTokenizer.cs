using System.Text.RegularExpressions;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// Unicode-aware tokenizer (FS-CRAWL-002 §5.2): letter/digit sequences with
/// locale-aware apostrophe and hyphen handling, case folding, configurable
/// minimum token length, and optional numeric-token exclusion. Sentence
/// segmentation bounds n-gram construction — n-grams never cross sentence or
/// zone boundaries (§5.2 step 3).
/// </summary>
public static partial class NGramTokenizer
{
    /// <summary>
    /// Tokenizes text into sentences of case-folded tokens. Apostrophes and
    /// hyphens join word parts ("don't", "well-known"); terminal punctuation
    /// (. ! ? …) ends a sentence.
    /// </summary>
    /// <param name="text">Zone text.</param>
    /// <param name="minTokenLength">Minimum token length (default 2).</param>
    /// <param name="excludeNumericTokens">Whether purely numeric tokens are dropped.</param>
    /// <returns>Sentences in document order, each a token list.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> TokenizeSentences(
        string text, int minTokenLength = 2, bool excludeNumericTokens = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(minTokenLength, 1);

        var sentences = new List<IReadOnlyList<string>>();
        // Sentence boundaries: split the raw text on terminal punctuation and
        // tokenize each segment so n-grams never cross sentences.
        foreach (var segment in SentenceSplitRegex().Split(text))
        {
            var tokens = new List<string>();
            foreach (Match match in TokenRegex().Matches(segment))
            {
                var token = match.Value.ToLowerInvariant();
                if (token.Length < minTokenLength || (excludeNumericTokens && IsNumeric(token)))
                {
                    continue;
                }

                tokens.Add(token);
            }

            if (tokens.Count > 0)
            {
                sentences.Add(tokens);
            }
        }

        return sentences;
    }

    /// <summary>
    /// Builds contiguous n-grams of the given orders within each sentence,
    /// keyed by their normalized space-joined form (§5.2 step 5).
    /// </summary>
    /// <param name="sentences">Tokenized sentences.</param>
    /// <param name="orders">N-gram orders (clamped to ≥1 by the caller).</param>
    /// <returns>N-gram term counts per order.</returns>
    public static IReadOnlyDictionary<int, Dictionary<string, long>> BuildNgrams(
        IReadOnlyList<IReadOnlyList<string>> sentences, IReadOnlyList<int> orders)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        ArgumentNullException.ThrowIfNull(orders);

        var result = new Dictionary<int, Dictionary<string, long>>();
        foreach (var order in orders.Distinct())
        {
            if (order < 1)
            {
                continue;
            }

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var sentence in sentences)
            {
                for (var i = 0; i + order <= sentence.Count; i++)
                {
                    var term = string.Join(' ', sentence.Skip(i).Take(order));
                    counts[term] = counts.TryGetValue(term, out var existing) ? existing + 1 : 1;
                }
            }

            result[order] = counts;
        }

        return result;
    }

    private static bool IsNumeric(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return token.Length > 0;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[.!?…]+", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex SentenceSplitRegex();
}
