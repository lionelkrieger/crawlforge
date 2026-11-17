using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-001 §5.7 checkpointing over the real persistence stack: save/load
/// round-trips, periodic and on-pause paths, and criterion #6 — crash recovery
/// preserving frontier order, seen-set, and counters.
/// </summary>
public class CrawlCheckpointTests : IAsyncLifetime
{
    private FrontierTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await FrontierTestDatabase.CreateAsync();
        foreach (var auditId in new long[] { 1, 2, 3, 5, 7, 9, 10 })
        {
            await _database.SeedAuditAsync(auditId);
        }
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static FrontierEntry Entry(string url, string host, int depth = 0, double priority = 0, DiscoverySource source = DiscoverySource.Link) =>
        new(url, host, depth, priority, source);

    private async Task<long> CountCheckpointsAsync(long auditId)
    {
        await using var connection = await _database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM crawl_checkpoints WHERE audit_id = @audit";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task SaveAndLoadLatestRoundTripsAllFields()
    {
        var service = new CrawlCheckpointService(_database.Provider);
        var savedAt = DateTimeOffset.UtcNow;
        var checkpoint = new CrawlCheckpointDto(0, 7, "{\"Entries\":[]}", "seen.log", "{\"Queued\":3}", savedAt)
        {
            AimdStateJson = "{\"Hosts\":[]}"
        };
        await service.SaveAsync(checkpoint);

        var loaded = await service.LoadLatestAsync(7);
        Assert.NotNull(loaded);
        Assert.True(loaded!.Id > 0);
        Assert.Equal(7, loaded.AuditId);
        Assert.Equal("{\"Entries\":[]}", loaded.SerializedFrontier);
        Assert.Equal("seen.log", loaded.SeenSetReference);
        Assert.Equal("{\"Queued\":3}", loaded.CountersJson);
        Assert.Equal(savedAt, loaded.SavedAt);
        Assert.Equal("{\"Hosts\":[]}", loaded.AimdStateJson);
    }

    [Fact]
    public async Task LoadLatestReturnsNewestCheckpointPerAudit()
    {
        var service = new CrawlCheckpointService(_database.Provider);
        await service.SaveAsync(new CrawlCheckpointDto(0, 9, "older", null, "{}", DateTimeOffset.UtcNow.AddMinutes(-5)));
        await service.SaveAsync(new CrawlCheckpointDto(0, 9, "newer", null, "{}", DateTimeOffset.UtcNow));
        await service.SaveAsync(new CrawlCheckpointDto(0, 10, "other-audit", null, "{}", DateTimeOffset.UtcNow));

        var latest = await service.LoadLatestAsync(9);
        Assert.Equal("newer", latest!.SerializedFrontier);
        Assert.Null(await service.LoadLatestAsync(999));
    }

    [Fact]
    public async Task OnPauseCheckpointCapturesFrontierSeenSetCountersAndAimdState()
    {
        var politeness = new TestPolitenessController
        {
            CheckpointHosts = [new HostThrottleState("example.com", 1.5, 1, 90, 2, DateTimeOffset.UtcNow, null)]
        };
        var frontier = new PriorityFrontier(politeness);
        await frontier.EnqueueAsync(Entry("https://example.com/seed", "example.com", source: DiscoverySource.Seed));
        await frontier.EnqueueAsync(Entry("https://example.com/a", "example.com", priority: 2));
        await frontier.EnqueueAsync(Entry("https://example.com/b", "example.com", priority: 1));

        var seenSetPath = Path.Combine(_database.DirectoryPath, "seen.log");
        using var seenSet = new PersistentSeenSet(seenSetPath);
        seenSet.TryAdd("https://example.com/seed");
        seenSet.TryAdd("https://example.com/a");

        var service = new CrawlCheckpointService(_database.Provider);
        await using var manager = new CrawlCheckpointManager(frontier, seenSet, service, politeness);
        var counters = new CrawlCounters(Queued: 3, Fetched: 1, Errors: 0, Blocked: 0, Skipped: 0, Redirected: 0);

        var checkpoint = await manager.CheckpointOnPauseAsync(1, counters);
        Assert.Equal(1, checkpoint.AuditId);

        var loaded = await service.LoadLatestAsync(1);
        Assert.NotNull(loaded);
        Assert.Equal(seenSetPath, loaded!.SeenSetReference);

        var snapshot = JsonSerializer.Deserialize<FrontierSnapshot>(loaded.SerializedFrontier)!;
        Assert.Equal(
            ["https://example.com/seed", "https://example.com/a", "https://example.com/b"],
            snapshot.Entries.Select(e => e.Url));

        var restoredCounters = JsonSerializer.Deserialize<CrawlCounters>(loaded.CountersJson)!;
        Assert.Equal(counters, restoredCounters);

        var aimd = JsonSerializer.Deserialize<AimdCheckpointState>(loaded.AimdStateJson!)!;
        var host = Assert.Single(aimd.Hosts);
        Assert.Equal("example.com", host.Host);
        Assert.Equal(1.5, host.CurrentRate);
    }

    [Fact]
    public async Task PeriodicCheckpointsAccumulateDuringRun()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://example.com/", "example.com", source: DiscoverySource.Seed));
        var seenSetPath = Path.Combine(_database.DirectoryPath, "periodic-seen.log");
        using var seenSet = new PersistentSeenSet(seenSetPath);
        var service = new CrawlCheckpointService(_database.Provider);
        await using var manager = new CrawlCheckpointManager(
            frontier, seenSet, service, politeness: null, periodicInterval: TimeSpan.FromMilliseconds(50));

        var counters = new CrawlCounters(1, 0, 0, 0, 0, 0);
        manager.StartPeriodicCheckpoints(2, () => counters);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await CountCheckpointsAsync(2) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await manager.StopPeriodicCheckpointsAsync();
        Assert.Null(manager.LastPeriodicError);
        Assert.True(await CountCheckpointsAsync(2) >= 2, "Periodic checkpointing produced fewer than two rows.");

        var latest = await service.LoadLatestAsync(2);
        Assert.Equal(seenSetPath, latest!.SeenSetReference);
        Assert.Null(latest.AimdStateJson);
    }

