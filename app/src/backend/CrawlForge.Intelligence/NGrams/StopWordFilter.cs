using System.Collections.Concurrent;
using System.Data.Common;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// Stop-word filter (FS-CRAWL-002 §5.2): bundled per-language lists shipped as
/// offline locale assets plus local user additions from the
/// <c>user_stopwords</c> table. Language selection follows the §7.8 rule —
/// the page's declared language, falling back to the preferred analysis
/// language. Stop words are removed BEFORE n-gram construction; contiguity is
/// defined over the filtered token stream. Never fetched remotely.
/// </summary>
public sealed class StopWordFilter : IStopWordFilter
{
    private readonly string _stopwordsDirectory;
    private readonly string _preferredLanguage;
    private readonly IDbProvider? _provider;
    private readonly ConcurrentDictionary<string, HashSet<string>> _bundledCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _userCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the filter over the bundled asset directory.</summary>
    /// <param name="provider">Database provider for user_stopwords; null disables user additions.</param>
    /// <param name="stopwordsDirectory">Bundled stop-word directory; null resolves the shipped location.</param>
    /// <param name="preferredLanguage">Fallback language when the page declares none.</param>
    public StopWordFilter(IDbProvider? provider = null, string? stopwordsDirectory = null, string preferredLanguage = "en")
    {
        _provider = provider;
        _stopwordsDirectory = stopwordsDirectory ?? ResolveAssetDirectory();
        _preferredLanguage = preferredLanguage;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RemoveStopWords(IReadOnlyList<string> tokens, string language)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var resolved = ResolveLanguage(language) ?? ResolveLanguage(_preferredLanguage);
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resolved is not null)
        {
            foreach (var word in LoadBundled(resolved))
            {
                stopWords.Add(word);
            }

            foreach (var word in LoadUserWords(resolved))
            {
                stopWords.Add(word);
            }
        }

        var filtered = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!stopWords.Contains(token))
            {
                filtered.Add(token);
            }
        }

        return filtered;
    }

    /// <summary>Clears cached user stop words so later additions apply.</summary>
    public void RefreshUserStopwords() => _userCache.Clear();

    private string? ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || !Directory.Exists(_stopwordsDirectory))
        {
            return null;
        }

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (File.Exists(Path.Combine(_stopwordsDirectory, normalized + ".txt")))
        {
            return normalized;
        }

        var dash = normalized.IndexOf('-');
        var baseTag = dash > 0 ? normalized[..dash] : normalized;
        return File.Exists(Path.Combine(_stopwordsDirectory, baseTag + ".txt")) ? baseTag : null;
    }

    private HashSet<string> LoadBundled(string language) =>
        _bundledCache.GetOrAdd(language, static (lang, dir) =>
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(dir, lang + ".txt");
            if (!File.Exists(path))
            {
                return words;
            }

            foreach (var line in File.ReadLines(path))
            {
                var word = line.Trim();
                if (word.Length > 0 && !word.StartsWith('#'))
                {
                    words.Add(word.ToLowerInvariant());
                }
            }

            return words;
        }, _stopwordsDirectory);

    private HashSet<string> LoadUserWords(string language)
    {
        if (_provider is null)
        {
            return [];
        }

        return _userCache.GetOrAdd(language, static (lang, provider) =>
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baseTag = lang.Contains('-') ? lang[..lang.IndexOf('-')] : lang;
            using var connection = provider.OpenConnectionAsync().GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT `word` FROM `user_stopwords` WHERE `language` = @exact OR `language` = @baseTag";
            AddParameter(command, "@exact", lang);
            AddParameter(command, "@baseTag", baseTag);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                words.Add(reader.GetString(0).ToLowerInvariant());
            }

            return words;
        }, _provider);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string ResolveAssetDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDirectory, "assets", "stopwords");
        if (Directory.Exists(direct))
        {
            return Path.GetFullPath(direct);
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "stopwords");
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            var appCandidate = Path.Combine(directory.FullName, "app", "assets", "stopwords");
            if (Directory.Exists(appCandidate))
            {
                return Path.GetFullPath(appCandidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Bundled asset directory 'assets/stopwords' was not found from '{baseDirectory}'. " +
            "Assets ship with the product; runtime fetching is not permitted.");
    }
}
