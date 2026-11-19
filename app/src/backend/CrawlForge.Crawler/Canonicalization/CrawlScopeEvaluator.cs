using System.Text.RegularExpressions;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Disposition of a discovered URL under the crawl scope (FS-CRAWL-001
/// §5.1/§5.5/§5.13): in-scope URLs are enqueued; external URLs are recorded as
/// external links only (or status-checked when the toggle is on); excluded
/// URLs are recorded as skipped with the reason.
/// </summary>
public enum CrawlScopeDisposition
{
    /// <summary>In scope: enqueue for fetching.</summary>
    Enqueue = 0,

    /// <summary>External: record as an external link only (baseline).</summary>
    RecordExternal = 1,

    /// <summary>External: check the link status without crawling beyond it.</summary>
    CheckExternalStatus = 2,

    /// <summary>Skipped: an exclusion pattern disqualified the URL.</summary>
    SkippedExcluded = 3,

    /// <summary>Skipped: the resource-type fetch toggle is off.</summary>
    SkippedResource = 4,

    /// <summary>Skipped: outside the host scope or path-prefix rules.</summary>
    SkippedOutOfScope = 5
}

/// <summary>
/// Scope verdict for one canonical URL.
/// </summary>
/// <param name="Disposition">What the engine should do with the URL.</param>
/// <param name="Reason">Human-readable skip/exclusion reason, when skipped.</param>
/// <param name="ResourceType">Classified resource type of the URL.</param>
/// <param name="IsInternalHost">Whether the host is internal (seed scope or asset domain).</param>
public sealed record CrawlScopeVerdict(
    CrawlScopeDisposition Disposition,
    string? Reason,
    CrawlResourceType ResourceType,
    bool IsInternalHost);

/// <summary>
/// Evaluates discovered URLs against the configured crawl scope
/// (FS-CRAWL-001 §5.1/§5.13): host scope, inclusion patterns, exclusion
/// patterns (evaluated after inclusion), path-prefix rules, resource-type
/// fetch toggles, internal asset domains, and the external-link status-check
/// toggle. Regex evaluation is time-bounded and invalid patterns are rejected
/// at construction (§4.3/§10.4). Pure and thread-safe: all state is immutable
/// after construction, so concurrent fetch workers may share one evaluator.
/// </summary>
public sealed class CrawlScopeEvaluator
{
    private readonly CrawlScopeOptions _options;
    private readonly ResourceScopeOptions _resources;
    private readonly HashSet<string> _seedHosts;
    private readonly IReadOnlyList<Regex> _include;
    private readonly IReadOnlyList<Regex> _exclude;

    /// <summary>Creates the evaluator; invalid regex patterns throw here (§4.3).</summary>
    /// <param name="options">Scope options; null uses the baseline defaults.</param>
    /// <param name="seedHosts">Hosts of the audit seeds (scope anchors).</param>
    /// <param name="resources">Resource toggles; null keeps resources unfetched (baseline).</param>
    /// <exception cref="ArgumentException">A pattern is not a valid regular expression.</exception>
    public CrawlScopeEvaluator(
        CrawlScopeOptions? options,
        IEnumerable<string> seedHosts,
        ResourceScopeOptions? resources = null)
    {
        ArgumentNullException.ThrowIfNull(seedHosts);
        _options = options ?? new CrawlScopeOptions();
        _resources = resources ?? new ResourceScopeOptions();
        if (_options.RegexTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.RegexTimeoutMs, "The regex timeout must be positive.");
        }

        _seedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in seedHosts)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                _seedHosts.Add(host.Trim());
            }
        }

        var timeout = TimeSpan.FromMilliseconds(_options.RegexTimeoutMs);
        _include = Compile(_options.IncludePatterns, timeout, nameof(_options.IncludePatterns));
        _exclude = Compile(_options.ExcludePatterns, timeout, nameof(_options.ExcludePatterns));
    }

    /// <summary>
    /// Classifies one canonical URL. The URL must be an absolute http(s) URL
    /// (canonicalization already guarantees this for frontier entries).
    /// </summary>
    /// <param name="canonicalUrl">The canonical URL to classify.</param>
    /// <returns>The scope verdict.</returns>
    /// <exception cref="ArgumentException">The URL is not an absolute http(s) URL.</exception>
    public CrawlScopeVerdict Evaluate(string canonicalUrl)
    {
        if (!Uri.TryCreate(canonicalUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Only absolute http(s) URLs can be scope-evaluated: '{canonicalUrl}'.", nameof(canonicalUrl));
        }

        var resourceType = CrawlResourceClassifier.FromUrl(uri);
        var internalHost = IsInternalHost(uri.Host);

        if (!internalHost)
        {
            return new CrawlScopeVerdict(
                _options.CheckExternalLinks ? CrawlScopeDisposition.CheckExternalStatus : CrawlScopeDisposition.RecordExternal,
                Reason: null,
                resourceType,
                IsInternalHost: false);
        }

        // Inclusion first: at least one pattern must match when configured.
        if (_include.Count > 0 && !AnyMatch(_include, canonicalUrl))
        {
            return new CrawlScopeVerdict(
                CrawlScopeDisposition.SkippedOutOfScope,
                "no inclusion pattern matched",
                resourceType,
                IsInternalHost: true);
        }

        // Exclusion is evaluated after inclusion (FS-CRAWL-001 §5.1).
        foreach (var pattern in _exclude)
        {
            if (SafeIsMatch(pattern, canonicalUrl))
            {
                return new CrawlScopeVerdict(
                    CrawlScopeDisposition.SkippedExcluded,
                    $"exclusion pattern '{pattern.ToString()}' matched",
                    resourceType,
                    IsInternalHost: true);
            }
        }

        if (_options.PathPrefixes.Count > 0
            && !_options.PathPrefixes.Any(prefix => uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return new CrawlScopeVerdict(
                CrawlScopeDisposition.SkippedOutOfScope,
                "path is outside the configured path prefixes",
                resourceType,
                IsInternalHost: true);
        }

        if (!_resources.ShouldFetch(resourceType))
        {
            return new CrawlScopeVerdict(
                CrawlScopeDisposition.SkippedResource,
                $"resource fetch toggle for '{resourceType}' is off",
                resourceType,
                IsInternalHost: true);
        }

        return new CrawlScopeVerdict(CrawlScopeDisposition.Enqueue, Reason: null, resourceType, IsInternalHost: true);
    }

    /// <summary>
    /// Whether a host is internal: a seed host under the configured scope mode,
    /// or an asset domain (CDN) treated as internal (§5.13).
    /// </summary>
    /// <param name="host">The host to test.</param>
    /// <returns>True when the host is internal to the crawl.</returns>
    public bool IsInternalHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = host.Trim();
        foreach (var seed in _seedHosts)
        {
            if (HostInScope(normalized, seed))
            {
                return true;
            }
        }

        foreach (var asset in _options.AssetDomains)
        {
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            var domain = asset.Trim();
            if (string.Equals(normalized, domain, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HostInScope(string host, string seed) => _options.Mode switch
    {
        CrawlScopeMode.IncludeSubdomains =>
            string.Equals(host, seed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + seed, StringComparison.OrdinalIgnoreCase),
        CrawlScopeMode.WholeDomain =>
            string.Equals(RegisteredDomain(host), RegisteredDomain(seed), StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(host, seed, StringComparison.OrdinalIgnoreCase)
    };

    private static string RegisteredDomain(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
    }

    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns, TimeSpan timeout, string paramName)
    {
        var compiled = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("Scope patterns must not be empty.", paramName);
            }

            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.CultureInvariant, timeout));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid scope regex pattern '{pattern}': {ex.Message}", paramName, ex);
            }
        }

        return compiled;
    }

    private static bool AnyMatch(IReadOnlyList<Regex> patterns, string input)
    {
        foreach (var pattern in patterns)
        {
            if (SafeIsMatch(pattern, input))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SafeIsMatch(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Bounded evaluation (§10.4): a pathological pattern never stalls
            // a fetch worker; the URL is treated as not matching.
            return false;
        }
    }
}

