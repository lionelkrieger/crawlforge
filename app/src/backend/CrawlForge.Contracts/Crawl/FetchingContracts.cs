namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Fetches URLs over HTTP(S) toward user-configured crawl targets only
/// (network boundary, FS-CRAWL-001 §2.6). Records timing for politeness and
/// performance analysis.
/// </summary>
public interface IHttpFetcher
{
    /// <summary>Fetches a URL and returns status, headers, body, and timing.</summary>
    /// <param name="request">Fetch request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fetch result.</returns>
    Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// An outbound fetch request. Optional headers/cookies come from the crawl
/// configuration; sensitive values are user-owned and stored encrypted at rest.
/// </summary>
/// <param name="Url">Target URL.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Headers">Optional request headers.</param>
/// <param name="Cookies">Optional request cookies.</param>
/// <param name="TimeoutSeconds">Optional per-request timeout.</param>
/// <param name="FollowRedirects">Whether redirects are followed automatically.</param>
public sealed record HttpFetchRequest(
    string Url,
    string Method = "GET",
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? Cookies = null,
    int? TimeoutSeconds = null,
    bool FollowRedirects = true);

/// <summary>
/// Result of one fetch. The body is a bounded per-page payload; bulk streaming
/// patterns apply to stores and imports, not to single-page fetches.
/// </summary>
/// <param name="FinalUrl">URL after redirects.</param>
/// <param name="StatusCode">Final HTTP status code.</param>
/// <param name="Headers">Final response headers (multi-values joined).</param>
/// <param name="Body">Response body bytes.</param>
/// <param name="Ttfb">Time to first byte.</param>
/// <param name="TotalDuration">Total fetch duration.</param>
public sealed record HttpFetchResult(
    string FinalUrl,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    TimeSpan Ttfb,
    TimeSpan TotalDuration);

/// <summary>
/// Records redirect hop chains per URL (FS-CRAWL-001 §5.4) for the URL detail
/// surface and the migration redirect-matrix validator.
/// </summary>
public interface IRedirectChainRecorder
{
    /// <summary>Appends one hop to a URL's redirect chain.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="hop">Observed hop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the hop is recorded.</returns>
    Task RecordHopAsync(long auditId, long urlId, RedirectHop hop, CancellationToken cancellationToken = default);

    /// <summary>Returns the recorded redirect chain of a URL in hop order.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordered redirect chain.</returns>
    Task<IReadOnlyList<RedirectHop>> GetChainAsync(long auditId, long urlId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One hop of a redirect chain.
/// </summary>
/// <param name="Ordinal">Zero-based hop position.</param>
/// <param name="FromUrl">URL that issued the redirect.</param>
/// <param name="ToUrl">Redirect target URL.</param>
/// <param name="StatusCode">Redirect status code (3xx).</param>
public sealed record RedirectHop(int Ordinal, string FromUrl, string ToUrl, int StatusCode);

/// <summary>
/// Applies user-configured request signing to outbound crawl requests
/// (FS-CRAWL-001 v1.4 signed-request crawler access). Signing secrets are
/// user-owned, stored encrypted at rest, and used solely for the user's own
/// target sites — never identity-bearing vendor headers (exclusion row 2).
/// </summary>
public interface ISignedRequestHandler
{
    /// <summary>Returns a copy of the request with signature headers applied.</summary>
    /// <param name="request">Original request.</param>
    /// <param name="secret">User-configured signing secret.</param>
    /// <returns>The signed request.</returns>
    HttpFetchRequest Sign(HttpFetchRequest request, SignedRequestSecret secret);
}

/// <summary>
/// A user-configured secret for signed requests (encrypted at rest).
/// </summary>
/// <param name="KeyId">Identifier of the signing key.</param>
/// <param name="Secret">Secret key material.</param>
/// <param name="Algorithm">Signature algorithm identifier.</param>
/// <param name="HeaderName">Header carrying the signature.</param>
public sealed record SignedRequestSecret(
    string KeyId,
    string Secret,
    string Algorithm,
    string HeaderName);

/// <summary>
/// Validates start URLs before a crawl begins (FS-CRAWL-001 v1.4 pre-crawl
/// start-URL validation): reachability, redirects, and basic indexability.
/// </summary>
public interface IPreCrawlValidator
{
    /// <summary>Validates a candidate start URL.</summary>
    /// <param name="startUrl">Candidate start URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation verdict with issues.</returns>
    Task<PreCrawlValidationResult> ValidateAsync(string startUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of pre-crawl start-URL validation.
/// </summary>
/// <param name="IsValid">Whether the URL is usable as a crawl seed.</param>
/// <param name="Issues">Human-readable issues found during validation.</param>
/// <param name="FinalUrl">Final URL after redirects, when reachable.</param>
/// <param name="StatusCode">Observed status code, when reachable.</param>
public sealed record PreCrawlValidationResult(
    bool IsValid,
    IReadOnlyList<string> Issues,
    string? FinalUrl,
    int? StatusCode);
