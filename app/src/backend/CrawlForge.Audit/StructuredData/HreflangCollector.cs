using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>One hreflang annotation observed on a page.</summary>
/// <param name="Hreflang">Raw hreflang value (language tag or x-default).</param>
/// <param name="Href">Absolute target URL (resolved against the page URL).</param>
/// <param name="Source">Observation source: "html" or "header".</param>
public sealed record HreflangAnnotation(string Hreflang, string Href, string Source);

/// <summary>
/// Collects hreflang annotations from both directive sources (FS-CRAWL-001
/// §7.13): HTML <c>link rel="alternate" hreflang</c> elements and HTTP
/// <c>Link</c> response headers. Set-level validation across the two sources is
/// owned by <see cref="HreflangAnalyzer"/>; the header-vs-HTML equivalency
/// finding keys belong to the equivalency analyzer (WP-03-02) and are never
/// emitted here.
/// </summary>
public static class HreflangCollector
{
    private static readonly Regex LinkEntrySplit = new(@",\s*(?=<)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LinkParameter = new(
        """(?<key>[A-Za-z0-9_-]+)\s*=\s*(?:"(?<quoted>[^"]*)"|(?<bare>[^;,\s]+))""",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Collects HTML link-element hreflang annotations in document order.</summary>
    /// <param name="document">Parsed document, when available.</param>
    /// <param name="pageUrl">Page URL used to resolve relative targets.</param>
    /// <returns>Annotations with source "html".</returns>
    public static IReadOnlyList<HreflangAnnotation> FromDocument(DomDocument? document, string pageUrl)
    {
        var annotations = new List<HreflangAnnotation>();
        if (document is null)
        {
            return annotations;
        }

        foreach (var node in Walk(document.Root))
        {
            if (node.TagName != "link" ||
                !node.Attributes.TryGetValue("rel", out var rel) ||
                !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase) ||
                !node.Attributes.TryGetValue("hreflang", out var hreflang) ||
                !node.Attributes.TryGetValue("href", out var href))
            {
                continue;
            }

            annotations.Add(new HreflangAnnotation(hreflang.Trim(), NormalizeUrl(href, pageUrl), "html"));
        }

        return annotations;
    }

    /// <summary>
    /// Collects hreflang annotations from HTTP Link response headers. The
    /// contract joins multi-value headers with commas; entries are split only
    /// at commas that precede a new <c>&lt;URI&gt;</c> reference.
    /// </summary>
    /// <param name="headers">Response headers, when available.</param>
    /// <param name="pageUrl">Page URL used to resolve relative targets.</param>
    /// <returns>Annotations with source "header".</returns>
    public static IReadOnlyList<HreflangAnnotation> FromLinkHeaders(IReadOnlyDictionary<string, string>? headers, string pageUrl)
    {
        var annotations = new List<HreflangAnnotation>();
        if (headers is null)
        {
            return annotations;
        }

        var linkValue = headers.FirstOrDefault(h => string.Equals(h.Key, "Link", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(linkValue))
        {
            return annotations;
        }

        foreach (var entry in LinkEntrySplit.Split(linkValue))
        {
            var trimmed = entry.Trim();
            if (!trimmed.StartsWith('<'))
            {
                continue;
            }

            var close = trimmed.IndexOf('>');
            if (close < 0)
            {
                continue;
            }

            var href = trimmed[1..close];
            var parameters = trimmed[(close + 1)..];
            string? rel = null;
            string? hreflang = null;
            foreach (Match match in LinkParameter.Matches(parameters))
            {
                var key = match.Groups["key"].Value;
                var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
                if (string.Equals(key, "rel", StringComparison.OrdinalIgnoreCase))
                {
                    rel = value;
                }
                else if (string.Equals(key, "hreflang", StringComparison.OrdinalIgnoreCase))
                {
                    hreflang = value;
                }
            }

            if (rel is not null &&
                rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(hreflang))
            {
                annotations.Add(new HreflangAnnotation(hreflang.Trim(), NormalizeUrl(href, pageUrl), "header"));
            }
        }

        return annotations;
    }

    /// <summary>
    /// Normalizes a URL for set comparison: resolves against the page URL,
    /// lowercases scheme and host, drops default ports and fragments. Path and
    /// query are preserved verbatim (trailing slashes remain significant).
    /// </summary>
    /// <param name="href">Raw target URL (absolute or relative).</param>
    /// <param name="baseUrl">Page URL used as the resolution base.</param>
    /// <returns>The normalized absolute URL, or the trimmed input when unparseable.</returns>
    public static string NormalizeUrl(string href, string baseUrl)
    {
        var trimmed = href.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
            (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(new Uri(baseUrl), trimmed, out absolute)))
        {
            return trimmed;
        }

        var builder = new UriBuilder(absolute) { Fragment = string.Empty };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
            (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    private static IEnumerable<DomNode> Walk(DomNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
