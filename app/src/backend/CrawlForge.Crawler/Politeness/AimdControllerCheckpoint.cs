namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Full-fidelity per-host AIMD state for exact pause/resume rehydration
/// (FS-CRAWL-002 §3.5). The locked <c>AimdCheckpointState</c> contract carries
/// the <c>HostThrottleState</c> subset; this local surface additionally
/// preserves the transient control state (streak, warm-up, cooldown, pause,
/// latency EWMA, spacing clock) so a checkpoint round-trip is bit-exact.
/// </summary>
/// <param name="Host">Target host.</param>
/// <param name="Rate">Current adaptive rate (requests/second).</param>
/// <param name="Concurrency">Current per-host concurrency.</param>
/// <param name="BaselineMilliseconds">Rolling EWMA TTFB baseline.</param>
/// <param name="WarmupCount">Healthy samples observed toward warm-up.</param>
/// <param name="HealthyStreak">Consecutive healthy, fast responses.</param>
/// <param name="CooldownUntil">Cooldown expiry (MinValue when inactive).</param>
/// <param name="PausedUntil">Retry-After pause expiry (MinValue when inactive).</param>
/// <param name="LatencyEwmaSeconds">EWMA of full response latency in seconds.</param>
/// <param name="AdjustmentCount">Total rate adjustments applied.</param>
/// <param name="LastAdjustmentAt">Timestamp of the last adjustment, when any.</param>
/// <param name="ManualOverrideRate">Manual rate pin, when set.</param>
/// <param name="NextPermittedAt">Next permitted request time for spacing.</param>
public sealed record AimdHostCheckpoint(
    string Host,
    double Rate,
    int Concurrency,
    double BaselineMilliseconds,
    int WarmupCount,
    int HealthyStreak,
    DateTimeOffset CooldownUntil,
    DateTimeOffset PausedUntil,
    double LatencyEwmaSeconds,
    long AdjustmentCount,
    DateTimeOffset? LastAdjustmentAt,
    double? ManualOverrideRate,
    DateTimeOffset NextPermittedAt);

/// <summary>
/// Full-fidelity controller checkpoint: every host's complete internal state.
/// Value equality is structural over the host list, so checkpoint round-trips
/// compare by content (FS-CRAWL-002 §3.5).
/// </summary>
/// <param name="Hosts">Per-host full states at checkpoint time.</param>
public sealed record AimdControllerCheckpoint(IReadOnlyList<AimdHostCheckpoint> Hosts)
{
    /// <summary>Compares two checkpoints structurally, host by host.</summary>
    /// <param name="other">The other checkpoint.</param>
    /// <returns>True when both carry the same per-host states in order.</returns>
    public bool Equals(AimdControllerCheckpoint? other) =>
        other is not null && (ReferenceEquals(this, other) || Hosts.SequenceEqual(other.Hosts));

    /// <summary>Hashes the per-host states in order.</summary>
    /// <returns>The structural hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var host in Hosts)
        {
            hash.Add(host);
        }

        return hash.ToHashCode();
    }
}
