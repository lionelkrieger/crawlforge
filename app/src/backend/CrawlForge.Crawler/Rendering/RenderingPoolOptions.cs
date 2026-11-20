using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Local rendering-pool configuration (FS-CRAWL-001 §5.6/§5.6.2). Defaults
/// preserve the spec baseline: bounded concurrency (half the logical
/// processors, minimum 2 — <see cref="CaptureSettings.DefaultRenderingPoolSize"/>),
/// DOM+network quiet 500 ms under a 20 s hard cap, no resource blocking, no
/// proxy routing, no custom headers, stealth off. Every option is
/// user-configured locally; nothing is resolved remotely.
/// </summary>
public sealed class RenderingPoolOptions
{
    /// <summary>
    /// Maximum concurrent renders (bounded pool of long-lived browser
    /// processes, §5.6.2). Default: half the logical processors, minimum 2.
    /// </summary>
    public int MaxConcurrency { get; init; } = CaptureSettings.DefaultRenderingPoolSize;

    /// <summary>Default per-render timeout when the request carries none.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>Browser launch timeout.</summary>
    public int LaunchTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Wait rule: DOM-and-network quiet period in milliseconds (§5.6.2,
    /// CR-009; CaptureSettings.renderWaitQuietMs). Default 500.
    /// </summary>
    public int RenderWaitQuietMs { get; init; } = 500;

    /// <summary>
    /// Wait rule: hard cap in milliseconds (§5.6.2, CR-009;
    /// CaptureSettings.renderWaitHardCapMs). Default 20 000. Fixed delays are
    /// not used.
    /// </summary>
    public int RenderWaitHardCapMs { get; init; } = 20_000;

    /// <summary>Hard bound on infinite-scroll iterations.</summary>
    public int InfiniteScrollMaxIterations { get; init; } = 20;

    /// <summary>Consecutive unchanged-height checks that end scrolling.</summary>
    public int InfiniteScrollStableChecks { get; init; } = 2;

    /// <summary>Bound on captured console messages per render.</summary>
    public int MaxConsoleMessages { get; init; } = 500;

    /// <summary>Soft bound on retained network-waterfall entries per render.</summary>
    public int MaxNetworkEntries { get; init; } = 5000;

    /// <summary>
    /// Age after which a response-less pending request is treated as
    /// long-polling and excluded from the quiet-network test (§5.6.2
    /// streaming exclusion; implementation heuristic).
    /// </summary>
    public int StreamingPendingThresholdMs { get; init; } = 3000;

    /// <summary>
    /// Opt-in resource-blocking profile (§5.6.2): CDP resource type names to
    /// block (for example <see cref="ResourceBlockingProfiles.Fast"/> = fonts
    /// and media). The DEFAULT profile blocks nothing so Core Web Vitals and
    /// tag checks are not skewed.
    /// </summary>
    public IReadOnlyList<string> BlockedResourceTypes { get; init; } = [];

    /// <summary>
    /// Optional root for fresh per-browser profile directories. Null uses the
    /// local temp directory. Profiles are transient and deleted on disposal.
    /// </summary>
    public string? UserDataRootDirectory { get; init; }

    /// <summary>Emulated locale (Accept-Language / browser lang).</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>
    /// Explicit opt-in to route rendered page traffic through the built-in
    /// loopback intercepting proxy for deterministic local routing scenarios
    /// (§5.9). NOTE (CR-006): the network waterfall is ALWAYS captured
    /// directly by the CDP control layer — never from the proxy — and the
    /// engine's rendered capture leaves this off. The proxy's primary role is
    /// plain-HTTP NON-rendered traffic (§5.9 as amended).
    /// </summary>
    public bool RouteThroughProxy { get; init; }

    /// <summary>Optional user-configured upstream proxy for routed traffic.</summary>
    public string? UpstreamProxyUrl { get; init; }

    /// <summary>Browser binary resolution (local only).</summary>
    public BrowserResolutionOptions BrowserResolution { get; init; } = new();

    /// <summary>
    /// Non-credential headers (for example language preference) applied to ALL
    /// rendered requests (§5.6 header split).
    /// </summary>
    public IReadOnlyDictionary<string, string> NonCredentialHeaders { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Credential-bearing headers sent ONLY to in-scope origins (§5.6 header
    /// split). User-owned secrets; the configuration layer encrypts them at rest.
    /// </summary>
    public IReadOnlyDictionary<string, string> CredentialHeaders { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// In-scope origins (scheme://host[:port]) allowed to receive credential
    /// headers. Empty means no origin receives them.
    /// </summary>
    public IReadOnlyList<string> InScopeOrigins { get; init; } = [];

    /// <summary>
    /// Maps the locked <see cref="CaptureSettings"/> document onto pool options
    /// (the coordinator composes per-audit options from the frozen snapshot).
    /// Null settings keep every application default.
    /// </summary>
    /// <param name="settings">Locked capture settings, when any.</param>
    /// <returns>Pool options carrying the configured wait rule and pool size.</returns>
    public static RenderingPoolOptions FromCaptureSettings(CaptureSettings? settings)
    {
        if (settings is null)
        {
            return new RenderingPoolOptions();
        }

        return new RenderingPoolOptions
        {
            RenderWaitQuietMs = settings.RenderWaitQuietMs,
            RenderWaitHardCapMs = settings.RenderWaitHardCapMs,
            MaxConcurrency = settings.RenderingPoolSize ?? CaptureSettings.DefaultRenderingPoolSize,
        };
    }
}

/// <summary>
/// Artifact persistence context for one render: the owning audit and URL
/// records the rendered DOM, screenshot and network waterfall are stored under.
/// </summary>
/// <param name="AuditId">Owning audit identifier.</param>
/// <param name="UrlId">Owning URL record identifier.</param>
public sealed record RenderArtifactContext(long AuditId, long UrlId);

/// <summary>Classification of an isolated render failure.</summary>
public enum RenderFailureKind
{
    /// <summary>No local browser could be launched.</summary>
    BrowserUnavailable = 0,

    /// <summary>Navigation or page capture failed.</summary>
    Navigation = 1,

    /// <summary>The render exceeded its timeout budget.</summary>
    Timeout = 2,
}

/// <summary>
/// An isolated render failure (§5.6): one page's timeout or crash never takes
/// down the pool or an in-flight audit. Diagnostics are local only.
/// </summary>
public sealed class RenderFailureException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Local diagnostic description.</param>
    public RenderFailureException(RenderFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Creates the failure with an inner cause.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Local diagnostic description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public RenderFailureException(RenderFailureKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure classification.</summary>
    public RenderFailureKind Kind { get; }
}
