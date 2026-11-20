using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.Logging;
using ConsoleMessage = CrawlForge.Contracts.Crawl.ConsoleMessage;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Direct-CDP headless rendering pool (FS-CRAWL-001 §5.6/§5.6.2, D-002/CR-006;
/// replaces the WP-02-06 browser-automation implementation — no third-party
/// automation library sits between the engine and the browser).
/// <para>
/// Process model: a bounded pool of long-lived browser processes (default
/// half the logical processors, minimum 2 — CaptureSettings.DefaultRenderingPoolSize),
/// each leased for one render at a time. Every page renders in a FRESH browser
/// context (isolated cookies/storage/cache). Rendered traffic is captured
/// DIRECTLY through the CDP Network domain into
/// <see cref="RenderedPage.NetworkWaterfall"/> (CR-006) — the loopback
/// intercepting proxy is NOT used for rendered traffic; it routes plain-HTTP
/// non-rendered traffic (§5.9).
/// </para>
/// <para>
/// Wait rule (§5.6.2, CR-009): DOM + network quiet 500 ms under a 20 s hard
/// cap, both configurable; streaming responses are excluded from the quiet
/// test; no fixed delays. Service workers are bypassed on every render
/// (§7.19) and the registration fact is exposed through
/// <see cref="RenderDetailedAsync"/>. Per-render timeout and failure isolation:
/// a faulted browser is retired and the next render launches a fresh one.
/// The browser binary resolves LOCALLY only (§5.6.3, D-003) — no runtime
/// browser or driver download exists.
/// </para>
/// </summary>
public sealed class HeadlessRenderingPool : IRenderingPool, IAsyncDisposable
{
    private const int WafBodySnippetChars = 8192;

    private readonly RenderingPoolOptions _options;
    private readonly LocalBrowserResolver _resolver;
    private readonly IContentStore? _contentStore;
    private readonly IWafChallengeEvaluator? _wafEvaluator;
    private readonly ILoopbackInterceptingProxy? _proxy;
    private readonly ILogger<HeadlessRenderingPool>? _logger;
    private readonly ICdpBrowserFactory _browserFactory;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly ConcurrentQueue<BrowserLease> _idleBrowsers = new();
    private readonly List<BrowserLease> _allBrowsers = [];
    private readonly object _registryLock = new();

    private ProxyEndpoint? _proxyEndpoint;
    private bool _proxyOwned;
    private bool _disposed;

    /// <summary>Creates the pool. All dependencies arrive via constructor composition.</summary>
    /// <param name="options">Local pool options (baseline defaults when null).</param>
    /// <param name="resolver">Local browser resolver (default resolver when null).</param>
    /// <param name="contentStore">Optional artifact store for rendered DOM + screenshot + waterfall.</param>
    /// <param name="wafEvaluator">Optional WAF pass consuming the render-resolution hook.</param>
    /// <param name="proxy">Optional loopback intercepting proxy (explicit routing opt-in only; CR-006 keeps the waterfall CDP-direct).</param>
    /// <param name="logger">Optional local-only logger.</param>
    public HeadlessRenderingPool(
        RenderingPoolOptions? options = null,
        LocalBrowserResolver? resolver = null,
        IContentStore? contentStore = null,
        IWafChallengeEvaluator? wafEvaluator = null,
        ILoopbackInterceptingProxy? proxy = null,
        ILogger<HeadlessRenderingPool>? logger = null)
        : this(options, resolver, contentStore, wafEvaluator, proxy, logger, browserFactory: null)
    {
    }

    /// <summary>Composition seam: component tests inject a scripted CDP browser factory.</summary>
    internal HeadlessRenderingPool(
        RenderingPoolOptions? options,
        LocalBrowserResolver? resolver,
        IContentStore? contentStore,
        IWafChallengeEvaluator? wafEvaluator,
        ILoopbackInterceptingProxy? proxy,
        ILogger<HeadlessRenderingPool>? logger,
        ICdpBrowserFactory? browserFactory)
    {
        _options = options ?? new RenderingPoolOptions();
        if (_options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        }

        _resolver = resolver ?? new LocalBrowserResolver(_options.BrowserResolution);
        _contentStore = contentStore;
        _wafEvaluator = wafEvaluator;
        _proxy = proxy;
        _logger = logger;
        _browserFactory = browserFactory ?? new DefaultCdpBrowserFactory(logger);
        _concurrencyGate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    /// <inheritdoc />
    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default) =>
        RenderAsync(request, artifactContext: null, cancellationToken);

    /// <summary>
    /// Renders a URL and persists the rendered DOM + screenshot + network
    /// waterfall artifacts into the content store when an artifact context and
    /// store are supplied (storage kinds RenderedDom / Screenshot /
    /// NetworkWaterfall — the engine's per-rendered-URL persistence path).
    /// </summary>
    /// <param name="request">Render request.</param>
    /// <param name="artifactContext">Owning audit/URL for artifact persistence, when any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered page (waterfall included).</returns>
    public async Task<RenderedPage> RenderAsync(RenderRequest request, RenderArtifactContext? artifactContext, CancellationToken cancellationToken = default)
    {
        var result = await RenderCoreAsync(request, artifactContext, cancellationToken);
        return result.Page;
    }

    /// <summary>
    /// Renders a URL and returns the locked page PLUS the observability facts
    /// the locked contract does not yet carry: service-worker registrations
    /// (§7.19; renders bypass them) and the resolved browser identity (§5.6.3).
    /// The wiring handoff requests additive RenderedPage members for these.
    /// </summary>
    /// <param name="request">Render request.</param>
    /// <param name="artifactContext">Owning audit/URL for artifact persistence, when any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detailed render result.</returns>
    public Task<CdpRenderResult> RenderDetailedAsync(RenderRequest request, RenderArtifactContext? artifactContext = null, CancellationToken cancellationToken = default) =>
        RenderCoreAsync(request, artifactContext, cancellationToken);

    /// <summary>
    /// Resolves the local browser identity WITHOUT launching anything — the
    /// read the UI-21 browser section binds to (pinned bundle version, source,
    /// product/version of the resolved fallback).
    /// </summary>
    /// <returns>The resolved browser.</returns>
    /// <exception cref="BrowserResolutionException">No local browser resolves.</exception>
    public ResolvedBrowser ResolveBrowserInfo() => _resolver.ResolveDetailed();

    /// <summary>Reads the bundled pin manifest when present (UI-21 pinned version).</summary>
    /// <returns>The manifest, or null when the bundle carries none.</returns>
    public BrowserPinManifest? TryLoadPinManifest() =>
        BrowserProvisioning.TryLoadPinManifest(_options.BrowserResolution);

    /// <summary>
    /// Drains the network capture observed through the intercepting proxy since
    /// the previous drain. NOTE (CR-006): this is the NON-rendered plain-HTTP
    /// capture surface (§5.9); the rendered waterfall lives on
    /// <see cref="RenderedPage.NetworkWaterfall"/>. Empty when no proxy is
    /// configured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured requests in observation order.</returns>
    public Task<IReadOnlyList<ProxyCapturedRequest>> DrainNetworkCaptureAsync(CancellationToken cancellationToken = default) =>
        _proxy is null
            ? Task.FromResult<IReadOnlyList<ProxyCapturedRequest>>([])
            : _proxy.DrainCapturedAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BrowserLease[] leases;
        lock (_registryLock)
        {
            leases = [.. _allBrowsers];
            _allBrowsers.Clear();
        }

        while (_idleBrowsers.TryDequeue(out _))
        {
            // Drain the idle queue; the leases are disposed below.
        }

        foreach (var lease in leases)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or CdpCommandException)
            {
                // Browser already gone.
            }
        }

        if (_proxyOwned && _proxy is not null)
        {
            await _proxy.StopAsync();
        }

        _concurrencyGate.Dispose();
        _launchLock.Dispose();
    }

    private async Task<CdpRenderResult> RenderCoreAsync(RenderRequest request, RenderArtifactContext? artifactContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new RenderFailureException(RenderFailureKind.Navigation, $"Render target '{request.Url}' is not an absolute http(s) URL.");
        }

        await _concurrencyGate.WaitAsync(cancellationToken);
        BrowserLease? lease = null;
        var healthy = false;
        try
        {
            var timeoutSeconds = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : _options.DefaultTimeoutSeconds;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var token = timeoutCts.Token;

            try
            {
                await EnsureProxyAsync(token);
                lease = await RentBrowserAsync(token);
                var result = await RenderWithSessionAsync(lease, request, artifactContext, token);
                healthy = true;
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RenderFailureException(RenderFailureKind.Timeout, $"Render of '{request.Url}' exceeded {timeoutSeconds}s.");
            }
            catch (RenderFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = IsTimeoutFailure(ex) ? RenderFailureKind.Timeout : RenderFailureKind.Navigation;
                throw new RenderFailureException(kind, $"Render of '{request.Url}' failed: {ex.Message}", ex);
            }
        }
        finally
        {
            if (lease is not null)
            {
                if (healthy && lease.IsHealthy)
                {
                    _idleBrowsers.Enqueue(lease);
                }
                else
                {
                    // Failure isolation: retire the faulted browser; the next
                    // render launches a fresh one. The pool survives.
                    await RetireLeaseAsync(lease);
                }
            }

            _concurrencyGate.Release();
        }
    }

    private async Task EnsureProxyAsync(CancellationToken cancellationToken)
    {
        if (_proxy is null || !_options.RouteThroughProxy || _proxyEndpoint is not null)
        {
            return;
        }

        _proxyEndpoint = await _proxy.StartAsync(new ProxyOptions(_options.UpstreamProxyUrl), cancellationToken);
        _proxyOwned = true;
        _logger?.LogInformation(
            "Explicit proxy opt-in: rendered page traffic routes through loopback proxy {ProxyHost}:{ProxyPort} (waterfall stays CDP-direct, CR-006).",
            _proxyEndpoint.Host,
            _proxyEndpoint.Port);
    }

    private async Task<BrowserLease> RentBrowserAsync(CancellationToken cancellationToken)
    {
        while (_idleBrowsers.TryDequeue(out var idle))
        {
            if (idle.IsHealthy)
            {
                return idle;
            }

            await RetireLeaseAsync(idle);
        }

        await _launchLock.WaitAsync(cancellationToken);
        try
        {
            // Another renter may have returned a healthy browser meanwhile.
            while (_idleBrowsers.TryDequeue(out var idle))
            {
                if (idle.IsHealthy)
                {
                    return idle;
                }

                await RetireLeaseAsync(idle);
            }

            ResolvedBrowser resolved;
            try
            {
                resolved = _resolver.ResolveDetailed();
            }
            catch (BrowserResolutionException ex)
            {
                throw new RenderFailureException(RenderFailureKind.BrowserUnavailable, ex.Message, ex);
            }

            var lease = await _browserFactory.LaunchAsync(resolved, _options, _proxyEndpoint, cancellationToken);
            lock (_registryLock)
            {
                _allBrowsers.Add(lease);
            }

            _logger?.LogInformation(
                "Pooled browser ready ({Source} {Product} {Version}).",
                resolved.Source,
                resolved.Product,
                resolved.Version);
            return lease;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private async Task RetireLeaseAsync(BrowserLease lease)
    {
        lock (_registryLock)
        {
            _allBrowsers.Remove(lease);
        }

        try
        {
            await lease.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or CdpCommandException)
        {
            // Already gone.
        }
    }

    private async Task<CdpRenderResult> RenderWithSessionAsync(
        BrowserLease lease,
        RenderRequest request,
        RenderArtifactContext? artifactContext,
        CancellationToken cancellationToken)
    {
        var browser = lease.Browser;
        var contextId = await browser.CreateBrowserContextAsync(cancellationToken);
        string targetId;
        string sessionId;
        try
        {
            targetId = await browser.CreateTargetAsync(contextId, "about:blank", cancellationToken);
            sessionId = await browser.AttachAsync(targetId, cancellationToken);
        }
        catch
        {
            try
            {
                await browser.DisposeBrowserContextAsync(contextId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is CdpCommandException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Context cleanup is best-effort.
            }

            throw;
        }

        // Fresh context per page: the session disposes the target AND context.
        await using var session = new CdpPageSession(browser, targetId, sessionId, contextId, _options, _logger);
        await session.InitializeAsync(request, cancellationToken);

        var navigationClock = Stopwatch.StartNew();
        await session.NavigateAsync(request.Url, cancellationToken);
        await session.WaitForSettleAsync(navigationClock, requireNavigationActivity: true, cancellationToken);

        if (request.InfiniteScroll)
        {
            await session.DriveInfiniteScrollAsync(cancellationToken);
        }

        var capture = await session.CaptureAsync(cancellationToken);
        var waterfall = session.Recorder.GetEntries();
        var verdict = EvaluatePostRenderWaf(capture.FinalUrl, session.Recorder, capture.Html);

        var page = new RenderedPage(
            capture.FinalUrl,
            capture.Html,
            capture.Screenshot,
            session.ConsoleMessages,
            verdict)
        {
            NetworkWaterfall = waterfall,
        };

        await PersistArtifactsAsync(artifactContext, capture, waterfall, cancellationToken);
        return new CdpRenderResult(page, session.ServiceWorkerRegistrations, lease.Identity);
    }

    private WafEvaluationResult? EvaluatePostRenderWaf(string finalUrl, NetworkWaterfallRecorder recorder, string html)
    {
        if (_wafEvaluator is null)
        {
            return null;
        }

        var document = recorder.GetDocumentResponse(finalUrl);
        var headers = document?.Headers is { } responseHeaders
            ? new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snippet = html.Length > WafBodySnippetChars ? html[..WafBodySnippetChars] : html;
        return _wafEvaluator.Evaluate(new WafEvaluationInput(
            finalUrl,
            document?.StatusCode ?? 200,
            headers,
            snippet));
    }

    private async Task PersistArtifactsAsync(
        RenderArtifactContext? context,
        PageCapture capture,
        IReadOnlyList<RenderedNetworkEntry> waterfall,
        CancellationToken cancellationToken)
    {
        if (context is null || _contentStore is null)
        {
            return;
        }

        var domBytes = Encoding.UTF8.GetBytes(capture.Html);
        await _contentStore.PutAsync(context.AuditId, context.UrlId, ContentArtifactKind.RenderedDom, new MemoryStream(domBytes), cancellationToken);
        await _contentStore.PutAsync(context.AuditId, context.UrlId, ContentArtifactKind.Screenshot, new MemoryStream(capture.Screenshot), cancellationToken);

        // CR-006/CR-031: the rendered waterfall artifact — one camelCase JSON
        // array of RenderedNetworkEntry per rendered URL (TransferredBytes
        // feeds the oversized-image rule). Shared writer keeps the http-mode
        // proxy waterfall artifact byte-identical in shape.
        var waterfallBytes = NetworkWaterfallArtifactWriter.Serialize(waterfall);
        await _contentStore.PutAsync(context.AuditId, context.UrlId, ContentArtifactKind.NetworkWaterfall, new MemoryStream(waterfallBytes), cancellationToken);
    }

    private static bool IsTimeoutFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException)
            {
                return true;
            }

            if (current.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
