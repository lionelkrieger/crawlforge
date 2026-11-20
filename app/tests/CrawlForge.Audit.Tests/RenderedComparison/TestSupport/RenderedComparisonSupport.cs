using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.RenderedComparison.TestSupport;

/// <summary>
/// Fixture pages copied verbatim from app/tests/fixture-site/pages.mjs (the
/// phase-gate acceptance traps) plus the post-render HTML the fixture's inline
/// scripts produce. The fake rendering pool returns the post-render snapshots;
/// the REAL AngleSharp parser and the REAL comparer consume them, so these
/// conformance tests feed real producer output into the real consumer.
/// </summary>
internal static class FixturePages
{
    public const string BaseUrl = "https://fixture.test";

    public static string Words(int n, string seed) =>
        string.Join(' ', Enumerable.Range(0, n).Select(i => seed + (i % 17)));

    private static string Page(string title, string head, string body) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{title}</title>{head}</head><body>{body}</body></html>";

    // ── js-only-link ─────────────────────────────────────────────────────────
    public static string JsLinkRaw => Page(
        "JS link source", "",
        $"<h1>JS link source</h1><p>{Words(60, "jslink")}</p><div id=\"out\"></div><script>var a=document.createElement('a');a.href='/pages/js-only-target.html';a.textContent='Rendered only link';document.getElementById('out').appendChild(a);</script>");

    public static string JsLinkRendered => Page(
        "JS link source", "",
        $"<h1>JS link source</h1><p>{Words(60, "jslink")}</p><div id=\"out\"><a href=\"/pages/js-only-target.html\">Rendered only link</a></div><script>var a=document.createElement('a');a.href='/pages/js-only-target.html';a.textContent='Rendered only link';document.getElementById('out').appendChild(a);</script>");

    // ── script-injected-content ──────────────────────────────────────────────
    public static string JsContentRaw => Page(
        "Script-injected content", "",
        $"<h1>Script-injected content</h1><div id=\"root\"></div><script>document.getElementById('root').innerHTML='<p>{Words(200, "injected")}</p>';</script>");

    public static string JsContentRendered => Page(
        "Script-injected content", "",
        $"<h1>Script-injected content</h1><div id=\"root\"><p>{Words(200, "injected")}</p></div><script>document.getElementById('root').innerHTML='<p>{Words(200, "injected")}</p>';</script>");

    // ── late-meta-change ─────────────────────────────────────────────────────
    public static string LateMetaRaw => Page(
        "Raw title before script",
        "<link rel=\"canonical\" href=\"/pages/late-meta.html\"><meta name=\"description\" content=\"Raw description\">",
        $"<h1>Late meta change</h1><p>{Words(150, "meta")}</p><script>document.title='Rendered title after script';document.querySelector('link[rel=canonical]').href='/pages/late-meta-other.html';var m=document.createElement('meta');m.name='robots';m.content='noindex, nofollow';document.head.appendChild(m);</script>");

    public static string LateMetaRendered => Page(
        "Rendered title after script",
        "<link rel=\"canonical\" href=\"/pages/late-meta-other.html\"><meta name=\"description\" content=\"Raw description\"><meta name=\"robots\" content=\"noindex, nofollow\">",
        $"<h1>Late meta change</h1><p>{Words(150, "meta")}</p><script>document.title='Rendered title after script';document.querySelector('link[rel=canonical]').href='/pages/late-meta-other.html';var m=document.createElement('meta');m.name='robots';m.content='noindex, nofollow';document.head.appendChild(m);</script>");

    // ── service-worker ───────────────────────────────────────────────────────
    public static string SwAppRaw => Page(
        "Service worker app", "",
        $"<h1>Service worker app</h1><p>{Words(150, "sw")}</p><script>if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw-app/sw.js');</script>");

    public static string SwAppRendered => SwAppRaw;
}

/// <summary>
/// Fake rendering pool returning registered post-render HTML snapshots (the real
/// CDP pool is WP-10-01's parallel work). Records requests; no browser/network.
/// </summary>
internal sealed class FakeRenderingPool : IRenderingPool
{
    private readonly Dictionary<string, string> _html = new(StringComparer.Ordinal);

    public List<RenderRequest> Requests { get; } = [];

    public IReadOnlyList<RenderedNetworkEntry>? Waterfall { get; set; }

    public FakeRenderingPool Register(string url, string html)
    {
        _html[url] = html;
        return this;
    }

    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        string html = _html.TryGetValue(request.Url, out string? registered) ? registered : "<html><head><title>identity</title></head><body></body></html>";
        return Task.FromResult(new RenderedPage(
            request.Url, html, ReadOnlyMemory<byte>.Empty, Array.Empty<ConsoleMessage>(), null)
        { NetworkWaterfall = Waterfall });
    }
}

/// <summary>Deterministic PageAuditContext construction for comparison tests.</summary>
internal static class ComparisonContext
{
    public const long AuditId = 1;
    public const long UrlId = 1;

    public static UrlRecordDto UrlRecord(string url, int? finalStatusCode = 200) =>
        new(UrlId, AuditId, url, new Uri(url).Host, new Uri(url).AbsolutePath, 0, DiscoverySource.Seed,
            CrawlStatusClass.Success, finalStatusCode, null, "text/html", 1024, null, RobotsState.Allowed,
            IndexabilityFlags.None, true);

    public static CrawlConfigurationDto Configuration(CaptureMode? mode = CaptureMode.Dual) =>
        new(1, 1, "cfg", "[\"https://fixture.test/\"]", null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        { CaptureMode = mode };

    public static PageAuditContext Context(
        DomDocument? document,
        DomDocument? renderedDocument,
        string url = FixturePages.BaseUrl + "/page",
        int fetchStatus = 200,
        int? finalStatus = 200,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(AuditId, UrlRecord(url, finalStatus), Configuration(),
            new HttpFetchResult(url, fetchStatus, headers ?? new Dictionary<string, string>(), Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero),
            document, renderedDocument, null, null);
}

/// <summary>An empty rule host so the real pipeline runs without analyzer families.</summary>
internal sealed class EmptyRuleHost : IAuditRuleAnalyzer
{
    public IReadOnlyList<string> SupportedRuleKeys => Array.Empty<string>();

    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FindingDraft>>(Array.Empty<FindingDraft>());
}
