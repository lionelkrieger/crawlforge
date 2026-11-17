using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Limits;

/// <summary>
/// Pure local limits resolver (FS-CRAWL-001 §4.1): merges override layers in the
/// fixed priority order per-audit override → project default → application
/// default → built-in unlimited profile. No code path consults any remote
/// service, license, account, entitlement, or identity (exclusion row 8;
/// precedence rule 5; criterion #4). A fresh audit with no overrides resolves
/// to unlimited total and per-domain ceilings.
/// </summary>
/// <remarks>
/// <para>
/// Unlimited is the distinguished absence-of-value sentinel (§4.3): null for
/// the nullable ceilings, and <see cref="double.PositiveInfinity"/> for the
/// non-nullable throughput rate — never a magic large number that could
/// overflow arithmetic. The resolver is stateless: a running audit keeps the
/// snapshot it started with because callers hold the returned record; later
/// settings changes never mutate it.
/// </para>
/// <para>
/// Invalid overrides are rejected with a descriptive <see cref="LimitValidationException"/>
/// naming the layer, field, and violated rule (§4.3). The extended
/// <see cref="ResolveProfile"/> view exposes the full §4.2 parameter set
/// (per-domain ceiling, retry budget, redirect hops, response size) whose
/// built-in defaults are local implementation choices.
/// </para>
/// </remarks>
public sealed class UnlimitedLimitsResolver : ILimitsResolver
{
    private readonly LimitsResolverOptions _options;

    /// <summary>Creates a resolver with the configured built-in profile.</summary>
    /// <param name="options">Built-in profile options; null uses the baseline unlimited defaults.</param>
    /// <exception cref="LimitValidationException">An option violates its invariant.</exception>
    public UnlimitedLimitsResolver(LimitsResolverOptions? options = null)
    {
        _options = options ?? new LimitsResolverOptions();
        _options.Validate();
    }

    /// <summary>
    /// The built-in unlimited profile for the configured options: unlimited
    /// total, per-domain, depth, duration, and throughput ceilings; concurrency
    /// scaled to the machine's logical processor count within the safety cap.
    /// </summary>
    /// <param name="options">Built-in profile options; null uses the baseline defaults.</param>
    /// <returns>The built-in profile.</returns>
    public static CrawlLimitsProfile BuiltInProfile(LimitsResolverOptions? options = null)
    {
        var effective = options ?? new LimitsResolverOptions();
        effective.Validate();
        return new CrawlLimitsProfile(
            MaxUrls: null,
            PerDomainMaxUrls: null,
            DomainMode: effective.DefaultDomainMode,
            MaxDepth: null,
            MaxDurationSeconds: null,
            MaxConcurrency: BuiltInConcurrency(effective),
            RequestsPerSecond: double.PositiveInfinity,
            ThroughputScope: effective.DefaultThroughputScope,
            FetchTimeoutSeconds: effective.DefaultFetchTimeoutSeconds,
            RetryBudget: effective.DefaultRetryBudget,
            RedirectHopCeiling: effective.DefaultRedirectHopCeiling,
            ResponseSizeBytes: effective.DefaultResponseSizeBytes);
    }

    /// <inheritdoc/>
    public ResolvedCrawlLimits Resolve(
        CrawlLimitOverrides? auditOverrides,
        CrawlLimitOverrides? projectDefaults,
        CrawlLimitOverrides? applicationDefaults)
    {
        ValidateLayer(auditOverrides, "per-audit", _options.ConcurrencySafetyCap);
        ValidateLayer(projectDefaults, "project-default", _options.ConcurrencySafetyCap);
        ValidateLayer(applicationDefaults, "application-default", _options.ConcurrencySafetyCap);

        var builtInConcurrency = BuiltInConcurrency(_options);
        var resolved = new ResolvedCrawlLimits(
            MaxUrls: auditOverrides?.MaxUrls ?? projectDefaults?.MaxUrls ?? applicationDefaults?.MaxUrls,
            MaxDepth: auditOverrides?.MaxDepth ?? projectDefaults?.MaxDepth ?? applicationDefaults?.MaxDepth,
            MaxDurationSeconds: auditOverrides?.MaxDurationSeconds ?? projectDefaults?.MaxDurationSeconds ?? applicationDefaults?.MaxDurationSeconds,
            MaxConcurrency: auditOverrides?.MaxConcurrency ?? projectDefaults?.MaxConcurrency ?? applicationDefaults?.MaxConcurrency ?? builtInConcurrency,
            RequestsPerSecond: auditOverrides?.RequestsPerSecond ?? projectDefaults?.RequestsPerSecond ?? applicationDefaults?.RequestsPerSecond ?? double.PositiveInfinity,
            FetchTimeoutSeconds: auditOverrides?.FetchTimeoutSeconds ?? projectDefaults?.FetchTimeoutSeconds ?? applicationDefaults?.FetchTimeoutSeconds ?? _options.DefaultFetchTimeoutSeconds);

        ValidateResolved(resolved, _options.ConcurrencySafetyCap);
        return resolved;
    }

