using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Tests.CaptureModes.TestSupport;

/// <summary>
/// Fake rendering pool (WP-10-01 builds the real CDP pool in parallel). Returns
/// registered post-render HTML per URL, records every request, and can inject
/// failures or a network waterfall. No browser, no network. Disposal is tracked
/// (WP-10-04 per-audit pool lifetime proofs).
/// </summary>
internal sealed class FakeRenderingPool : IRenderingPool, IAsyncDisposable
{
    private readonly Dictionary<string, RenderedPage> _pages = new(StringComparer.Ordinal);

    public List<RenderRequest> Requests { get; } = [];

    public HashSet<string> FailUrls { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<RenderedNetworkEntry>? Waterfall { get; set; }

    /// <summary>Number of times the pool was disposed (0 = never owned/disposed).</summary>
    public int DisposeCount { get; private set; }

    public FakeRenderingPool Register(string url, string html)
    {
        _pages[url] = new RenderedPage(
            url,
            html,
            Screenshot: ReadOnlyMemory<byte>.Empty,
            ConsoleMessages: Array.Empty<ConsoleMessage>(),
            PostRenderWafVerdict: null)
        {
            NetworkWaterfall = Waterfall,
        };
        return this;
    }

    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailUrls.Contains(request.Url))
        {
            throw new InvalidOperationException("simulated render failure");
        }

        if (_pages.TryGetValue(request.Url, out RenderedPage? page))
        {
            return Task.FromResult(page);
        }

        // Unregistered URLs render to themselves (identity render).
        return Task.FromResult(new RenderedPage(
            request.Url,
            "<html><head><title>identity</title></head><body></body></html>",
            ReadOnlyMemory<byte>.Empty,
            Array.Empty<ConsoleMessage>(),
            null)
        { NetworkWaterfall = Waterfall });
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Tolerant-parser double mapping registered HTML snapshots to prebuilt contract
/// documents. Unknown HTML throws so a test cannot silently pass on a mismatch.
/// </summary>
internal sealed class RegisteredDomParser : ITolerantDomParser
{
    private readonly Dictionary<string, DomDocument> _documents = new(StringComparer.Ordinal);

    public RegisteredDomParser Register(string html, DomDocument document)
    {
        _documents[html] = document;
        return this;
    }

    public DomDocument Parse(string html, string url) =>
        _documents.TryGetValue(html, out DomDocument? document)
            ? document
            : throw new InvalidOperationException($"No registered document for HTML snapshot: {html[..Math.Min(80, html.Length)]}");
}

/// <summary>
/// Snippet-runner double recording the context each batch received, so tests can
/// prove the raw/rendered/both source selection builds the right document view.
/// </summary>
internal sealed class RecordingSnippetRunner : IJsSnippetRunner
{
    public List<(PageAuditContext Context, IReadOnlyList<JsSnippetDefinition> Snippets)> Calls { get; } = [];

    public Func<PageAuditContext, IReadOnlyList<JsSnippetDefinition>, IReadOnlyList<SnippetResultDraft>>? Responder { get; set; }

    public Task<IReadOnlyList<SnippetResultDraft>> RunAsync(
        PageAuditContext context,
        IReadOnlyList<JsSnippetDefinition> snippets,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((context, snippets));
        IReadOnlyList<SnippetResultDraft> results = Responder is not null
            ? Responder(context, snippets)
            : snippets.Select(s => new SnippetResultDraft(s.Key, "[]", "[]", false)).ToList();
        return Task.FromResult(results);
    }
}
