using System.Text;
using System.Text.RegularExpressions;

namespace CrawlForge.Crawler.Traps;

/// <summary>
/// Relation of one discovered pagination link (FS-CRAWL-001 §5.5: document-level
/// link relations next/previous are discovery sources; §5.12 toggles their use).
/// </summary>
public enum PaginationRelation
{
    /// <summary>rel="next" — the next page of a paginated sequence.</summary>
    Next = 0,

    /// <summary>rel="prev"/"previous" — the previous page of a paginated sequence.</summary>
    Prev = 1,

    /// <summary>A numbered pagination link (anchor text is a page number).</summary>
    Numbered = 2,
}

/// <summary>
/// Local bounds of pagination discovery (the §7.9 bound philosophy). Pagination
/// sequences are classic crawler traps, so discovery is bounded per page and
/// scope-limited; the frontier additionally consults the crawler-trap detector
/// and the list-mode gate before enqueueing.
/// </summary>
/// <param name="MaxLinksPerPage">Maximum pagination links returned per page. Default 100.</param>
/// <param name="MaxNumberedPages">Maximum numbered-page links per page. Default 50.</param>
/// <param name="MaxBodyScanBytes">Maximum body bytes scanned for pagination markup. Default 64 KiB.</param>
public sealed record PaginationOptions(
    int MaxLinksPerPage = 100,
    int MaxNumberedPages = 50,
    int MaxBodyScanBytes = 64 * 1024)
{
    /// <summary>Shared baseline bounds.</summary>
    public static PaginationOptions Default { get; } = new();

    /// <summary>Validates the bounds at configuration time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public void Validate()
    {
        if (MaxLinksPerPage < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLinksPerPage), MaxLinksPerPage, "At least one link must be discoverable.");
        }

        if (MaxNumberedPages < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxNumberedPages), MaxNumberedPages, "The numbered-page bound cannot be negative.");
        }

        if (MaxBodyScanBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBodyScanBytes), MaxBodyScanBytes, "The body scan bound must be positive.");
        }
    }
}

/// <summary>
/// One discovered pagination link.
/// </summary>
/// <param name="Url">Resolved absolute URL of the pagination target.</param>
/// <param name="Relation">How the link was declared.</param>
/// <param name="AnchorText">Visible anchor text, when the link came from an anchor.</param>
public sealed record PaginationLink(string Url, PaginationRelation Relation, string? AnchorText);

/// <summary>
/// Result of pagination discovery over one fetched page.
/// </summary>
/// <param name="Links">Discovered pagination links in declaration order, deduplicated.</param>
/// <param name="TruncatedCount">Links dropped beyond the per-page bound (counted only).</param>
public sealed record PaginationDiscoveryResult(IReadOnlyList<PaginationLink> Links, int TruncatedCount);

/// <summary>
/// Pagination handling (FS-CRAWL-001 §5.5 link discovery; §5.12 directive
/// toggles): discovers rel=next/prev pagination from the HTTP <c>Link</c>
/// header, HTML <c>&lt;link&gt;</c> elements and anchors, plus numbered
/// pagination links (numeric anchor text targeting a page-like query parameter
/// or trailing page path segment). Discovery is scope-limited (default: the
/// page's own host; callers pass the audit's scope predicate), bounded per
/// page, and deduplicated against the current page. Purely local and
/// deterministic — the component never fetches; the engine feeds it the
/// response it already fetched and enqueues the returned links through the
/// normal frontier admission (trap detector + list gate + seen-set).
/// </summary>
public sealed partial class PaginationDiscovery
{
    private static readonly string[] PageLikeQueryKeys = ["page", "p", "pg", "pagenum", "page_no"];

    private readonly PaginationOptions _options;

    /// <summary>Creates a discovery component over local bounds.</summary>
    /// <param name="options">Discovery bounds; null uses the baseline.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public PaginationDiscovery(PaginationOptions? options = null)
    {
        _options = options ?? PaginationOptions.Default;
        _options.Validate();
    }

