using System.Text;
using System.Text.Json;
using PuppeteerSharp;
using PuppeteerSharp.PageCoverage;
using PuppeteerCoverage = PuppeteerSharp.PageCoverage;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Local lab-probe options. Everything is user-configured locally; the
/// browser binary is resolved from local sources only and nothing is
/// downloaded at runtime.
/// </summary>
public sealed class LabProbeOptions
{
    /// <summary>Maximum concurrent lab measurements (bounded probe).</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Default per-measurement timeout, seconds.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>Browser launch timeout, seconds.</summary>
    public int LaunchTimeoutSeconds { get; init; } = 60;

    /// <summary>Settle delay after load so LCP/CLS observers flush, milliseconds.</summary>
    public int SettleDelayMs { get; init; } = 500;

    /// <summary>Emulated locale (browser lang).</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>
    /// Optional loopback intercepting proxy endpoint: when set, ALL probe
    /// page traffic routes through it (§5.9 posture, including removal of
    /// the browser's implicit loopback bypass) so capture can prove
    /// loopback-only egress.
    /// </summary>
    public CrawlForge.Contracts.Crawl.ProxyEndpoint? Proxy { get; init; }

    /// <summary>Local browser resolution (packaged → user path → install).</summary>
    public LabBrowserResolutionOptions BrowserResolution { get; init; } = new();
}

/// <summary>
/// Bounded lab measurement probe (WP-03-06, orchestrator-approved design):
/// instrumented local renders capturing lab LCP/CLS/INP-proxy/TBT/FCP via
/// PerformanceObserver, JS/CSS code coverage via CDP, and page composition
/// via computed-style evaluation. Idle-lazy: no browser launches until the
/// first measurement (and never when the category is disabled — the
/// analyzers guard before calling). Offline: navigates only to the supplied
/// crawl-target URL; the launch posture below suppresses all vendor
/// background traffic. Failures are isolated per page.
/// </summary>
public sealed class LabRenderingProbe : ILabVitalsProbe, IAsyncDisposable
{
    // ── Zero-vendor-egress launch posture ─────────────────────────────────
    // Copied VERBATIM from CrawlForge.Crawler\Rendering\HeadlessRenderingPool.cs
    // (WP-02-06, phase-02 accepted) — that file is the source of truth.
    // Audit does not reference Crawler (WP-03-D3 design decision); the
    // real-browser loopback-only traffic tests are the empirical drift guard.
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

    private const string ObserverScript = """
        window.__cfLab = { lcp: 0, cls: 0, tbt: 0, longestTask: 0, inp: 0, hasEvents: false };
        try {
          new PerformanceObserver((list) => {
            for (const e of list.getEntries()) {
              const t = e.renderTime || e.loadTime || e.startTime;
              if (t > window.__cfLab.lcp) window.__cfLab.lcp = t;
            }
          }).observe({ type: 'largest-contentful-paint', buffered: true });
        } catch (e) {}
        try {
          new PerformanceObserver((list) => {
            for (const e of list.getEntries()) {
              if (!e.hadRecentInput) window.__cfLab.cls += e.value;
            }
          }).observe({ type: 'layout-shift', buffered: true });
        } catch (e) {}
        try {
          new PerformanceObserver((list) => {
            for (const e of list.getEntries()) {
              if (e.duration > 50) window.__cfLab.tbt += e.duration - 50;
              if (e.duration > window.__cfLab.longestTask) window.__cfLab.longestTask = e.duration;
            }
          }).observe({ type: 'longtask', buffered: true });
        } catch (e) {}
        try {
          new PerformanceObserver((list) => {
            for (const e of list.getEntries()) {
              window.__cfLab.hasEvents = true;
              if (e.duration > window.__cfLab.inp) window.__cfLab.inp = e.duration;
            }
          }).observe({ type: 'event', buffered: true, durationThreshold: 16 });
        } catch (e) {}
        """;

    private const string VitalsCollectionScript = """
        () => {
          const lab = window.__cfLab || {};
          let fcp = null;
          try {
            for (const p of performance.getEntriesByType('paint')) {
              if (p.name === 'first-contentful-paint') fcp = p.startTime;
            }
          } catch (e) {}
          const inp = lab.hasEvents ? lab.inp : Math.max(0, (lab.longestTask || 0) - 50);
          return JSON.stringify({
            lcp: lab.lcp > 0 ? lab.lcp : null,
            cls: typeof lab.cls === 'number' ? lab.cls : null,
            tbt: lab.tbt || 0,
            inp: inp,
            fcp: fcp,
            inpBasis: lab.hasEvents ? 'event-timing' : 'long-task-blocking'
          });
        }
        """;

