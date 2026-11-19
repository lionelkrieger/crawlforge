using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Sitemap ingestion input (FS-CRAWL-001 §5.1/§5.5): crawl seeds define the
/// origins to discover sitemaps for; configured sitemap seeds are expanded
/// additionally; already-cached robots documents avoid re-fetching.
/// </summary>
/// <param name="AuditId">Owning audit identifier (diagnostics context).</param>
/// <param name="SeedUrls">Crawl seed URLs; their origins are discovered.</param>
/// <param name="ConfiguredSitemapSeeds">User-supplied sitemap URLs (§5.1), when configured.</param>
/// <param name="RobotsByHost">
/// Cached robots documents keyed by host (the engine's per-host robots cache),
/// when available. Hosts without an entry trigger a robots.txt fetch.
/// </param>
public sealed record SitemapIngestionRequest(
    long AuditId,
    IReadOnlyList<string> SeedUrls,
    IReadOnlyList<string>? ConfiguredSitemapSeeds = null,
    IReadOnlyDictionary<string, RobotsDocument>? RobotsByHost = null);

/// <summary>
/// Ingests XML sitemaps into crawl-ready URL seeds (CAP-A-008, FS-CRAWL-001
/// §5.4 content-type routing / §5.5 link discovery from sitemaps): discovery
/// (robots directives + default probe + configured seeds), recursive sitemap
/// index expansion with cycle and depth guards, canonicalized deduplication,
/// and per-URL discovery source sitemap. Bounded memory and fetch count
/// (scale invariant); tolerant of unreachable or hostile sitemap endpoints —
/// failures become diagnostics, never audit failures. Stateless per call;
/// safe as a singleton.
/// </summary>
public interface ISitemapIngestor
{
    /// <summary>Discovers and expands sitemaps for the request's crawl origins.</summary>
    /// <param name="request">Ingestion input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Seeded URLs (discovery source sitemap), traversed sitemaps, diagnostics.</returns>
    Task<SitemapIngestionResult> IngestAsync(SitemapIngestionRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISitemapIngestor"/>
public sealed class SitemapIngestor : ISitemapIngestor
{
    private readonly IHttpFetcher _fetcher;
    private readonly IUrlCanonicalizer _canonicalizer;
    private readonly SitemapDiscoverer _discoverer;
    private readonly SitemapIngestionOptions _options;

    /// <summary>Creates the ingestor over the locked fetch/canonicalization contracts.</summary>
    /// <param name="fetcher">HTTP fetcher toward the crawl target.</param>
    /// <param name="robotsParser">Tolerant robots.txt parser.</param>
    /// <param name="canonicalizer">Canonical URL keying (§5.2).</param>
    /// <param name="options">Ingestion bounds; defaults when null.</param>
    public SitemapIngestor(
        IHttpFetcher fetcher,
        IRobotsTxtParser robotsParser,
        IUrlCanonicalizer canonicalizer,
        SitemapIngestionOptions? options = null)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
        _discoverer = new SitemapDiscoverer(fetcher, robotsParser, canonicalizer);
        _options = (options ?? new SitemapIngestionOptions()).Validate();
    }

    /// <inheritdoc />
    public async Task<SitemapIngestionResult> IngestAsync(SitemapIngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SeedUrls);

        var diagnostics = new List<string>();
        var sitemapQueue = new Queue<(string Url, SitemapSourceKind Source, int Depth)>();
        var queuedSitemaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traversed = new List<DiscoveredSitemap>();
        var seededUrls = new List<SitemapSeededUrl>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalUrlBoundReached = false;

        // Discovery per distinct seed origin, in first-seed order.
        var origins = new List<(string SeedUrl, Uri Uri)>();
        var seenOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in request.SeedUrls)
        {
            if (!Uri.TryCreate(seed, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                diagnostics.Add($"Ignored sitemap-discovery seed '{seed}': not an absolute http(s) URL.");
                continue;
            }

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (seenOrigins.Add(origin))
            {
                origins.Add((seed, uri));
            }
        }

        foreach (var (seedUrl, uri) in origins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RobotsDocument? robots = null;
            request.RobotsByHost?.TryGetValue(uri.Host, out robots);
            SitemapDiscoveryResult discovery = await _discoverer
                .DiscoverAsync(seedUrl, robots, request.ConfiguredSitemapSeeds, cancellationToken)
                .ConfigureAwait(false);
            diagnostics.AddRange(discovery.Diagnostics);
            foreach (var sitemap in discovery.Sitemaps)
            {
                var key = _canonicalizer.Canonicalize(sitemap.Url);
                if (queuedSitemaps.Add(key))
                {
                    sitemapQueue.Enqueue((sitemap.Url, sitemap.Source, Depth: 0));
                }
            }
        }

        // Breadth-first expansion of sitemap documents with cycle/depth/count guards.
        var fetchAttempts = 0;
        while (sitemapQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sitemapUrl, source, depth) = sitemapQueue.Dequeue();

            if (depth > _options.MaxIndexDepth)
            {
                diagnostics.Add($"Sitemap '{sitemapUrl}' exceeds the index recursion depth bound ({_options.MaxIndexDepth}); not expanded.");
                continue;
            }

            if (fetchAttempts >= _options.MaxSitemaps)
            {
                diagnostics.Add($"Sitemap ingestion stopped at the {_options.MaxSitemaps}-document bound; {sitemapQueue.Count + 1} sitemap(s) left unexpanded.");
                break;
            }

            fetchAttempts++;
            traversed.Add(new DiscoveredSitemap(sitemapUrl, source));

            string body;
            try
            {
                HttpFetchResult result = await _fetcher
                    .FetchAsync(new HttpFetchRequest(sitemapUrl, TimeoutSeconds: _options.FetchTimeoutSeconds), cancellationToken)
                    .ConfigureAwait(false);
                if (result.StatusCode != 200)
                {
                    diagnostics.Add($"Sitemap '{sitemapUrl}' returned HTTP {result.StatusCode}; not expanded.");
                    continue;
                }

                body = System.Text.Encoding.UTF8.GetString(result.Body.Span);
            }
            catch (FetchFailedException ex)
            {
                diagnostics.Add($"Sitemap '{sitemapUrl}' failed to fetch ({ex.Kind}); not expanded.");
                continue;
            }

            SitemapParseResult parsed = SitemapXmlParser.Parse(body, _options.MaxUrlsPerSitemap);
            foreach (var diagnostic in parsed.Diagnostics)
            {
                diagnostics.Add($"Sitemap '{sitemapUrl}': {diagnostic}");
            }

            if (parsed.Document is null)
            {
                continue;
            }

            if (parsed.Document.Kind == SitemapDocumentKind.UrlSet)
            {
                foreach (SitemapUrlEntry entry in parsed.Document.Urls)
                {
                    if (seenUrls.Count >= _options.MaxTotalUrls)
                    {
                        if (!totalUrlBoundReached)
                        {
                            diagnostics.Add($"Sitemap ingestion stopped collecting URLs at the {_options.MaxTotalUrls}-URL bound.");
                            totalUrlBoundReached = true;
                        }

                        break;
                    }

                    if (!TryResolveHttpUrl(entry.Url, sitemapUrl, out var resolved))
                    {
                        diagnostics.Add($"Sitemap '{sitemapUrl}': ignored <loc> '{entry.Url}' (not an http(s) URL).");
                        continue;
                    }

                    var canonical = _canonicalizer.Canonicalize(resolved);
                    if (!seenUrls.Add(canonical))
                    {
                        continue;
                    }

                    seededUrls.Add(new SitemapSeededUrl(
                        canonical,
                        new Uri(canonical).Host,
                        entry.Url,
                        sitemapUrl,
                        entry.LastModified,
                        entry.ChangeFrequency,
                        entry.Priority));
                }
            }
            else
            {
                foreach (var child in parsed.Document.ChildSitemapUrls)
                {
                    if (!TryResolveHttpUrl(child, sitemapUrl, out var resolved))
                    {
                        diagnostics.Add($"Sitemap index '{sitemapUrl}': ignored child <loc> '{child}' (not an http(s) URL).");
                        continue;
                    }

                    var key = _canonicalizer.Canonicalize(resolved);
                    if (queuedSitemaps.Add(key))
                    {
                        sitemapQueue.Enqueue((resolved, SitemapSourceKind.IndexRecursion, depth + 1));
                    }
                }
            }
        }

        return new SitemapIngestionResult(seededUrls, traversed, diagnostics);
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
