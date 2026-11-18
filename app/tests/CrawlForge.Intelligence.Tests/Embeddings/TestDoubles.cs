using System.Net;
using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.Embeddings;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Deterministic embedding provider double: marker rules map input text to
/// controlled vectors (exact cosine scenarios); unmatched text falls back to a
/// stable hash vector. Counts calls to prove cache reuse.
/// </summary>
internal sealed class FakeEmbeddingProvider : ILocalEmbeddingProvider
{
    private readonly List<(string Marker, float[] Vector)> _rules;

    public FakeEmbeddingProvider(string modelTag = "fake:model:4", IEnumerable<(string Marker, float[] Vector)>? rules = null)
    {
        ModelTag = modelTag;
        _rules = rules?.ToList() ?? [];
    }

    public string ModelTag { get; }

    public int Dimensions => 4;

    // WP-08-A2: the pipeline embeds with bounded concurrency
    // (SemanticPipelineOptions.MaxConcurrency, default 2), so the double must
    // count atomically and guard its text log; a plain ++ lost increments
    // under full-solution gate load (3 instead of 4).
    private int _embedCallCount;
    private readonly object _sync = new();

    public int EmbedCallCount => Volatile.Read(ref _embedCallCount);

    public List<string> Texts { get; } = [];

    public EmbeddingProviderTestResult TestResult { get; set; } = new(true, true, "fake provider ready");

    public Task<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _embedCallCount);
        lock (_sync)
        {
            Texts.Add(text);
        }
        foreach (var (marker, vector) in _rules)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return Task.FromResult(new EmbeddingVector(VectorMath.Normalize(vector), ModelTag));
            }
        }

        return Task.FromResult(new EmbeddingVector(StableHashVector(text), ModelTag));
    }

    public Task<EmbeddingProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TestResult);

    public static float[] StableHashVector(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[4];
        for (var i = 0; i < 4; i++)
        {
            vector[i] = (hash[i] - 128) / 128f;
        }

        return VectorMath.Normalize(vector);
    }
}

/// <summary>Recorded HTTP request snapshot (method, URI, auth presence).</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Uri">Absolute request URI.</param>
/// <param name="HasAuthorization">Whether an Authorization header was present.</param>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, bool HasAuthorization);

/// <summary>
/// Canned HTTP handler double: records every request and returns configured
/// responses. Proves the daemon provider only ever contacts loopback and never
/// sends credentials.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => Json(HttpStatusCode.OK, "{}");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization is not null));
        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };
}
