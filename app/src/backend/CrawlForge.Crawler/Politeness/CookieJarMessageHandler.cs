namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Fetcher integration hook for the per-audit cookie jar (FS-CRAWL-001
/// §5.11): a delegating handler that attaches the jar's cookies to every
/// outbound request and captures Set-Cookie headers from every response —
/// including intermediate redirect responses, because the crawl fetcher
/// follows redirects hop-by-hop (AllowAutoRedirect=false). Wire one handler
/// per audit into the crawl HttpClient's handler chain:
/// <c>new CookieJarMessageHandler(jar, innerHandler)</c>. The fetcher folder
/// (WP-09-01) owns the integration; this component is self-contained and
/// tested with scripted handler doubles.
/// </summary>
public sealed class CookieJarMessageHandler : DelegatingHandler
{
    private readonly AuditCookieJar _jar;

    /// <summary>Creates the handler over one per-audit jar.</summary>
    /// <param name="jar">The per-audit cookie jar.</param>
    /// <param name="innerHandler">The transport handler (HttpClientHandler or a test double).</param>
    public CookieJarMessageHandler(AuditCookieJar jar, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(jar);
        _jar = jar;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
        {
            var cookieHeader = _jar.GetCookieHeader(uri);
            if (cookieHeader is not null)
            {
                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.RequestUri is { } responseUri
            && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            _jar.SetFromResponse(responseUri, setCookies);
        }

        return response;
    }
}
