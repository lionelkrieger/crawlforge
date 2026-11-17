using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Per-host AIMD auto-adaptive politeness controller (FS-CRAWL-002 §3).
/// Implements the locked <see cref="IAimdPolitenessController"/> surface and
/// the full §3.4 control law:
/// additive increase by alpha after K consecutive healthy responses with
/// TTFB at or below T_abs, clamped at the resolved throughput ceiling (or
/// R_max-auto when unlimited) and never above <c>1 / crawl-delay</c>;
/// multiplicative decrease by beta to the rate floor on a latency spike over
/// kappa times the rolling EWMA baseline, a WAF soft-block, HTTP 429/503, or
/// (optionally) other 5xx, with a D-second cooldown during which the baseline
/// is frozen and increases are suspended. Retry-After pauses the host for
/// <c>max(retry-after, D)</c>. Concurrency couples to rate through the EWMA of
/// full response latency and the sum across hosts never exceeds the resolved
/// worker ceiling. A manual pin suspends adaptation until cleared. With the
/// local disable toggle off, spacing and concurrency reproduce the parent's
/// static politeness exactly (criterion #36).
/// </summary>
/// <remarks>
/// <para>
/// Determinism (§3.4): all timing reads go through the injected
/// <see cref="TimeProvider"/> and all state mutations serialize through one
/// gate, so the same sample sequence and parameters produce a reproducible
/// adjustment sequence. The locked checkpoint surface preserves every
/// <see cref="HostThrottleState"/> field exactly; transient streak/cooldown
/// restart conservatively on restore (§3.5). The local full-fidelity
/// checkpoint (<see cref="CreateFullCheckpoint"/>) additionally preserves the
/// transient state bit-exactly.
/// </para>
/// <para>
/// The controller never exceeds the resolved ceilings and always honors the
/// robots crawl-delay floor. It emits typed adjustments through
/// <see cref="IHostThrottleAdjustmentSink"/>; hub wiring belongs to Phase 05.
/// </para>
/// </remarks>
public sealed class AimdPolitenessController : IAimdPolitenessController
{
    private readonly AimdControllerOptions _options;
    private readonly long _auditId;
    private readonly TimeProvider _time;
    private readonly IHostThrottleAdjustmentSink? _sink;
    private readonly ICrawlDelaySource? _crawlDelaySource;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the controller for one audit.</summary>
    /// <param name="options">Control parameters and resolved ceilings; null uses the §3.3 defaults.</param>
    /// <param name="auditId">Audit reference carried by emitted adjustments.</param>
    /// <param name="adjustmentSink">Optional typed adjustment sink (history recorder / Phase 05 hub adapter).</param>
    /// <param name="crawlDelaySource">Optional robots crawl-delay floor source.</param>
    /// <param name="timeProvider">Deterministic clock; null uses the system clock.</param>
    public AimdPolitenessController(
        AimdControllerOptions? options = null,
        long auditId = 0,
        IHostThrottleAdjustmentSink? adjustmentSink = null,
        ICrawlDelaySource? crawlDelaySource = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new AimdControllerOptions();
        _options.Validate();
        _auditId = auditId;
        _sink = adjustmentSink;
        _crawlDelaySource = crawlDelaySource;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async ValueTask WaitAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        TimeSpan delay;
        lock (_gate)
        {
            var state = GetOrCreateState(host);
            var now = _time.GetUtcNow();
            var effectiveRate = EffectiveRate(state);
            var interval = double.IsPositiveInfinity(effectiveRate)
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(1.0 / effectiveRate);

            var crawlDelay = _crawlDelaySource?.GetCrawlDelaySeconds(host);
            if (crawlDelay is > 0)
            {
                var crawlInterval = TimeSpan.FromSeconds(crawlDelay.Value);
                if (crawlInterval > interval)
                {
                    interval = crawlInterval;
                }
            }

            var earliest = now;
            if (state.NextPermittedAt > earliest)
            {
                earliest = state.NextPermittedAt;
            }

            if (state.PausedUntil > earliest)
            {
                earliest = state.PausedUntil;
            }

            delay = earliest - now;
            state.NextPermittedAt = earliest + interval;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void RecordResponse(string host, HostResponseSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        RecordResponse(host, new AimdResponseObservation(sample));
    }

    /// <summary>
    /// Records one response observation with optional transport extras
    /// (Retry-After, full response latency) and applies the §3.4 control law.
    /// </summary>
    /// <param name="host">Target host.</param>
    /// <param name="observation">The response observation.</param>
    public void RecordResponse(string host, AimdResponseObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(observation);

        lock (_gate)
        {
            if (!_options.Enabled)
            {
                return;
            }

            var state = GetOrCreateState(host);
            if (state.ManualOverrideRate is not null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var sample = observation.Sample;

            if (state.CooldownUntil > DateTimeOffset.MinValue && now >= state.CooldownUntil)
            {
                state.CooldownUntil = DateTimeOffset.MinValue;
                EmitAdjustment(host, state, HostThrottleTriggerCodes.CooldownExpiry, state.Rate, state.Rate, state.Concurrency, state.Concurrency, ttfb: null, now);
            }

            var inCooldown = now < state.CooldownUntil;
            var isHealthy = sample.StatusCode is >= 200 and < 400 && !sample.IsChallenge;
            var spike = isHealthy
                && state.WarmupCount >= _options.WarmupSamples
                && sample.TtfbMilliseconds > _options.LatencySpikeFactor * state.BaselineMilliseconds;

            var trigger = ClassifyTrigger(sample, spike);
            if (trigger is not null)
            {
                ApplyDecrease(host, state, trigger, sample, observation, now);
                return;
            }

            if (!isHealthy)
            {
                // Other errors stay with the parent's backoff path: no streak, no baseline.
                state.HealthyStreak = 0;
                return;
            }

            if (inCooldown)
            {
                // Baseline and adaptation are frozen during cooldown (§3.4).
                return;
            }

            UpdateBaselineAndLatency(state, sample, observation);

            if (sample.TtfbMilliseconds <= _options.AbsoluteTtfbThresholdMs)
            {
                state.HealthyStreak++;
            }
            else
            {
                state.HealthyStreak = 0;
            }

            if (state.HealthyStreak >= _options.HealthyStreakTarget)
            {
                state.HealthyStreak = 0;
                ApplyIncreaseIfBelowCap(host, state, sample, now);
            }
        }
    }

    /// <inheritdoc/>
    public HostThrottleState GetState(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        lock (_gate)
        {
            return _hosts.TryGetValue(host.Trim(), out var state)
                ? ToThrottleState(host, state)
                : new HostThrottleState(
                    host,
                    _options.Enabled ? Math.Min(_options.InitialRate, ComputeRateCap(host)) : _options.ResolvedThroughputCeiling,
                    _options.Enabled ? 1 : StaticConcurrency(),
                    EwmaTtfbMilliseconds: 0,
                    AdjustmentCount: 0,
                    LastAdjustmentAt: null,
                    ManualOverrideRate: null);
        }
    }

    /// <inheritdoc/>
    public void SetManualOverride(string host, double? requestsPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (requestsPerSecond is { } rate && (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), rate, "The manual rate pin must be a positive finite number or null to clear.");
        }

        lock (_gate)
        {
            var state = GetOrCreateState(host);
            var now = _time.GetUtcNow();
            var previousRate = EffectiveRate(state);
            state.ManualOverrideRate = requestsPerSecond;
            var newRate = EffectiveRate(state);
            state.AdjustmentCount++;
            state.LastAdjustmentAt = now;
            EmitAdjustment(host, state, HostThrottleTriggerCodes.ManualOverride, previousRate, newRate, state.Concurrency, state.Concurrency, ttfb: null, now);
        }
    }

    /// <inheritdoc/>
    public AimdCheckpointState CreateCheckpointState()
    {
        lock (_gate)
        {
            var hosts = _hosts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => ToThrottleState(pair.Key, pair.Value))
                .ToList();
            return new AimdCheckpointState(hosts.AsReadOnly());
        }
    }

    /// <inheritdoc/>
    public void RestoreCheckpointState(AimdCheckpointState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _hosts.Clear();
            foreach (var host in state.Hosts)
            {
                var restored = new HostState
                {
                    Rate = host.CurrentRate,
                    Concurrency = host.CurrentConcurrency,
                    BaselineMilliseconds = host.EwmaTtfbMilliseconds,
                    WarmupCount = host.EwmaTtfbMilliseconds > 0 ? _options.WarmupSamples : 0,
                    AdjustmentCount = host.AdjustmentCount,
                    LastAdjustmentAt = host.LastAdjustmentAt,
                    ManualOverrideRate = host.ManualOverrideRate
                };
                _hosts[host.Host.Trim()] = restored;
            }
        }
    }

