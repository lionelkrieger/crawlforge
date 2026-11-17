using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Transactional retention (§9.5): keep-last / age / explicit policies, soft-delete
/// sweeping, configuration preservation, artifact cleanup with shared-content
/// awareness, orphan reclamation, and full rollback on mid-prune failure.
/// </summary>
public sealed class RetentionPrunerTests
{
    [Fact]
    public async Task Prune_KeepLastAudits_RemovesOlderAuditsAndFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 5);

        var pruner = new RetentionPruner(database.Provider, store);
        var result = await pruner.PruneAsync(new RetentionPolicy(KeepLastAudits: 2));

        Assert.True(result.DeletedRowCount > 0);
        Assert.Contains("audits", result.PrunedTables);
        Assert.Contains("url_records", result.PrunedTables);
        Assert.Contains("findings", result.PrunedTables);
        Assert.Contains("content_artifacts", result.PrunedTables);

        // Base-chain audit (day 0) + days 1–3 pruned; days 4–5 kept.
        Assert.Equal(2, await database.CountAsync("audits"));
        Assert.Equal(4, await database.CountAsync("url_records"));
        Assert.Equal(2, await database.CountAsync("findings"));
        Assert.Equal(2, await database.CountAsync("content_artifacts"));

        var remainingKeys = await store.ListStorageKeysAsync();
        Assert.Equal(2, remainingKeys.Count);
        Assert.Contains(scenario[^1].StorageKey, remainingKeys);
        Assert.Contains(scenario[^2].StorageKey, remainingKeys);
        Assert.DoesNotContain(scenario[0].StorageKey, remainingKeys);
    }

    [Fact]
    public async Task Prune_SharedContentAcrossKeptAndPrunedAudits_Survives()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 3);

        // Store identical content on the oldest and newest audits.
        var sharedPayload = new byte[] { 8, 8, 8, 8 };
        var sharedOld = await store.PutAsync(scenario[0].AuditId, scenario[0].UrlIds[0], ContentArtifactKind.RenderedDom, new MemoryStream(sharedPayload));
        var sharedNew = await store.PutAsync(scenario[^1].AuditId, scenario[^1].UrlIds[0], ContentArtifactKind.RenderedDom, new MemoryStream(sharedPayload));
        Assert.Equal(sharedOld.StorageKey, sharedNew.StorageKey);

        var pruner = new RetentionPruner(database.Provider, store);
        await pruner.PruneAsync(new RetentionPolicy(KeepLastAudits: 1));

        Assert.True(store.Exists(sharedOld.StorageKey), "Shared file referenced by a kept audit must survive.");
        Assert.False(store.Exists(scenario[0].StorageKey), "Unreferenced pruned-audit file must be reclaimed.");
    }

    [Fact]
    public async Task Prune_OlderThan_UsesFinishedTimestamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        await SeedScenarioAsync(database, store, auditCount: 5);

        var pruner = new RetentionPruner(database.Provider, store);
        var result = await pruner.PruneAsync(new RetentionPolicy(OlderThan: SampleFactory.BaseTime.AddDays(3.5)));

        Assert.True(result.DeletedRowCount > 0);
        Assert.Equal(2, await database.CountAsync("audits")); // days 4 and 5 remain
        Assert.Equal(4, await database.CountAsync("url_records"));
    }

    [Fact]
    public async Task Prune_ExplicitAuditId_BypassesKeepLastProtection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 3);
        var newest = scenario[^1];

        var pruner = new RetentionPruner(database.Provider, store);
        await pruner.PruneAsync(new RetentionPolicy(AuditId: newest.AuditId, KeepLastAudits: 10));

        Assert.Equal(0L, await database.ScalarAsync($"SELECT COUNT(*) FROM audits WHERE id = {newest.AuditId}"));
        Assert.Equal(0L, await database.ScalarAsync($"SELECT COUNT(*) FROM url_records WHERE audit_id = {newest.AuditId}"));
        Assert.False(store.Exists(newest.StorageKey));
        Assert.Equal(3, await database.CountAsync("audits")); // base + days 1–2 untouched
    }

    [Fact]
    public async Task Prune_SoftDeletedAudits_AlwaysPruned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 2);

        var audits = new SqlRepository<AuditDto>(database.Provider);
        var deleted = await audits.GetByIdAsync(scenario[0].AuditId);
        await audits.UpdateAsync(deleted! with { IsDeleted = true });

        var pruner = new RetentionPruner(database.Provider, store);
        await pruner.PruneAsync(new RetentionPolicy(KeepLastAudits: 100));

        Assert.Equal(0L, await database.ScalarAsync($"SELECT COUNT(*) FROM audits WHERE id = {scenario[0].AuditId}"));
        Assert.Equal(2, await database.CountAsync("audits")); // base + day 2 live audit kept
    }

    [Fact]
    public async Task Prune_NeverTouchesConfigurationTables()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        await SeedScenarioAsync(database, store, auditCount: 3);

        var settings = new SqlRepository<ApplicationSettingsDto>(database.Provider);
        await settings.CreateAsync(SampleFactory.Create<ApplicationSettingsDto>());
        var dictionary = new SqlRepository<UserDictionaryEntryDto>(database.Provider);
        await dictionary.CreateAsync(SampleFactory.Create<UserDictionaryEntryDto>());

        var before = new Dictionary<string, long>();
        foreach (var table in new[] { "projects", "crawl_configurations", "schedule_definitions", "application_settings", "user_dictionary", "export_destinations", "content_scope_profiles" })
        {
            before[table] = await database.CountAsync(table);
        }

        var pruner = new RetentionPruner(database.Provider, store);
        await pruner.PruneAsync(new RetentionPolicy(KeepLastAudits: 0));

        foreach (var (table, count) in before)
        {
            Assert.Equal(count, await database.CountAsync(table));
        }
    }

    [Fact]
    public async Task Prune_OlderThan_PurgesActionLogAndDetachedScheduleRuns()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        await SeedScenarioAsync(database, store, auditCount: 1);

        var log = new SqlRepository<AutomationActionLogDto>(database.Provider);
        await log.CreateAsync(SampleFactory.Create<AutomationActionLogDto>() with { Timestamp = SampleFactory.BaseTime.AddDays(-10) });
        await log.CreateAsync(SampleFactory.Create<AutomationActionLogDto>() with { Timestamp = SampleFactory.BaseTime.AddDays(10) });

        var runs = new SqlRepository<ScheduleRunDto>(database.Provider);
        await runs.CreateAsync(SampleFactory.Create<ScheduleRunDto>() with { AuditId = null, StartedAt = SampleFactory.BaseTime.AddDays(-10) });
        await runs.CreateAsync(SampleFactory.Create<ScheduleRunDto>() with { AuditId = null, StartedAt = SampleFactory.BaseTime.AddDays(10) });

        var pruner = new RetentionPruner(database.Provider, store);
        var result = await pruner.PruneAsync(new RetentionPolicy(OlderThan: SampleFactory.BaseTime));

        Assert.Contains("automation_action_log", result.PrunedTables);
        Assert.Equal(1, await database.CountAsync("automation_action_log"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT(*) FROM schedule_runs WHERE audit_id IS NULL"));
    }

    [Fact]
    public async Task Prune_MidTransactionFailure_RollsBackRowsAndFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 3);

        var auditsBefore = await database.CountAsync("audits");
        var urlsBefore = await database.CountAsync("url_records");
        var findingsBefore = await database.CountAsync("findings");
        var artifactsBefore = await database.CountAsync("content_artifacts");
        var filesBefore = (await store.ListStorageKeysAsync()).Count;

        // Inject a failure partway through the dependent-row deletion sequence.
        await database.ExecuteAsync(
            "CREATE TRIGGER fail_findings_delete BEFORE DELETE ON findings BEGIN SELECT RAISE(ABORT, 'injected retention failure'); END;");

        var pruner = new RetentionPruner(database.Provider, store);
        await Assert.ThrowsAnyAsync<Exception>(() => pruner.PruneAsync(new RetentionPolicy(KeepLastAudits: 1)));

        Assert.Equal(auditsBefore, await database.CountAsync("audits"));
        Assert.Equal(urlsBefore, await database.CountAsync("url_records"));
        Assert.Equal(findingsBefore, await database.CountAsync("findings"));
        Assert.Equal(artifactsBefore, await database.CountAsync("content_artifacts"));
        Assert.Equal(filesBefore, (await store.ListStorageKeysAsync()).Count);
        Assert.True(store.Exists(scenario[0].StorageKey));
    }

    [Fact]
    public async Task Prune_ReclaimsOrphanedFiles_FromInterruptedOperations()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var scenario = await SeedScenarioAsync(database, store, auditCount: 1);

        var orphanKey = "sha256/ee/" + new string('e', 64);
        await store.SaveCompressedAsync(orphanKey, new MemoryStream(new byte[] { 1, 2, 3 }));
        Assert.True(store.Exists(orphanKey));

        var pruner = new RetentionPruner(database.Provider, store);
        await pruner.PruneAsync(new RetentionPolicy(AuditId: scenario[0].AuditId));

        Assert.False(store.Exists(orphanKey), "Orphan sweep must reclaim unreferenced files.");
        Assert.Empty(await store.ListStorageKeysAsync());
    }

    private sealed record AuditScenario(long AuditId, long[] UrlIds, string StorageKey);

    private static async Task<List<AuditScenario>> SeedScenarioAsync(
        TestDatabase database, CompressedContentStore store, int auditCount)
    {
        var audits = new SqlRepository<AuditDto>(database.Provider);
        var urls = new SqlRepository<UrlRecordDto>(database.Provider);
        var findings = new SqlRepository<FindingDto>(database.Provider);
        var scenarios = new List<AuditScenario>();

        for (var i = 1; i <= auditCount; i++)
        {
            var audit = await audits.CreateAsync(SampleFactory.Create<AuditDto>() with
            {
                StartedAt = SampleFactory.BaseTime.AddDays(i),
                FinishedAt = SampleFactory.BaseTime.AddDays(i).AddHours(1)
            });

            var urlIds = new long[2];
            for (var u = 0; u < 2; u++)
            {
                var url = await urls.CreateAsync(SampleFactory.Create<UrlRecordDto>() with
                {
                    AuditId = audit.Id,
                    CanonicalUrl = $"https://example.test/a{i}/u{u}",
                    Path = $"/a{i}/u{u}"
                });
                urlIds[u] = url.Id;
            }

            await findings.CreateAsync(SampleFactory.Create<FindingDto>() with { AuditId = audit.Id, UrlId = urlIds[0] });
            var artifact = await store.PutAsync(audit.Id, urlIds[0], ContentArtifactKind.Body,
                new MemoryStream(new byte[] { (byte)i, 1, 2, 3, 4, 5 }));
            scenarios.Add(new AuditScenario(audit.Id, urlIds, artifact.StorageKey));
        }

        return scenarios;
    }
}
