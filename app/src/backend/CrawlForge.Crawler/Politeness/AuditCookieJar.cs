namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Per-audit cookie jar configuration (FS-CRAWL-001 §5.11). The jar is on by
/// default; the toggle crawls cookieless instead. User-supplied cookies
/// (§5.1) are explicit credentials and are sent even in cookieless mode —
/// only site-set cookie retention is disabled.
/// </summary>
public sealed record CookieJarOptions
{
    /// <summary>Whether site-set cookies are retained and replayed (default on).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Origin of a cookie stored in the jar.</summary>
public enum CookieSource
{
    /// <summary>Set by a crawled site through a Set-Cookie response header.</summary>
    Site = 0,

    /// <summary>User-supplied credential cookie (§5.1 audit inputs).</summary>
    UserSupplied = 1,

    /// <summary>Merged by the request authenticator (WP-09-03 form-login flow).</summary>
    Authenticator = 2
}

/// <summary>
/// One cookie stored in the per-audit jar, with the security attributes
/// captured for the D2 security rules (FS-CRAWL-001 §7.4 as amended by
/// CR-017: Secure, HttpOnly, SameSite). Data only — the jar emits no findings;
/// the D2 analyzer consumes these records.
/// </summary>
/// <param name="Name">Cookie name.</param>
/// <param name="Value">Cookie value.</param>
/// <param name="Domain">Domain the cookie is scoped to (lowercase, no leading dot).</param>
/// <param name="Path">Path the cookie is scoped to.</param>
/// <param name="HostOnly">True when the cookie matches only the exact host (no Domain attribute).</param>
/// <param name="Secure">Secure attribute observed.</param>
/// <param name="HttpOnly">HttpOnly attribute observed.</param>
/// <param name="SameSite">SameSite attribute value when present (strict/lax/none).</param>
/// <param name="ExpiresAt">Expiry instant; null for session cookies.</param>
/// <param name="IsSession">True when no expiry was declared.</param>
/// <param name="Source">Where the cookie came from.</param>
/// <param name="CreatedAt">First storage instant (UTC).</param>
public sealed record CookieRecord(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool HostOnly,
    bool Secure,
    bool HttpOnly,
    string? SameSite,
    DateTimeOffset? ExpiresAt,
    bool IsSession,
    CookieSource Source,
    DateTimeOffset CreatedAt);

/// <summary>
/// One cookie-security observation captured at response receipt for the D2
/// rules (Secure/HttpOnly/SameSite attributes, session vs persistent, scope).
/// </summary>
/// <param name="Name">Cookie name.</param>
/// <param name="Domain">Scoped domain.</param>
/// <param name="Path">Scoped path.</param>
/// <param name="Secure">Secure attribute observed.</param>
/// <param name="HttpOnly">HttpOnly attribute observed.</param>
/// <param name="SameSite">SameSite attribute value when present.</param>
/// <param name="IsSession">True when no expiry was declared.</param>
/// <param name="ExpiresAt">Expiry instant; null for session cookies.</param>
/// <param name="SourceUrl">Response URL that set the cookie.</param>
/// <param name="ObservedAt">Observation instant (UTC).</param>
public sealed record CookieSecurityObservation(
    string Name,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    string? SameSite,
    bool IsSession,
    DateTimeOffset? ExpiresAt,
    string SourceUrl,
    DateTimeOffset ObservedAt);

/// <summary>
/// A recorded response change after cookies were set (§5.11): the same URL
/// produced a different content hash once the jar retained new cookies.
/// </summary>
/// <param name="Url">The URL whose response changed.</param>
/// <param name="PreviousContentHash">Content hash of the earlier response.</param>
/// <param name="NewContentHash">Content hash of the changed response.</param>
/// <param name="CookieNamesSetBetween">Names of cookies set between the two observations.</param>
public sealed record CookieResponseChange(
    string Url,
    string PreviousContentHash,
    string NewContentHash,
    IReadOnlyList<string> CookieNamesSetBetween);

/// <summary>
/// Per-audit cookie jar (FS-CRAWL-001 §5.11): cookies set by crawled sites
/// are retained for the audit, scoped by host and path as a browser would
/// scope them (RFC 6265 domain/path matching, Secure transport restriction,
/// expiry handling), and sent on subsequent in-scope requests. The jar is on
/// by default; <see cref="CookieJarOptions.Enabled"/> false crawls cookieless
/// (site cookies are neither stored nor replayed; user-supplied credentials
/// still apply). User-supplied cookies are merged in. Security attributes are
/// captured for the D2 rules, and response changes after cookie set are
/// recorded. Thread-safe: fetch workers (pipeline MaxConcurrency ≥ 2) share
/// one jar per audit through a single synchronization gate.
/// </summary>
public sealed class AuditCookieJar
{
    private readonly object _gate = new();
    private readonly CookieJarOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<CookieKey, StoredCookie> _cookies = new();
    private readonly List<SetEvent> _setEvents = [];
    private readonly List<CookieSecurityObservation> _securityObservations = [];
    private readonly Dictionary<string, ResponseBaseline> _responseBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CookieResponseChange> _responseChanges = [];
    private long _creationCounter;
    private long _generation;