/// <summary>
/// Classifies URLs and content types into the §5.13 resource types. Extension
/// classification is the primary signal for discovered links; content-type
/// classification serves fetched responses.
/// </summary>
public static class CrawlResourceClassifier
{
    /// <summary>Classifies a URL by its path extension.</summary>
    /// <param name="url">The URL to classify.</param>
    /// <returns>The resource type (Html when no known resource extension).</returns>
    public static CrawlResourceType FromUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var extension = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".js" or ".mjs" or ".cjs" => CrawlResourceType.Script,
            ".css" => CrawlResourceType.Stylesheet,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" or ".svg" or ".ico" or ".bmp" => CrawlResourceType.Image,
            ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot" => CrawlResourceType.Font,
            ".html" or ".htm" or ".xhtml" or ".aspx" or ".php" or ".jsp" or "" => CrawlResourceType.Html,
            _ => CrawlResourceType.Other
        };
    }

    /// <summary>Classifies a response content type.</summary>
    /// <param name="contentType">The content-type header value, when present.</param>
    /// <returns>The resource type (Other for unknown or absent types).</returns>
    public static CrawlResourceType FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return CrawlResourceType.Other;
        }

        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mime is "text/html" or "application/xhtml+xml")
        {
            return CrawlResourceType.Html;
        }

        if (mime is "text/javascript" or "application/javascript" or "application/x-javascript" or "text/ecmascript")
        {
            return CrawlResourceType.Script;
        }

        if (mime == "text/css")
        {
            return CrawlResourceType.Stylesheet;
        }

        if (mime.StartsWith("image/", StringComparison.Ordinal))
        {
            return CrawlResourceType.Image;
        }

        if (mime.StartsWith("font/", StringComparison.Ordinal) || mime is "application/font-woff" or "application/x-font-ttf")
        {
            return CrawlResourceType.Font;
        }

        return CrawlResourceType.Other;
    }
}

