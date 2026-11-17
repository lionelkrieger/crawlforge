using System.Collections.Concurrent;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Local configuration of the per-host robots cache (FS-CRAWL-001 §4.4).
/// </summary>
public sealed record RobotsCacheOptions
{
    /// <summary>Lifetime of a successfully fetched (or definitively absent) robots document.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Lifetime of a permissive fallback cached after a transient fetch failure.</summary>
    public TimeSpan NegativeTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Scheme used to fetch <c>/robots.txt</c> from each host.</summary>
    public string Scheme { get; init; } = "https";

    /// <summary>Crawler user-agent token used for group selection and crawl-delay lookup.</summary>
    public string CrawlerUserAgent { get; init; } = "CrawlForge";
}

/// <summary>
/// Per-host robots.txt cache (FS-CRAWL-001 §4.4): fetches each host's robots
/// document once per TTL through the locked <see cref="IHttpFetcher"/>, parses
/// it with the locked <see cref="IRobotsTxtParser"/>, and serves allow/disallow
/// and crawl-delay decisions from cache. Virtual robots overrides take
/// precedence over fetched documents and never alter limits resolution.
/// A 404/410 means "no restrictions"; transient failures cache a permissive
/// fallback for the shorter negative TTL (local implementation choice).
/// </summary>
public sealed class RobotsCache : ICrawlDelaySource
{
    private readonly IHttpFetcher _fetcher;
    private readonly IRobotsTxtParser _parser;
    private readonly VirtualRobotsStore? _virtualRobots;
    private readonly RobotsCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the cache over the locked fetching and parsing contracts.</summary>
    /// <param name="fetcher">HTTP fetcher used to retrieve robots documents.</param>
    /// <param name="parser">Robots parser used for fetched and virtual bodies.</param>
    /// <param name="virtualRobots">Optional user-local virtual robots overrides.</param>
    /// <param name="options">Cache options; null uses the baseline defaults.</param>
    /// <param name="timeProvider">Clock for TTL expiry; null uses the system clock.</param>
    public RobotsCache(
        IHttpFetcher fetcher,
        IRobotsTxtParser parser,
        VirtualRobotsStore? virtualRobots = null,
        RobotsCacheOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(parser);
        _fetcher = fetcher;
        _parser = parser;
        _virtualRobots = virtualRobots;
        _options = options ?? new RobotsCacheOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the effective robots document for a host: the virtual override
    /// when present, otherwise the cached document, fetching and caching on
    /// first use or TTL expiry.
    /// </summary>
    /// <param name="host">Target host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective robots document.</returns>
    public async Task<RobotsDocument> GetDocumentAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = Normalize(host);

        if (_virtualRobots is not null && _virtualRobots.TryGet(normalized, out var virtualBody) && virtualBody is not null)
        {
            return _parser.Parse(virtualBody);
        }

        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(normalized, out var entry) && now < entry.ExpiresAt)
        {
            return entry.Document;
        }

        RobotsDocument document;
        TimeSpan ttl;
        var url = string.Concat(_options.Scheme, "://", normalized, "/robots.txt");
        try
        {
            var request = new HttpFetchRequest(url, Method: "GET", FollowRedirects: true);
            var response = await _fetcher.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == 200)
            {
                document = _parser.Parse(System.Text.Encoding.UTF8.GetString(response.Body.Span));
                ttl = _options.Ttl;
            }
            else if (response.StatusCode is 404 or 410)
            {
                document = EmptyDocument();
                ttl = _options.Ttl;
            }
            else
            {
                document = EmptyDocument();
                ttl = _options.NegativeTtl;
            }
        }
        catch (HttpRequestException)
        {
            document = EmptyDocument();
            ttl = _options.NegativeTtl;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            document = EmptyDocument();
            ttl = _options.NegativeTtl;
        }

        _cache[normalized] = new CacheEntry(document, now + ttl);
        return document;
    }

    /// <summary>
    /// Reads the effective document without fetching: virtual override first,
    /// then a non-expired cache entry.
    /// </summary>
    /// <param name="host">Target host.</param>
    /// <param name="document">The effective document, when known.</param>
    /// <returns>True when a document is available without a fetch.</returns>
    public bool TryGetCached(string host, out RobotsDocument? document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = Normalize(host);

        if (_virtualRobots is not null && _virtualRobots.TryGet(normalized, out var virtualBody) && virtualBody is not null)
        {
            document = _parser.Parse(virtualBody);
            return true;
        }

        if (_cache.TryGetValue(normalized, out var entry) && _time.GetUtcNow() < entry.ExpiresAt)
        {
            document = entry.Document;
            return true;
        }

        document = null;
        return false;
    }

    /// <inheritdoc/>
    public double? GetCrawlDelaySeconds(string host)
    {
        return TryGetCached(host, out var document) && document is not null
            ? RobotsTxtParser.SelectGroup(document, _options.CrawlerUserAgent)?.CrawlDelaySeconds
            : null;
    }

    /// <summary>Drops the cached document for one host.</summary>
    /// <param name="host">Target host.</param>
    public void Invalidate(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _cache.TryRemove(Normalize(host), out _);
    }

    /// <summary>Drops every cached document.</summary>
    public void Clear() => _cache.Clear();

    private static RobotsDocument EmptyDocument() => new([], []);

    private static string Normalize(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return trimmed;
    }

    private sealed record CacheEntry(RobotsDocument Document, DateTimeOffset ExpiresAt);
}
