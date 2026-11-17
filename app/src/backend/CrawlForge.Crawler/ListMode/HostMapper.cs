using System.Globalization;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Staging↔production host mapping (FS-CRAWL-002 §10.5): source host → target
/// host with an optional path-prefix transform. Audit and content comparison
/// align URLs by path (plus query) after host mapping. Without a matching
/// rule, URLs pass through unchanged — comparison behavior is exactly the
/// parent's. All work is local.
/// </summary>
public sealed class HostMapper
{
    private readonly IReadOnlyList<HostMappingRule> _rules;

    /// <summary>Creates a mapper over the configured host-mapping rules.</summary>
    /// <param name="rules">Host-mapping rules; null means no mapping.</param>
    public HostMapper(IReadOnlyList<HostMappingRule>? rules)
    {
        _rules = rules ?? [];
    }

    /// <summary>
    /// Maps a URL's host (and optional path prefix) through the first matching
    /// rule. Scheme, port, query, and fragment are preserved; the remainder of
    /// the path is kept verbatim after the prefix transform.
    /// </summary>
    /// <param name="url">Input URL.</param>
    /// <returns>The mapped URL, or the input when no rule matches or it is not absolute.</returns>
    public string MapUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        foreach (var rule in _rules)
        {
            if (!string.Equals(uri.Host, rule.SourceHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = uri.AbsolutePath;
            if (rule.SourcePathPrefix is { Length: > 0 } sourcePrefix &&
                path.StartsWith(sourcePrefix, StringComparison.Ordinal))
            {
                path = (rule.TargetPathPrefix ?? string.Empty) + path[sourcePrefix.Length..];
            }

            var builder = new UriBuilder(uri)
            {
                Host = rule.TargetHost,
                Path = path,
            };
            return builder.Uri.ToString();
        }

        return url;
    }

    /// <summary>
    /// Aligns two URLs by path plus query after host mapping (§10.5): both URLs
    /// are mapped through the rules, then their paths and queries are compared
    /// ordinally. Hosts are aligned by the mapping itself; scheme is ignored.
    /// </summary>
    /// <param name="urlA">First URL (e.g. staging).</param>
    /// <param name="urlB">Second URL (e.g. production).</param>
    /// <returns>True when the mapped URLs align by path and query.</returns>
    public bool AlignByPath(string urlA, string urlB)
    {
        ArgumentNullException.ThrowIfNull(urlA);
        ArgumentNullException.ThrowIfNull(urlB);

        if (!Uri.TryCreate(MapUrl(urlA), UriKind.Absolute, out var a) ||
            !Uri.TryCreate(MapUrl(urlB), UriKind.Absolute, out var b))
        {
            return false;
        }

        return string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.Ordinal) &&
               string.Equals(a.Query, b.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalized matrix comparison (§10.3 strictness <c>normalized</c> +
    /// §10.5): the observed URL is host-mapped, then host + path + query are
    /// compared scheme-insensitively against the expected URL (also mapped).
    /// </summary>
    /// <param name="observedUrl">Observed final URL.</param>
    /// <param name="expectedUrl">Expected target URL.</param>
    /// <returns>True when the URLs agree under normalized comparison.</returns>
    public bool NormalizedEquals(string observedUrl, string expectedUrl)
    {
        ArgumentNullException.ThrowIfNull(observedUrl);
        ArgumentNullException.ThrowIfNull(expectedUrl);

        if (!Uri.TryCreate(MapUrl(observedUrl), UriKind.Absolute, out var observed) ||
            !Uri.TryCreate(MapUrl(expectedUrl), UriKind.Absolute, out var expected))
        {
            return false;
        }

        return string.Equals(observed.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(observed.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal) &&
               string.Equals(observed.Query, expected.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Labels a mapped host pair for comparison surfaces (§10.5: surfaces label
    /// the mapped hosts), e.g. <c>staging.example.com → www.example.com</c>.
    /// </summary>
    /// <returns>One label per configured rule, in order.</returns>
    public IReadOnlyList<string> DescribeMappings() =>
        _rules.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.SourceHost} → {r.TargetHost}")).ToList();
}
