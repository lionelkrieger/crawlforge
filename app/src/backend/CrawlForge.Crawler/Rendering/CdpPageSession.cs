using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using Microsoft.Extensions.Logging;
using ConsoleMessage = CrawlForge.Contracts.Crawl.ConsoleMessage;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// One page rendered through direct CDP (FS-CRAWL-001 §5.6.2, D-002/CR-006):
/// Target/Page/Network/Runtime (+ServiceWorker/Fetch) domains on a flat-mode
/// session inside a FRESH browser context. Captures the post-render DOM, a PNG
/// screenshot, console messages and runtime exceptions, and the network
/// waterfall DIRECTLY from Network-domain events (the loopback proxy is not
/// used for rendered traffic). Service workers are bypassed on every render
/// (§7.19) and the registration fact is exposed.
/// </summary>
public sealed class CdpPageSession : IAsyncDisposable
{
    private const int PollIntervalMs = 50;

    private const string DomQuietObserverScript = """
        (() => {
          if (window.__cfQuiet) return;
          const state = { last: performance.now(), count: 0 };
          window.__cfQuiet = state;
          const observer = new MutationObserver(() => { state.last = performance.now(); state.count++; });
          observer.observe(document, { childList: true, subtree: true, attributes: true, characterData: true });
        })();
        """;

    private const string DomQuietStateExpression = """
        (() => {
          const s = window.__cfQuiet;
          return { ready: document.readyState, domQuietMs: s ? Math.max(0, performance.now() - s.last) : -1, count: s ? s.count : -1 };
        })()
        """;

    private const string DomHtmlExpression = """
        (() => {
          const dt = document.doctype;
          let doctype = '';
          if (dt) {
            doctype = '<!DOCTYPE ' + dt.name;
            if (dt.publicId) doctype += ' PUBLIC "' + dt.publicId + '"';
            if (dt.systemId) doctype += ' "' + dt.systemId + '"';
            doctype += '>';
          }
          return doctype + document.documentElement.outerHTML;
        })()
        """;

    private const string ScrollHeightExpression =
        "Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement ? document.documentElement.scrollHeight : 0)";

    private const string ServiceWorkerQueryExpression = """
        (async () => {
          if (!navigator.serviceWorker) return [];
          const regs = await navigator.serviceWorker.getRegistrations();
          return regs.map(r => ({
            scope: r.scope,
            script: (r.active && r.active.scriptURL) || (r.waiting && r.waiting.scriptURL) || (r.installing && r.installing.scriptURL) || null
          }));
        })()
        """;

    private readonly CdpBrowser _browser;
    private readonly RenderingPoolOptions _options;
    private readonly ILogger? _logger;
    private readonly List<ConsoleMessage> _consoleMessages = [];
    private readonly Dictionary<string, ServiceWorkerRegistrationFact> _serviceWorkers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inScopeOrigins;
    private readonly HashSet<string> _blockedResourceTypes;
    private readonly RenderWaitRule _waitRule;
    private readonly Action<CdpEvent> _eventHandler;
    private bool _sawNavigationActivity;
    private int _disposed;