    private const string CompositionScript = """
        () => {
          let nodes = 0, maxDepth = 0, maxWidth = 0, images = 0, minFont = null;
          const walk = (el, depth) => {
            nodes++;
            if (depth > maxDepth) maxDepth = depth;
            if (el.children.length > maxWidth) maxWidth = el.children.length;
            if (el.tagName === 'IMG') images++;
            for (const child of el.childNodes) {
              if (child.nodeType === 3 && child.textContent.trim()) {
                const fs = parseFloat(getComputedStyle(el).fontSize);
                if (!isNaN(fs) && (minFont === null || fs < minFont)) minFont = fs;
                break;
              }
            }
            for (const child of el.children) walk(child, depth + 1);
          };
          if (document.documentElement) walk(document.documentElement, 1);
          let imageBytes = 0;
          try {
            for (const r of performance.getEntriesByType('resource')) {
              if (r.initiatorType === 'img') imageBytes += (r.transferSize || r.encodedBodySize || 0);
            }
          } catch (e) {}
          return JSON.stringify({ nodes, maxDepth, maxWidth, images, imageBytes, minFont });
        }
        """;

    private readonly LabProbeOptions _options;
    private readonly LocalBrowserLocator _locator;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    private IBrowser? _browser;
    private bool _disposed;

    /// <summary>Creates the probe. No browser is launched (idle-lazy).</summary>
    /// <param name="options">Local options; null uses bounded defaults.</param>
    public LabRenderingProbe(LabProbeOptions? options = null)
    {
        _options = options ?? new LabProbeOptions();
        if (_options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        }

        _locator = new LocalBrowserLocator(_options.BrowserResolution);
        _concurrencyGate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    /// <inheritdoc />
    public async Task<LabVitalsSample> CollectAsync(
        LabVitalsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LabProbeException(LabProbeFailureKind.Navigation, $"Lab target '{request.Url}' is not an absolute http(s) URL.");
        }

        await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeoutSeconds = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : _options.DefaultTimeoutSeconds;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var token = timeoutCts.Token;

            try
            {
                var browser = await EnsureBrowserAsync(token).ConfigureAwait(false);
                return await MeasureWithPageAsync(browser, request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LabProbeException(LabProbeFailureKind.Timeout, $"Lab measurement of '{request.Url}' exceeded {timeoutSeconds}s.");
            }
            catch (LabProbeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LabProbeException(LabProbeFailureKind.Navigation, $"Lab measurement of '{request.Url}' failed: {ex.Message}", ex);
            }
            finally
            {
                await RetireDeadBrowserAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

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
                await _browser.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Browser already gone.
            }

            _browser = null;
        }

        _concurrencyGate.Dispose();
        _browserLock.Dispose();
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is { IsClosed: false, IsConnected: true })
            {
                return _browser;
            }

