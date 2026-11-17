namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Trigger codes of the host-throttle adjustment history and telemetry
/// (FS-CRAWL-002 §3.6/§3.7). Stored verbatim in <c>host_throttle_events.reason</c>
/// and carried by the typed emission surface consumed by the hub in Phase 05.
/// </summary>
public static class HostThrottleTriggerCodes
{
    /// <summary>Additive increase after K consecutive healthy, fast responses.</summary>
    public const string AdditiveIncrease = "additive-increase";

    /// <summary>TTFB exceeded kappa times the rolling EWMA baseline.</summary>
    public const string LatencySpike = "latency-spike";

    /// <summary>WAF soft-block / challenge classification.</summary>
    public const string WafSoftBlock = "waf-soft-block";

    /// <summary>HTTP 429 Too Many Requests.</summary>
    public const string Http429 = "http-429";

    /// <summary>HTTP 503 Service Unavailable.</summary>
    public const string Http503 = "http-503";

    /// <summary>Other HTTP 5xx (only when the optional toggle is enabled).</summary>
    public const string Http5xx = "http-5xx";

    /// <summary>Retry-After pause applied to the host subqueue.</summary>
    public const string RetryAfter = "retry-after";

    /// <summary>Cooldown expired; additive increases resume.</summary>
    public const string CooldownExpiry = "cooldown-expiry";

    /// <summary>Manual rate pin set or cleared.</summary>
    public const string ManualOverride = "manual-override";
}
