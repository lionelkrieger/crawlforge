using System.Text.RegularExpressions;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Scripted <see cref="IHttpFetcher"/> double: per-URL queues of canned
/// responses model hop chains; no network is ever touched. Unscripted URLs
/// fail with a transport exception unless a default response is configured.
/// </summary>
internal sealed class ScriptedHttpFetcher : IHttpFetcher
{
    private readonly Dictionary<string, Queue<HttpFetchResult>> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    public List<string> RequestedUrls { get; } = [];

    public HttpFetchResult? DefaultResponse { get; set; }

    public ScriptedHttpFetcher Script(string url, params HttpFetchResult[] responses)
    {
        _scripts[url] = new Queue<HttpFetchResult>(responses);
        return this;
    }

    public ScriptedHttpFetcher Fail(string url, Exception exception)
    {
        _failures[url] = exception;
        return this;
    }

    public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedUrls.Add(request.Url);

        if (_failures.TryGetValue(request.Url, out var exception))
        {
            throw exception;
        }

        if (_scripts.TryGetValue(request.Url, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        if (DefaultResponse is not null)
        {
            return Task.FromResult(DefaultResponse);
        }

        throw new HttpRequestException($"No script for URL '{request.Url}'.");
    }

    public static HttpFetchResult Response(
        string url,
        int statusCode,
        string? location = null,
        string? body = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (location is not null)
        {
            headers["Location"] = location;
        }

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                headers[name] = value;
            }
        }

        return new HttpFetchResult(
            url,
            statusCode,
            headers,
            body is null ? ReadOnlyMemory<byte>.Empty : System.Text.Encoding.UTF8.GetBytes(body),
            TimeSpan.Zero,
            TimeSpan.Zero);
    }
}

/// <summary>
/// Deterministic <see cref="IUrlCanonicalizer"/> double: lowercases scheme and
/// host, drops fragments and default ports, trims trailing slashes (except
/// root), preserves the query. No network, no sibling code.
/// </summary>
internal sealed class TestCanonicalizer : IUrlCanonicalizer
{
    public string Canonicalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : uri.AbsolutePath;
        return string.Concat(scheme, "://", host, port, path, uri.Query);
    }
}

/// <summary>
/// Deterministic <see cref="IUrlRewriteEngine"/> double applying enabled rules
/// in order with time-bounded regex replacement. This is a test double, not
/// the sibling production implementation.
/// </summary>
internal sealed class TestRewriteEngine : IUrlRewriteEngine
{
    public string Rewrite(string url, IReadOnlyList<UrlRewriteRule> rules)
    {
        var current = url;
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            current = Regex.Replace(current, rule.Pattern, rule.Replacement, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }

        return current;
    }
}

/// <summary>Recording <see cref="IRealtimeHub"/> double capturing every event.</summary>
internal sealed class RecordingHub : IRealtimeHub
{
    public List<HubEvent> Events { get; } = [];

    public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(hubEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Recording <see cref="IPriorityFrontier"/> double capturing enqueues.</summary>
internal sealed class RecordingFrontier : IPriorityFrontier
{
    private readonly List<FrontierEntry> _entries = [];

    public List<FrontierEntry> Enqueued { get; } = [];

    public long Count => _entries.Count;

    public ValueTask EnqueueAsync(FrontierEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(entry);
        Enqueued.Add(entry);
        return ValueTask.CompletedTask;
    }

    public ValueTask<FrontierEntry?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.Count == 0)
        {
            return ValueTask.FromResult<FrontierEntry?>(null);
        }

        var next = _entries[0];
        _entries.RemoveAt(0);
        return ValueTask.FromResult<FrontierEntry?>(next);
    }

    public FrontierSnapshot CreateSnapshot() => new(_entries.ToList());

    public void Restore(FrontierSnapshot snapshot)
    {
        _entries.Clear();
        _entries.AddRange(snapshot.Entries);
    }
}