    /// <summary>Creates a per-audit jar.</summary>
    /// <param name="options">Jar options; null uses the default (enabled).</param>
    /// <param name="timeProvider">Deterministic clock; null uses the system clock.</param>
    public AuditCookieJar(CookieJarOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CookieJarOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether site-set cookie retention is enabled.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Captures cookies from one response's Set-Cookie header values
    /// (individual header lines, never a joined string — expiry dates contain
    /// commas). No-op when the jar is disabled (cookieless crawl).
    /// </summary>
    /// <param name="responseUrl">URL that produced the response.</param>
    /// <param name="setCookieHeaders">Individual Set-Cookie header values.</param>
    public void SetFromResponse(Uri responseUrl, IEnumerable<string> setCookieHeaders)
    {
        ArgumentNullException.ThrowIfNull(responseUrl);
        ArgumentNullException.ThrowIfNull(setCookieHeaders);
        if (!_options.Enabled)
        {
            return;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var storedAny = false;
            foreach (var header in setCookieHeaders)
            {
                if (TryParseAndStore(responseUrl, header, now, CookieSource.Site))
                {
                    storedAny = true;
                }
            }

            if (storedAny)
            {
                _generation++;
            }
        }
    }

    /// <summary>
    /// Merges user-supplied or authenticator cookies for an origin (§5.1/§5.11;
    /// WP-09-03 consumes this surface). Merged cookies are host-only, path "/",
    /// session-scoped, and are sent even in cookieless mode — they are explicit
    /// user credentials, not site-set tracking state.
    /// </summary>
    /// <param name="origin">Origin the cookies belong to (host scope).</param>
    /// <param name="cookies">Name/value pairs to merge.</param>
    /// <param name="source">Credential source (default user-supplied).</param>
    public void MergeCookies(Uri origin, IReadOnlyDictionary<string, string> cookies, CookieSource source = CookieSource.UserSupplied)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(cookies);
        if (source == CookieSource.Site)
        {
            throw new ArgumentException("Site cookies arrive through SetFromResponse, not MergeCookies.", nameof(source));
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var (name, value) in cookies)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var stored = new StoredCookie(
                    name.Trim(),
                    value,
                    origin.Host.ToLowerInvariant(),
                    "/",
                    HostOnly: true,
                    Secure: false,
                    HttpOnly: false,
                    SameSite: null,
                    ExpiresAt: null,
                    source,
                    now,
                    Interlocked.Increment(ref _creationCounter));
                _cookies[new CookieKey(stored.Domain, stored.Path, stored.Name)] = stored;
            }
        }
    }

    /// <summary>
    /// Returns the Cookie header value for a request URL: matching, unexpired
    /// cookies ordered by path length (longest first) then creation order
    /// (RFC 6265 §5.4). Same-name cookies on different paths are all sent,
    /// most-specific first (browser behavior; servers read the first). Null
    /// when no cookie applies. In cookieless mode only user-supplied/
    /// authenticator credentials are returned.
    /// </summary>
    /// <param name="requestUrl">The request URL.</param>
    /// <returns>The Cookie header value, or null.</returns>
    public string? GetCookieHeader(Uri requestUrl)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        lock (_gate)
        {
            var matching = MatchingCookies(requestUrl, _time.GetUtcNow());
            return !matching.Any()
                ? null
                : string.Join("; ", matching.Select(c => $"{c.Name}={c.Value}"));
        }
    }

    /// <summary>
    /// Returns the name→value map of cookies the jar would send to the origin
    /// (read surface for the WP-09-03 authenticator merge). When several paths
    /// match the same name, the highest-priority cookie wins.
    /// </summary>
    /// <param name="requestUrl">The request URL.</param>
    /// <returns>Matching cookies in send order.</returns>
    public IReadOnlyDictionary<string, string> GetCookieMap(Uri requestUrl)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var stored in MatchingCookies(requestUrl, now))
            {
                if (!result.ContainsKey(stored.Name))
                {
                    result[stored.Name] = stored.Value;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Enumerates every cookie currently stored in the per-audit jar
    /// (read surface for the WP-09-03 authenticator and diagnostics), in
    /// deterministic domain/path/name order. Expired cookies are excluded.
    /// </summary>
    /// <returns>Snapshot of the stored cookies.</returns>
    public IReadOnlyList<CookieRecord> GetCookies()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return _cookies.Values
                .Where(c => c.ExpiresAt is null || c.ExpiresAt > now)
                .OrderBy(c => c.Domain, StringComparer.Ordinal)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new CookieRecord(
                    c.Name, c.Value, c.Domain, c.Path, c.HostOnly, c.Secure, c.HttpOnly,
                    c.SameSite, c.ExpiresAt, c.ExpiresAt is null, c.Source, c.CreatedAt))
                .ToList();
        }
    }

    /// <summary>
    /// Cookie-security observations captured for the D2 rules (data only —
    /// the jar emits no findings; see the WP-09-02 handoff notation gap on
    /// the missing Security finding category).
    /// </summary>
    public IReadOnlyList<CookieSecurityObservation> SecurityObservations
    {
        get
        {
            lock (_gate)
            {
                return _securityObservations.ToList();
            }
        }
    }

    /// <summary>
    /// Records one response content hash for a URL. When the hash differs from
    /// the earlier observation and cookies were set in between, the change is
    /// recorded (§5.11: "when a URL's response changes after a cookie was set,
    /// the change is recorded").
    /// </summary>
    /// <param name="url">The fetched URL.</param>
    /// <param name="contentHash">Stable content hash of the response body.</param>
    public void NoteResponse(string url, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        lock (_gate)
        {
            if (!_responseBaselines.TryGetValue(url, out var baseline))
            {
                _responseBaselines[url] = new ResponseBaseline(contentHash, _generation);
                return;
            }

            if (string.Equals(baseline.ContentHash, contentHash, StringComparison.Ordinal)
                || _generation <= baseline.Generation)
            {
                return;
            }

            var names = _setEvents
                .Where(e => e.Generation > baseline.Generation)
                .Select(e => e.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            _responseChanges.Add(new CookieResponseChange(url, baseline.ContentHash, contentHash, names));
            _responseBaselines[url] = new ResponseBaseline(contentHash, _generation);
        }
    }

    /// <summary>Recorded response changes after cookie set (§5.11).</summary>
    public IReadOnlyList<CookieResponseChange> ResponseChanges
    {
        get
        {
            lock (_gate)
            {
                return _responseChanges.ToList();
            }
        }
    }

    /// <summary>Drops every stored cookie and observation (audit teardown).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cookies.Clear();
            _setEvents.Clear();
            _securityObservations.Clear();
            _responseBaselines.Clear();
            _responseChanges.Clear();
            _generation = 0;
        }
    }

    private IEnumerable<StoredCookie> MatchingCookies(Uri requestUrl, DateTimeOffset now)
    {
        var host = requestUrl.Host.ToLowerInvariant();
        var path = requestUrl.AbsolutePath;
        var isHttps = string.Equals(requestUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        return _cookies.Values
            .Where(c => _options.Enabled || c.Source != CookieSource.Site)
            .Where(c => c.ExpiresAt is null || c.ExpiresAt > now)
            .Where(c => !c.Secure || isHttps)
            .Where(c => DomainMatches(c, host))
            .Where(c => PathMatches(c.Path, path))
            .OrderByDescending(c => c.Path.Length)
            .ThenBy(c => c.CreationOrdinal)
            .ToList();
    }

    private static bool DomainMatches(StoredCookie cookie, string host) =>
        cookie.HostOnly
            ? string.Equals(cookie.Domain, host, StringComparison.Ordinal)
            : string.Equals(cookie.Domain, host, StringComparison.Ordinal)
              || host.EndsWith("." + cookie.Domain, StringComparison.Ordinal);

    private static bool PathMatches(string cookiePath, string requestPath)
    {
        if (string.Equals(cookiePath, requestPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            return false;
        }

        return cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/';
    }

    private bool TryParseAndStore(Uri responseUrl, string header, DateTimeOffset now, CookieSource source)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var parts = header.Split(';');
        var nameValue = parts[0];
        var separator = nameValue.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var name = nameValue[..separator].Trim();
        var value = nameValue[(separator + 1)..].Trim();
        if (name.Length == 0)
        {
            return false;
        }

        string? domainAttribute = null;
        string? pathAttribute = null;
        DateTimeOffset? expires = null;
        long? maxAge = null;
        var secure = false;
        var httpOnly = false;
        string? sameSite = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var attribute = parts[i].Trim();
            if (attribute.Length == 0)
            {
                continue;
            }

            var eq = attribute.IndexOf('=');
            var key = (eq < 0 ? attribute : attribute[..eq]).Trim().ToLowerInvariant();
            var attributeValue = eq < 0 ? string.Empty : attribute[(eq + 1)..].Trim();
            switch (key)
            {
                case "domain":
                    domainAttribute = attributeValue.TrimStart('.').ToLowerInvariant();
                    break;
                case "path":
                    pathAttribute = attributeValue;
                    break;
                case "expires":
                    if (DateTimeOffset.TryParse(
                            attributeValue,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        expires = parsed;
                    }

                    break;
                case "max-age":
                    if (long.TryParse(attributeValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        maxAge = seconds;
                    }

                    break;
                case "secure":
                    secure = true;
                    break;
                case "httponly":
                    httpOnly = true;
                    break;
                case "samesite":
                    sameSite = attributeValue.ToLowerInvariant();
                    break;
            }
        }

        var host = responseUrl.Host.ToLowerInvariant();
        var hostOnly = string.IsNullOrEmpty(domainAttribute);
        var domain = hostOnly ? host : domainAttribute!;

        // A Domain attribute must domain-match the response host; browsers
        // reject cookies scoped to unrelated hosts (RFC 6265 §5.3).
        if (!hostOnly
            && !string.Equals(domain, host, StringComparison.Ordinal)
            && !host.EndsWith("." + domain, StringComparison.Ordinal))
        {
            return false;
        }

        var path = !string.IsNullOrEmpty(pathAttribute) && pathAttribute.StartsWith('/')
            ? pathAttribute
            : DefaultPath(responseUrl.AbsolutePath);

        // Max-Age wins over Expires (RFC 6265 §5.3).
        DateTimeOffset? expiresAt = maxAge is { } age
            ? now.AddSeconds(age)
            : expires;

        // Cookie-prefix hardening (RFC 6265bis): __Secure- requires Secure;
        // __Host- requires Secure, host-only scope and Path=/.
        if (name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase) && !secure)
        {
            return false;
        }

        if (name.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase)
            && (!secure || !hostOnly || path != "/"))
        {
            return false;
        }

        var cookieKey = new CookieKey(domain, path, name);
        if (expiresAt is { } expiry && expiry <= now)
        {
            // Expired cookie: delete any stored instance, store nothing.
            return _cookies.Remove(cookieKey);
        }

        var creationOrdinal = _cookies.TryGetValue(cookieKey, out var existing)
            ? existing.CreationOrdinal
            : Interlocked.Increment(ref _creationCounter);
        var createdAt = _cookies.TryGetValue(cookieKey, out var prior) ? prior.CreatedAt : now;

        _cookies[cookieKey] = new StoredCookie(
            name, value, domain, path, hostOnly, secure, httpOnly, sameSite, expiresAt, source, createdAt, creationOrdinal);
        _setEvents.Add(new SetEvent(_generation + 1, name));
        _securityObservations.Add(new CookieSecurityObservation(
            name, domain, path, secure, httpOnly, sameSite, expiresAt is null, expiresAt, responseUrl.AbsoluteUri, now));
        return true;
    }

    private static string DefaultPath(string requestPath)
    {
        if (requestPath.Length == 0 || requestPath[0] != '/')
        {
            return "/";
        }

        var lastSlash = requestPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : requestPath[..lastSlash];
    }

    private readonly record struct CookieKey(string Domain, string Path, string Name);

    private sealed record StoredCookie(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool HostOnly,
        bool Secure,
        bool HttpOnly,
        string? SameSite,
        DateTimeOffset? ExpiresAt,
        CookieSource Source,
        DateTimeOffset CreatedAt,
        long CreationOrdinal);

    private sealed record SetEvent(long Generation, string Name);

    private sealed record ResponseBaseline(string ContentHash, long Generation);
}
