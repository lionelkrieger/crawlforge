using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// One locally launched headless Chromium-class browser process driven through
/// direct CDP (FS-CRAWL-001 §5.6.2/§5.6.3, D-002/D-003). The executable comes
/// from the LOCAL resolver only — no runtime browser or driver download exists.
/// The DevTools endpoint is an ephemeral LOOPBACK port chosen by the browser
/// (<c>--remote-debugging-port=0</c>); the process model opens no other listener.
/// </summary>
public sealed class CdpBrowserProcess : IAsyncDisposable
{
    private static readonly Regex DevToolsEndpointRegex = new(
        @"DevTools listening on (ws://127\.0\.0\.1:\d+/devtools/browser/[A-Za-z0-9\-]+)",
        RegexOptions.Compiled);

    private readonly ILogger? _logger;
    private readonly string _userDataDirectory;
    private readonly bool _ownsUserDataDirectory;
    private Process? _process;
    private Task? _stderrPump;
    private int _disposed;

    /// <summary>Creates the process wrapper (nothing is launched yet).</summary>
    /// <param name="executablePath">Resolved LOCAL browser executable.</param>
    /// <param name="userDataDirectory">Fresh per-process profile directory.</param>
    /// <param name="ownsUserDataDirectory">Whether dispose deletes the profile directory.</param>
    /// <param name="logger">Optional local-only logger.</param>
    public CdpBrowserProcess(string executablePath, string userDataDirectory, bool ownsUserDataDirectory = true, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ExecutablePath = executablePath;
        _userDataDirectory = userDataDirectory;
        _ownsUserDataDirectory = ownsUserDataDirectory;
        _logger = logger;
    }

    /// <summary>The resolved local executable path.</summary>
    public string ExecutablePath { get; }

    /// <summary>The loopback DevTools WebSocket endpoint (available after launch).</summary>
    public Uri? DevToolsEndpoint { get; private set; }

