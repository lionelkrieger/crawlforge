using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Rendering;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Fake browser factory: connects the REAL CDP control layer to the scripted
/// loopback CDP server instead of launching a process. No real browser, no
/// download — the composition seam the pool exposes for component tests.
/// </summary>
internal sealed class FakeCdpBrowserFactory : ICdpBrowserFactory
{
    private readonly FakeCdpServer _server;
    private int _launches;

    /// <summary>Creates the factory for one fake browser server.</summary>
    /// <param name="server">The scripted CDP server.</param>
    public FakeCdpBrowserFactory(FakeCdpServer server)
    {
        _server = server;
    }

    /// <summary>Number of browser launches requested.</summary>
    public int Launches => Volatile.Read(ref _launches);

    /// <summary>The proxy endpoint passed at the last launch (null = direct).</summary>
    public ProxyEndpoint? LastProxyEndpoint { get; private set; }

    /// <inheritdoc />
    public async Task<BrowserLease> LaunchAsync(ResolvedBrowser browser, RenderingPoolOptions options, ProxyEndpoint? proxyEndpoint, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _launches);
        LastProxyEndpoint = proxyEndpoint;
        var connection = await CdpBrowser.ConnectAsync(_server.Endpoint, cancellationToken);
        return new BrowserLease(connection, browser, process: null);
    }
}

/// <summary>
/// A fake packaged Chrome for Testing bundle layout on disk (D-003 component
/// tests): pin manifest + platform executable stub. Proves the resolver's
/// bundled-first fallback chain without any real browser binary.
/// </summary>
public sealed class FakeBundleLayout : IDisposable
{
    private FakeBundleLayout(string root, string executablePath, string? pinnedVersion)
    {
        Root = root;
        ExecutablePath = executablePath;
        PinnedVersion = pinnedVersion;
    }

    /// <summary>The fake packaged asset root (stands in for assets/browser).</summary>
    public string Root { get; }

    /// <summary>The stub executable path inside the bundle.</summary>
    public string ExecutablePath { get; }

    /// <summary>The manifest pin, when a manifest was written.</summary>
    public string? PinnedVersion { get; }

    /// <summary>Resolution options pointing at this fake bundle (no local-install probing).</summary>
    public BrowserResolutionOptions ResolutionOptions => new()
    {
        PackagedAssetsRoot = Root,
        ProbeLocalInstalls = false,
    };

