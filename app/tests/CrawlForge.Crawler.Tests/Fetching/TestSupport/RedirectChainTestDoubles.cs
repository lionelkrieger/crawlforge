using System.Collections.Concurrent;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.Tests.Fetching.TestSupport;

/// <summary>
/// Thread-safe in-memory redirect-chain store double (WP-09-01 component
/// tests): mirrors the url_elements upsert semantics without a database.
/// </summary>
internal sealed class InMemoryRedirectChainStore : IRedirectChainStore
{
    private readonly ConcurrentDictionary<long, RedirectChainPayload> _payloads = new();

    public int SaveCalls => Volatile.Read(ref _saveCalls);
    private int _saveCalls;

    public Task SaveChainAsync(RedirectChainSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SaveOutcomeAsync(snapshot.UrlId, RedirectChainJson.FromSnapshot(snapshot), cancellationToken);
    }

    public Task SaveOutcomeAsync(long urlId, RedirectChainPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref _saveCalls);
        _payloads[urlId] = payload;
        return Task.CompletedTask;
    }

    public Task<RedirectChainPayload?> GetPayloadAsync(long urlId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_payloads.TryGetValue(urlId, out var payload) ? payload : null);
}

/// <summary>
/// Thread-safe in-memory url_elements repository pair double: supports the
/// equality filters (urlId, elementGroup, id) and ordinal sort the store uses.
/// </summary>
internal sealed class InMemoryUrlElementRepositories : IReadRepository<UrlElementDto>, IWriteRepository<UrlElementDto>
{
    private readonly ConcurrentDictionary<long, UrlElementDto> _rows = new();
    private long _nextId;

    public int RowCount => _rows.Count;

    public Task<UrlElementDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.TryGetValue(id, out var row) ? row : null);

    public Task<PagedResult<UrlElementDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        IEnumerable<UrlElementDto> rows = _rows.Values;
        if (query.Filters is not null)
        {
            foreach (var (field, value) in query.Filters)
            {
                rows = field.ToLowerInvariant() switch
                {
                    "urlid" => rows.Where(r => r.UrlId.ToString() == value),
                    "elementgroup" => rows.Where(r => string.Equals(r.ElementGroup, value, StringComparison.Ordinal)),
                    "id" => rows.Where(r => r.Id.ToString() == value),
                    _ => throw new ArgumentException($"Unknown filter field '{field}'."),
                };
            }
        }

        var ordered = query.SortBy?.ToLowerInvariant() switch
        {
            "ordinal" => rows.OrderBy(r => r.Ordinal),
            _ => rows.OrderBy(r => r.Id),
        };
        var materialized = ordered.ToList();
        var page = materialized.Skip(query.Offset).Take(query.Limit).ToList();
        return Task.FromResult(new PagedResult<UrlElementDto>(page, materialized.Count, query.Offset, query.Limit));
    }

    public Task<UrlElementDto> CreateAsync(UrlElementDto entity, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var created = entity with { Id = id };
        _rows[id] = created;
        return Task.FromResult(created);
    }

    public Task<UrlElementDto> UpdateAsync(UrlElementDto entity, CancellationToken cancellationToken = default)
    {
        _rows[entity.Id] = entity;
        return Task.FromResult(entity);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.TryRemove(id, out _));
}

/// <summary>
/// Reference cookie-jar fetch hook double demonstrating the cf-wp-09-02
/// integration pattern over <see cref="IFetchHook"/>: retains Set-Cookie per
/// host across hops and requests, merges the jar into the Cookie header, and
/// allows ONE cookie-driven revisit per URL (the cookie-gate pattern: 302 to
/// self with Set-Cookie, then 200). Thread-safe.
/// </summary>
internal sealed class CookieJarFetchHook : IFetchHook
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _jar = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _revisits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public List<(string Url, string? CookieHeader)> Sent { get; } = new();

    public List<(string Url, IReadOnlyList<string> SetCookies)> Received { get; } = new();

    public ValueTask BeforeSendAsync(HttpRequestMessage request, FetchHookContext context, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        string? existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
        string? jarHeader = null;
        if (_jar.TryGetValue(host, out var cookies))
        {
            lock (_gate)
            {
                jarHeader = cookies.Count > 0 ? string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")) : null;
            }
        }

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

        lock (_gate)
        {
            Sent.Add((request.RequestUri?.AbsoluteUri ?? string.Empty, merged));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AfterReceiveAsync(HttpResponseMessage response, FetchHookContext context, CancellationToken cancellationToken)
    {
        // The hop URL comes from the context (response.RequestMessage is not
        // guaranteed to be populated by every transport).
        var host = Uri.TryCreate(context.CurrentUrl, UriKind.Absolute, out var currentUri) ? currentUri.Host : string.Empty;
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : new List<string>();
        if (setCookies.Count > 0 && host.Length > 0)
        {
            var cookies = _jar.GetOrAdd(host, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            lock (_gate)
            {
                foreach (var setCookie in setCookies)
                {
                    var pair = setCookie.Split(';')[0];
                    var separator = pair.IndexOf('=');
                    if (separator > 0)
                    {
                        cookies[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
                    }
                }
            }
        }

        lock (_gate)
        {
            Received.Add((context.CurrentUrl, setCookies));
        }

        return ValueTask.CompletedTask;
    }

    public bool AllowRevisit(FetchHookContext context)
    {
        // One cookie-driven revisit per URL: the previous response must have
        // set a cookie the jar now holds for the revisited host.
        if (context.PreviousResponse is null
            || !context.PreviousResponse.Headers.ContainsKey("Set-Cookie")
            || !Uri.TryCreate(context.CurrentUrl, UriKind.Absolute, out var uri)
            || !_jar.ContainsKey(uri.Host))
        {
            return false;
        }

        return _revisits.AddOrUpdate(context.CurrentUrl, 1, (_, count) => count + 1) == 1;
    }
}

/// <summary>
/// Fetch hook double that rewrites the request URI at fetch time
/// (cf-wp-09-03 host-mapping pattern) and records hook contexts.
/// </summary>
internal sealed class RewritingFetchHook : IFetchHook
{
    private readonly string _fromHost;
    private readonly string _toHost;

    public RewritingFetchHook(string fromHost, string toHost)
    {
        _fromHost = fromHost;
        _toHost = toHost;
    }

    public List<FetchHookContext> Contexts { get; } = new();

    public ValueTask BeforeSendAsync(HttpRequestMessage request, FetchHookContext context, CancellationToken cancellationToken)
    {
        lock (Contexts)
        {
            Contexts.Add(context);
        }

        if (request.RequestUri is { } uri && string.Equals(uri.Host, _fromHost, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Host = _toHost };
            request.RequestUri = builder.Uri;
        }

        return ValueTask.CompletedTask;
    }
}
