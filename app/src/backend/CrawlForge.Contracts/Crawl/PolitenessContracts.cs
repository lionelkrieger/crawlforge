using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Resolves effective crawl limits through the fixed priority order
/// (FS-CRAWL-001 §4.1): per-audit override → project default → application
/// default → built-in profile. No code path consults any remote service or
/// entitlement (exclusion row 8; precedence rule 5). The built-in profile is
/// unlimited; first-run defaults reproduce it exactly.
/// </summary>
public interface ILimitsResolver
{
    /// <summary>
    /// Merges the override layers in priority order and returns the effective
    /// limits. A null field in the result means unlimited.
    /// </summary>
    /// <param name="auditOverrides">Per-audit overrides (highest priority), if any.</param>
    /// <param name="projectDefaults">Project-level defaults, if any.</param>
    /// <param name="applicationDefaults">Application-level defaults, if any.</param>
    /// <returns>The resolved effective limits.</returns>
    ResolvedCrawlLimits Resolve(
        CrawlLimitOverrides? auditOverrides,
        CrawlLimitOverrides? projectDefaults,
        CrawlLimitOverrides? applicationDefaults);
}

/// <summary>
/// A partial set of crawl limit overrides. Every field is optional; null means
/// "inherit from the next lower-priority layer". Used for per-audit overrides,
/// project defaults, and application defaults alike.
/// </summary>
/// <param name="MaxUrls">Maximum number of URLs to crawl.</param>
/// <param name="MaxDepth">Maximum link depth from the seeds.</param>
/// <param name="MaxDurationSeconds">Maximum crawl duration in seconds.</param>
/// <param name="MaxConcurrency">Maximum concurrent requests.</param>
/// <param name="RequestsPerSecond">Maximum request rate per host.</param>
/// <param name="FetchTimeoutSeconds">Per-request timeout in seconds.</param>
public sealed record CrawlLimitOverrides(
    long? MaxUrls = null,
    int? MaxDepth = null,
    int? MaxDurationSeconds = null,
    int? MaxConcurrency = null,
    double? RequestsPerSecond = null,
    int? FetchTimeoutSeconds = null);

/// <summary>
/// Effective crawl limits after resolution. A null field means unlimited
/// (the built-in profile). Limits never vary with license, account, or any
/// remote input (exclusion row 8).
/// </summary>
/// <param name="MaxUrls">Maximum number of URLs to crawl; null = unlimited.</param>
/// <param name="MaxDepth">Maximum link depth; null = unlimited.</param>
/// <param name="MaxDurationSeconds">Maximum duration in seconds; null = unlimited.</param>
/// <param name="MaxConcurrency">Maximum concurrent requests.</param>
/// <param name="RequestsPerSecond">Maximum request rate per host.</param>
/// <param name="FetchTimeoutSeconds">Per-request timeout in seconds.</param>
public sealed record ResolvedCrawlLimits(
    long? MaxUrls,
    int? MaxDepth,
    int? MaxDurationSeconds,
    int MaxConcurrency,
    double RequestsPerSecond,
    int FetchTimeoutSeconds);

/// <summary>
/// Per-host additive-increase/multiplicative-decrease politeness controller
/// (FS-CRAWL-002 §3). Operates under the resolved ceilings and adapts rate and
/// concurrency from observed responses using R0 (initial rate), alpha
/// (additive increase), beta (multiplicative decrease), kappa (EWMA smoothing),
/// and EWMA TTFB. Enabled by default with a local disable toggle; it is a
/// correctness/politeness safeguard, never an entitlement.
/// </summary>
public interface IAimdPolitenessController
{
    /// <summary>Waits until the next request to the host is permitted.</summary>
    /// <param name="host">Target host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the request may proceed.</returns>
    ValueTask WaitAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Feeds an observed response sample into the controller.</summary>
    /// <param name="host">Host the response came from.</param>
    /// <param name="sample">Timing and status sample.</param>
    void RecordResponse(string host, HostResponseSample sample);

    /// <summary>Returns the current throttle state of a host.</summary>
    /// <param name="host">Target host.</param>
    /// <returns>The host throttle state.</returns>
    HostThrottleState GetState(string host);

    /// <summary>Pins or clears a manual rate override for a host.</summary>
    /// <param name="host">Target host.</param>
    /// <param name="requestsPerSecond">Manual rate, or null to clear the override.</param>
    void SetManualOverride(string host, double? requestsPerSecond);

    /// <summary>Captures per-host controller state for checkpointing.</summary>
    /// <returns>Serializable controller state.</returns>
    AimdCheckpointState CreateCheckpointState();

    /// <summary>Restores per-host controller state from a checkpoint.</summary>
    /// <param name="state">Previously captured state.</param>
    void RestoreCheckpointState(AimdCheckpointState state);
}

/// <summary>
/// AIMD tuning parameters. Defaults are seeded locally; the controller merges
/// through the standard limits priority order (precedence rule 5).
/// </summary>
/// <param name="InitialRate">R0: initial requests per second per host.</param>
/// <param name="AdditiveIncrease">Alpha: additive rate increase per healthy window.</param>
/// <param name="MultiplicativeDecrease">Beta: multiplicative rate decrease on strain (0–1).</param>
/// <param name="EwmaSmoothing">Kappa: EWMA smoothing factor for TTFB (0–1).</param>
/// <param name="MinRate">Floor for the adaptive rate.</param>
/// <param name="MaxConcurrency">Ceiling for per-host concurrency.</param>
public sealed record AimdSettings(
    double InitialRate,
    double AdditiveIncrease,
    double MultiplicativeDecrease,
    double EwmaSmoothing,
    double MinRate,
    int MaxConcurrency);

/// <summary>
/// Current AIMD state of a single host (REST: GET /api/audits/{id}/hosts).
/// </summary>
/// <param name="Host">Host name.</param>
/// <param name="CurrentRate">Effective requests per second currently applied.</param>
/// <param name="CurrentConcurrency">Effective concurrency currently applied.</param>
/// <param name="EwmaTtfbMilliseconds">EWMA-smoothed time to first byte.</param>
/// <param name="AdjustmentCount">Number of AIMD adjustments performed.</param>
/// <param name="LastAdjustmentAt">Timestamp of the last adjustment, if any.</param>
/// <param name="ManualOverrideRate">Pinned manual rate, if set.</param>
public sealed record HostThrottleState(
    string Host,
    double CurrentRate,
    int CurrentConcurrency,
    double EwmaTtfbMilliseconds,
    long AdjustmentCount,
    DateTimeOffset? LastAdjustmentAt,
    double? ManualOverrideRate);

/// <summary>
/// One observed response sample fed to the AIMD controller.
/// </summary>
/// <param name="TtfbMilliseconds">Time to first byte in milliseconds.</param>
/// <param name="StatusCode">HTTP status code of the response.</param>
/// <param name="IsChallenge">Whether the response was classified as a WAF challenge.</param>
/// <param name="ObservedAt">Observation timestamp (UTC).</param>
public sealed record HostResponseSample(
    double TtfbMilliseconds,
    int StatusCode,
    bool IsChallenge,
    DateTimeOffset ObservedAt);

/// <summary>
/// Serialized per-host AIMD controller state, stored in the additive
/// crawl_checkpoints payload field (FS-CRAWL-002 §3.6).
/// </summary>
/// <param name="Hosts">Per-host states at checkpoint time.</param>
public sealed record AimdCheckpointState(IReadOnlyList<HostThrottleState> Hosts);
