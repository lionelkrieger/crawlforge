namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Immutable snapshot of a redirect response, offered to
/// <see cref="IFetchHook.AllowRevisit"/> so a hook can decide whether a
/// revisited URL is legitimate (e.g. a cookie gate that sets a cookie and
/// redirects to itself once). Snapshots avoid response-lifetime coupling:
/// the live response is disposed at the end of its hop.
/// </summary>
/// <param name="StatusCode">HTTP status of the response.</param>
/// <param name="Headers">Flattened response headers (multi-values joined).</param>
public sealed record FetchResponseSnapshot(int StatusCode, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Ambient context offered to <see cref="IFetchHook"/> at every fetch step.
/// Audit/URL identifiers come from <see cref="FetchContextScope"/> when the
/// crawl worker set it; they are null for infrastructure fetches (robots.txt,
/// sitemap documents, pre-crawl probes).
/// </summary>
/// <param name="AuditId">Owning audit identifier, when a fetch context is set.</param>
/// <param name="UrlId">Owning url_records identifier, when a fetch context is set.</param>
/// <param name="OriginalUrl">URL the fetch was issued for (before any redirect).</param>
/// <param name="CurrentUrl">URL of the current hop (before hook rewriting).</param>
/// <param name="HopOrdinal">Zero-based hop position.</param>
/// <param name="PreviousResponse">
/// Snapshot of the redirect response that led to this request; null on the
/// first hop. Used by <see cref="IFetchHook.AllowRevisit"/>.
/// </param>
public sealed record FetchHookContext(
    long? AuditId,
    long? UrlId,
    string OriginalUrl,
    string CurrentUrl,
    int HopOrdinal,
    FetchResponseSnapshot? PreviousResponse);

/// <summary>
/// Generic per-hop fetch interception point (WP-09-01 sibling integration
/// surface). Composition injects ONE optional hook into <see cref="HttpFetcher"/>;
/// when absent, fetch behavior is byte-for-byte identical to the unhooked
/// fetcher. Intended consumers (implemented by their owning workpieces, NOT
/// here):
/// <list type="bullet">
/// <item>cf-wp-09-02 per-audit cookie jar: add the jar's Cookie header in
/// <see cref="BeforeSendAsync"/> (merge with any existing header), capture
/// Set-Cookie from every response in <see cref="AfterReceiveAsync"/>
/// (including redirect hops), and allow one cookie-driven revisit in
/// <see cref="AllowRevisit"/> so a cookie gate returns 200 after one
/// self-redirect.</item>
/// <item>cf-wp-09-03 URL rewrite / host-mapping at fetch time: rewrite
/// <c>request.RequestUri</c> in <see cref="BeforeSendAsync"/> (redirect
/// recording keeps the original crawl URL). Signed-request headers already
/// flow through the existing <c>ISignedRequestHandler</c> constructor
/// dependency; upstream proxy routing flows through the existing
/// <c>ILoopbackInterceptingProxy</c> dependency or the fetcher's optional
/// <c>httpClientFactory</c> constructor parameter.</item>
/// </list>
/// All members have no-op defaults; implementations override only what they
/// need. Hooks must be thread-safe (the crawl pipeline fetches concurrently).
/// </summary>
public interface IFetchHook
{
    /// <summary>
    /// Called after the request message is built (headers/cookies applied)
    /// and before it is sent, on every hop and every retry attempt. Mutate
    /// the message in place (e.g. add cookies, rewrite the request URI).
    /// </summary>
    /// <param name="request">The outgoing request message.</param>
    /// <param name="context">Fetch context of the current hop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when interception is done.</returns>
    ValueTask BeforeSendAsync(HttpRequestMessage request, FetchHookContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Called immediately after a response arrives, BEFORE redirect following
    /// and the size guard — so Set-Cookie on 3xx hops is observable. The
    /// response is disposed by the fetcher after the hop completes; do not
    /// retain it beyond this call. Derive the hop host from
    /// <c>context.CurrentUrl</c> — <c>response.RequestMessage</c> is not
    /// guaranteed to be populated by every transport.
    /// </summary>
    /// <param name="response">The received response message.</param>
    /// <param name="context">Fetch context of the current hop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when interception is done.</returns>
    ValueTask AfterReceiveAsync(HttpResponseMessage response, FetchHookContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Called when the current hop's URL was already visited in this fetch
    /// (the fetcher's loop guard). Return true to allow the revisit (e.g. a
    /// cookie gate: the previous response set a cookie the jar now holds).
    /// The default rejects every revisit, preserving the strict loop guard.
    /// </summary>
    /// <param name="context">Fetch context; <c>PreviousResponse</c> carries the redirect response that revisited the URL.</param>
    /// <returns>True to allow the revisit; false to fail as a redirect loop.</returns>
    bool AllowRevisit(FetchHookContext context) => false;
}