            _browser = null;
            var executablePath = _locator.Resolve();
            var args = new List<string>(BoundaryHardeningArgs)
            {
                $"--lang={_options.Locale}",
            };
            if (_options.Proxy is { } proxy)
            {
                args.Add($"--proxy-server={proxy.Host}:{proxy.Port}");
                // Remove the browser's implicit loopback bypass so local
                // crawl targets also route through the intercepting proxy.
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
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new LabProbeException(
                    LabProbeFailureKind.BrowserUnavailable,
                    $"Local browser launch failed ({ex.Message}). No runtime download is attempted.",
                    ex);
            }

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task<LabVitalsSample> MeasureWithPageAsync(
        IBrowser browser,
        LabVitalsRequest request,
        CancellationToken cancellationToken)
    {
        var page = await browser.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = request.Device.ViewportWidth,
                Height = request.Device.ViewportHeight,
                IsMobile = request.Device.IsMobile,
                HasTouch = request.Device.IsMobile,
                DeviceScaleFactor = 1,
            }).ConfigureAwait(false);
            await page.SetUserAgentAsync(request.Device.UserAgent).ConfigureAwait(false);

            // Observers must be registered before page scripts run.
            await page.EvaluateExpressionOnNewDocumentAsync(ObserverScript).ConfigureAwait(false);

            if (request.CaptureCoverage)
            {
                await page.Coverage.StartJSCoverageAsync(new CoverageStartOptions
                {
                    ResetOnNavigation = true,
                }).ConfigureAwait(false);
                await page.Coverage.StartCSSCoverageAsync(new CoverageStartOptions
                {
                    ResetOnNavigation = true,
                }).ConfigureAwait(false);
            }

            await page.GoToAsync(request.Url, new NavigationOptions
            {
                Timeout = _options.DefaultTimeoutSeconds * 1000,
                WaitUntil = [WaitUntilNavigation.Load],
            }).ConfigureAwait(false);

            // Bounded settle so LCP/CLS observers flush their final entries.
            await Task.Delay(_options.SettleDelayMs, cancellationToken).ConfigureAwait(false);

            var vitalsJson = await page.EvaluateFunctionAsync<string>(VitalsCollectionScript).ConfigureAwait(false);
            var compositionJson = await page.EvaluateFunctionAsync<string>(CompositionScript).ConfigureAwait(false);

            CodeCoverageSample? coverage = null;
            if (request.CaptureCoverage)
            {
                var jsEntries = await page.Coverage.StopJSCoverageAsync().ConfigureAwait(false);
                var cssEntries = await page.Coverage.StopCSSCoverageAsync().ConfigureAwait(false);
                coverage = BuildCoverageSample(request.Url, jsEntries, cssEntries);
            }

            return BuildSample(vitalsJson, compositionJson, coverage);
        }
        finally
        {
            try
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Page isolation: a failed close never masks the measurement.
            }
        }
    }

    private static LabVitalsSample BuildSample(string vitalsJson, string compositionJson, CodeCoverageSample? coverage)
    {
        using var vitals = JsonDocument.Parse(vitalsJson);
        var root = vitals.RootElement;
        double? lcp = root.TryGetProperty("lcp", out var lcpProperty) && lcpProperty.ValueKind == JsonValueKind.Number
            ? lcpProperty.GetDouble()
            : null;
        double? cls = root.TryGetProperty("cls", out var clsProperty) && clsProperty.ValueKind == JsonValueKind.Number
            ? clsProperty.GetDouble()
            : null;
        double? inp = root.TryGetProperty("inp", out var inpProperty) && inpProperty.ValueKind == JsonValueKind.Number
            ? inpProperty.GetDouble()
            : null;
        double? tbt = root.TryGetProperty("tbt", out var tbtProperty) && tbtProperty.ValueKind == JsonValueKind.Number
            ? tbtProperty.GetDouble()
            : null;
        double? fcp = root.TryGetProperty("fcp", out var fcpProperty) && fcpProperty.ValueKind == JsonValueKind.Number
            ? fcpProperty.GetDouble()
            : null;

        PageCompositionSample? composition = null;
        using (var compositionDocument = JsonDocument.Parse(compositionJson))
        {
            var c = compositionDocument.RootElement;
            composition = new PageCompositionSample(
                c.TryGetProperty("nodes", out var nodes) ? nodes.GetInt32() : 0,
                c.TryGetProperty("maxDepth", out var depth) ? depth.GetInt32() : 0,
                c.TryGetProperty("maxWidth", out var width) ? width.GetInt32() : 0,
                c.TryGetProperty("images", out var images) ? images.GetInt32() : 0,
                c.TryGetProperty("imageBytes", out var imageBytes) ? imageBytes.GetInt64() : 0,
                c.TryGetProperty("minFont", out var minFont) && minFont.ValueKind == JsonValueKind.Number
                    ? minFont.GetDouble()
                    : null);
        }

        return new LabVitalsSample(lcp, cls, inp, tbt, fcp, coverage, composition);
    }

    private static CodeCoverageSample BuildCoverageSample(
        string pageUrl,
        PuppeteerCoverage.CoverageEntry[] jsEntries,
        PuppeteerCoverage.CoverageEntry[] cssEntries)
    {
        var entries = new List<CoverageEntry>();
        long jsUsed = 0, jsTotal = 0, cssUsed = 0, cssTotal = 0;

        foreach (var entry in jsEntries)
        {
            var used = UnionLength(entry.Ranges);
            var total = Encoding.UTF8.GetByteCount(entry.Text ?? string.Empty);
            jsUsed += used;
            jsTotal += total;
            entries.Add(new CoverageEntry(
                string.IsNullOrEmpty(entry.Url) ? pageUrl + " (inline)" : entry.Url, "js", used, total));
        }

        foreach (var entry in cssEntries)
        {
            var used = UnionLength(entry.Ranges);
            var total = Encoding.UTF8.GetByteCount(entry.Text ?? string.Empty);
            cssUsed += used;
            cssTotal += total;
            entries.Add(new CoverageEntry(
                string.IsNullOrEmpty(entry.Url) ? pageUrl + " (inline)" : entry.Url, "css", used, total));
        }

        return new CodeCoverageSample(jsUsed, jsTotal, cssUsed, cssTotal, entries);
    }

    /// <summary>Union length of coverage ranges (ranges may nest/overlap).</summary>
    private static long UnionLength(PuppeteerCoverage.CoverageEntryRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return 0;
        }

        long used = 0;
        long currentStart = -1;
        long currentEnd = -1;
        foreach (var range in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (currentStart < 0)
            {
                currentStart = range.Start;
                currentEnd = range.End;
            }
            else if (range.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, range.End);
            }
            else
            {
                used += currentEnd - currentStart;
                currentStart = range.Start;
                currentEnd = range.End;
            }
        }

        if (currentStart >= 0)
        {
            used += currentEnd - currentStart;
        }

        return used;
    }

    private async Task RetireDeadBrowserAsync()
    {
        if (_browser is { IsClosed: true } or { IsConnected: false })
        {
            var dead = _browser;
            _browser = null;
            try
            {
                await dead.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or PuppeteerException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone; the next measurement launches a fresh browser.
            }
        }
    }
}
