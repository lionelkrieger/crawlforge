using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;
using WeCantSpell.Hunspell;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Spelling checker options (FS-CRAWL-001 §7.8).
/// </summary>
/// <param name="DictionaryDirectory">Bundled dictionary directory; null resolves the shipped location.</param>
/// <param name="PreferredCheckingLanguage">User's preferred checking language (page-language fallback).</param>
/// <param name="MaxIssues">Bound on issues reported per check.</param>
/// <param name="MaxSuggestions">Bound on suggestions per issue.</param>
public sealed record SpellingCheckerOptions(
    string? DictionaryDirectory = null,
    string PreferredCheckingLanguage = "en-US",
    int MaxIssues = 100,
    int MaxSuggestions = 5);

/// <summary>
/// Dictionary-based spelling checker (FS-CRAWL-001 §7.8) over the pure-managed
/// WeCantSpell.Hunspell reader and BUNDLED per-language dictionaries. The
/// dictionary is chosen from the page's declared language, falling back to the
/// user's preferred checking language; user-dictionary entries suppress
/// findings; issues carry the offending token and its character offset.
/// Proper nouns and code-like tokens are excluded by generic heuristics.
/// Fully offline — dictionaries are never fetched at runtime.
/// </summary>
public sealed partial class HunspellSpellingChecker : ISpellingChecker, IDisposable
{
    private readonly string _dictionaryDirectory;
    private readonly SpellingCheckerOptions _options;
    private readonly ConcurrentDictionary<string, WordList?> _dictionaries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the checker over the bundled dictionary directory.</summary>
    /// <param name="options">Checker options.</param>
    public HunspellSpellingChecker(SpellingCheckerOptions? options = null)
    {
        _options = options ?? new SpellingCheckerOptions();
        _dictionaryDirectory = _options.DictionaryDirectory ?? AssetLocator.ResolveDictionariesDirectory();
    }

    /// <inheritdoc />
    public Task<SpellingCheckResult> CheckAsync(SpellingCheckInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.Run(() => CheckCore(input, cancellationToken), cancellationToken);
    }

    /// <summary>Releases cached dictionary readers.</summary>
    public void Dispose()
    {
        _dictionaries.Clear();
    }

    private SpellingCheckResult CheckCore(SpellingCheckInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Text))
        {
            return new SpellingCheckResult([]);
        }

        var wordList = ResolveDictionary(input.Language);
        if (wordList is null)
        {
            return new SpellingCheckResult([]);
        }

        var accepted = new HashSet<string>(input.AcceptedWords ?? [], StringComparer.OrdinalIgnoreCase);
        var excludedSpans = FindExcludedSpans(input.Text);
        var issues = new List<SpellingIssue>();

        foreach (Match match in TokenRegex().Matches(input.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issues.Count >= _options.MaxIssues)
            {
                break;
            }

            var word = match.Value;
            if (!IsCheckableToken(word, match.Index, input.Text, excludedSpans))
            {
                continue;
            }

            if (accepted.Contains(word))
            {
                continue;
            }

            if (wordList.Check(word))
            {
                continue;
            }

            // Capitalized tokens whose lowercase form is valid are proper-noun
            // or sentence-start variants — generic heuristic exclusion (§7.8).
            var lower = word.ToLowerInvariant();
            if (!string.Equals(lower, word, StringComparison.Ordinal) && wordList.Check(lower))
            {
                continue;
            }

            if (accepted.Contains(lower))
            {
                continue;
            }

            var suggestions = wordList.Suggest(word).Take(_options.MaxSuggestions).ToList();
            issues.Add(new SpellingIssue(word, match.Index, suggestions));
        }

        return new SpellingCheckResult(issues);
    }

    private WordList? ResolveDictionary(string? language)
    {
        var resolved = ResolveLanguageFile(language) ?? ResolveLanguageFile(_options.PreferredCheckingLanguage);
        if (resolved is null)
        {
            return null;
        }

        return _dictionaries.GetOrAdd(resolved.Value.DicPath, static path =>
        {
            var affPath = Path.ChangeExtension(path, ".aff");
            return File.Exists(affPath) ? WordList.CreateFromFiles(path, affPath) : null;
        });
    }

    private (string DicPath, string Language)? ResolveLanguageFile(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || !Directory.Exists(_dictionaryDirectory))
        {
            return null;
        }

        var normalized = language.Trim().Replace('-', '_');
        var exact = Path.Combine(_dictionaryDirectory, normalized + ".dic");
        if (File.Exists(exact))
        {
            return (exact, normalized);
        }

        var underscore = normalized.IndexOf('_');
        var prefix = underscore > 0 ? normalized[..underscore] : normalized;
        var candidate = Directory.EnumerateFiles(_dictionaryDirectory, prefix + "_*.dic")
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is not null)
        {
            return (candidate, Path.GetFileNameWithoutExtension(candidate));
        }

        var bare = Path.Combine(_dictionaryDirectory, prefix + ".dic");
        return File.Exists(bare) ? (bare, prefix) : null;
    }

    private static bool IsCheckableToken(string word, int index, string text, List<(int Start, int End)> excludedSpans)
    {
        if (word.Length < 2)
        {
            return false;
        }

        foreach (var (start, end) in excludedSpans)
        {
            if (index < end && index + word.Length > start)
            {
                return false;
            }
        }

        var hasInternalUpper = false;
        var allUpper = true;
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (char.IsDigit(c) || c == '_')
            {
                return false;
            }

            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    hasInternalUpper = true;
                }
            }
            else
            {
                allUpper = false;
            }
        }

        // CamelCase identifiers and ALL-CAPS acronyms are code-like/proper.
        if (hasInternalUpper || allUpper)
        {
            return false;
        }

        return true;
    }

    private static List<(int Start, int End)> FindExcludedSpans(string text)
    {
        var spans = new List<(int, int)>();
        foreach (Match match in UrlRegex().Matches(text))
        {
            spans.Add((match.Index, match.Index + match.Length));
        }

        foreach (Match match in EmailRegex().Matches(text))
        {
            spans.Add((match.Index, match.Index + match.Length));
        }

        return spans;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+(?:['’\-][\p{L}\p{Nd}_]+)*", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[^\s<>""']+@[^\s<>""']+\.[^\s<>""']+", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex EmailRegex();
}
