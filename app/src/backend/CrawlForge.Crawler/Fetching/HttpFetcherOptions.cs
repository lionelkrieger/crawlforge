namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Local fetcher configuration (FS-CRAWL-001 §5.4): timeouts, retry budget with
/// exponential backoff, response size guard, and the redirect hop ceiling.
/// All values are local configuration — nothing is resolved remotely and no
/// limit varies with license/account input (exclusion rows 5/8). Effective
/// crawl ceilings still flow through the standard limits resolution; these
/// options govern the HTTP mechanics of a single fetch.
/// </summary>
public sealed class HttpFetcherOptions
{
    /// <summary>Creates fetcher options, validating every bound.</summary>
    /// <param name="defaultTimeoutSeconds">Per-hop timeout when the request carries none (at least 1).</param>
    /// <param name="maxRetryAttempts">Transient-failure retry budget after the first attempt (at least 0).</param>
    /// <param name="retryBackoffBaseMilliseconds">Exponential backoff base in milliseconds (at least 0).</param>
    /// <param name="maxResponseBytes">Response body size guard in bytes (at least 1).</param>
    /// <param name="maxRedirectHops">Redirect hop ceiling per fetch (at least 0).</param>
    /// <param name="userAgent">User-agent token sent when the request carries none.</param>
    /// <exception cref="ArgumentOutOfRangeException">A numeric bound is invalid.</exception>
    /// <exception cref="ArgumentException">The user agent is empty.</exception>
    public HttpFetcherOptions(
        int defaultTimeoutSeconds = 30,
        int maxRetryAttempts = 2,
        int retryBackoffBaseMilliseconds = 250,
        long maxResponseBytes = 10L * 1024 * 1024,
        int maxRedirectHops = 10,
        string userAgent = "CrawlForge/0.0.1")
    {
        if (defaultTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTimeoutSeconds), defaultTimeoutSeconds, "Timeout must be at least 1 second.");
        }

        if (maxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryAttempts), maxRetryAttempts, "The retry budget cannot be negative.");
        }

        if (retryBackoffBaseMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBackoffBaseMilliseconds), retryBackoffBaseMilliseconds, "The backoff base cannot be negative.");
        }

        if (maxResponseBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), maxResponseBytes, "The size guard must be positive.");
        }

        if (maxRedirectHops < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRedirectHops), maxRedirectHops, "The hop ceiling cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            throw new ArgumentException("The user agent must be a non-empty token.", nameof(userAgent));
        }

        DefaultTimeoutSeconds = defaultTimeoutSeconds;
        MaxRetryAttempts = maxRetryAttempts;
        RetryBackoffBaseMilliseconds = retryBackoffBaseMilliseconds;
        MaxResponseBytes = maxResponseBytes;
        MaxRedirectHops = maxRedirectHops;
        UserAgent = userAgent;
    }

    /// <summary>Per-hop timeout in seconds when the request carries none.</summary>
    public int DefaultTimeoutSeconds { get; }

    /// <summary>Transient-failure retry budget after the first attempt.</summary>
    public int MaxRetryAttempts { get; }

    /// <summary>Exponential backoff base in milliseconds (base * 2^attempt).</summary>
    public int RetryBackoffBaseMilliseconds { get; }

    /// <summary>Response body size guard in bytes; larger bodies fail as oversize.</summary>
    public long MaxResponseBytes { get; }

    /// <summary>Redirect hop ceiling per fetch; exceeding it fails the fetch.</summary>
    public int MaxRedirectHops { get; }

    /// <summary>User-agent token sent when the request carries none.</summary>
    public string UserAgent { get; }
}