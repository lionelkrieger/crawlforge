using System.Globalization;
using System.Text;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Deterministic, idempotent URL canonicalization (FS-CRAWL-001 §5.2): scheme and
/// host lowercased, default ports removed, fragments discarded, percent-encoding
/// normalized, trailing-slash policy applied consistently, query parameters sorted
/// and filtered by the configured policy with known session trackers stripped, and
/// relative references resolved against the document base (honoring base overrides).
/// Ordered regex rewrite rules run during canonicalization before seen-set keying
/// (FS-CRAWL-002 §10.4). The canonical form is the unique crawl key per audit.
/// </summary>
public sealed class UrlCanonicalizer : IUrlCanonicalizer
{
    /// <summary>
    /// Bundled local session-tracking parameter set (always stripped). Data, not
    /// engine logic — extended locally via <see cref="CanonicalizationOptions.AdditionalSessionParameters"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> BundledSessionParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_cid",
        "fbclid", "gclid", "msclkid", "dclid", "twclid", "ttclid", "yclid", "zanpid",
        "mc_cid", "mc_eid", "igshid", "_ga", "_gl", "mkt_tok", "vero_id", "wickedid",
        "s_cid", "icid", "_openstat", "pk_campaign", "pk_kwd"
    };

    private readonly CanonicalizationOptions _options;
    private readonly UrlRewriteEngine _rewriteEngine;
    private readonly HashSet<string> _sessionParameters;
    private readonly HashSet<string> _whitelist;

    /// <summary>Creates the canonicalizer.</summary>
    /// <param name="options">Local options; null reproduces the baseline exactly.</param>
    /// <param name="rewriteEngine">Rewrite engine; null creates a default engine.</param>
    public UrlCanonicalizer(CanonicalizationOptions? options = null, UrlRewriteEngine? rewriteEngine = null)
    {
        _options = options ?? new CanonicalizationOptions();
        _rewriteEngine = rewriteEngine ?? new UrlRewriteEngine();
        _rewriteEngine.ValidateRules(_options.RewriteRules);

        _sessionParameters = new HashSet<string>(BundledSessionParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in _options.AdditionalSessionParameters)
        {
            _sessionParameters.Add(extra);
        }

        _whitelist = new HashSet<string>(_options.QueryWhitelist, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string Canonicalize(string url) => Canonicalize(url, documentBase: null);

    /// <summary>
    /// Canonicalizes a URL, resolving relative references against an explicit
    /// document base (a declared base override wins over the configured default).
    /// </summary>
    /// <param name="url">Absolute or relative URL reference.</param>
    /// <param name="documentBase">Declaring document base; null falls back to the configured default.</param>
    /// <returns>The canonical URL string.</returns>
    /// <exception cref="ArgumentException">The URL is empty, non-http(s), or relative without any base.</exception>
    public string Canonicalize(string url, string? documentBase)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("The URL must not be empty.", nameof(url));
        }

        var trimmed = url.Trim();
        var absolute = ResolveAbsolute(trimmed, documentBase);

        // Rewrite rules apply during canonicalization, before seen-set keying.
        var rewritten = _options.RewriteRules.Count == 0
            ? absolute
            : _rewriteEngine.Rewrite(absolute, _options.RewriteRules);

        if (!Uri.TryCreate(rewritten, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Only absolute http(s) URLs can be canonicalized: '{rewritten}'.", nameof(url));
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme.ToLowerInvariant()).Append("://");
        builder.Append(uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        var path = NormalizePercentEncoding(uri.AbsolutePath);
        if (path.Length == 0)
        {
            path = "/";
        }

        path = ApplyTrailingSlashPolicy(path);
        builder.Append(path);

        var query = BuildCanonicalQuery(uri.Query);
        if (query.Length > 0)
        {
            builder.Append('?').Append(query);
        }

        // Fragments are discarded.
        return builder.ToString();
    }

    private string ResolveAbsolute(string url, string? documentBase)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        var baseUri = documentBase ?? _options.DefaultBase
            ?? throw new ArgumentException($"Relative URL '{url}' requires a document base.", nameof(url));
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var parsedBase))
        {
            throw new ArgumentException($"Invalid document base '{baseUri}'.", nameof(documentBase));
        }

        return new Uri(parsedBase, url).AbsoluteUri;
    }

    private string ApplyTrailingSlashPolicy(string path) => _options.TrailingSlash switch
    {
        TrailingSlashPolicy.Append when path != "/" && !path.EndsWith('/') => path + "/",
        TrailingSlashPolicy.Strip when path.Length > 1 => path.TrimEnd('/'),
        _ => path
    };

    private string BuildCanonicalQuery(string rawQuery)
    {
        if (_options.QueryPolicy == QueryParameterPolicy.StripAll || rawQuery.Length <= 1)
        {
            return string.Empty;
        }

        var pairs = new List<QueryPair>();
        foreach (var segment in rawQuery[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? segment : segment[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(segment[(separator + 1)..]);

            if (IsSessionParameter(name))
            {
                continue;
            }

            if (_options.QueryPolicy == QueryParameterPolicy.KeepWhitelist && !_whitelist.Contains(name))
            {
                continue;
            }

            pairs.Add(new QueryPair(name, value));
        }

        pairs.Sort(static (a, b) =>
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Value, b.Value);
        });

        var builder = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Name)).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    private sealed record QueryPair(string Name, string Value);

    private bool IsSessionParameter(string name) =>
        _sessionParameters.Contains(name) || name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes percent-encoding: uppercase hex digits, unreserved octets decoded
    /// (RFC 3986 §2.3). Stable under repeated application (idempotence).
    /// </summary>
    /// <param name="input">The escaped URI component.</param>
    /// <returns>The normalized component.</returns>
    public static string NormalizePercentEncoding(string input)
    {
        var builder = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '%' && i + 2 < input.Length &&
                Uri.IsHexDigit(input[i + 1]) && Uri.IsHexDigit(input[i + 2]))
            {
                var octet = Convert.ToByte(input.Substring(i + 1, 2), 16);
                var decoded = (char)octet;
                if (IsUnreserved(decoded))
                {
                    builder.Append(decoded);
                }
                else
                {
                    builder.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
                }

                i += 2;
            }
            else
            {
                builder.Append(input[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~';
}
