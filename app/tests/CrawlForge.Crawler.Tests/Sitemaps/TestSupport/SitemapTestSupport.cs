using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;

namespace CrawlForge.Crawler.Tests.Sitemaps.TestSupport;

/// <summary>
/// Route-based fake transport for sitemap component tests: deterministic
/// responses keyed by absolute URL (order-independent), recording every
/// request. No network is touched.
/// </summary>
internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public RoutingHttpMessageHandler Route(string absoluteUrl, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _routes[absoluteUrl] = factory;
        return this;
    }

    public RoutingHttpMessageHandler Route(string absoluteUrl, int statusCode, string? body = null, string? contentType = null, string? location = null) =>
        Route(absoluteUrl, _ => ScriptedHttpMessageHandler.Response(statusCode, body, contentType, location));

    public RoutingHttpMessageHandler RouteException(string absoluteUrl, Exception exception) =>
        Route(absoluteUrl, _ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(request);
        }

        var key = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (_routes.TryGetValue(key, out var factory))
        {
            return Task.FromResult(factory(request));
        }

        if (Fallback is not null)
        {
            return Task.FromResult(Fallback(request));
        }

        throw new InvalidOperationException($"No scripted route for '{key}'.");
    }
}

/// <summary>Shared construction helpers and sitemap body builders.</summary>
internal static class SitemapTestSupport
{
    public const string Base = "http://site.example";

    public static HttpFetcher CreateFetcher(HttpMessageHandler handler) =>
        new(new HttpFetcherOptions(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1), new HttpClient(handler));

    public static SitemapDiscoverer CreateDiscoverer(HttpMessageHandler handler) =>
        new(CreateFetcher(handler), new RobotsTxtParser(), new UrlCanonicalizer());

    public static SitemapIngestor CreateIngestor(HttpMessageHandler handler, SitemapIngestionOptions? options = null) =>
        new(CreateFetcher(handler), new RobotsTxtParser(), new UrlCanonicalizer(), options);

    public static string Robots(string? sitemap = null) =>
        "User-agent: *\nDisallow:\n" + (sitemap is null ? string.Empty : $"Sitemap: {sitemap}\n");

    public static string UrlSet(params string[] locs) =>
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(locs.Select(l => $"<url><loc>{l}</loc></url>")) +
        "</urlset>";

    public static string UrlSetWithMetadata(string loc, string lastmod, string changefreq, string priority) =>
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        $"<url><loc>{loc}</loc><lastmod>{lastmod}</lastmod><changefreq>{changefreq}</changefreq><priority>{priority}</priority></url>" +
        "</urlset>";

    public static string Index(params string[] locs) =>
        "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(locs.Select(l => $"<sitemap><loc>{l}</loc></sitemap>")) +
        "</sitemapindex>";
}