    /// <summary>
    /// Resolves the full §4.2 parameter set. The six contract fields merge
    /// through the same priority chain as <see cref="Resolve"/>; the extended
    /// parameters (per-domain ceiling, retry budget, redirect hops, response
    /// size, scope selectors) carry the built-in local defaults because the
    /// locked override contract does not carry them.
    /// </summary>
    /// <param name="auditOverrides">Per-audit overrides (highest priority), if any.</param>
    /// <param name="projectDefaults">Project-level defaults, if any.</param>
    /// <param name="applicationDefaults">Application-level defaults, if any.</param>
    /// <returns>The complete resolved profile.</returns>
    public CrawlLimitsProfile ResolveProfile(
        CrawlLimitOverrides? auditOverrides,
        CrawlLimitOverrides? projectDefaults,
        CrawlLimitOverrides? applicationDefaults)
    {
        var resolved = Resolve(auditOverrides, projectDefaults, applicationDefaults);
        return new CrawlLimitsProfile(
            MaxUrls: resolved.MaxUrls,
            PerDomainMaxUrls: null,
            DomainMode: _options.DefaultDomainMode,
            MaxDepth: resolved.MaxDepth,
            MaxDurationSeconds: resolved.MaxDurationSeconds,
            MaxConcurrency: resolved.MaxConcurrency,
            RequestsPerSecond: resolved.RequestsPerSecond,
            ThroughputScope: _options.DefaultThroughputScope,
            FetchTimeoutSeconds: resolved.FetchTimeoutSeconds,
            RetryBudget: _options.DefaultRetryBudget,
            RedirectHopCeiling: _options.DefaultRedirectHopCeiling,
            ResponseSizeBytes: _options.DefaultResponseSizeBytes);
    }

    private static int BuiltInConcurrency(LimitsResolverOptions options) =>
        Math.Clamp(Environment.ProcessorCount * options.ConcurrencyProcessorFactor, 1, options.ConcurrencySafetyCap);

    private static void ValidateLayer(CrawlLimitOverrides? layer, string layerName, int concurrencySafetyCap)
    {
        if (layer is null)
        {
            return;
        }

        if (layer.MaxUrls is <= 0)
        {
            throw new LimitValidationException(layerName, nameof(layer.MaxUrls), layer.MaxUrls, "the total page ceiling must be a positive integer or unlimited (null).");
        }

        if (layer.MaxDepth is < 0)
        {
            throw new LimitValidationException(layerName, nameof(layer.MaxDepth), layer.MaxDepth, "the crawl depth must be a non-negative integer or unlimited (null).");
        }

        if (layer.MaxDurationSeconds is <= 0)
        {
            throw new LimitValidationException(layerName, nameof(layer.MaxDurationSeconds), layer.MaxDurationSeconds, "the audit duration ceiling must be a positive duration or unlimited (null).");
        }

        if (layer.MaxConcurrency is { } concurrency && (concurrency < 1 || concurrency > concurrencySafetyCap))
        {
            throw new LimitValidationException(layerName, nameof(layer.MaxConcurrency), concurrency, $"the worker concurrency must be a positive integer within the safety cap of {concurrencySafetyCap}.");
        }

        if (layer.RequestsPerSecond is { } rate && !IsValidRate(rate))
        {
            throw new LimitValidationException(layerName, nameof(layer.RequestsPerSecond), rate, "the throughput ceiling must be a positive number or unlimited (positive infinity).");
        }

        if (layer.FetchTimeoutSeconds is <= 0)
        {
            throw new LimitValidationException(layerName, nameof(layer.FetchTimeoutSeconds), layer.FetchTimeoutSeconds, "the request timeout must be a positive duration.");
        }
    }

    private static void ValidateResolved(ResolvedCrawlLimits resolved, int concurrencySafetyCap)
    {
        if (resolved.MaxUrls is <= 0)
        {
            throw new LimitValidationException("resolved", nameof(resolved.MaxUrls), resolved.MaxUrls, "the total page ceiling must be a positive integer or unlimited (null).");
        }

        if (resolved.MaxDepth is < 0)
        {
            throw new LimitValidationException("resolved", nameof(resolved.MaxDepth), resolved.MaxDepth, "the crawl depth must be a non-negative integer or unlimited (null).");
        }

        if (resolved.MaxDurationSeconds is <= 0)
        {
            throw new LimitValidationException("resolved", nameof(resolved.MaxDurationSeconds), resolved.MaxDurationSeconds, "the audit duration ceiling must be a positive duration or unlimited (null).");
        }

        if (resolved.MaxConcurrency < 1 || resolved.MaxConcurrency > concurrencySafetyCap)
        {
            throw new LimitValidationException("resolved", nameof(resolved.MaxConcurrency), resolved.MaxConcurrency, $"the worker concurrency must be a positive integer within the safety cap of {concurrencySafetyCap}.");
        }

        if (!IsValidRate(resolved.RequestsPerSecond))
        {
            throw new LimitValidationException("resolved", nameof(resolved.RequestsPerSecond), resolved.RequestsPerSecond, "the throughput ceiling must be a positive number or unlimited (positive infinity).");
        }

        if (resolved.FetchTimeoutSeconds <= 0)
        {
            throw new LimitValidationException("resolved", nameof(resolved.FetchTimeoutSeconds), resolved.FetchTimeoutSeconds, "the request timeout must be a positive duration.");
        }
    }

    private static bool IsValidRate(double rate) =>
        !double.IsNaN(rate) && (rate > 0 || double.IsPositiveInfinity(rate));
}
