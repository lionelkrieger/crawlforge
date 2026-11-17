namespace CrawlForge.Crawler.Limits;

/// <summary>
/// Local configuration of the built-in unlimited limits profile
/// (FS-CRAWL-001 §4.2). The built-in defaults reproduce the first-run
/// initialization exactly: unlimited ceilings, machine-scaled concurrency,
/// generous fixed operational defaults. Every value here is a local
/// implementation choice — none derives from license, account, entitlement,
/// or any remote input (exclusion row 8; criterion #4).
/// </summary>
public sealed record LimitsResolverOptions
{
    /// <summary>
    /// Safety cap on worker concurrency. The built-in concurrency scales with
    /// the machine's logical processor count but never exceeds this cap.
    /// </summary>
    public int ConcurrencySafetyCap { get; init; } = 64;

    /// <summary>
    /// Multiplier applied to the logical processor count to derive the built-in
    /// worker concurrency (before the safety cap). Implementation choice per §4.2.
    /// </summary>
    public int ConcurrencyProcessorFactor { get; init; } = 2;

    /// <summary>Built-in per-request timeout in seconds (§4.2: order of tens of seconds).</summary>
    public int DefaultFetchTimeoutSeconds { get; init; } = 30;

    /// <summary>Built-in retry budget per URL (§4.2: small fixed default).</summary>
    public int DefaultRetryBudget { get; init; } = 2;

    /// <summary>Built-in redirect hop ceiling (§4.2: small fixed default).</summary>
    public int DefaultRedirectHopCeiling { get; init; } = 5;

    /// <summary>Built-in response size ceiling in bytes (§4.2: generous fixed default).</summary>
    public long DefaultResponseSizeBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Built-in per-domain ceiling mode (§4.2: selectable).</summary>
    public DomainCeilingMode DefaultDomainMode { get; init; } = DomainCeilingMode.RegisteredDomain;

    /// <summary>Built-in throughput scope (§4.2: global or per host).</summary>
    public ThroughputScope DefaultThroughputScope { get; init; } = ThroughputScope.PerHost;

    /// <summary>
    /// Validates the option values, throwing when a setting is out of range.
    /// </summary>
    /// <exception cref="LimitValidationException">An option violates its invariant.</exception>
    public void Validate()
    {
        if (ConcurrencySafetyCap < 1)
        {
            throw new LimitValidationException("resolver-options", nameof(ConcurrencySafetyCap), ConcurrencySafetyCap, "the concurrency safety cap must be a positive integer.");
        }

        if (ConcurrencyProcessorFactor < 1)
        {
            throw new LimitValidationException("resolver-options", nameof(ConcurrencyProcessorFactor), ConcurrencyProcessorFactor, "the processor factor must be a positive integer.");
        }

        if (DefaultFetchTimeoutSeconds < 1)
        {
            throw new LimitValidationException("resolver-options", nameof(DefaultFetchTimeoutSeconds), DefaultFetchTimeoutSeconds, "the fetch timeout must be a positive duration.");
        }

        if (DefaultRetryBudget < 0)
        {
            throw new LimitValidationException("resolver-options", nameof(DefaultRetryBudget), DefaultRetryBudget, "the retry budget must be non-negative.");
        }

        if (DefaultRedirectHopCeiling < 1)
        {
            throw new LimitValidationException("resolver-options", nameof(DefaultRedirectHopCeiling), DefaultRedirectHopCeiling, "the redirect hop ceiling must be a positive integer.");
        }

        if (DefaultResponseSizeBytes < 1)
        {
            throw new LimitValidationException("resolver-options", nameof(DefaultResponseSizeBytes), DefaultResponseSizeBytes, "the response size ceiling must be a positive number of bytes.");
        }
    }
}
