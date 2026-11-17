using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Test double for the locked <see cref="IHttpFetcher"/>: canned responses per
/// URL, a request log, and an optional transport failure. No network is ever
/// touched (exclusion row 1).
/// </summary>
internal sealed class FakeHttpFetcher : IHttpFetcher
{
    private readonly Dictionary<string, HttpFetchResult> _responses = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RequestedUrls { get; } = [];

    public Exception? ThrowOnFetch { get; set; }

    public void SetResponse(string url, int statusCode, string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        _responses[url] = new HttpFetchResult(
            url,
            statusCode,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new ReadOnlyMemory<byte>(bytes),
            TimeSpan.Zero,
            TimeSpan.Zero);
    }

    public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        RequestedUrls.Add(request.Url);
        if (ThrowOnFetch is not null)
        {
            throw ThrowOnFetch;
        }

        if (_responses.TryGetValue(request.Url, out var response))
        {
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpFetchResult(
            request.Url,
            404,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ReadOnlyMemory<byte>.Empty,
            TimeSpan.Zero,
            TimeSpan.Zero));
    }
}
