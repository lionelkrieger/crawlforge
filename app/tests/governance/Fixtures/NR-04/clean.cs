namespace Fixture.Nr04;

/// <summary>Local system-health heartbeat (hub, loopback) — explicitly permitted.</summary>
public sealed class SystemHealthHeartbeat
{
    public TimeSpan Interval { get; } = TimeSpan.FromSeconds(2);

    public object Snapshot() => new { workingSetMb = 128, managedHeapMb = 64 };
}
