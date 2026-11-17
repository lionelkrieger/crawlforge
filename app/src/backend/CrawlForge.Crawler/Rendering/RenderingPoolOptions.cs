namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Local rendering-pool configuration (FS-CRAWL-001 §5.6). Defaults preserve
/// the baseline: bounded concurrency, no proxy routing, no custom headers,
/// stealth off. Every option is user-configured locally; nothing is resolved
/// remotely.
/// </summary>
public sealed class RenderingPoolOptions
{
    /// <summary>Maximum concurrent renders (bounded pool).</summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>Default per-render timeout when the request carries none.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>Browser launch timeout.</summary>
    public int LaunchTimeoutSeconds { get; init; } = 60;

    /// <summary>Hard bound on infinite-scroll iterations.</summary>
    public int InfiniteScrollMaxIterations { get; init; } = 20;

    /// <summary>Consecutive unchanged-height checks that end scrolling.</summary>
    public int InfiniteScrollStableChecks { get; init; } = 2;

    /// <summary>Pause between scroll steps so lazy content can load.</summary>
    public int InfiniteScrollPauseMs { get; init; } = 250;

    /// <summary>Bound on captured console messages per render.</summary>
    public int MaxConsoleMessages { get; init; } = 500;

    /// <summary>Emulated locale (Accept-Language / browser lang).</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>
    /// When true and a proxy is supplied, all rendered page traffic routes
    /// through the built-in loopback intercepting proxy (§5.6/§5.9).
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
}

/// <summary>
/// Artifact persistence context for one render: the owning audit and URL
/// records the rendered DOM and screenshot are stored under.
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
