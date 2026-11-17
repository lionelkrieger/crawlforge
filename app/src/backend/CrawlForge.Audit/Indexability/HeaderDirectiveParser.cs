namespace CrawlForge.Audit.Indexability;

/// <summary>
/// One parsed X-Robots-Tag directive scope (FS-CRAWL-002 §15.4): an agent
/// ("*" for the generic scope) and its directive list, with the raw header
/// value retained as evidence.
/// </summary>
/// <param name="Agent">User-agent scope; "*" when unscoped.</param>
/// <param name="Directives">Lowercase directive tokens in declaration order.</param>
/// <param name="RawValue">The raw header value the scope was parsed from.</param>
public sealed record RobotsHeaderDirective(
    string Agent,
    IReadOnlyList<string> Directives,
    string RawValue);

/// <summary>
/// One parsed Link header entry (RFC 8288 subset used by §15.4): relation,
/// target URI, optional hreflang, and the raw entry as evidence.
/// </summary>
/// <param name="Relation">Lowercase relation token (canonical, alternate, ...).</param>
/// <param name="Target">Target URI reference as declared.</param>
/// <param name="Hreflang">hreflang parameter value, when present.</param>
/// <param name="RawValue">The raw Link entry the record was parsed from.</param>
public sealed record LinkHeaderEntry(
    string Relation,
    string Target,
    string? Hreflang,
    string RawValue);

/// <summary>
/// All directives parsed from one response's headers: the persisted shape of
/// the <c>header_directives</c> element group (FS-CRAWL-002 §15.5).
/// </summary>
/// <param name="Robots">Parsed X-Robots-Tag scopes.</param>
/// <param name="Links">Parsed Link header entries.</param>
public sealed record ParsedHeaderDirectives(
    IReadOnlyList<RobotsHeaderDirective> Robots,
    IReadOnlyList<LinkHeaderEntry> Links);

/// <summary>
/// Parses HTTP response header directives for the header/HTML equivalency
/// pass (FS-CRAWL-002 §15.4). Pure, offline, culture-invariant: the same
/// stored headers always produce the same parse on re-audit.
/// </summary>
public static class HeaderDirectiveParser
{
    /// <summary>Generic (unscoped) robots agent token.</summary>
    public const string GenericAgent = "*";

    private static readonly HashSet<string> DirectiveVocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "noindex", "nofollow", "none", "all", "noarchive", "nosnippet",
        "noodp", "noydir", "notranslate", "noimageindex", "unavailable_after",
        "indexifembedded", "max-snippet", "max-image-preview", "max-video-preview",
    };

    /// <summary>
    /// Parses X-Robots-Tag and Link headers from a response header dictionary.
    /// Missing headers yield empty lists; malformed entries are skipped
    /// defensively (tolerant parse, never throws on hostile input).
    /// </summary>
    /// <param name="headers">Response headers (case-insensitive keys expected).</param>
    /// <returns>The parsed directive set.</returns>
    public static ParsedHeaderDirectives Parse(IReadOnlyDictionary<string, string>? headers)
    {
        var robots = new List<RobotsHeaderDirective>();
        var links = new List<LinkHeaderEntry>();
        if (headers is null)
        {
            return new ParsedHeaderDirectives(robots, links);
        }

        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.Equals(header.Key, "X-Robots-Tag", StringComparison.OrdinalIgnoreCase))
            {
                robots.AddRange(ParseRobotsTag(header.Value));
            }
            else if (string.Equals(header.Key, "Link", StringComparison.OrdinalIgnoreCase))
            {
                links.AddRange(ParseLinkHeader(header.Value));
            }
        }

        return new ParsedHeaderDirectives(robots, links);
    }

    /// <summary>Parses one X-Robots-Tag value into agent scopes.</summary>
    /// <param name="value">Raw header value (may carry an agent prefix).</param>
    /// <returns>The parsed scopes; empty when no directives are present.</returns>
    public static IReadOnlyList<RobotsHeaderDirective> ParseRobotsTag(string value)
    {
        var result = new List<RobotsHeaderDirective>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        string raw = value.Trim();
        string agent = GenericAgent;
        string directivePart = raw;

        int colon = raw.IndexOf(':');
        if (colon > 0)
        {
            string prefix = raw.Substring(0, colon).Trim();
            // A known directive name before the colon (unavailable_after:,
            // max-snippet:) is not an agent scope.
            if (!DirectiveVocabulary.Contains(prefix) && IsAgentToken(prefix))
            {
                agent = prefix.ToLowerInvariant();
                directivePart = raw.Substring(colon + 1);
            }
        }

        List<string> directives = SplitDirectives(directivePart);
        if (directives.Count > 0)
        {
            result.Add(new RobotsHeaderDirective(agent, directives, raw));
        }

        return result;
    }

    /// <summary>Parses one Link header value into entries.</summary>
    /// <param name="value">Raw Link header value (comma-separated entries).</param>
    /// <returns>The parsed entries; malformed entries are skipped.</returns>
    public static IReadOnlyList<LinkHeaderEntry> ParseLinkHeader(string value)
    {
        var result = new List<LinkHeaderEntry>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (string entry in SplitOutsideDelimiters(value, ','))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            List<string> segments = SplitOutsideDelimiters(trimmed, ';');
            string target = segments[0].Trim();
            if (target.StartsWith('<') && target.EndsWith('>'))
            {
                target = target.Substring(1, target.Length - 2).Trim();
            }

            if (target.Length == 0)
            {
                continue;
            }

            string? relation = null;
            string? hreflang = null;
            for (int i = 1; i < segments.Count; i++)
            {
                string segment = segments[i].Trim();
                int equals = segment.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = segment.Substring(0, equals).Trim().ToLowerInvariant();
                string parameterValue = Unquote(segment.Substring(equals + 1).Trim());
                if (key == "rel")
                {
                    relation = parameterValue.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault()?.ToLowerInvariant();
                }
                else if (key == "hreflang")
                {
                    hreflang = parameterValue;
                }
            }

            if (relation is not null)
            {
                result.Add(new LinkHeaderEntry(relation, target, hreflang, trimmed));
            }
        }

        return result;
    }

    private static List<string> SplitDirectives(string value)
    {
        var directives = new List<string>();
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Directive parameters (unavailable_after: date, max-snippet: n)
            // keep their value attached to the directive token.
            string normalized = token.ToLowerInvariant();
            if (normalized.Length > 0)
            {
                directives.Add(normalized);
            }
        }

        return directives;
    }

    private static bool IsAgentToken(string token)
    {
        if (token.Length == 0 || token.Length > 64)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static List<string> SplitOutsideDelimiters(string value, char delimiter)
    {
        var parts = new List<string>();
        bool inAngle = false;
        bool inQuote = false;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"' && !inAngle)
            {
                inQuote = !inQuote;
            }
            else if (c == '<' && !inQuote)
            {
                inAngle = true;
            }
            else if (c == '>' && !inQuote)
            {
                inAngle = false;
            }
            else if (c == delimiter && !inAngle && !inQuote)
            {
                parts.Add(value.Substring(start, i - start));
                start = i + 1;
            }
        }

        parts.Add(value.Substring(start));
        return parts;
    }
}