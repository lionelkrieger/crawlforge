using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// Compact service tests (criterion #41; FS-CRAWL-002 §7.2; locked
/// ICompactService/CompactResult from amendment WP-05-A1): before/after
/// snapshots, ≥10 s rate limit and audit-running refusal (both
/// InvalidOperationException), external-provider degradation, and a real
/// SQLite truncating checkpoint.
/// </summary>
public sealed class CompactServiceTests
{
    private readonly TestHealthSampler _sampler = new();
    private readonly MutableTimeProvider _time = new();

    private sealed class FakeCheckpointer(bool result) : IWalCheckpointer
    {
        public int Calls { get; private set; }

        public Task<bool> TryCheckpointTruncateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Compact_PerformsGcAndCheckpoint_ReturnsLockedBeforeAfter()
    {
        var checkpointer = new FakeCheckpointer(true);
        var service = new CompactService(_sampler, checkpointer, timeProvider: _time);
        _sampler.Payload = _sampler.Payload with { BackendWorkingSetBytes = 2_000_000 };

        CompactResult result = await service.CompactAsync();

        Assert.Contains(CompactService.ActionGcCompaction, service.LastActionsPerformed);
        Assert.Contains(CompactService.ActionWalCheckpoint, service.LastActionsPerformed);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(CompactService.ActionWorkingSetTrim, service.LastActionsPerformed);
        }

        Assert.Equal(2_000_000, result.Before.WorkingSetBytes);
        Assert.Equal(500_000, result.Before.GcHeapBytes);
        Assert.Equal(4096, result.Before.WalSizeBytes);
        Assert.Equal(1, checkpointer.Calls);
        Assert.True(result.CompletedAt >= result.Before.Timestamp);

        // Locked wire shape (WP-06-A1): ActionsPerformed populated in execution order.
        Assert.NotNull(result.ActionsPerformed);
        Assert.NotEmpty(result.ActionsPerformed!);
        Assert.Equal(CompactService.ActionGcCompaction, result.ActionsPerformed![0]);
        Assert.Contains(CompactService.ActionWalCheckpoint, result.ActionsPerformed!);
        Assert.Equal(service.LastActionsPerformed, result.ActionsPerformed);
    }

    [Fact]
    public async Task Compact_Result_ActionsPerformed_MatchExecutionOrder_AndDegradeHonestly()
    {
        // Full success path: GC first, then WAL checkpoint, then working-set trim.
        var full = new CompactService(_sampler, new FakeCheckpointer(true), timeProvider: _time);
        var fullResult = await full.CompactAsync();
        Assert.NotNull(fullResult.ActionsPerformed);
        Assert.Equal(CompactService.ActionGcCompaction, fullResult.ActionsPerformed![0]);
        Assert.Equal(full.LastActionsPerformed, fullResult.ActionsPerformed);
        Assert.NotNull(fullResult.Before);
        Assert.NotNull(fullResult.After);

        // Degraded path (external provider, no WAL): the result must NOT claim the
        // WAL action — the wire reflects exactly what ran.
        _time.Advance(TimeSpan.FromSeconds(11));
        var degraded = new CompactService(_sampler, walCheckpointer: null, timeProvider: _time);
        var degradedResult = await degraded.CompactAsync();
        Assert.NotNull(degradedResult.ActionsPerformed);
        Assert.Contains(CompactService.ActionGcCompaction, degradedResult.ActionsPerformed!);
        Assert.DoesNotContain(CompactService.ActionWalCheckpoint, degradedResult.ActionsPerformed!);
        Assert.Equal(degraded.LastActionsPerformed, degradedResult.ActionsPerformed);
    }

    [Fact]
    public async Task Compact_RateLimited_Within10Seconds_ThrowsInvalidOperation()
    {
        var service = new CompactService(_sampler, timeProvider: _time);
        await service.CompactAsync();

        _time.Advance(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync());

        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await service.CompactAsync(); // t=10s: allowed again.
        Assert.NotEmpty(service.LastActionsPerformed);
        Assert.True(result.CompletedAt > result.Before.Timestamp);
    }

    [Fact]
    public async Task Compact_AuditInFlight_Refuses_ThrowsInvalidOperation()
    {
        var checkpointer = new FakeCheckpointer(true);
        var service = new CompactService(_sampler, checkpointer, isAuditRunning: () => true, timeProvider: _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync());
        Assert.Equal(0, checkpointer.Calls); // No transaction interference.
        Assert.Empty(service.LastActionsPerformed);
    }

    [Fact]
    public async Task Compact_ExternalProvider_OmitsWalAction()
    {
        var service = new CompactService(_sampler, walCheckpointer: null, timeProvider: _time);
        var result = await service.CompactAsync();
        Assert.Contains(CompactService.ActionGcCompaction, service.LastActionsPerformed);
        Assert.DoesNotContain(CompactService.ActionWalCheckpoint, service.LastActionsPerformed);
        Assert.DoesNotContain(CompactService.ActionWalCheckpoint, result.ActionsPerformed!);
    }

    [Fact]
    public async Task Compact_CheckpointBusy_OmitsWalAction()
    {
        var service = new CompactService(_sampler, new FakeCheckpointer(false), timeProvider: _time);
        var result = await service.CompactAsync();
        Assert.DoesNotContain(CompactService.ActionWalCheckpoint, service.LastActionsPerformed);
        Assert.DoesNotContain(CompactService.ActionWalCheckpoint, result.ActionsPerformed!);
    }

    [Fact]
    public void HealthService_GetSnapshot_ReturnsLockedShape()
    {
        ISystemHealthService service = new SystemHealthSampler();
        var snapshot = service.GetSnapshot();
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.GcHeapBytes > 0);
        Assert.True(snapshot.Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RealSqlite_CheckpointTruncate_ShrinksWal()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-05-02-compact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "compact.db");
            using var provider = new SqliteDbProvider(new SqliteProviderOptions { DatabasePath = databasePath });
            await using (var connection = await provider.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(x); INSERT INTO t VALUES (1),(2),(3);";
                await command.ExecuteNonQueryAsync();
            }

            var monitor = new SqliteWalMonitor(databasePath);
            var checkpointer = new SqliteWalCheckpointer(provider);
            var succeeded = await checkpointer.TryCheckpointTruncateAsync();

            Assert.True(succeeded);
            var walSize = monitor.TryGetWalSizeBytes();
            Assert.True(walSize is null or 0, $"WAL should be truncated, was {walSize}.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(50);
                }
            }
        }
    }
}