    /// <summary>
    /// Creates a full-fidelity checkpoint preserving every internal field,
    /// including transient streak, warm-up, cooldown, pause, and spacing state.
    /// </summary>
    /// <returns>The complete controller checkpoint.</returns>
    public AimdControllerCheckpoint CreateFullCheckpoint()
    {
        lock (_gate)
        {
            var hosts = _hosts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => ToFullCheckpoint(pair.Key, pair.Value))
                .ToList();
            return new AimdControllerCheckpoint(hosts.AsReadOnly());
        }
    }

    /// <summary>
    /// Restores a full-fidelity checkpoint bit-exactly (exact pause/resume
    /// rehydration, FS-CRAWL-002 §3.5).
    /// </summary>
    /// <param name="checkpoint">The complete controller checkpoint.</param>
    public void RestoreFullCheckpoint(AimdControllerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_gate)
        {
            _hosts.Clear();
            foreach (var host in checkpoint.Hosts)
            {
                var restored = new HostState
                {
                    Rate = host.Rate,
                    Concurrency = host.Concurrency,
                    BaselineMilliseconds = host.BaselineMilliseconds,
                    WarmupCount = host.WarmupCount,
                    HealthyStreak = host.HealthyStreak,
                    CooldownUntil = host.CooldownUntil,
                    PausedUntil = host.PausedUntil,
                    LatencyEwmaSeconds = host.LatencyEwmaSeconds,
                    AdjustmentCount = host.AdjustmentCount,
                    LastAdjustmentAt = host.LastAdjustmentAt,
                    ManualOverrideRate = host.ManualOverrideRate,
                    NextPermittedAt = host.NextPermittedAt
                };
                _hosts[host.Host.Trim()] = restored;
            }
        }
    }

    private string? ClassifyTrigger(HostResponseSample sample, bool spike)
    {
        if (sample.StatusCode == 429)
        {
            return HostThrottleTriggerCodes.Http429;
        }

        if (sample.StatusCode == 503)
        {
            return HostThrottleTriggerCodes.Http503;
        }

        if (sample.IsChallenge)
        {
            return HostThrottleTriggerCodes.WafSoftBlock;
        }

        if (sample.StatusCode is >= 500 and < 600 && _options.DecreaseOnOther5xx)
        {
            return HostThrottleTriggerCodes.Http5xx;
        }

        return spike ? HostThrottleTriggerCodes.LatencySpike : null;
    }

    private void ApplyDecrease(string host, HostState state, string trigger, HostResponseSample sample, AimdResponseObservation observation, DateTimeOffset now)
    {
        var previousRate = state.Rate;
        var previousConcurrency = state.Concurrency;
        state.Rate = Math.Max(_options.MultiplicativeDecrease * state.Rate, _options.MinRate);
        state.Concurrency = Math.Max(1, (int)Math.Floor(_options.MultiplicativeDecrease * state.Concurrency));
        state.HealthyStreak = 0;
        state.CooldownUntil = now + TimeSpan.FromSeconds(_options.CooldownSeconds);
        state.AdjustmentCount++;
        state.LastAdjustmentAt = now;
        EmitAdjustment(host, state, trigger, previousRate, state.Rate, previousConcurrency, state.Concurrency, sample.TtfbMilliseconds, now);

        if (sample.StatusCode is 429 or 503 && observation.RetryAfterSeconds is > 0)
        {
            var pauseSeconds = Math.Max(observation.RetryAfterSeconds.Value, _options.CooldownSeconds);
            state.PausedUntil = now + TimeSpan.FromSeconds(pauseSeconds);
            EmitAdjustment(host, state, HostThrottleTriggerCodes.RetryAfter, state.Rate, state.Rate, state.Concurrency, state.Concurrency, sample.TtfbMilliseconds, now);
        }
    }

    private void UpdateBaselineAndLatency(HostState state, HostResponseSample sample, AimdResponseObservation observation)
    {
        state.BaselineMilliseconds = state.WarmupCount == 0
            ? sample.TtfbMilliseconds
            : (_options.EwmaLambda * sample.TtfbMilliseconds) + ((1 - _options.EwmaLambda) * state.BaselineMilliseconds);
        if (state.WarmupCount < _options.WarmupSamples)
        {
            state.WarmupCount++;
        }

        var latencyMs = observation.FullResponseLatencyMs ?? sample.TtfbMilliseconds;
        state.LatencyEwmaSeconds = (_options.EwmaLambda * (latencyMs / 1000.0)) + ((1 - _options.EwmaLambda) * state.LatencyEwmaSeconds);
    }

    private void ApplyIncreaseIfBelowCap(string host, HostState state, HostResponseSample sample, DateTimeOffset now)
    {
        var cap = ComputeRateCap(host);
        if (state.Rate >= cap)
        {
            return;
        }

        var previousRate = state.Rate;
        var previousConcurrency = state.Concurrency;
        state.Rate = Math.Min(state.Rate + _options.AdditiveIncrease, cap);
        state.Concurrency = ComputeConcurrency(state);
        state.AdjustmentCount++;
        state.LastAdjustmentAt = now;
        EmitAdjustment(host, state, HostThrottleTriggerCodes.AdditiveIncrease, previousRate, state.Rate, previousConcurrency, state.Concurrency, sample.TtfbMilliseconds, now);
    }

    private double ComputeRateCap(string host)
    {
        var cap = double.IsPositiveInfinity(_options.ResolvedThroughputCeiling)
            ? _options.MaxAutoRate
            : _options.ResolvedThroughputCeiling;
        var crawlDelay = _crawlDelaySource?.GetCrawlDelaySeconds(host);
        if (crawlDelay is > 0)
        {
            cap = Math.Min(cap, 1.0 / crawlDelay.Value);
        }

        return cap;
    }

    private int ComputeConcurrency(HostState state)
    {
        var total = 0;
        foreach (var other in _hosts.Values)
        {
            total += other.Concurrency;
        }

        var available = Math.Max(1, _options.WorkerConcurrencyCeiling - (total - state.Concurrency));
        if (_options.PerHostConcurrencyCap is { } perHost)
        {
            available = Math.Min(available, perHost);
        }

        var target = (int)Math.Ceiling(state.Rate * state.LatencyEwmaSeconds);
        return Math.Clamp(target, 1, available);
    }

    private int StaticConcurrency()
    {
        var share = _options.WorkerConcurrencyCeiling;
        return _options.PerHostConcurrencyCap is { } perHost ? Math.Min(share, perHost) : share;
    }

    private double EffectiveRate(HostState state) =>
        state.ManualOverrideRate ?? (_options.Enabled ? state.Rate : _options.ResolvedThroughputCeiling);

    private HostState GetOrCreateState(string host)
    {
        var key = host.Trim();
        if (_hosts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new HostState
        {
            Rate = Math.Min(_options.InitialRate, ComputeRateCap(key)),
            Concurrency = _options.Enabled ? 1 : StaticConcurrency()
        };
        _hosts[key] = created;
        if (_options.Enabled)
        {
            RebalanceConcurrency();
        }

        return created;
    }

    /// <summary>
    /// Keeps the sum of per-host concurrency within the resolved worker ceiling
    /// when a new host joins: deterministically shrinks the largest shares
    /// (ordinal host order breaks ties) until the sum fits. When hosts outnumber
    /// slots, every host keeps its minimum of one (degenerate overload).
    /// </summary>
    private void RebalanceConcurrency()
    {
        var total = 0;
        foreach (var state in _hosts.Values)
        {
            total += state.Concurrency;
        }

        while (total > _options.WorkerConcurrencyCeiling)
        {
            HostState? largest = null;
            foreach (var pair in _hosts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pair.Value.Concurrency > 1 && (largest is null || pair.Value.Concurrency > largest.Concurrency))
                {
                    largest = pair.Value;
                }
            }

            if (largest is null)
            {
                break;
            }

            largest.Concurrency--;
            total--;
        }
    }

    private HostThrottleState ToThrottleState(string host, HostState state) => new(
        host,
        EffectiveRate(state),
        _options.Enabled ? state.Concurrency : StaticConcurrency(),
        state.BaselineMilliseconds,
        state.AdjustmentCount,
        state.LastAdjustmentAt,
        state.ManualOverrideRate);

    private static AimdHostCheckpoint ToFullCheckpoint(string host, HostState state) => new(
        host,
        state.Rate,
        state.Concurrency,
        state.BaselineMilliseconds,
        state.WarmupCount,
        state.HealthyStreak,
        state.CooldownUntil,
        state.PausedUntil,
        state.LatencyEwmaSeconds,
        state.AdjustmentCount,
        state.LastAdjustmentAt,
        state.ManualOverrideRate,
        state.NextPermittedAt);

    private void EmitAdjustment(string host, HostState state, string trigger, double previousRate, double newRate, int previousConcurrency, int newConcurrency, double? ttfb, DateTimeOffset now)
    {
        _sink?.Emit(new HostThrottleAdjustment(
            _auditId,
            host,
            trigger,
            previousRate,
            newRate,
            previousConcurrency,
            newConcurrency,
            ttfb,
            state.BaselineMilliseconds,
            now));
    }

    private sealed class HostState
    {
        public double Rate { get; set; }

        public int Concurrency { get; set; }

        public double BaselineMilliseconds { get; set; }

        public int WarmupCount { get; set; }

        public int HealthyStreak { get; set; }

        public DateTimeOffset CooldownUntil { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset PausedUntil { get; set; } = DateTimeOffset.MinValue;

        public double LatencyEwmaSeconds { get; set; } = 1.0;

        public long AdjustmentCount { get; set; }

        public DateTimeOffset? LastAdjustmentAt { get; set; }

        public double? ManualOverrideRate { get; set; }

        public DateTimeOffset NextPermittedAt { get; set; } = DateTimeOffset.MinValue;
    }
}
