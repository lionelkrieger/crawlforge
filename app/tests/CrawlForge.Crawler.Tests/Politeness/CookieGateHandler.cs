using System.Collections.Concurrent;
using System.Net;

namespace CrawlForge.Crawler.Tests.Politeness;

/// <summary>
/// Scripted HttpMessageHandler double emulating the fixture-site cookie-gate
/// trap (server.mjs): /cookie-gate/ answers 302 + Set-Cookie fixture_gate=1
/// until the request carries the cookie, then 200. Thread-safe: the pipeline
/// runs fetch workers concurrently (MaxConcurrency=2). No network is touched.
/// </summary>
internal sealed class CookieGateHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<string?> _observedCookies = new();

    /// <summary>Cookie header values observed on each request, in order.</summary>
    public IReadOnlyList<string?> ObservedCookies => _observedCookies.ToArray();

    /// <summary>Number of gate requests served.</summary>
    public int RequestCount => _observedCookies.Count;

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("Cookie", out var cookies);
        var cookieHeader = cookies is null ? null : string.Join("; ", cookies);
        _observedCookies.Enqueue(cookieHeader);

        if (request.RequestUri!.AbsolutePath == "/cookie-gate/"
            && cookieHeader is not null
            && cookieHeader.Contains("fixture_gate=1", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head><title>Cookie gate passed</title></head><body>Cookie gate passed</body></html>"),
                RequestMessage = request
            });
        }

        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = request
        };
        redirect.Headers.Location = new Uri("/cookie-gate/", UriKind.Relative);
        redirect.Headers.TryAddWithoutValidation("Set-Cookie", "fixture_gate=1; Path=/");
        return Task.FromResult(redirect);
    }
}