    [Fact]
    public async Task CrashRecoveryPreservesFrontierOrderSeenSetAndCounters()
    {
        // ---- Phase 1: a running crawl writes its on-pause checkpoint. ----
        var politeness1 = new TestPolitenessController
        {
            CheckpointHosts =
            [
                new HostThrottleState("a.example.com", 2.0, 1, 100, 1, DateTimeOffset.UtcNow, null),
                new HostThrottleState("b.example.com", 0.5, 1, 400, 3, DateTimeOffset.UtcNow, null)
            ]
        };
        var frontier1 = new PriorityFrontier(politeness1);
        await frontier1.EnqueueAsync(Entry("https://b.example.com/seed", "b.example.com", source: DiscoverySource.Seed));
        await frontier1.EnqueueAsync(Entry("https://a.example.com/deep", "a.example.com", depth: 2, priority: 5));
        await frontier1.EnqueueAsync(Entry("https://a.example.com/shallow", "a.example.com", depth: 0, priority: 5));
        await frontier1.EnqueueAsync(Entry("https://a.example.com/high", "a.example.com", priority: 9));

        var seenSetPath = Path.Combine(_database.DirectoryPath, "crash-seen.log");
        var seenSet1 = new PersistentSeenSet(seenSetPath);
        seenSet1.TryAdd("https://b.example.com/seed");
        seenSet1.TryAdd("https://a.example.com/shallow");
        seenSet1.TryAdd("https://a.example.com/deep");
        seenSet1.TryAdd("https://a.example.com/high");

        var service1 = new CrawlCheckpointService(_database.Provider);
        var manager1 = new CrawlCheckpointManager(frontier1, seenSet1, service1, politeness1);
        var counters1 = new CrawlCounters(Queued: 10, Fetched: 4, Errors: 1, Blocked: 1, Skipped: 0, Redirected: 2);
        await manager1.CheckpointOnPauseAsync(3, counters1);

        var expectedOrder = frontier1.CreateSnapshot().Entries.Select(e => e.Url).ToList();

        // ---- Simulated crash: the process dies. Handles are released by the OS;
        // no graceful shutdown runs. Durability comes from the flushed checkpoint. ----
        seenSet1.Dispose();

        // ---- Phase 2: a fresh process reopens the database and resumes. ----
        using var provider2 = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_database.DirectoryPath, "frontier.db")
        });
        var service2 = new CrawlCheckpointService(provider2);
        var frontier2 = new PriorityFrontier(new TestPolitenessController());
        var seenSet2 = new PersistentSeenSet(seenSetPath);
        var politeness2 = new TestPolitenessController();
        await using var manager2 = new CrawlCheckpointManager(frontier2, seenSet2, service2, politeness2);

        var state = await manager2.ResumeAsync(3);
        Assert.NotNull(state);
        Assert.Equal(counters1, state!.Counters);
        Assert.Equal(seenSetPath, state.SeenSetReference);
        Assert.NotNull(state.AimdStateJson);

        // Frontier order survives exactly.
        var resumedOrder = new List<string>();
        while (await frontier2.DequeueAsync() is { } entry)
        {
            resumedOrder.Add(entry.Url);
        }

        Assert.Equal(expectedOrder, resumedOrder);
        Assert.Equal(
            ["https://b.example.com/seed", "https://a.example.com/high", "https://a.example.com/shallow", "https://a.example.com/deep"],
            resumedOrder);

        // Seen-set survives: every URL is still deduplicated.
        Assert.Equal(4, seenSet2.Count);
        Assert.False(seenSet2.TryAdd("https://a.example.com/shallow"));
        Assert.True(seenSet2.Contains("https://b.example.com/seed"));
        Assert.True(seenSet2.TryAdd("https://a.example.com/new"));

        // AIMD controller state was restored through the contract.
        Assert.NotNull(politeness2.RestoredState);
        Assert.Equal(2, politeness2.RestoredState!.Hosts.Count);
        Assert.Equal("a.example.com", politeness2.RestoredState.Hosts[0].Host);
        Assert.Equal(0.5, politeness2.RestoredState.Hosts[1].CurrentRate);

        seenSet2.Dispose();
    }

    [Fact]
    public async Task ResumeWithoutCheckpointReturnsNull()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        using var seenSet = new PersistentSeenSet(Path.Combine(_database.DirectoryPath, "empty-seen.log"));
        var service = new CrawlCheckpointService(_database.Provider);
        await using var manager = new CrawlCheckpointManager(frontier, seenSet, service);
        Assert.Null(await manager.ResumeAsync(42));
    }

    [Fact]
    public async Task ResumeRebuildsSeenSetFromCheckpointReference()
    {
        var originalPath = Path.Combine(_database.DirectoryPath, "original-seen.log");
        using (var original = new PersistentSeenSet(originalPath))
        {
            original.TryAdd("https://example.com/one");
            original.Flush();
        }

        var service = new CrawlCheckpointService(_database.Provider);
        await service.SaveAsync(new CrawlCheckpointDto(
            0, 5, "{\"Entries\":[]}", originalPath, "{\"Queued\":1,\"Fetched\":1,\"Errors\":0,\"Blocked\":0,\"Skipped\":0,\"Redirected\":0}", DateTimeOffset.UtcNow));

        // The resuming process starts with a different local seen-set file.
        var otherPath = Path.Combine(_database.DirectoryPath, "other-seen.log");
        using var seenSet = new PersistentSeenSet(otherPath);
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await using var manager = new CrawlCheckpointManager(frontier, seenSet, service);

        var state = await manager.ResumeAsync(5);
        Assert.NotNull(state);
        Assert.Equal(originalPath, seenSet.Reference);
        Assert.True(seenSet.Contains("https://example.com/one"));
        Assert.Equal(1, state!.Counters.Fetched);
    }
}
