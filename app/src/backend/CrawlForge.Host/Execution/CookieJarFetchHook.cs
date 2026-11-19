using System.Collections.Concurrent;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Host.Execution;

/// <summary>
/// WP-09-11 wiring of the accepted per-audit cookie jar (WP-09-02) into the
/// fetch transport through the locked <see cref="IFetchHook"/> surface:
/// merges the jar's Cookie header into every outgoing request (user-supplied
/// credentials win on name conflicts because they were merged first), captures
/// Set-Cookie from every response — including redirect hops — and allows ONE
/// cookie-driven revisit per URL so a cookie gate (302 to self with Set-Cookie,
/// then 200) resolves instead of failing as a redirect loop.
/// <para>
/// Production counterpart of the WP-09-02 test-support reference hook; the
/// storage/expiration semantics live in the accepted <see cref="AuditCookieJar"/>
/// (RFC 6265), not here. Thread-safe: the jar guards its own state; the revisit
/// budget is a concurrent dictionary. All state is local to the audit run.
/// </para>
/// </summary>
public sealed class CookieJarFetchHook : IFetchHook
{
    private readonly AuditCookieJar _jar;
    private readonly ConcurrentDictionary<string, int> _revisits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the hook over the per-audit jar.</summary>
    /// <param name="jar">The audit's cookie jar (enabled or cookieless).</param>
    public CookieJarFetchHook(AuditCookieJar jar)
    {
        _jar = jar ?? throw new ArgumentNullException(nameof(jar));
    }

    /// <inheritdoc />
    public ValueTask BeforeSendAsync(HttpRequestMessage request, FetchHookContext context, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            return ValueTask.CompletedTask;
        }

        var jarHeader = _jar.GetCookieHeader(request.RequestUri);
        string? existing = request.Headers.TryGetValues("Cookie", out var values)
            ? string.Join("; ", values)
            : null;

        var merged = (existing, jarHeader) switch
        {
            (null, null) => null,
            (null, _) => jarHeader,
            (_, null) => existing,
            _ => existing + "; " + jarHeader,
        };

        if (merged is not null)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", merged);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AfterReceiveAsync(HttpResponseMessage response, FetchHookContext context, CancellationToken cancellationToken)
    {
        // The hop URL comes from the context (response.RequestMessage is not
        // guaranteed to be populated by every transport).
        if (Uri.TryCreate(context.CurrentUrl, UriKind.Absolute, out var currentUri)
            && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            _jar.SetFromResponse(currentUri, setCookies);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public bool AllowRevisit(FetchHookContext context)
    {
        // One cookie-driven revisit per URL: the previous response must have
        // set a cookie, and the jar must now hold a cookie for the revisited
        // URL (the gate cookie that resolves the self-redirect).
        if (context.PreviousResponse is null
            || !context.PreviousResponse.Headers.ContainsKey("Set-Cookie")
            || !Uri.TryCreate(context.CurrentUrl, UriKind.Absolute, out var uri)
            || _jar.GetCookieHeader(uri) is null)
        {
            return false;
        }

        return _revisits.AddOrUpdate(context.CurrentUrl, 1, (_, count) => count + 1) == 1;
    }
}