    /// <summary>Whether the process is still running.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Boundary-hardening launch arguments: the browser emits no vendor
    /// background traffic (no update pings, no component fetches, no metrics
    /// upload, no experiments). Reused verbatim from the accepted WP-02-06
    /// posture so the zero-egress evidence carries over to direct CDP.
    /// </summary>
    public static IReadOnlyList<string> BoundaryHardeningArgs { get; } =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        "--disable-background-timer-throttling",
        "--disable-backgrounding-occluded-windows",
        "--disable-renderer-backgrounding",
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
        "--safebrowsing-disable-auto-update",
        "--disable-features=Translate,MediaRouter,NetworkTimeServiceQuerying,CertVerificationNetworkTime,PreconnectFromKeyedService,AccountPreviewDataPreconnect,EarlyCookieLoadOnPreconnect,OptimizationHints,OptimizationGuideFetching,OptimizationGuideModelDownloading,OptimizationGuideOnDeviceModel,InterestFeedContentSuggestions,AutofillServerCommunication,CertificateTransparencyComponentUpdater,PrivacySandboxSettings4,PrivacySandboxFledge,PrivacySandboxTopics,PrivacySandboxAttributionReporting,PrivacySandboxAdsAPIs,SharedStorageAPI,AttributionReporting,ChromeWhatsNewUI,SearchEngineChoice,DsePreload,DsePreload2,ContextualTasks,ContextualTasksCookiePrefetch,SearchCompanion,SafeBrowsingOhttpKeyService,AimEnabled,AimServerEligibilityEnabled,AimServerRequestOnStartupEnabled,SafeBrowsingHashPrefixRealTimeLookups,PreconnectToSearch,PreconnectToSearchDesktop,DseIntegrity,BackForwardCache,AcceptCHFrame",
        "--enable-features=SkipListAccountsWithoutGaiaCookies",
    ];

    /// <summary>
    /// Launches the browser headless and waits for its loopback DevTools endpoint.
    /// </summary>
    /// <param name="extraArguments">Additional local arguments (locale, optional explicit proxy opt-in).</param>
    /// <param name="launchTimeout">Launch bound; exceeding it kills the process locally.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RenderFailureException">The local browser failed to launch (BrowserUnavailable).</exception>
    public async Task LaunchAsync(IEnumerable<string> extraArguments, TimeSpan launchTimeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Directory.CreateDirectory(_userDataDirectory);

        var arguments = new List<string>
        {
            "--headless=new",
            "--remote-debugging-port=0",
            $"--user-data-dir={_userDataDirectory}",
            "--hide-scrollbars",
            // Inert startup tab (matches the accepted LabRenderingProbe's
            // browser-automation launch, which always passes about:blank).
            // Without an initial URL, new-headless Chrome opens the New Tab
            // Page, whose search-box preconnect emits vendor CONNECT tunnels
            // through a launch proxy (www.google.com:443 etc.) — real
            // non-loopback egress. about:blank suppresses the NTP entirely,
            // keeping the strict loopback-only boundary satisfiable
            // (WP-10-01 §5.3 correction).
            "about:blank",
        };
        arguments.AddRange(BoundaryHardeningArgs);
        arguments.AddRange(extraArguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var endpointFound = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _process = Process.Start(startInfo)
                ?? throw new RenderFailureException(RenderFailureKind.BrowserUnavailable, $"Local browser '{ExecutablePath}' did not start. No runtime download is attempted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RenderFailureException)
        {
            throw new RenderFailureException(
                RenderFailureKind.BrowserUnavailable,
                $"Local browser launch failed ({ex.Message}). No runtime download is attempted.",
                ex);
        }

        _stderrPump = PumpStderrAsync(_process, endpointFound, _disposeTokenSource.Token);
        _ = PumpStdoutAsync(_process, _disposeTokenSource.Token);

        using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        launchCts.CancelAfter(launchTimeout);
        await using var registration = launchCts.Token.Register(() => endpointFound.TrySetCanceled(launchCts.Token));

        try
        {
            DevToolsEndpoint = await endpointFound.Task;
        }
        catch (OperationCanceledException)
        {
            await KillCoreAsync();
            throw new RenderFailureException(
                RenderFailureKind.BrowserUnavailable,
                $"Local browser '{ExecutablePath}' did not expose a DevTools endpoint within {(int)launchTimeout.TotalSeconds}s. No runtime download is attempted.");
        }
        catch (Exception ex)
        {
            await KillCoreAsync();
            throw new RenderFailureException(
                RenderFailureKind.BrowserUnavailable,
                $"Local browser launch failed ({ex.Message}). No runtime download is attempted.",
                ex);
        }

        _logger?.LogInformation("Headless browser launched from local binary; DevTools on loopback port {Port}.", DevToolsEndpoint.Port);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await KillCoreAsync();
        _disposeTokenSource.Cancel();
        if (_stderrPump is not null)
        {
            try
            {
                await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
                // Pump already ended with the process.
            }
        }

        _disposeTokenSource.Dispose();
        if (_ownsUserDataDirectory)
        {
            TryDeleteProfile();
        }
    }

    private readonly CancellationTokenSource _disposeTokenSource = new();

    private async Task KillCoreAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private void TryDeleteProfile()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_userDataDirectory))
                {
                    Directory.Delete(_userDataDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Profile files may linger briefly after process exit.
            }
        }
    }

    private static async Task PumpStderrAsync(Process process, TaskCompletionSource<Uri> endpointFound, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                var match = DevToolsEndpointRegex.Match(line);
                if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var endpoint))
                {
                    endpointFound.TrySetResult(endpoint);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended.
        }
        finally
        {
            endpointFound.TrySetException(new IOException("The browser process ended before exposing a DevTools endpoint."));
        }
    }

    private static async Task PumpStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended.
        }
    }
}