    /// <summary>Creates a fake bundle with the current platform's layout.</summary>
    /// <param name="pinnedVersion">Manifest pin (null writes no manifest).</param>
    /// <param name="withExecutable">Whether the stub executable exists.</param>
    /// <param name="legacyLayout">Use the flat chrome.exe layout instead of the manifest platform path.</param>
    /// <returns>The disposable fake bundle.</returns>
    public static FakeBundleLayout Create(string? pinnedVersion = "131.0.6778.85", bool withExecutable = true, bool legacyLayout = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-10-01-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var platformKey = BrowserPinManifest.CurrentPlatformKey();
        var relativePath = platformKey switch
        {
            "win64" => Path.Combine("chrome-win64", "chrome.exe"),
            "linux64" => Path.Combine("chrome-linux64", "chrome"),
            "mac-arm64" => Path.Combine("chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
            _ => Path.Combine("chrome-mac-x64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
        };
        if (legacyLayout && OperatingSystem.IsWindows())
        {
            relativePath = "chrome.exe";
        }

        var executablePath = Path.Combine(root, relativePath);
        if (withExecutable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            File.WriteAllText(executablePath, "stub-browser");
        }

        if (pinnedVersion is not null)
        {
            var manifest = new Dictionary<string, object?>
            {
                ["product"] = "chrome-for-testing",
                ["version"] = pinnedVersion,
                ["channel"] = "stable",
                ["platforms"] = new Dictionary<string, object?>
                {
                    [platformKey] = new Dictionary<string, object?>
                    {
                        ["executablePath"] = relativePath.Replace('\\', '/'),
                    },
                },
            };
            File.WriteAllText(Path.Combine(root, BrowserPinManifest.FileName), JsonSerializer.Serialize(manifest));
        }

        return new FakeBundleLayout(root, executablePath, pinnedVersion);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}

/// <summary>
/// One-stop fake render harness: scripted CDP server + fake bundle + the REAL
/// pool wired through the internal factory seam. Fast wait-rule defaults keep
/// component tests quick; pass explicit options for wait-rule proofs.
/// </summary>
public sealed class FakeRenderHarness : IAsyncDisposable
{
    private FakeRenderHarness(FakeCdpServer server, FakeBundleLayout bundle, FakeCdpBrowserFactory factory, HeadlessRenderingPool pool)
    {
        Server = server;
        Bundle = bundle;
        Factory = factory;
        Pool = pool;
    }

    /// <summary>The scripted CDP browser.</summary>
    public FakeCdpServer Server { get; }

    /// <summary>The fake bundled browser layout.</summary>
    public FakeBundleLayout Bundle { get; }

    /// <summary>The fake browser factory (launch counting).</summary>
    internal FakeCdpBrowserFactory Factory { get; }

    /// <summary>The real pool under test.</summary>
    public HeadlessRenderingPool Pool { get; }

    /// <summary>Fast wait-rule defaults for component tests.</summary>
    /// <returns>Options with a 100 ms quiet period and 5 s hard cap.</returns>
    public static RenderingPoolOptions FastOptions() => new()
    {
        RenderWaitQuietMs = 100,
        RenderWaitHardCapMs = 5000,
    };

    /// <summary>Creates the harness.</summary>
    /// <param name="configure">Optional fake-browser script configuration.</param>
    /// <param name="options">Pool options (null = fast defaults).</param>
    /// <param name="contentStore">Optional real content store for artifact proofs.</param>
    /// <param name="wafEvaluator">Optional WAF evaluator double.</param>
    /// <param name="proxy">Optional loopback proxy (explicit routing opt-in).</param>
    /// <returns>The running harness.</returns>
    public static FakeRenderHarness Create(
        Action<FakeCdpOptions>? configure = null,
        RenderingPoolOptions? options = null,
        IContentStore? contentStore = null,
        IWafChallengeEvaluator? wafEvaluator = null,
        ILoopbackInterceptingProxy? proxy = null)
    {
        var server = FakeCdpServer.Start(configure);
        var bundle = FakeBundleLayout.Create();
        var factory = new FakeCdpBrowserFactory(server);
        var baseOptions = options ?? FastOptions();
        var poolOptions = new RenderingPoolOptions
        {
            MaxConcurrency = baseOptions.MaxConcurrency,
            DefaultTimeoutSeconds = baseOptions.DefaultTimeoutSeconds,
            LaunchTimeoutSeconds = baseOptions.LaunchTimeoutSeconds,
            RenderWaitQuietMs = baseOptions.RenderWaitQuietMs,
            RenderWaitHardCapMs = baseOptions.RenderWaitHardCapMs,
            InfiniteScrollMaxIterations = baseOptions.InfiniteScrollMaxIterations,
            InfiniteScrollStableChecks = baseOptions.InfiniteScrollStableChecks,
            MaxConsoleMessages = baseOptions.MaxConsoleMessages,
            MaxNetworkEntries = baseOptions.MaxNetworkEntries,
            StreamingPendingThresholdMs = baseOptions.StreamingPendingThresholdMs,
            BlockedResourceTypes = baseOptions.BlockedResourceTypes,
            UserDataRootDirectory = baseOptions.UserDataRootDirectory,
            Locale = baseOptions.Locale,
            RouteThroughProxy = baseOptions.RouteThroughProxy,
            UpstreamProxyUrl = baseOptions.UpstreamProxyUrl,
            BrowserResolution = bundle.ResolutionOptions,
            NonCredentialHeaders = baseOptions.NonCredentialHeaders,
            CredentialHeaders = baseOptions.CredentialHeaders,
            InScopeOrigins = baseOptions.InScopeOrigins,
        };
        var pool = new HeadlessRenderingPool(
            poolOptions,
            new LocalBrowserResolver(poolOptions.BrowserResolution),
            contentStore,
            wafEvaluator,
            proxy,
            logger: null,
            factory);
        return new FakeRenderHarness(server, bundle, factory, pool);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Pool.DisposeAsync();
        await Server.DisposeAsync();
        Bundle.Dispose();
    }
}
