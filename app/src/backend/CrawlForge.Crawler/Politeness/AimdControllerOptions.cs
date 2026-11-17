using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Full FS-CRAWL-002 §3.3 parameter set of the AIMD controller with the
/// §3.3 defaults, plus the resolved ceilings the controller must never exceed
/// (§3.2). The locked <see cref="AimdSettings"/> DTO carries the persisted
/// subset; <see cref="FromSettings"/> maps it onto this local surface together
/// with the resolved limits. All values are local configuration — never
/// license, account, or entitlement inputs.
/// </summary>
public sealed record AimdControllerOptions
{
    /// <summary>Local disable toggle (§3.2): when false the controller reproduces the parent's static politeness exactly (criterion #36).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>R0 — initial per-host rate in requests/second.</summary>
    public double InitialRate { get; init; } = 1.0;

    /// <summary>Alpha — additive increase step in requests/second.</summary>
    public double AdditiveIncrease { get; init; } = 0.5;

    /// <summary>Beta — multiplicative decrease factor (0 &lt; beta &lt; 1).</summary>
    public double MultiplicativeDecrease { get; init; } = 0.5;

    /// <summary>R_min — rate floor in requests/second.</summary>
    public double MinRate { get; init; } = 0.1;

    /// <summary>R_max-auto — ceiling used only when the resolved throughput ceiling is unlimited.</summary>
    public double MaxAutoRate { get; init; } = 10.0;

    /// <summary>K — consecutive healthy, fast responses required before an additive increase.</summary>
    public int HealthyStreakTarget { get; init; } = 5;

    /// <summary>T_abs — absolute TTFB threshold in milliseconds gating increases.</summary>
    public double AbsoluteTtfbThresholdMs { get; init; } = 800;

    /// <summary>Kappa — latency-spike factor over the rolling EWMA baseline.</summary>
    public double LatencySpikeFactor { get; init; } = 2.5;

    /// <summary>D — cooldown duration in seconds after a decrease.</summary>
    public double CooldownSeconds { get; init; } = 10;

    /// <summary>Lambda — EWMA smoothing factor for the TTFB baseline and latency.</summary>
    public double EwmaLambda { get; init; } = 0.2;

    /// <summary>M — healthy samples required before spike detection activates.</summary>
    public int WarmupSamples { get; init; } = 10;

    /// <summary>Optional toggle: treat other HTTP 5xx as decrease triggers (§3.3, default off).</summary>
    public bool DecreaseOnOther5xx { get; init; }

    /// <summary>Resolved per-host throughput ceiling; positive infinity = unlimited (§4.2).</summary>
    public double ResolvedThroughputCeiling { get; init; } = double.PositiveInfinity;

    /// <summary>Resolved worker concurrency ceiling shared across all hosts (§4.2).</summary>
    public int WorkerConcurrencyCeiling { get; init; } = 8;

    /// <summary>Optional per-host concurrency share cap (locked <see cref="AimdSettings.MaxConcurrency"/>).</summary>
    public int? PerHostConcurrencyCap { get; init; }

    /// <summary>
    /// Maps the locked persisted settings plus the resolved limits onto the
    /// full local parameter set, keeping the §3.3 defaults for everything the
    /// locked DTO does not carry.
    /// </summary>
    /// <param name="settings">Persisted AIMD settings.</param>
    /// <param name="limits">Resolved crawl limits supplying the ceilings.</param>
    /// <returns>The controller options.</returns>
    public static AimdControllerOptions FromSettings(AimdSettings settings, ResolvedCrawlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        return new AimdControllerOptions
        {
            InitialRate = settings.InitialRate,
            AdditiveIncrease = settings.AdditiveIncrease,
            MultiplicativeDecrease = settings.MultiplicativeDecrease,
            EwmaLambda = settings.EwmaSmoothing,
            MinRate = settings.MinRate,
            PerHostConcurrencyCap = settings.MaxConcurrency,
            ResolvedThroughputCeiling = limits.RequestsPerSecond,
            WorkerConcurrencyCeiling = limits.MaxConcurrency
        };
    }

    /// <summary>Validates the parameter ranges, throwing on invalid configuration.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public void Validate()
    {
        if (!IsPositiveFinite(InitialRate))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRate), InitialRate, "The initial rate must be a positive finite number.");
        }

        if (!IsPositiveFinite(AdditiveIncrease))
        {
            throw new ArgumentOutOfRangeException(nameof(AdditiveIncrease), AdditiveIncrease, "The additive increase must be a positive finite number.");
        }

        if (MultiplicativeDecrease is <= 0 or >= 1 || double.IsNaN(MultiplicativeDecrease))
        {
            throw new ArgumentOutOfRangeException(nameof(MultiplicativeDecrease), MultiplicativeDecrease, "The multiplicative decrease factor must be between 0 and 1 (exclusive).");
        }

        if (!IsPositiveFinite(MinRate))
        {
            throw new ArgumentOutOfRangeException(nameof(MinRate), MinRate, "The rate floor must be a positive finite number.");
        }

        if (!IsPositiveFinite(MaxAutoRate))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAutoRate), MaxAutoRate, "The automatic rate ceiling must be a positive finite number.");
        }

        if (HealthyStreakTarget < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthyStreakTarget), HealthyStreakTarget, "The healthy streak target must be at least 1.");
        }

        if (!IsPositiveFinite(AbsoluteTtfbThresholdMs))
        {
            throw new ArgumentOutOfRangeException(nameof(AbsoluteTtfbThresholdMs), AbsoluteTtfbThresholdMs, "The absolute TTFB threshold must be a positive finite number.");
        }

        if (LatencySpikeFactor <= 1 || double.IsNaN(LatencySpikeFactor) || double.IsInfinity(LatencySpikeFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(LatencySpikeFactor), LatencySpikeFactor, "The latency spike factor must be greater than 1.");
        }

        if (CooldownSeconds < 0 || double.IsNaN(CooldownSeconds) || double.IsInfinity(CooldownSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(CooldownSeconds), CooldownSeconds, "The cooldown must be a non-negative finite number of seconds.");
        }

        if (EwmaLambda is <= 0 or > 1 || double.IsNaN(EwmaLambda))
        {
            throw new ArgumentOutOfRangeException(nameof(EwmaLambda), EwmaLambda, "The EWMA smoothing factor must be in (0, 1].");
        }

        if (WarmupSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WarmupSamples), WarmupSamples, "The warm-up sample count must be at least 1.");
        }

        if (WorkerConcurrencyCeiling < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerConcurrencyCeiling), WorkerConcurrencyCeiling, "The worker concurrency ceiling must be at least 1.");
        }

        if (PerHostConcurrencyCap is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PerHostConcurrencyCap), PerHostConcurrencyCap, "The per-host concurrency cap must be at least 1 when set.");
        }

        if (double.IsNaN(ResolvedThroughputCeiling) || ResolvedThroughputCeiling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ResolvedThroughputCeiling), ResolvedThroughputCeiling, "The resolved throughput ceiling must be positive or unlimited (positive infinity).");
        }
    }

    private static bool IsPositiveFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
}