/// <summary>
/// Applies the §5.12 directive-handling toggles to discovery decisions.
/// Detection and reporting of directives are unaffected (§5.12): these
/// helpers govern only whether links/targets are followed and whether
/// noindex pages are crawled.
/// </summary>
public static class DirectiveHandlingEvaluator
{
    /// <summary>
    /// Whether a link is followed given page-level and link-level nofollow.
    /// Page nofollow suppresses every link on the page unless its toggle is on;
    /// link nofollow suppresses the individual link unless its toggle is on.
    /// </summary>
    /// <param name="options">Directive toggles.</param>
    /// <param name="pageNofollow">The page declares nofollow (meta or header).</param>
    /// <param name="linkNofollow">The link declares rel=nofollow.</param>
    /// <returns>True when the link may be followed.</returns>
    public static bool ShouldFollowLink(DirectiveHandlingOptions options, bool pageNofollow, bool linkNofollow)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageNofollow && !options.FollowPageNofollow)
        {
            return false;
        }

        return !linkNofollow || options.FollowLinkNofollow;
    }

    /// <summary>Whether a noindex page is still crawled so it can be reported (default on).</summary>
    /// <param name="options">Directive toggles.</param>
    /// <param name="noindex">The page declares noindex.</param>
    /// <returns>True when the page may be crawled.</returns>
    public static bool ShouldCrawlPage(DirectiveHandlingOptions options, bool noindex)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !noindex || options.CrawlNoindexPages;
    }

    /// <summary>Whether canonical targets are enqueued as discovery (default on).</summary>
    /// <param name="options">Directive toggles.</param>
    /// <returns>The toggle value.</returns>
    public static bool ShouldEnqueueCanonical(DirectiveHandlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseCanonicalDiscovery;
    }

    /// <summary>Whether hreflang alternates and rel next/previous are enqueued (default on).</summary>
    /// <param name="options">Directive toggles.</param>
    /// <returns>The toggle value.</returns>
    public static bool ShouldEnqueueAlternates(DirectiveHandlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseHreflangAndPaginationDiscovery;
    }

    /// <summary>Whether meta-refresh targets are followed (default on).</summary>
    /// <param name="options">Directive toggles.</param>
    /// <returns>The toggle value.</returns>
    public static bool ShouldFollowMetaRefresh(DirectiveHandlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.FollowMetaRefresh;
    }

    /// <summary>Whether iframe sources are followed (default off).</summary>
    /// <param name="options">Directive toggles.</param>
    /// <returns>The toggle value.</returns>
    public static bool ShouldFollowIframes(DirectiveHandlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.FollowIframes;
    }
}
