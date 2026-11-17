namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// Local, user-supplied routing settings for the built-in intercepting proxy
/// (FS-CRAWL-001 §5.9). All configuration is local; no remote proxy
/// auto-configuration or discovery channel exists. Defaults preserve the
/// baseline: no upstream, no bypass entries, no blocking rules, no fallback.
/// </summary>
public sealed class ProxyRoutingSettings
{
    /// <summary>
    /// Optional user-configured upstream proxy URL (http/https CONNECT class or
    /// socks4/socks5 class). Credentials, when present, are user-supplied URL
    /// userinfo; the configuration layer encrypts them at rest (§9.1).
    /// </summary>
    public string? UpstreamProxyUrl { get; init; }

    /// <summary>
    /// Per-host bypass list: hosts exempt from upstream proxying (exact host,
    /// ".example.com" suffix, or "*.example.com" wildcard). Bypassed hosts are
    /// reached directly; they remain crawl targets configured by the user.
    /// </summary>
    public IReadOnlyList<string> BypassHosts { get; init; } = [];

    /// <summary>
    /// Per-audit option: when the upstream proxy is unreachable, fall back to a
    /// direct connection. When false, proxy failures surface as fetch failures.
    /// </summary>
    public bool AllowDirectFallback { get; init; }

    /// <summary>
    /// User-defined local resource-blocking rules applied before any outbound
    /// connection (for example, suppressing selected third-party resource
    /// classes during rendering). Blocked requests receive a local 403.
    /// </summary>
    public IReadOnlyList<ResourceBlockingRule> BlockingRules { get; init; } = [];
}

/// <summary>
/// One user-defined local resource-blocking rule (§5.9). Matching is
/// case-insensitive on the host; an optional path substring narrows the rule.
/// </summary>
/// <param name="HostPattern">
/// Exact host ("ads.example.com"), suffix (".example.com"), or wildcard
/// ("*.example.com") pattern.
/// </param>
/// <param name="PathContains">Optional case-insensitive path substring filter.</param>
public sealed record ResourceBlockingRule(string HostPattern, string? PathContains = null);