    /// <summary>Creates the page session (nothing is enabled until InitializeAsync).</summary>
    /// <param name="browser">Owning browser connection.</param>
    /// <param name="targetId">Page target id.</param>
    /// <param name="sessionId">Flat-mode session id.</param>
    /// <param name="browserContextId">Fresh owning context.</param>
    /// <param name="options">Local pool options.</param>
    /// <param name="logger">Optional local-only logger.</param>
    public CdpPageSession(
        CdpBrowser browser,
        string targetId,
        string sessionId,
        string browserContextId,
        RenderingPoolOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(options);
        _browser = browser;
        TargetId = targetId;
        SessionId = sessionId;
        BrowserContextId = browserContextId;
        _options = options;
        _logger = logger;
        Recorder = new NetworkWaterfallRecorder(options.MaxNetworkEntries, options.StreamingPendingThresholdMs);
        _waitRule = new RenderWaitRule(options.RenderWaitQuietMs, options.RenderWaitHardCapMs);
        _inScopeOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in options.InScopeOrigins)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                _inScopeOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        _blockedResourceTypes = new HashSet<string>(options.BlockedResourceTypes, StringComparer.OrdinalIgnoreCase);
        _eventHandler = OnEvent;
        _browser.Connection.EventReceived += _eventHandler;
    }

    /// <summary>The page target id.</summary>
    public string TargetId { get; }

    /// <summary>The flat-mode session id.</summary>
    public string SessionId { get; }

    /// <summary>The fresh browser context owning this page.</summary>
    public string BrowserContextId { get; }

    /// <summary>The direct network capture feeding the waterfall and wait rule.</summary>
    public NetworkWaterfallRecorder Recorder { get; }

    /// <summary>Console messages and runtime exceptions captured so far (bounded).</summary>
    public IReadOnlyList<ConsoleMessage> ConsoleMessages
    {
        get
        {
            lock (_consoleMessages)
            {
                return [.. _consoleMessages];
            }
        }
    }

    /// <summary>Service-worker registrations observed (renders bypass them; §7.19).</summary>
    public IReadOnlyList<ServiceWorkerRegistrationFact> ServiceWorkerRegistrations
    {
        get
        {
            lock (_serviceWorkers)
            {
                return [.. _serviceWorkers.Values];
            }
        }
    }

    /// <summary>
    /// Enables the CDP domains and applies emulation, the header split, the
    /// stealth profile, resource blocking and the service-worker bypass.
    /// </summary>
    /// <param name="request">Render request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        await SendAsync("Page.enable", null, cancellationToken);
        await SendAsync("Network.enable", null, cancellationToken);
        await SendAsync("Runtime.enable", null, cancellationToken);
        await SendAsync("ServiceWorker.enable", null, cancellationToken);

        // §7.19: service workers are bypassed on EVERY render.
        await SendAsync("Network.setBypassServiceWorker", new Dictionary<string, object?>
        {
            ["bypass"] = true,
        }, cancellationToken);

        await SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object?>
        {
            ["width"] = request.Device.ViewportWidth,
            ["height"] = request.Device.ViewportHeight,
            ["deviceScaleFactor"] = 1,
            ["mobile"] = request.Device.IsMobile,
        }, cancellationToken);

        await SendAsync("Emulation.setUserAgentOverride", new Dictionary<string, object?>
        {
            ["userAgent"] = request.Device.UserAgent,
            ["acceptLanguage"] = _options.Locale,
        }, cancellationToken);

        var nonCredential = new Dictionary<string, string>(_options.NonCredentialHeaders, StringComparer.OrdinalIgnoreCase);
        if (!nonCredential.ContainsKey("Accept-Language"))
        {
            nonCredential["Accept-Language"] = _options.Locale;
        }

        await SendAsync("Network.setExtraHTTPHeaders", new Dictionary<string, object?>
        {
            ["headers"] = nonCredential,
        }, cancellationToken);

        // The DOM-quiet observer runs before page scripts on every document.
        await SendAsync("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?>
        {
            ["source"] = DomQuietObserverScript,
        }, cancellationToken);

        if (request.Stealth)
        {
            await SendAsync("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?>
            {
                ["source"] = StealthProfile.Script,
            }, cancellationToken);
        }

        // Fetch interception carries the credential-header split (§5.6) and
        // the opt-in resource-blocking profile (§5.6.2). The default profile
        // blocks nothing and sends no credential headers → no interception.
        if (_options.CredentialHeaders.Count > 0 || _blockedResourceTypes.Count > 0)
        {
            await SendAsync("Fetch.enable", new Dictionary<string, object?>
            {
                ["patterns"] = new object[]
                {
                    new Dictionary<string, object?> { ["urlPattern"] = "*", ["requestStage"] = "Request" },
                },
            }, cancellationToken);
        }
    }

    /// <summary>Navigates the page (Page.navigate).</summary>
    /// <param name="url">Absolute http(s) target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RenderFailureException">Navigation failed locally (net error).</exception>
    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        _sawNavigationActivity = false;
        var result = await SendAsync("Page.navigate", new Dictionary<string, object?>
        {
            ["url"] = url,
        }, cancellationToken);

        if (result.TryGetProperty("errorText", out var errorText)
            && errorText.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(errorText.GetString()))
        {
            throw new RenderFailureException(RenderFailureKind.Navigation, $"Navigation to '{url}' failed: {errorText.GetString()}.");
        }
    }

    /// <summary>
    /// Waits for the §5.6.2 wait rule: DOM + network quiet for the configured
    /// period, subject to the hard cap. Streaming requests are excluded.
    /// </summary>
    /// <param name="navigationClock">Elapsed-time source (navigation start or scroll step start).</param>
    /// <param name="requireNavigationActivity">Whether a navigation commit must be observed before settling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="domMutationBaseline">
    /// Scroll-step gate: when set, settling additionally requires either a DOM
    /// mutation beyond this count (scroll-triggered lazy content arrived) or
    /// one quiet-period observation window with no mutation (content truly
    /// stable). Without this gate a step could settle before the scroll's
    /// asynchronous content append mutates the DOM.
    /// </param>
    /// <returns>True when settled; false when the hard cap elapsed.</returns>
    public async Task<bool> WaitForSettleAsync(Stopwatch navigationClock, bool requireNavigationActivity, CancellationToken cancellationToken = default, long? domMutationBaseline = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (domReady, domQuietMs, mutations) = await ReadDomQuietStateAsync(cancellationToken);
            var decision = _waitRule.Evaluate(
                domReady,
                domQuietMs,
                Recorder.InFlightNonStreamingCount,
                Recorder.LastActivityAgeMs,
                navigationClock.ElapsedMilliseconds);

            if (decision == RenderWaitDecision.Settled && mutations < 0)
            {
                // The quiet observer is absent from the current document —
                // the poll hit the pre-commit predecessor (initial about:blank),
                // not the navigated page. No DOM quiescence was actually
                // observed; keep waiting for the observer-bearing document
                // (the hard cap still bounds the wait). Without this gate a
                // loaded machine could settle before the page committed.
                decision = RenderWaitDecision.Waiting;
            }

            if (decision == RenderWaitDecision.Settled && requireNavigationActivity && !_sawNavigationActivity)
            {
                decision = RenderWaitDecision.Waiting;
            }

            if (decision == RenderWaitDecision.Settled
                && domMutationBaseline is { } baseline
                && mutations >= 0
                && mutations <= baseline
                && navigationClock.ElapsedMilliseconds < _waitRule.QuietMs)
            {
                // No scroll-triggered mutation yet; keep observing for one
                // quiet-period window before accepting stability.
                decision = RenderWaitDecision.Waiting;
            }

            if (decision == RenderWaitDecision.Settled)
            {
                return true;
            }

            if (decision == RenderWaitDecision.HardCapReached)
            {
                _logger?.LogDebug("Render wait reached the hard cap ({CapMs}ms); capturing current state.", _waitRule.HardCapMs);
                return false;
            }

            await Task.Delay(PollIntervalMs, cancellationToken);
        }
    }

    /// <summary>
    /// Drives bounded infinite-scroll expansion (§5.6): scroll until the
    /// content height stabilizes or the iteration bound is reached. Each step
    /// settles through the same wait rule — no fixed settle delays.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DriveInfiniteScrollAsync(CancellationToken cancellationToken = default)
    {
        long lastHeight = -1;
        var stableChecks = 0;

        for (var iteration = 0; iteration < _options.InfiniteScrollMaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var height = await EvaluateLongAsync(ScrollHeightExpression, cancellationToken);
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

            // Observe scroll-triggered lazy content: gate settling on a DOM
            // mutation beyond the pre-scroll count (or one quiet window with
            // no mutation = genuinely stable content).
            var (_, _, mutationsBeforeScroll) = await ReadDomQuietStateAsync(cancellationToken);
            await EvaluateVoidAsync($"window.scrollTo(0, {height})", cancellationToken);
            await WaitForSettleAsync(Stopwatch.StartNew(), requireNavigationActivity: false, cancellationToken, domMutationBaseline: mutationsBeforeScroll);
        }

        _logger?.LogDebug("Infinite scroll stopped at the iteration bound ({Bound}).", _options.InfiniteScrollMaxIterations);
    }

    /// <summary>
    /// Captures the post-render DOM, PNG screenshot and final URL, and merges
    /// any service-worker registrations visible to the page.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page capture.</returns>
    public async Task<PageCapture> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var html = await EvaluateStringAsync(DomHtmlExpression, cancellationToken);
        var finalUrl = await EvaluateStringAsync("location.href", cancellationToken);

        byte[] screenshot;
        var screenshotResult = await SendAsync("Page.captureScreenshot", new Dictionary<string, object?>
        {
            ["format"] = "png",
        }, cancellationToken);
        var base64 = screenshotResult.TryGetProperty("data", out var data) ? data.GetString() : null;
        screenshot = string.IsNullOrEmpty(base64) ? [] : Convert.FromBase64String(base64);

        await MergeServiceWorkerRegistrationsAsync(cancellationToken);
        return new PageCapture(finalUrl, html, screenshot);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _browser.Connection.EventReceived -= _eventHandler;
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _browser.CloseTargetAsync(TargetId, cleanupCts.Token);
        }
        catch (Exception ex) when (ex is CdpCommandException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Page isolation: a failed close never masks the render result.
        }

        try
        {
            await _browser.DisposeBrowserContextAsync(BrowserContextId, cleanupCts.Token);
        }
        catch (Exception ex) when (ex is CdpCommandException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Context already gone with the browser.
        }
    }

    private Task<JsonElement> SendAsync(string method, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
        _browser.SendAsync(method, parameters, SessionId, cancellationToken);

    private void OnEvent(CdpEvent cdpEvent)
    {
        if (!string.Equals(cdpEvent.SessionId, SessionId, StringComparison.Ordinal))
        {
            return;
        }

        Recorder.OnEvent(cdpEvent);
        switch (cdpEvent.Method)
        {
            case "Network.requestWillBeSent":
            case "Page.frameNavigated":
            case "Page.loadEventFired":
                _sawNavigationActivity = true;
                break;
            case "Runtime.consoleAPICalled":
                OnConsoleApiCalled(cdpEvent.Params);
                break;
            case "Runtime.exceptionThrown":
                OnExceptionThrown(cdpEvent.Params);
                break;
            case "ServiceWorker.workerRegistrationUpdated":
                OnServiceWorkerRegistrationUpdated(cdpEvent.Params);
                break;
            case "ServiceWorker.workerVersionUpdated":
                OnServiceWorkerVersionUpdated(cdpEvent.Params);
                break;
            case "Fetch.requestPaused":
                _ = HandleRequestPausedAsync(cdpEvent.Params);
                break;
        }
    }

    private void OnConsoleApiCalled(JsonElement parameters)
    {
        var level = parameters.TryGetProperty("type", out var type) ? type.GetString() ?? "log" : "log";
        var text = new StringBuilder();
        if (parameters.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in args.EnumerateArray())
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(DescribeRemoteObject(arg));
            }
        }

        AddConsoleMessage(level.ToLowerInvariant(), text.ToString());
    }

    private void OnExceptionThrown(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("exceptionDetails", out var details))
        {
            return;
        }

        var text = details.TryGetProperty("text", out var t) ? t.GetString() ?? "Runtime exception" : "Runtime exception";
        if (details.TryGetProperty("exception", out var exception))
        {
            var description = DescribeRemoteObject(exception);
            if (!string.IsNullOrEmpty(description))
            {
                text = description;
            }
        }

        AddConsoleMessage("error", text);
    }

    private void AddConsoleMessage(string level, string text)
    {
        lock (_consoleMessages)
        {
            if (_consoleMessages.Count < _options.MaxConsoleMessages)
            {
                _consoleMessages.Add(new ConsoleMessage(level, text, DateTimeOffset.UtcNow));
            }
        }
    }

    private static string DescribeRemoteObject(JsonElement arg)
    {
        if (arg.ValueKind != JsonValueKind.Object)
        {
            return arg.ToString();
        }

        if (arg.TryGetProperty("value", out var value) && value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        if (arg.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
        {
            return description.GetString() ?? string.Empty;
        }

        if (arg.TryGetProperty("unserializableValue", out var unserializable) && unserializable.ValueKind == JsonValueKind.String)
        {
            return unserializable.GetString() ?? string.Empty;
        }

        return arg.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty;
    }

    private void OnServiceWorkerRegistrationUpdated(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("registrations", out var registrations) || registrations.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (_serviceWorkers)
        {
            foreach (var registration in registrations.EnumerateArray())
            {
                var id = registration.TryGetProperty("registrationId", out var rid) ? rid.GetString() : null;
                var scope = registration.TryGetProperty("scopeURL", out var scopeUrl) ? scopeUrl.GetString() : null;
                if (id is null || scope is null)
                {
                    continue;
                }

                if (registration.TryGetProperty("isDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
                {
                    _serviceWorkers.Remove(id);
                }
                else
                {
                    _serviceWorkers[id] = new ServiceWorkerRegistrationFact(scope, null);
                }
            }
        }
    }

    private void OnServiceWorkerVersionUpdated(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (_serviceWorkers)
        {
            foreach (var version in versions.EnumerateArray())
            {
                var registrationId = version.TryGetProperty("registrationId", out var rid) ? rid.GetString() : null;
                var scriptUrl = version.TryGetProperty("scriptURL", out var script) ? script.GetString() : null;
                if (registrationId is null || scriptUrl is null)
                {
                    continue;
                }

                if (_serviceWorkers.TryGetValue(registrationId, out var existing))
                {
                    _serviceWorkers[registrationId] = existing with { ScriptUrl = scriptUrl };
                }
            }
        }
    }

    private async Task MergeServiceWorkerRegistrationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = ServiceWorkerQueryExpression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
            }, cancellationToken);

            if (!result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            lock (_serviceWorkers)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    var scope = entry.TryGetProperty("scope", out var s) ? s.GetString() : null;
                    if (scope is null)
                    {
                        continue;
                    }

                    var script = entry.TryGetProperty("script", out var scriptElement) && scriptElement.ValueKind == JsonValueKind.String
                        ? scriptElement.GetString()
                        : null;
                    var existing = _serviceWorkers.Values.FirstOrDefault(f => f.ScopeUrl == scope);
                    if (existing is null)
                    {
                        _serviceWorkers["page:" + scope] = new ServiceWorkerRegistrationFact(scope, script);
                    }
                    else if (existing.ScriptUrl is null && script is not null)
                    {
                        var key = _serviceWorkers.First(pair => pair.Value.ScopeUrl == scope).Key;
                        _serviceWorkers[key] = existing with { ScriptUrl = script };
                    }
                }
            }
        }
        catch (Exception ex) when (ex is CdpCommandException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best-effort fact: event-based registrations already captured.
        }
    }

    private async Task HandleRequestPausedAsync(JsonElement parameters)
    {
        try
        {
            var requestId = parameters.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
            if (requestId is null)
            {
                return;
            }

            var resourceType = parameters.TryGetProperty("resourceType", out var type) ? type.GetString() ?? "Other" : "Other";
            if (_blockedResourceTypes.Contains(resourceType))
            {
                await SendAsync("Fetch.failRequest", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["errorReason"] = "BlockedByClient",
                }, CancellationToken.None);
                return;
            }

            var request = parameters.TryGetProperty("request", out var r) ? r : default;
            var url = request.TryGetProperty("url", out var u) ? u.GetString() : null;
            var isInScope = url is not null
                && Uri.TryCreate(url, UriKind.Absolute, out var requestUri)
                && _inScopeOrigins.Contains(requestUri.GetLeftPart(UriPartial.Authority));

            if (_options.CredentialHeaders.Count > 0 && isInScope)
            {
                // Credential-bearing headers go ONLY to in-scope origins (§5.6).
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (request.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var header in headerElement.EnumerateObject())
                    {
                        headers[header.Name] = header.Value.GetString() ?? string.Empty;
                    }
                }

                foreach (var (name, value) in _options.CredentialHeaders)
                {
                    headers[name] = value;
                }

                await SendAsync("Fetch.continueRequest", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["headers"] = headers.Select(pair => new Dictionary<string, object?>
                    {
                        ["name"] = pair.Key,
                        ["value"] = pair.Value,
                    }).ToArray(),
                }, CancellationToken.None);
                return;
            }

            await SendAsync("Fetch.continueRequest", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is CdpCommandException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Page closed mid-interception; nothing to forward.
        }
    }

    private async Task<(bool Ready, long QuietMs, long Mutations)> ReadDomQuietStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = DomQuietStateExpression,
                ["returnByValue"] = true,
            }, cancellationToken);

            if (result.TryGetProperty("result", out var remote) && remote.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Object)
            {
                var readyState = value.TryGetProperty("ready", out var ready) ? ready.GetString() : null;
                var domReady = readyState is "interactive" or "complete";
                // domQuietMs is a DOMHighResTimeStamp difference — FRACTIONAL
                // milliseconds. TryGetInt64 fails on fractional JSON numbers,
                // which previously mapped active pages to "infinitely quiet"
                // and let the wait rule settle mid-mutation under load
                // (WP-10-01 hard-cap flake root cause). Parse as double.
                var quietMs = value.TryGetProperty("domQuietMs", out var quiet) && quiet.TryGetDouble(out var q) ? (long)Math.Max(0, q) : -1;
                var mutations = value.TryGetProperty("count", out var count) && count.TryGetInt64(out var c) ? c : -1;
                // count -1: the quiet observer is absent from the CURRENT
                // document. That is the pre-commit predecessor (the target's
                // initial about:blank) or an exotic document — NOT the
                // navigated page. The wait rule must not claim DOM quiescence
                // it never observed; WaitForSettleAsync keeps waiting until
                // the observer-bearing document commits (hard cap still bounds
                // the wait).
                return (domReady, quietMs < 0 ? long.MaxValue : quietMs, mutations);
            }
        }
        catch (CdpCommandException)
        {
            // Execution context destroyed mid-navigation; keep polling.
        }

        return (false, 0, -1);
    }

    private async Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, cancellationToken);

        if (result.TryGetProperty("exceptionDetails", out _))
        {
            throw new RenderFailureException(RenderFailureKind.Navigation, $"Page evaluation failed: {expression}");
        }

        return result.TryGetProperty("result", out var remote) && remote.TryGetProperty("value", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task<long> EvaluateLongAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, cancellationToken);

        return result.TryGetProperty("result", out var remote)
            && remote.TryGetProperty("value", out var value)
            && value.TryGetInt64(out var number)
                ? number
                : 0;
    }

    private async Task EvaluateVoidAsync(string expression, CancellationToken cancellationToken)
    {
        await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, cancellationToken);
    }
}