    /// <summary>
    /// Discovers pagination links of one fetched page.
    /// </summary>
    /// <param name="pageUrl">Absolute URL of the fetched page (base for relative targets).</param>
    /// <param name="headers">Response headers (multi-values joined); may be empty.</param>
    /// <param name="body">Response body bytes; may be empty (header-only discovery).</param>
    /// <param name="inScope">
    /// Scope predicate over resolved absolute URLs; null defaults to same-host
    /// scope. Out-of-scope targets are never returned.
    /// </param>
    /// <returns>Bounded, deduplicated pagination links in declaration order.</returns>
    public PaginationDiscoveryResult Discover(
        string pageUrl,
        IReadOnlyDictionary<string, string>? headers,
        ReadOnlyMemory<byte> body,
        Func<string, bool>? inScope = null)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new PaginationDiscoveryResult([], 0);
        }

        headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        inScope ??= url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                           string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);

        var links = new List<PaginationLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { baseUri.ToString() };
        var truncated = 0;
        var numberedCount = 0;

        void Add(string? target, PaginationRelation relation, string? anchorText)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            if (!Uri.TryCreate(baseUri, target, out var resolved) ||
                (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            var url = resolved.ToString();
            if (!inScope(url) || !seen.Add(url))
            {
                return;
            }

            if (relation == PaginationRelation.Numbered)
            {
                if (numberedCount >= _options.MaxNumberedPages)
                {
                    truncated++;
                    return;
                }

                numberedCount++;
            }

            if (links.Count >= _options.MaxLinksPerPage)
            {
                truncated++;
                return;
            }

            links.Add(new PaginationLink(url, relation, anchorText));
        }

        // 1. HTTP Link header entries with rel=next/prev.
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match entry in LinkEntryRegex().Matches(value))
            {
                var relation = RelationOf(entry.Groups["rel"].Value);
                if (relation is { } r)
                {
                    Add(entry.Groups["target"].Value, r, null);
                }
            }
        }

        // 2. Bounded HTML scan: <link rel=next/prev> then anchors.
        if (body.Length > 0)
        {
            var scanLength = Math.Min(body.Length, _options.MaxBodyScanBytes);
            var text = Encoding.UTF8.GetString(body.Span[..scanLength]);

            foreach (Match tag in LinkTagRegex().Matches(text))
            {
                var relation = RelationOf(ReadAttribute(tag.Value, "rel"));
                if (relation is { } r)
                {
                    Add(ReadAttribute(tag.Value, "href"), r, null);
                }
            }

            foreach (Match anchor in AnchorRegex().Matches(text))
            {
                var attributes = anchor.Groups["attrs"].Value;
                var href = ReadAttribute(attributes, "href");
                var anchorText = anchor.Groups["text"].Value.Trim();

                var relation = RelationOf(ReadAttribute(attributes, "rel"));
                if (relation is { } r)
                {
                    Add(href, r, anchorText.Length == 0 ? null : anchorText);
                    continue;
                }

                // Numbered pagination: numeric anchor text + page-like target.
                if (IsPageNumber(anchorText) && href is not null &&
                    IsPageLikeTarget(baseUri, href, out var resolvedTarget))
                {
                    Add(resolvedTarget, PaginationRelation.Numbered, anchorText);
                }
            }
        }

        return new PaginationDiscoveryResult(links, truncated);
    }

    private static PaginationRelation? RelationOf(string? relValue)
    {
        if (string.IsNullOrWhiteSpace(relValue))
        {
            return null;
        }

        foreach (var relation in relValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = relation.Trim('"', '\'');
            if (string.Equals(token, "next", StringComparison.OrdinalIgnoreCase))
            {
                return PaginationRelation.Next;
            }

            if (string.Equals(token, "prev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "previous", StringComparison.OrdinalIgnoreCase))
            {
                return PaginationRelation.Prev;
            }
        }

        return null;
    }

    private static bool IsPageNumber(string anchorText) =>
        anchorText.Length is >= 1 and <= 4 &&
        anchorText.All(char.IsAsciiDigit) &&
        int.TryParse(anchorText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
        number >= 1;

    private bool IsPageLikeTarget(Uri baseUri, string href, out string? resolvedTarget)
    {
        resolvedTarget = null;
        if (!Uri.TryCreate(baseUri, href, out var target) ||
            !string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Shape A: same path, query differs only in page-like parameters.
        if (string.Equals(target.AbsolutePath, baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            var baseQuery = ParseQuery(baseUri.Query);
            var targetQuery = ParseQuery(target.Query);
            var hasPageParam = false;
            foreach (var (key, value) in targetQuery)
            {
                if (baseQuery.TryGetValue(key, out var baseValue) && string.Equals(baseValue, value, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!PageLikeQueryKeys.Contains(key, StringComparer.Ordinal) ||
                    !value.All(char.IsAsciiDigit) || value.Length == 0)
                {
                    return false;
                }

                hasPageParam = true;
            }

            if (hasPageParam)
            {
                resolvedTarget = target.ToString();
                return true;
            }

            return false;
        }

        // Shape B: trailing page path segment — /page/N or /N over the same
        // base path (e.g. /products/page/2, /products/2). The current page may
        // itself be page 1 without a segment (/products).
        var targetBase = TrimPageSegment(target.AbsolutePath);
        if (targetBase is not null &&
            !string.Equals(target.AbsolutePath, baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            var currentBase = TrimPageSegment(baseUri.AbsolutePath) ?? baseUri.AbsolutePath.TrimEnd('/');
            if (string.Equals(currentBase, targetBase, StringComparison.Ordinal))
            {
                resolvedTarget = target.ToString();
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = (separator < 0 ? pair : pair[..separator]).ToLowerInvariant();
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[key] = value;
        }

        return result;
    }

    private static string? TrimPageSegment(string path)
    {
        var match = PageSegmentRegex().Match(path);
        return match.Success ? match.Groups["base"].Value : null;
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

    [GeneratedRegex(@"<(?<target>[^>]*)>\s*;[^,]*?\brel\s*=\s*(?:""(?<rel>[^""]*)""|'(?<rel>[^']*)'|(?<rel>[^,;]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex LinkEntryRegex();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"\brel\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex RelAttributeRegex();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex HrefAttributeRegex();

    [GeneratedRegex(@"^(?<base>.+?)(?:/page)?/(?<num>\d+)/?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex PageSegmentRegex();
}
