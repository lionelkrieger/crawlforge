using System.Text;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// User-configured site authentication material (FS-CRAWL-001 §5.1: "custom
/// request headers / cookies — optional credentials for the target site,
/// stored locally"). Basic credentials and form-login session cookies are
/// user-owned, stored encrypted at rest by the configuration layer (§9.1),
/// and used solely for the user's own target sites.
/// </summary>
public sealed record CrawlAuthenticationSettings
{
    /// <summary>Optional HTTP Basic username (RFC 7617; must not contain ':').</summary>
    public string? BasicUsername { get; init; }

    /// <summary>Optional HTTP Basic password.</summary>
    public string? BasicPassword { get; init; }

    /// <summary>
    /// Optional user-supplied cookies — e.g. the session cookie of a form-login
    /// session the user established in their browser. Names are cookie names;
    /// values are cookie values.
    /// </summary>
    public IReadOnlyDictionary<string, string>? UserCookies { get; init; }

    /// <summary>True when both basic credentials are present.</summary>
    public bool HasBasicAuthentication =>
        !string.IsNullOrEmpty(BasicUsername) && !string.IsNullOrEmpty(BasicPassword);

    /// <summary>True when any credential material is configured.</summary>
    public bool HasCredentials =>
        HasBasicAuthentication || (UserCookies is { Count: > 0 });
}

/// <summary>
/// Applies user-configured authentication to outbound crawl requests
/// (FS-CRAWL-001 §5.1 custom headers/cookies; §5.6 credential scoping):
/// HTTP Basic <c>Authorization</c> and user/form-login cookies are attached
/// ONLY to in-scope origins — credential-bearing material never leaves the
/// crawl scope. Non-credential headers already on the request pass through
/// unchanged for every origin.
/// <para>
/// Cookie merge order (last wins): user-supplied cookies, then the per-audit
/// cookie jar's cookies for the origin (the jar is the live session state —
/// cf-wp-09-02's component feeds it through the optional
/// <paramref name="jarCookies"/> input), then cookies already on the request.
/// With no credentials configured the request passes through unchanged,
/// reproducing baseline behavior exactly. Purely local — the component never
/// fetches and never performs a login flow itself.
/// </para>
/// </summary>
public sealed class CrawlRequestAuthenticator
{
    private readonly CrawlAuthenticationSettings _settings;
    private readonly string? _basicHeaderValue;

    /// <summary>Creates an authenticator over local credential settings.</summary>
    /// <param name="settings">Credential settings; null means no authentication (baseline).</param>
    /// <exception cref="ArgumentException">The basic username contains ':' (RFC 7617).</exception>
    public CrawlRequestAuthenticator(CrawlAuthenticationSettings? settings = null)
    {
        _settings = settings ?? new CrawlAuthenticationSettings();
        if (_settings.BasicUsername is { } username && username.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The basic username must not contain ':' (RFC 7617).", nameof(settings));
        }

        _basicHeaderValue = _settings.HasBasicAuthentication
            ? BuildBasicHeaderValue(_settings.BasicUsername!, _settings.BasicPassword!)
            : null;
    }

    /// <summary>
    /// Returns the request with credential material applied when the target is
    /// in scope. Out-of-scope requests are returned unchanged — credentials
    /// never leak to third-party origins (§5.6).
    /// </summary>
    /// <param name="request">The outbound fetch request.</param>
    /// <param name="inScope">Whether the request's origin is in the audit's crawl scope.</param>
    /// <param name="jarCookies">
    /// Optional per-audit cookie-jar cookies for the origin (cf-wp-09-02's jar
    /// read/merge surface); null when the jar is off or empty.
    /// </param>
    /// <returns>The decorated request (the input instance when nothing applies).</returns>
    public HttpFetchRequest Decorate(
        HttpFetchRequest request,
        bool inScope,
        IReadOnlyDictionary<string, string>? jarCookies = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!inScope || !_settings.HasCredentials)
        {
            return request;
        }

        var headers = request.Headers is { Count: > 0 }
            ? new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_basicHeaderValue is not null && !headers.ContainsKey("Authorization"))
        {
            headers["Authorization"] = _basicHeaderValue;
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_settings.UserCookies is { Count: > 0 } userCookies)
        {
            foreach (var (name, value) in userCookies)
            {
                cookies[name] = value;
            }
        }

        if (jarCookies is { Count: > 0 })
        {
            foreach (var (name, value) in jarCookies)
            {
                cookies[name] = value;
            }
        }

        if (request.Cookies is { Count: > 0 } requestCookies)
        {
            foreach (var (name, value) in requestCookies)
            {
                cookies[name] = value;
            }
        }

        return request with
        {
            Headers = headers,
            Cookies = cookies.Count > 0 ? cookies : request.Cookies,
        };
    }

    /// <summary>
    /// Builds the RFC 7617 Basic header value (<c>Basic base64(user:password)</c>,
    /// UTF-8 charset). Exposed for contract tests and the fetcher wiring.
    /// </summary>
    /// <param name="username">Basic username (no ':').</param>
    /// <param name="password">Basic password.</param>
    /// <returns>The Authorization header value.</returns>
    public static string BuildBasicHeaderValue(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(username, ":", password));
        return "Basic " + Convert.ToBase64String(bytes);
    }
}
