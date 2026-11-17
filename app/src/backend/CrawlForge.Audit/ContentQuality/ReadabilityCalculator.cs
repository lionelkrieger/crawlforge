using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Flesch-class readability calculator (FS-CRAWL-001 §7.12): reading ease and
/// Flesch–Kincaid grade level over sentence, word, and syllable counts of
/// visible text. Deterministic, offline, and scope-agnostic — the caller
/// supplies the (possibly scoped) visible text.
/// </summary>
public sealed partial class ReadabilityCalculator : IReadabilityCalculator
{
    /// <inheritdoc />
    public ReadabilityScore Calculate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = WordRegex().Matches(text);
        var wordCount = words.Count;
        if (wordCount == 0)
        {
            return new ReadabilityScore(0, 0, 0, 0, 0);
        }

        var sentenceCount = CountSentences(text);
        var syllableCount = 0;
        foreach (Match word in words)
        {
            syllableCount += CountSyllables(word.Value);
        }

        var wordsPerSentence = (double)wordCount / sentenceCount;
        var syllablesPerWord = (double)syllableCount / wordCount;

        var readingEase = 206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord);
        var gradeLevel = (0.39 * wordsPerSentence) + (11.8 * syllablesPerWord) - 15.59;

        return new ReadabilityScore(readingEase, gradeLevel, wordCount, sentenceCount, syllableCount);
    }

    /// <summary>Counts sentences: terminal punctuation runs delimit sentences;
    /// non-empty text without terminators counts as one sentence.</summary>
    private static int CountSentences(string text)
    {
        var count = 0;
        var inSentence = false;
        foreach (var c in text)
        {
            if (c is '.' or '!' or '?' or '…')
            {
                if (inSentence)
                {
                    count++;
                    inSentence = false;
                }
            }
            else if (!char.IsWhiteSpace(c))
            {
                inSentence = true;
            }
        }

        if (inSentence)
        {
            count++;
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// Syllable heuristic: vowel groups, silent trailing 'e' removed unless the
    /// word ends in consonant+'le'; every word counts at least one syllable.
    /// </summary>
    private static int CountSyllables(string word)
    {
        var lower = word.ToLowerInvariant();
        var count = 0;
        var previousVowel = false;
        foreach (var c in lower)
        {
            var isVowel = c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
            if (isVowel && !previousVowel)
            {
                count++;
            }

            previousVowel = isVowel;
        }

        if (lower.Length > 2 && lower.EndsWith('e') && !lower.EndsWith("le")
            && !IsVowel(lower[^2]))
        {
            count--;
        }

        return Math.Max(1, count);
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex WordRegex();
}
