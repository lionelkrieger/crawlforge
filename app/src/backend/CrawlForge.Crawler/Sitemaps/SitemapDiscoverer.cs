using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Discovers XML sitemap locations for a crawl origin (FS-CRAWL-001 §5.1
/// "Sitemap seeds" input; CAP-A-008): robots.txt <c>Sitemap:</c> directives,
/// the <c>/sitemap.xml</c> default-location probe, and user-supplied sitemap
/// seeds. Outbound traffic goes only to the crawl target origin (network
/// boundary §2.6). Discovery is tolerant: unreachable robots.txt imposes no
/// restriction (RFC 9309) and probe misses become diagnostics, never audit
/// failures. Stateless per call; safe as a singleton.
/// </summary>
public sealed class SitemapDiscoverer
{
    private readonly IHttpFetcher _fetcher;
    private readonly IRobotsTxtParser _robotsParser;
    private readonly IUrlCanonicalizer _canonicalizer;

    /// <summary>Creates the discoverer over the locked fetch/robots/canonicalization contracts.</summary>
    /// <param name="fetcher">HTTP fetcher toward the crawl target.</param>
    /// <param name="robotsParser">Tolerant robots.txt parser (Sitemap directives).</param>
    /// <param name="canonicalizer">Canonical URL keying for deduplication (§5.2).</param>
    public SitemapDiscoverer(IHttpFetcher fetcher, IRobotsTxtParser robotsParser, IUrlCanonicalizer canonicalizer)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _robotsParser = robotsParser ?? throw new ArgumentNullException(nameof(robotsParser));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
    }

    /// <summary>
    /// Discovers sitemap locations for the origin of <paramref name="seedUrl"/>.
    /// </summary>
    /// <param name="seedUrl">A crawl seed URL; its scheme://host:port defines the origin.</param>
    /// <param name="robots">
    /// Already-fetched robots document for the origin, when the caller has one
    /// (the engine caches robots per host). When null, robots.txt is fetched.
    /// </param>
    /// <param name="configuredSitemapSeeds">User-supplied sitemap URLs (§5.1), when configured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered sitemaps in deterministic order plus diagnostics.</returns>
    /// <exception cref="ArgumentException">The seed URL is not an absolute http(s) URL.</exception>
    public async Task<SitemapDiscoveryResult> DiscoverAsync(
        string seedUrl,
        RobotsDocument? robots = null,
        IReadOnlyList<string>? configuredSitemapSeeds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out Uri? seedUri)
            || (seedUri.Scheme != Uri.UriSchemeHttp && seedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Seed URL '{seedUrl}' is not an absolute http(s) URL.", nameof(seedUrl));
        }

        var origin = seedUri.GetLeftPart(UriPartial.Authority);
        var robotsUrl = origin + "/robots.txt";
        var diagnostics = new List<string>();

        if (robots is null)
        {
            robots = await FetchRobotsAsync(robotsUrl, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        var candidates = new List<DiscoveredSitemap>();
        if (robots is not null)
        {
            foreach (var declared in robots.Sitemaps)
            {
                if (TryResolveHttpUrl(declared, robotsUrl, out var resolved))
                {
                    candidates.Add(new DiscoveredSitemap(resolved, SitemapSourceKind.RobotsDirective));
                }
                else
                {
                    diagnostics.Add($"Ignored robots.txt Sitemap directive '{declared}': not an http(s) URL.");
                }
            }
        }

        await ProbeDefaultSitemapAsync(origin, candidates, diagnostics, cancellationToken).ConfigureAwait(false);

        if (configuredSitemapSeeds is not null)
        {
            foreach (var configured in configuredSitemapSeeds)
            {
                if (TryResolveHttpUrl(configured, origin + "/", out var resolved))
                {
                    candidates.Add(new DiscoveredSitemap(resolved, SitemapSourceKind.ConfiguredSeed));
                }
                else
                {
                    diagnostics.Add($"Ignored configured sitemap seed '{configured}': not an http(s) URL.");
                }
            }
        }

        var deduplicated = new List<DiscoveredSitemap>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = _canonicalizer.Canonicalize(candidate.Url);
            if (seen.Add(key))
            {
                deduplicated.Add(candidate);
            }
        }

        return new SitemapDiscoveryResult(deduplicated, diagnostics, robots);
    }

    private async Task<RobotsDocument?> FetchRobotsAsync(string robotsUrl, List<string> diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            HttpFetchResult result = await _fetcher
                .FetchAsync(new HttpFetchRequest(robotsUrl, TimeoutSeconds: 30), cancellationToken)
                .ConfigureAwait(false);
            if (result.StatusCode == 200)
            {
                return _robotsParser.Parse(System.Text.Encoding.UTF8.GetString(result.Body.Span));
            }

            diagnostics.Add($"robots.txt at '{robotsUrl}' returned HTTP {result.StatusCode}; no Sitemap directives available.");
            return null;
        }
        catch (FetchFailedException ex)
        {
            // RFC 9309: an unreachable robots.txt imposes no restriction.
            diagnostics.Add($"robots.txt at '{robotsUrl}' is unreachable ({ex.Kind}); no Sitemap directives available.");
            return null;
        }
    }

    private async Task ProbeDefaultSitemapAsync(string origin, List<DiscoveredSitemap> candidates, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var probeUrl = origin + "/sitemap.xml";
        try
        {
            HttpFetchResult result = await _fetcher
                .FetchAsync(new HttpFetchRequest(probeUrl, TimeoutSeconds: 30), cancellationToken)
                .ConfigureAwait(false);
            if (result.StatusCode == 200)
            {
                candidates.Add(new DiscoveredSitemap(probeUrl, SitemapSourceKind.DefaultProbe));
            }
            else
            {
                diagnostics.Add($"Default sitemap probe '{probeUrl}' returned HTTP {result.StatusCode}.");
            }
        }
        catch (FetchFailedException ex)
        {
            diagnostics.Add($"Default sitemap probe '{probeUrl}' failed ({ex.Kind}).");
        }
    }

    private static bool TryResolveHttpUrl(string value, string baseUrl, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(new Uri(baseUrl), value.Trim(), out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        resolved = uri.AbsoluteUri;
        return true;
    }
}
