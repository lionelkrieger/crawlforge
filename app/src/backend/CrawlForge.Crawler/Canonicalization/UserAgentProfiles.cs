using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Bundled user-agent strings (local data, no runtime fetch — exclusions
/// E2/E4). The crawler identity is identifiable per FS-CRAWL-001 §4.4
/// (product name and version); the Googlebot and Chrome strings are the
/// publicly documented comparison identities of §5.15.
/// </summary>
public static class UserAgentProfiles
{
    /// <summary>Default identifiable crawler user-agent (product name + version).</summary>
    public const string CrawlForgeDefault = "CrawlForge/2.0 (offline SEO auditor; +https://crawlforge.app/bot)";

    /// <summary>Googlebot Smartphone identity for §5.15 comparison and simulation.</summary>
    public const string GooglebotSmartphone =
        "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.6422.76 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    /// <summary>Standard Chrome browser identity for §5.15 comparison.</summary>
    public const string ChromeBrowser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
}

/// <summary>
/// User-agent configuration (FS-CRAWL-001 §4.4/§5.1/§5.15): a custom string
/// wins; otherwise Googlebot simulation or the default crawler identity.
/// </summary>
public sealed record UserAgentOptions
{
    /// <summary>Custom user-agent string; null keeps the resolved default.</summary>
    public string? CustomUserAgent { get; init; }

    /// <summary>Simulate Googlebot Smartphone instead of the crawler identity (default off).</summary>
    public bool SimulateGooglebot { get; init; }

    /// <summary>Resolves the effective user-agent string.</summary>
    /// <returns>The user-agent the fetcher should send.</returns>
    public string Resolve() =>
        !string.IsNullOrWhiteSpace(CustomUserAgent) ? CustomUserAgent
        : SimulateGooglebot ? UserAgentProfiles.GooglebotSmartphone
        : UserAgentProfiles.CrawlForgeDefault;
}

/// <summary>
/// Per-audit request profile: the resolved user-agent plus user-configured
/// custom request headers and cookies (FS-CRAWL-001 §5.1). Applying the
/// profile produces a new <see cref="HttpFetchRequest"/>; the locked contract
/// is never mutated. User-supplied values win over profile defaults; an
/// explicit user <c>User-Agent</c> header wins over the resolved identity.
/// </summary>
public sealed record CrawlRequestProfile
{
    /// <summary>Resolved user-agent string sent when no explicit header overrides it.</summary>
    public string UserAgent { get; init; } = UserAgentProfiles.CrawlForgeDefault;

    /// <summary>User-configured custom request headers (stored locally, encrypted at rest upstream).</summary>
    public IReadOnlyDictionary<string, string> CustomHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-supplied cookies merged into every in-scope request (§5.1/§5.11).</summary>
    public IReadOnlyDictionary<string, string> UserCookies { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates a profile from the user-agent options and user inputs.</summary>
    /// <param name="userAgent">User-agent options; null uses the default identity.</param>
    /// <param name="customHeaders">Custom headers; null means none.</param>
    /// <param name="userCookies">User cookies; null means none.</param>
    /// <returns>The request profile.</returns>
    public static CrawlRequestProfile Create(
        UserAgentOptions? userAgent = null,
        IReadOnlyDictionary<string, string>? customHeaders = null,
        IReadOnlyDictionary<string, string>? userCookies = null) => new()
        {
            UserAgent = (userAgent ?? new UserAgentOptions()).Resolve(),
            CustomHeaders = customHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            UserCookies = userCookies ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };

    /// <summary>
    /// Returns a copy of the request with the profile applied: custom headers
    /// merged (request headers win on conflict), the user-agent header set
    /// unless already present, and user cookies merged (request cookies win).
    /// </summary>
    /// <param name="request">The original fetch request.</param>
    /// <returns>The request with profile headers and cookies applied.</returns>
    public HttpFetchRequest Apply(HttpFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in CustomHeaders)
        {
            headers[key] = value;
        }

        if (request.Headers is not null)
        {
            foreach (var (key, value) in request.Headers)
            {
                headers[key] = value;
            }
        }

        if (!headers.ContainsKey("User-Agent"))
        {
            headers["User-Agent"] = UserAgent;
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in UserCookies)
        {
            cookies[key] = value;
        }

        if (request.Cookies is not null)
        {
            foreach (var (key, value) in request.Cookies)
            {
                cookies[key] = value;
            }
        }

        return request with { Headers = headers, Cookies = cookies };
    }
}
