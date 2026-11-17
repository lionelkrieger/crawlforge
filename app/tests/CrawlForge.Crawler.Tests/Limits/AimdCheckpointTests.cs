using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// FS-CRAWL-002 §3.5/§3.6 checkpointing: the locked checkpoint surface
/// preserves every HostThrottleState field across pause/resume, the local
/// full-fidelity surface preserves transient state bit-exactly, and the
/// serialized payload round-trips through the real crawl_checkpoints additive
/// AimdStateJson column.
/// </summary>
public sealed class AimdCheckpointTests : IClassFixture<SqliteStackFixture>
{
    private const string HostA = "a.example";
    private const string HostB = "b.example";
    private readonly SqliteStackFixture _stack;

    public AimdCheckpointTests(SqliteStackFixture stack)
    {
        _stack = stack;
    }

    private static AimdPolitenessController DriveState(TimeProvider time)
    {
        var controller = AimdTestHelpers.CreateController(time: time);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(HostA, AimdTestHelpers.Healthy());
        }

        controller.RecordResponse(HostA, AimdTestHelpers.Status(429));
        controller.SetManualOverride(HostB, 3.0);
        controller.WaitAsync(HostA).AsTask().Wait();
        return controller;
    }

    [Fact]
    public void LockedRoundTripPreservesEveryHostThrottleStateField()
    {
        var time = new ManualTimeProvider();
        var original = DriveState(time);

        var checkpoint = original.CreateCheckpointState();
        var restored = AimdTestHelpers.CreateController(time: new ManualTimeProvider());
        restored.RestoreCheckpointState(checkpoint);

        Assert.Equal(2, checkpoint.Hosts.Count);
        foreach (var host in checkpoint.Hosts)
        {
            Assert.Equal(original.GetState(host.Host), restored.GetState(host.Host));
        }
    }

    [Fact]
    public void FullRoundTripPreservesTransientStateExactly()
    {
        var time = new ManualTimeProvider();
        var original = DriveState(time);

        var checkpoint = original.CreateFullCheckpoint();
        var restored = AimdTestHelpers.CreateController(time: new ManualTimeProvider());
        restored.RestoreFullCheckpoint(checkpoint);

        Assert.Equal(checkpoint, restored.CreateFullCheckpoint());
    }

    [Fact]
    public void RestoreClearsHostsAbsentFromTheCheckpoint()
    {
        var original = AimdTestHelpers.CreateController();
        original.RecordResponse(HostA, AimdTestHelpers.Healthy());
        original.RecordResponse(HostB, AimdTestHelpers.Healthy());
        var onlyA = new AimdCheckpointState(new[] { original.GetState(HostA) });

        var restored = AimdTestHelpers.CreateController();
        restored.RecordResponse(HostB, AimdTestHelpers.Healthy());
        restored.RestoreCheckpointState(onlyA);

        var checkpoint = restored.CreateCheckpointState();
        var host = Assert.Single(checkpoint.Hosts);
        Assert.Equal(HostA, host.Host);
        Assert.Equal(0, restored.GetState(HostB).AdjustmentCount);
    }

    [Fact]
    public async Task SerializedPayloadRoundTripsThroughRealCheckpointRow()
    {
        var auditId = await _stack.CreateAuditAsync();
        var time = new ManualTimeProvider();
        var original = AimdTestHelpers.CreateController(time: time, auditId: auditId);
        for (var i = 0; i < 3; i++)
        {
            original.RecordResponse(HostA, AimdTestHelpers.Healthy());
        }

        original.RecordResponse(HostA, AimdTestHelpers.Status(503));
        var checkpoint = original.CreateCheckpointState();
        var json = JsonSerializer.Serialize(checkpoint);

        var row = new CrawlCheckpointDto(
            Id: 0,
            AuditId: auditId,
            SerializedFrontier: "{}",
            SeenSetReference: null,
            CountersJson: "{}",
            SavedAt: DateTimeOffset.UtcNow)
        {
            AimdStateJson = json
        };
        var created = await _stack.Checkpoints.CreateAsync(row);
        var read = await _stack.Checkpoints.GetByIdAsync(created.Id);

        Assert.NotNull(read);
        Assert.Equal(json, read.AimdStateJson);
        var deserialized = JsonSerializer.Deserialize<AimdCheckpointState>(read.AimdStateJson!);
        Assert.NotNull(deserialized);

        var restored = AimdTestHelpers.CreateController(time: new ManualTimeProvider(), auditId: auditId);
        restored.RestoreCheckpointState(deserialized);
        Assert.Equal(original.GetState(HostA), restored.GetState(HostA));
    }
}
