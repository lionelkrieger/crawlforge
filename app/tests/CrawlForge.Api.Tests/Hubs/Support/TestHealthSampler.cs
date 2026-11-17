using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// Controllable health sampler for heartbeat/compact/metrics assertions.
/// </summary>
public sealed class TestHealthSampler : ISystemHealthSampler
{
    /// <summary>The payload returned by captures; mutate between assertions.</summary>
    public SystemHealthPayload Payload { get; set; } = new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        BackendWorkingSetBytes: 1_000_000,
        ManagedHeapBytes: 500_000,
        GcGen0Count: 3,
        GcGen1Count: 2,
        GcGen2Count: 1,
        SqliteWalBytes: 4096,
        AuditId: null);

    /// <inheritdoc />
    public SystemHealthSnapshot CaptureSnapshot() => new(
        Payload.BackendWorkingSetBytes,
        Payload.ManagedHeapBytes,
        Payload.SqliteWalBytes,
        Payload.Timestamp);

    /// <inheritdoc />
    public SystemHealthPayload CapturePayload() => Payload;
}
