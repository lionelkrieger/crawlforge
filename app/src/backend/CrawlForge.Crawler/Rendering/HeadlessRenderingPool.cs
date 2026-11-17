using System.Diagnostics;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using ConsoleMessage = CrawlForge.Contracts.Crawl.ConsoleMessage;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Headless Chromium rendering pool (FS-CRAWL-001 §5.6): bounded concurrency,
/// device/viewport/locale emulation, optional user-enabled stealth posture,
/// bounded infinite-scroll expansion, console capture, rendered DOM +
/// screenshot artifacts into the content store, and per-render timeout/failure
/// isolation. The browser binary is resolved LOCALLY only (WP-02-D1); no
/// remote driver or browser download exists. When proxy routing is enabled,
/// all page traffic goes through the built-in loopback intercepting proxy and
/// the network capture feeds the waterfall contract (§5.9).
/// </summary>
public sealed class HeadlessRenderingPool : IRenderingPool, IAsyncDisposable
{
    private const int WafBodySnippetChars = 8192;

    private static readonly string[] BoundaryHardeningArgs =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-sync",
        "--disable-default-apps",
        "--disable-breakpad",
        "--disable-client-side-phishing-detection",
        "--metrics-recording-only",
        "--mute-audio",
        "--no-pings",
        "--disable-domain-reliability",
        "--disable-component-extensions-with-background-pages",
        "--disable-extensions",
        "--no-service-autorun",
        "--disable-search-engine-choice-screen",
        "--disable-field-trial-config",
        "--no-experiments",
        "--disable-features=Translate,MediaRouter,NetworkTimeServiceQuerying,CertVerificationNetworkTime,PreconnectFromKeyedService,AccountPreviewDataPreconnect,EarlyCookieLoadOnPreconnect,OptimizationHints,InterestFeedContentSuggestions,AutofillServerCommunication,CertificateTransparencyComponentUpdater,PrivacySandboxSettings4,ChromeWhatsNewUI,SearchEngineChoice,DsePreload,DsePreload2,ContextualTasks,ContextualTasksCookiePrefetch,SearchCompanion,SafeBrowsingOhttpKeyService,AimEnabled,AimServerEligibilityEnabled,AimServerRequestOnStartupEnabled,SafeBrowsingHashPrefixRealTimeLookups,PreconnectToSearch,PreconnectToSearchDesktop,DseIntegrity",
        "--enable-features=SkipListAccountsWithoutGaiaCookies",
    ];

    private readonly RenderingPoolOptions _options;
    private readonly LocalBrowserResolver _resolver;
    private readonly IContentStore? _contentStore;
    private readonly IWafChallengeEvaluator? _wafEvaluator;
    private readonly ILoopbackInterceptingProxy? _proxy;
    private readonly ILogger<HeadlessRenderingPool>? _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly HashSet<string> _inScopeOrigins;

    private IBrowser? _browser;
    private ProxyEndpoint? _proxyEndpoint;
    private bool _proxyOwned;
    private bool _disposed;

    /// <summary>Creates the pool. All dependencies arrive via constructor composition.</summary>
    /// <param name="options">Local pool options (baseline defaults when null).</param>
    /// <param name="resolver">Local browser resolver (default resolver when null).</param>
    /// <param name="contentStore">Optional artifact store for rendered DOM + screenshots.</param>
    /// <param name="wafEvaluator">Optional WAF pass consuming the render-resolution hook.</param>
    /// <param name="proxy">Optional loopback intercepting proxy for page-traffic routing.</param>
    /// <param name="logger">Optional local-only logger.</param>
    public HeadlessRenderingPool(
        RenderingPoolOptions? options = null,
        LocalBrowserResolver? resolver = null,
        IContentStore? contentStore = null,
        IWafChallengeEvaluator? wafEvaluator = null,
        ILoopbackInterceptingProxy? proxy = null,
        ILogger<HeadlessRenderingPool>? logger = null)
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
        _concurrencyGate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        _inScopeOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in _options.InScopeOrigins)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                _inScopeOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }
    }

    /// <inheritdoc />
    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default) =>
        RenderAsync(request, artifactContext: null, cancellationToken);

    /// <summary>
    /// Renders a URL and persists the rendered DOM + screenshot artifacts into
    /// the content store when an artifact context and store are supplied.
    /// </summary>
    /// <param name="request">Render request.</param>
    /// <param name="artifactContext">Owning audit/URL for artifact persistence, when any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered page.</returns>
    public async Task<RenderedPage> RenderAsync(RenderRequest request, RenderArtifactContext? artifactContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new RenderFailureException(RenderFailureKind.Navigation, $"Render target '{request.Url}' is not an absolute http(s) URL.");
        }

        await _concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var timeoutSeconds = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : _options.DefaultTimeoutSeconds;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var token = timeoutCts.Token;

            try
            {
                await EnsureProxyAsync(token);
                var browser = await EnsureBrowserAsync(token);
                return await RenderWithPageAsync(browser, request, artifactContext, timeoutSeconds, token);
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
            finally
            {
                await RetireDeadBrowserAsync();
            }
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    /// <summary>
    /// Drains the network capture observed through the intercepting proxy since
    /// the previous drain — the waterfall contract feeding MarTech beacon
    /// inspection and code-coverage/composition metrics. Empty when no proxy
    /// is configured.
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
        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Browser already gone.
            }

            _browser = null;
        }

        if (_proxyOwned && _proxy is not null)
        {
            await _proxy.StopAsync();
        }

        _concurrencyGate.Dispose();
        _browserLock.Dispose();
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
            "Rendered page traffic routes through loopback proxy {ProxyHost}:{ProxyPort}.",
            _proxyEndpoint.Host,
            _proxyEndpoint.Port);
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsClosed: false, IsConnected: true })
            {
                return _browser;
            }

            _browser = null;
            string executablePath;
            try
            {
                executablePath = _resolver.Resolve();
            }
            catch (BrowserResolutionException ex)
            {
                throw new RenderFailureException(RenderFailureKind.BrowserUnavailable, ex.Message, ex);
            }

            var args = new List<string>(BoundaryHardeningArgs)
            {
                $"--lang={_options.Locale}",
            };
            if (_proxyEndpoint is not null)
            {
                args.Add($"--proxy-server={_proxyEndpoint.Host}:{_proxyEndpoint.Port}");
                // Remove the browser's implicit loopback bypass so local crawl
                // targets also route through the intercepting proxy (§5.9).
                args.Add("--proxy-bypass-list=<-loopback>");
            }

            try
            {
                _browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    ExecutablePath = executablePath,
                    HeadlessMode = HeadlessMode.True,
                    Args = [.. args],
                    Timeout = _options.LaunchTimeoutSeconds * 1000,
                    DefaultViewport = null,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RenderFailureException(
                    RenderFailureKind.BrowserUnavailable,
                    $"Local browser launch failed ({ex.Message}). No runtime download is attempted.",
                    ex);
            }

            _logger?.LogInformation("Headless browser launched from local binary.");
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task<RenderedPage> RenderWithPageAsync(
        IBrowser browser,
        RenderRequest request,
        RenderArtifactContext? artifactContext,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var page = await browser.NewPageAsync();
        try
        {
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = request.Device.ViewportWidth,
                Height = request.Device.ViewportHeight,
                IsMobile = request.Device.IsMobile,
                HasTouch = request.Device.IsMobile,
                DeviceScaleFactor = 1,
            });
            await page.SetUserAgentAsync(request.Device.UserAgent);

            if (request.Stealth)
            {
                await page.EvaluateExpressionOnNewDocumentAsync(StealthProfile.Script);
            }

            var consoleMessages = new List<ConsoleMessage>();
            if (request.CaptureConsole)
            {
                page.Console += (_, eventArgs) =>
                {
                    lock (consoleMessages)
                    {
                        if (consoleMessages.Count < _options.MaxConsoleMessages)
                        {
                            consoleMessages.Add(new ConsoleMessage(
                                eventArgs.Message.Type.ToString().ToLowerInvariant(),
                                eventArgs.Message.Text,
                                DateTimeOffset.UtcNow));
                        }
                    }
                };
            }

            await ConfigureHeadersAsync(page);

            var response = await page.GoToAsync(request.Url, new NavigationOptions
            {
                Timeout = timeoutSeconds * 1000,
                WaitUntil = [WaitUntilNavigation.Load],
            });

            if (request.InfiniteScroll)
            {
                await DriveInfiniteScrollAsync(page, cancellationToken);
            }

            var html = await page.GetContentAsync();
            var screenshot = await page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png });
            var finalUrl = page.Url;

            var verdict = EvaluatePostRenderWaf(finalUrl, response, html);
            await PersistArtifactsAsync(artifactContext, html, screenshot, cancellationToken);

            List<ConsoleMessage> capturedConsole;
            lock (consoleMessages)
            {
                capturedConsole = [.. consoleMessages];
            }

            return new RenderedPage(finalUrl, html, screenshot, capturedConsole, verdict);
        }
        finally
        {
            try
            {
                await page.CloseAsync();
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Page isolation: a failed close never masks the render result.
            }
        }
    }

    private async Task ConfigureHeadersAsync(IPage page)
    {
        var nonCredential = new Dictionary<string, string>(_options.NonCredentialHeaders, StringComparer.OrdinalIgnoreCase);
        if (!nonCredential.ContainsKey("Accept-Language"))
        {
            nonCredential["Accept-Language"] = _options.Locale;
        }

        var hasCredentials = _options.CredentialHeaders.Count > 0;
        if (!hasCredentials)
        {
            await page.SetExtraHttpHeadersAsync(nonCredential);
            return;
        }

        // Credential-bearing headers go ONLY to in-scope origins (§5.6 split):
        // intercept each request and merge per origin.
        await page.SetExtraHttpHeadersAsync(nonCredential);
        await page.SetRequestInterceptionAsync(true);
        page.Request += async (_, eventArgs) =>
        {
            try
            {
                var headers = new Dictionary<string, string>(eventArgs.Request.Headers, StringComparer.OrdinalIgnoreCase);
                if (Uri.TryCreate(eventArgs.Request.Url, UriKind.Absolute, out var requestUri)
                    && _inScopeOrigins.Contains(requestUri.GetLeftPart(UriPartial.Authority)))
                {
                    foreach (var (name, value) in _options.CredentialHeaders)
                    {
                        headers[name] = value;
                    }
                }

                await eventArgs.Request.ContinueAsync(new Payload { Headers = headers });
            }
            catch (Exception ex) when (ex is PuppeteerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Page closed mid-interception; nothing to forward.
            }
        };
    }

    private async Task DriveInfiniteScrollAsync(IPage page, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long lastHeight = -1;
        var stableChecks = 0;

        for (var iteration = 0; iteration < _options.InfiniteScrollMaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var height = await page.EvaluateExpressionAsync<long>("document.body ? document.body.scrollHeight : 0");
            if (height == lastHeight)
            {
                stableChecks++;
                if (stableChecks >= _options.InfiniteScrollStableChecks)
                {
                    return;
                }
            }
            else
            {
                stableChecks = 0;
                lastHeight = height;
            }

            await page.EvaluateExpressionAsync($"window.scrollTo(0, {height})");
            await Task.Delay(_options.InfiniteScrollPauseMs, cancellationToken);
        }

        _logger?.LogDebug(
            "Infinite scroll stopped at the iteration bound ({Bound}) after {ElapsedMs}ms.",
            _options.InfiniteScrollMaxIterations,
            stopwatch.ElapsedMilliseconds);
    }

    private WafEvaluationResult? EvaluatePostRenderWaf(string finalUrl, IResponse? response, string html)
    {
        if (_wafEvaluator is null)
        {
            return null;
        }

        var headers = response?.Headers is { } responseHeaders
            ? new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snippet = html.Length > WafBodySnippetChars ? html[..WafBodySnippetChars] : html;
        return _wafEvaluator.Evaluate(new WafEvaluationInput(
            finalUrl,
            response is not null ? (int)response.Status : 200,
            headers,
            snippet));
    }

    private async Task PersistArtifactsAsync(RenderArtifactContext? context, string html, byte[] screenshot, CancellationToken cancellationToken)
    {
        if (context is null || _contentStore is null)
        {
            return;
        }

        var domBytes = Encoding.UTF8.GetBytes(html);
        await _contentStore.PutAsync(context.AuditId, context.UrlId, ContentArtifactKind.RenderedDom, new MemoryStream(domBytes), cancellationToken);
        await _contentStore.PutAsync(context.AuditId, context.UrlId, ContentArtifactKind.Screenshot, new MemoryStream(screenshot), cancellationToken);
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

    private async Task RetireDeadBrowserAsync()
    {
        if (_browser is { IsClosed: true } or { IsConnected: false })
        {
            var dead = _browser;
            _browser = null;
            try
            {
                await dead.CloseAsync();
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone; the next render launches a fresh browser.
            }
        }
    }
}
