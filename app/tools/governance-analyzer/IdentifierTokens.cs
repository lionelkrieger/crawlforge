using System.Text;
using System.Text.RegularExpressions;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// CamelCase / snake_case / kebab-case aware identifier tokenization and token-sequence
/// matching. "GetRemoteFlagValue" => [get, remote, flag, value]; a pattern matches when
/// its token sequence appears contiguously. This avoids substring false positives
/// (e.g. "industrial" never matches "trial", "Activator" never matches "activation").
/// </summary>
internal static class IdentifierTokens
{
    private static readonly Regex IdentifierRegex = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static List<string> Tokenize(string identifier)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(tokens, current);
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0)
            {
                var prev = identifier[i - 1];
                var nextIsLower = i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                {
                    Flush(tokens, current);
                }
            }
            else if (char.IsDigit(c) && current.Length > 0 && char.IsLetter(identifier[i - 1]))
            {
                Flush(tokens, current);
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush(tokens, current);
        return tokens;
    }

    /// <summary>All identifier tokens appearing in a free-text fragment (argument lists, payloads).</summary>
    public static List<string> TokenizeText(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in IdentifierRegex.Matches(text))
        {
            tokens.AddRange(Tokenize(match.Value));
        }

        return tokens;
    }

    public static bool MatchesAnyPattern(IReadOnlyList<string> tokens, string[][] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (ContainsSequence(tokens, pattern))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsSequence(IReadOnlyList<string> tokens, string[] sequence)
    {
        if (sequence.Length == 0 || tokens.Count < sequence.Length)
        {
            return false;
        }

        for (var i = 0; i <= tokens.Count - sequence.Length; i++)
        {
            var found = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(tokens[i + j], Canonicalize(sequence[j]), StringComparison.Ordinal))
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Morphological normalization so plural identifiers match singular patterns:
    /// "signatures" => "signature", "proxies" => "proxy". Tokens ending in ss/us/is
    /// (address, class, bus, analysis) are left intact.
    /// </summary>
    public static string Canonicalize(string token)
    {
        if (token.Length >= 5 && token.EndsWith("ies", StringComparison.Ordinal))
        {
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");
        }

        if (token.Length >= 4
            && token.EndsWith('s')
            && !token.EndsWith("ss", StringComparison.Ordinal)
            && !token.EndsWith("us", StringComparison.Ordinal)
            && !token.EndsWith("is", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length > 0)
        {
            tokens.Add(Canonicalize(current.ToString()));
            current.Clear();
        }
    }
}
