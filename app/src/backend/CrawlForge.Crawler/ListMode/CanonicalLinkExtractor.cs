using System.Text;
using System.Text.RegularExpressions;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Extracts the declared canonical target of a fetched page for the migration
/// matrix validator (FS-CRAWL-002 §10.3): the HTTP <c>Link</c> header with
/// <c>rel="canonical"</c> takes precedence over the HTML
/// <c>&lt;link rel="canonical" href="..."&gt;</c> element. Scanning is bounded
/// and purely local; relative canonicals resolve against the final URL.
/// </summary>
public static partial class CanonicalLinkExtractor
{
    /// <summary>Maximum body bytes scanned for the canonical element.</summary>
    public const int MaxBodyScanBytes = 64 * 1024;

    /// <summary>
    /// Extracts the canonical URL from response headers and body. The Link
    /// header wins when present; otherwise the first canonical link element in
    /// the bounded body prefix is used. Returns null when nothing is declared.
    /// </summary>
    /// <param name="finalUrl">Final URL of the page (base for relative canonicals).</param>
    /// <param name="headers">Final response headers (multi-values joined).</param>
    /// <param name="body">Response body bytes.</param>
    /// <returns>The declared canonical URL, or null.</returns>
    public static string? Extract(
        string finalUrl,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fromHeader = ExtractFromLinkHeader(value);
            if (fromHeader is not null)
            {
                return Resolve(finalUrl, fromHeader);
            }
        }

        if (body.Length == 0)
        {
            return null;
        }

        var scanLength = Math.Min(body.Length, MaxBodyScanBytes);
        var text = Encoding.UTF8.GetString(body.Span[..scanLength]);
        foreach (Match tag in LinkTagRegex().Matches(text))
        {
            var attributes = tag.Value;
            var relations = ReadAttribute(attributes, "rel");
            if (relations is null)
            {
                continue;
            }

            var isCanonical = relations
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(relation => string.Equals(relation, "canonical", StringComparison.OrdinalIgnoreCase));
            if (!isCanonical)
            {
                continue;
            }

            var href = ReadAttribute(attributes, "href");
            if (!string.IsNullOrEmpty(href))
            {
                return Resolve(finalUrl, href);
            }
        }

        return null;
    }

    private static string? ReadAttribute(string tag, string attributeName)
    {
        var pattern = attributeName switch
        {
            "rel" => RelAttributeRegex(),
            "href" => HrefAttributeRegex(),
            _ => throw new ArgumentOutOfRangeException(nameof(attributeName)),
        };

        var match = pattern.Match(tag);
        if (!match.Success)
        {
            return null;
        }

        // Exactly one of the quoted/bare capture groups participates.
        foreach (var groupName in pattern.GetGroupNames())
        {
            if (int.TryParse(groupName, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var group = match.Groups[groupName];
            if (group.Success && group.Value.Length > 0)
            {
                return group.Value;
            }
        }

        return null;
    }

    private static string? ExtractFromLinkHeader(string headerValue)
    {
        // One Link header may carry several comma-separated entries; the
        // canonical entry has rel=canonical (quoted or bare) and a <...> target.
        foreach (Match entry in LinkEntryRegex().Matches(headerValue))
        {
            var relations = entry.Groups["rel"].Value;
            var isCanonical = relations
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(relation => string.Equals(relation.Trim('"'), "canonical", StringComparison.OrdinalIgnoreCase));
            if (isCanonical)
            {
                var target = entry.Groups["target"].Value;
                return target.Length == 0 ? null : target;
            }
        }

        return null;
    }

    private static string? Resolve(string baseUrl, string canonical)
    {
        if (Uri.TryCreate(canonical, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return Uri.TryCreate(new Uri(baseUrl, UriKind.Absolute), canonical, out var resolved)
            ? resolved.ToString()
            : null;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"\brel\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex RelAttributeRegex();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex HrefAttributeRegex();

    [GeneratedRegex(@"<(?<target>[^>]*)>\s*;[^,]*?\brel\s*=\s*(?:""(?<rel>[^""]*)""|'(?<rel>[^']*)'|(?<rel>[^,;]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex LinkEntryRegex();
}
