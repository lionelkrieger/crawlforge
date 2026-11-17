using System.IO.Compression;
using System.Text;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Project archive (criteria #15/#18): full-fidelity export/import including the
/// compressed content store, surrogate-key remapping into a fresh provider
/// instance (provider revert), global-table exclusion, and atomic rollback of a
/// corrupt import.
/// </summary>
public sealed class ProjectArchiveTests
{
    private static readonly string[] GlobalTables =
    [
        "application_settings", "schema_migrations", "saved_views", "export_destinations",
        "user_dictionary", "automation_action_log", "ui_plugins", "waf_user_signatures", "user_stopwords"
    ];

    [Fact]
    public async Task ExportImport_RoundTripsEveryProjectScopedTable_WithArtifacts()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceStore = new CompressedContentStore(source.ContentStoreRoot, source.Provider);
        var payloads = await SeedRichProjectAsync(source, sourceStore);

        var archiveService = new ProjectArchiveService(source.Provider, sourceStore);
        using var archiveStream = new MemoryStream();
        await archiveService.ExportAsync(1, archiveStream);
        archiveStream.Position = 0;

        await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
        var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);
        var importService = new ProjectArchiveService(target.Provider, targetStore);

        // The real migrator seeds global tables (application_settings) on first run;
        // capture the baseline counts — import must leave global tables untouched.
        var globalCountsBeforeImport = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in GlobalTables)
        {
            globalCountsBeforeImport[table] = await target.CountAsync(table);
        }

        var result = await importService.ImportAsync(archiveStream);

        Assert.True(result.ProjectId > 0);
        Assert.Equal(2, result.AuditsImported);
        Assert.Equal(3, result.UrlsImported);

        // Every project-scoped table must match the source exactly; global tables
        // must be unchanged from their pre-import (migration-seeded) baseline.
        foreach (var dtoType in SchemaNames.AllEntities)
        {
            var table = EntityMap.For(dtoType).TableName;
            if (GlobalTables.Contains(table))
            {
                Assert.Equal(globalCountsBeforeImport[table], await target.CountAsync(table));
                continue;
            }

            Assert.Equal(await source.CountAsync(table), await target.CountAsync(table));
        }

        // Artifact files: identical keys and identical compressed bytes.
        var sourceKeys = (await sourceStore.ListStorageKeysAsync()).OrderBy(k => k).ToList();
        var targetKeys = (await targetStore.ListStorageKeysAsync()).OrderBy(k => k).ToList();
        Assert.Equal(sourceKeys, targetKeys);
        Assert.Equal(4, sourceKeys.Count);
        foreach (var key in sourceKeys)
        {
            Assert.Equal(await ReadAllBytesAsync(sourceStore, key), await ReadAllBytesAsync(targetStore, key));
        }

        // Content remains readable offline after the move: original payloads intact.
        foreach (var (key, payload) in payloads)
        {
            await using var read = await targetStore.OpenReadAsync(key);
            Assert.NotNull(read);
            using var buffer = new MemoryStream();
            await read!.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }
    }

    [Fact]
    public async Task Import_RemapsAllReferences_Consistently()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceStore = new CompressedContentStore(source.ContentStoreRoot, source.Provider);
        await SeedRichProjectAsync(source, sourceStore);

        using var archiveStream = new MemoryStream();
        await new ProjectArchiveService(source.Provider, sourceStore).ExportAsync(1, archiveStream);
        archiveStream.Position = 0;

        await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
        var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);

        // Occupy identifier 1 so the imported project must receive a remapped id.
        await new SqlRepository<ProjectDto>(target.Provider).CreateAsync(
            SampleFactory.Create<ProjectDto>() with { Name = "placeholder" });

        var result = await new ProjectArchiveService(target.Provider, targetStore).ImportAsync(archiveStream);

        // Imported identifiers must differ from source identifiers (fresh sequence).
        Assert.NotEqual(1, result.ProjectId);

        // No dangling references anywhere (FKs are enforced, but verify explicitly).
        foreach (var (child, column, parent) in new[]
        {
            ("audits", "project_id", "projects"),
            ("url_records", "audit_id", "audits"),
            ("findings", "audit_id", "audits"),
            ("url_elements", "url_id", "url_records"),
            ("link_edges", "source_url_id", "url_records"),
            ("content_artifacts", "url_id", "url_records"),
            ("content_artifacts", "audit_id", "audits"),
            ("schedule_runs", "schedule_definition_id", "schedule_definitions"),
            ("log_url_aggregates", "log_import_id", "log_imports"),
            ("semantic_link_opportunities", "target_url_id", "url_records")
        })
        {
            var dangling = await target.ScalarAsync(
                $"SELECT COUNT(*) FROM {child} c LEFT JOIN {parent} p ON c.{column} = p.id WHERE c.{column} IS NOT NULL AND p.id IS NULL");
            Assert.Equal(0L, dangling);
        }

        // Natural-key fidelity spot checks.
        Assert.Equal(
            await source.ScalarAsync("SELECT name FROM projects WHERE id = 1"),
            await target.ScalarAsync($"SELECT name FROM projects WHERE id = {result.ProjectId}"));
        Assert.Equal(
            await source.ScalarAsync("SELECT COUNT(DISTINCT canonical_url) FROM url_records"),
            await target.ScalarAsync("SELECT COUNT(DISTINCT canonical_url) FROM url_records"));
    }

    [Fact]
    public async Task Import_Twice_CreatesIndependentCompleteProjects()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceStore = new CompressedContentStore(source.ContentStoreRoot, source.Provider);
        await SeedRichProjectAsync(source, sourceStore);

        using var archiveStream = new MemoryStream();
        await new ProjectArchiveService(source.Provider, sourceStore).ExportAsync(1, archiveStream);

        await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
        var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);
        var importService = new ProjectArchiveService(target.Provider, targetStore);

        archiveStream.Position = 0;
        var first = await importService.ImportAsync(archiveStream);
        archiveStream.Position = 0;
        var second = await importService.ImportAsync(archiveStream);

        Assert.NotEqual(first.ProjectId, second.ProjectId);
        Assert.Equal(2, await target.CountAsync("projects"));
        Assert.Equal(2 * await source.CountAsync("url_records"), await target.CountAsync("url_records"));
        Assert.Equal(2 * await source.CountAsync("findings"), await target.CountAsync("findings"));
        // Deduplicated artifact files are shared, not duplicated.
        Assert.Equal((await sourceStore.ListStorageKeysAsync()).Count, (await targetStore.ListStorageKeysAsync()).Count);
    }

    [Fact]
    public async Task Import_CorruptArchive_RollsBackRowsAndFiles()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceStore = new CompressedContentStore(source.ContentStoreRoot, source.Provider);
        await SeedRichProjectAsync(source, sourceStore);

        using var archiveStream = new MemoryStream();
        await new ProjectArchiveService(source.Provider, sourceStore).ExportAsync(1, archiveStream);

        // Tamper with a mid-import table so the failure lands after earlier inserts.
        archiveStream.Position = 0;
        using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("data/findings.jsonl")!;
            entry.Delete();
            var replacement = zip.CreateEntry("data/findings.jsonl");
            await using var writer = new StreamWriter(replacement.Open());
            await writer.WriteLineAsync("{ this is not valid JSON ");
        }

        archiveStream.Position = 0;
        await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
        var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ProjectArchiveService(target.Provider, targetStore).ImportAsync(archiveStream));

        Assert.Equal(0, await target.CountAsync("projects"));
        Assert.Equal(0, await target.CountAsync("audits"));
        Assert.Equal(0, await target.CountAsync("url_records"));
        Assert.Equal(0, await target.CountAsync("findings"));
        Assert.Empty(await targetStore.ListStorageKeysAsync());
    }

    [Fact]
    public async Task Import_MissingManifest_Throws()
    {
        await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
        var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);

        using var archiveStream = new MemoryStream();
        using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("data/projects.jsonl");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteLineAsync("{}");
        }

        archiveStream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProjectArchiveService(target.Provider, targetStore).ImportAsync(archiveStream));
    }

    [Fact]
    public async Task Export_ExcludesGlobalConfigurationTables()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceStore = new CompressedContentStore(source.ContentStoreRoot, source.Provider);
        await SeedRichProjectAsync(source, sourceStore);
        await new SqlRepository<ApplicationSettingsDto>(source.Provider).CreateAsync(SampleFactory.Create<ApplicationSettingsDto>());
        await new SqlRepository<AutomationActionLogDto>(source.Provider).CreateAsync(SampleFactory.Create<AutomationActionLogDto>());

        using var archiveStream = new MemoryStream();
        await new ProjectArchiveService(source.Provider, sourceStore).ExportAsync(1, archiveStream);

        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var table in GlobalTables)
        {
            Assert.Null(zip.GetEntry($"data/{table}.jsonl"));
        }

        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("data/projects.jsonl"));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("artifacts/sha256/", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAllBytesAsync(CompressedContentStore store, string key)
    {
        await using var stream = await store.OpenCompressedAsync(key);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Seeds one row of every project-scoped catalog table on the base chain plus a
    /// second audit, and four real content-store artifacts. Returns the stored
    /// payloads keyed by storage key for post-import verification.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> SeedRichProjectAsync(TestDatabase database, CompressedContentStore store)
    {
        var payloads = new Dictionary<string, byte[]>();

        async Task PutAsync(long auditId, long urlId, ContentArtifactKind kind, byte[] payload)
        {
            var artifact = await store.PutAsync(auditId, urlId, kind, new MemoryStream(payload));
            payloads[artifact.StorageKey] = payload;
        }

        await PutAsync(1, 1, ContentArtifactKind.Body, Encoding.UTF8.GetBytes("<html>body one</html>"));
        await PutAsync(1, 2, ContentArtifactKind.RenderedDom, Encoding.UTF8.GetBytes("<html>rendered two</html>"));
        await PutAsync(1, 1, ContentArtifactKind.Screenshot, Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());

        // One sample of every remaining project-scoped entity (FKs bind to the base chain).
        Type[] sampleTypes =
        [
            typeof(FindingDto), typeof(MetricSummaryDto), typeof(AuditScoreDto), typeof(CrawlCheckpointDto),
            typeof(UrlEmbeddingDto), typeof(ExtractedContentDto), typeof(ContentSearchResultDto), typeof(SnippetResultDto),
            typeof(TechnologyDetectionDto), typeof(WafDetectionDto), typeof(HostThrottleEventDto), typeof(NgramFrequencyDto),
            typeof(NgramSiteSummaryDto), typeof(RedirectMappingDto), typeof(SemanticLinkOpportunityDto), typeof(SemanticClusterDto),
            typeof(GovernanceMatrixRowDto), typeof(AnalyticsDetectionDto), typeof(ScheduleRunDto), typeof(LogUrlAggregateDto),
            typeof(FindingSuppressionDto), typeof(UrlElementDto), typeof(LinkEdgeDto)
        ];
        foreach (var type in sampleTypes)
        {
            var map = EntityMap.For(type);
            await using var connection = await database.Provider.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RepositoryOps.CreateAsync(map, database.Provider.Kind, SampleFactory.Create(type), connection, transaction);
            await transaction.CommitAsync();
        }

        // Second audit with its own URL and artifact (exercises multi-audit remapping).
        var audits = new SqlRepository<AuditDto>(database.Provider);
        var secondAudit = await audits.CreateAsync(SampleFactory.Create<AuditDto>() with
        {
            StartedAt = SampleFactory.BaseTime.AddDays(2),
            FinishedAt = SampleFactory.BaseTime.AddDays(2).AddHours(2)
        });
        var urls = new SqlRepository<UrlRecordDto>(database.Provider);
        var thirdUrl = await urls.CreateAsync(SampleFactory.Create<UrlRecordDto>() with
        {
            AuditId = secondAudit.Id,
            CanonicalUrl = "https://example.test/second-audit",
            Path = "/second-audit"
        });
        await PutAsync(secondAudit.Id, thirdUrl.Id, ContentArtifactKind.Body, Encoding.UTF8.GetBytes("second audit body"));

        return payloads;
    }
}
