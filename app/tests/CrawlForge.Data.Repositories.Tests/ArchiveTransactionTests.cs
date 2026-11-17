using System.Data.Common;
using System.Text;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// DEFECT D12 regression facts: the archive streaming export enlists its command
/// in the caller's ambient transaction (MySqlConnector requires the command's
/// transaction to match the connection's active transaction; SQLite's leniency
/// masked the defect). Proven here: explicit enlistment on the embedded
/// provider, the connection/transaction guards, and the LIVE MySQL → embedded
/// archive revert round trip with full body fidelity (criterion #18).
/// </summary>
public sealed class ArchiveTransactionTests
{
    private static readonly string[] GlobalTables =
    [
        "application_settings", "schema_migrations", "saved_views", "export_destinations",
        "user_dictionary", "automation_action_log", "ui_plugins", "waf_user_signatures", "user_stopwords"
    ];

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test with output capture.</summary>
    /// <param name="output">Test output sink.</param>
    public ArchiveTransactionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Streaming inside an ambient transaction reads the transaction's own
    /// uncommitted rows (enlisted command, consistent snapshot).
    /// </summary>
    [Fact]
    public async Task StreamAsync_EnlistedInAmbientTransaction_ReadsTransactionRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        var map = EntityMap.For(typeof(FindingDto));

        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var finding = SampleFactory.Create<FindingDto>();
        await RepositoryOps.CreateAsync(map, database.Provider.Kind, finding, connection, transaction);

        // Stream BEFORE commit on the same connection + transaction: the enlisted
        // command must see the transaction's row (and never throw on either provider).
        var seen = new List<object>();
        await foreach (var row in RepositoryOps.StreamAsync(
            map, database.Provider, "audit_id = @audit",
            new Dictionary<string, object?> { ["audit"] = finding.AuditId },
            connection, transaction))
        {
            seen.Add(row);
        }

        Assert.Single(seen);
        await transaction.CommitAsync();
    }

    /// <summary>A transaction from a different connection is rejected with a clear error.</summary>
    [Fact]
    public async Task StreamAsync_TransactionFromDifferentConnection_Throws()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var first = await database.Provider.OpenConnectionAsync();
        await using var second = await database.Provider.OpenConnectionAsync();
        await using var transaction = await first.BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in RepositoryOps.StreamAsync(
                EntityMap.For(typeof(ProjectDto)), database.Provider, null, null, second, transaction))
            {
                // Never reached: the guard throws before enumeration.
            }
        });
        Assert.Contains("different connection", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A transaction without its shared connection is rejected with a clear error.</summary>
    [Fact]
    public async Task StreamAsync_TransactionWithoutConnection_Throws()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in RepositoryOps.StreamAsync(
                EntityMap.For(typeof(ProjectDto)), database.Provider, null, null, null, transaction))
            {
                // Never reached: the guard throws before enumeration.
            }
        });
        Assert.Contains("shared connection", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// LIVE MySQL archive revert (the exact D12 failure path): a rich project is
    /// seeded on MySQL, ExportAsync streams every table inside the ambient
    /// transaction (previously threw "The transaction associated with this
    /// command is not the connection's active transaction"), and the bundle
    /// imports into a fresh embedded database with full row + body fidelity.
    /// </summary>
    [LiveMySqlFact]
    public async Task MySqlArchiveExport_StreamsInTransaction_RevertRoundTripPreservesBodies()
    {
        var settings = LiveMySqlSettings.Create();
        await using var serverConnection = new MySqlConnection(settings.ServerConnectionString);
        await serverConnection.OpenAsync();

        string serverVersion;
        await using (var versionCommand = serverConnection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT VERSION();";
            serverVersion = (string)(await versionCommand.ExecuteScalarAsync())!;
        }

        _output.WriteLine($"MySQL VERSION(): {serverVersion}; scratch database: {settings.Database}");

        var mysqlRoot = Path.Combine(Path.GetTempPath(), "cf-d12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mysqlRoot);
        try
        {
            await ExecuteServerAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
            await ExecuteServerAsync(serverConnection, $"CREATE DATABASE `{settings.Database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

            var mysqlProvider = new MySqlTestProvider(settings.DatabaseConnectionString);
            await TestSchema.ApplyAsync(mysqlProvider);
            var mysqlStore = new CompressedContentStore(Path.Combine(mysqlRoot, "content"), mysqlProvider);
            var payloads = await SeedRichProjectAsync(mysqlProvider, mysqlStore);

            // THE D12 PATH: streaming export from MySQL inside the ambient transaction.
            var archiveService = new ProjectArchiveService(mysqlProvider, mysqlStore);
            using var archiveStream = new MemoryStream();
            await archiveService.ExportAsync(1, archiveStream);
            archiveStream.Position = 0;
            _output.WriteLine($"MySQL streaming export succeeded: {archiveStream.Length} bytes.");

            // Explicit revert: import into a fresh embedded database.
            await using var target = await TestDatabase.CreateAsync(seedBaseChain: false);
            var targetStore = new CompressedContentStore(target.ContentStoreRoot, target.Provider);
            var result = await new ProjectArchiveService(target.Provider, targetStore).ImportAsync(archiveStream);

            Assert.True(result.ProjectId > 0);
            Assert.Equal(2, result.AuditsImported);
            Assert.Equal(3, result.UrlsImported);

            // Row fidelity: every project-scoped table matches the MySQL source.
            foreach (var dtoType in SchemaNames.AllEntities)
            {
                var table = EntityMap.For(dtoType).TableName;
                if (GlobalTables.Contains(table))
                {
                    continue;
                }

                Assert.Equal(await CountAsync(mysqlProvider, table), await target.CountAsync(table));
            }

            // Body fidelity: identical storage keys and identical compressed bytes.
            var sourceKeys = (await mysqlStore.ListStorageKeysAsync()).OrderBy(k => k).ToList();
            var targetKeys = (await targetStore.ListStorageKeysAsync()).OrderBy(k => k).ToList();
            Assert.Equal(sourceKeys, targetKeys);
            Assert.Equal(3, sourceKeys.Count);
            foreach (var key in sourceKeys)
            {
                Assert.Equal(await ReadAllBytesAsync(mysqlStore, key), await ReadAllBytesAsync(targetStore, key));
            }

            // Original payloads remain readable after the revert.
            foreach (var (key, payload) in payloads)
            {
                await using var read = await targetStore.OpenReadAsync(key);
                Assert.NotNull(read);
                using var buffer = new MemoryStream();
                await read!.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            _output.WriteLine($"D12 LIVE PROOF PASS — MySQL {serverVersion}: streaming export inside the ambient transaction, revert round trip with full row + body fidelity.");
        }
        finally
        {
            await ExecuteServerAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
            try
            {
                Directory.Delete(mysqlRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort teardown.
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> SeedRichProjectAsync(IDbProvider provider, CompressedContentStore store)
    {
        // Base reference chain (same shape as the embedded harness).
        await CreateAsync(provider, SampleFactory.Create<ProjectDto>());
        await CreateAsync(provider, SampleFactory.Create<ContentScopeProfileDto>());
        await CreateAsync(provider, SampleFactory.Create<CrawlConfigurationDto>());
        await CreateAsync(provider, SampleFactory.Create<AuditDto>());
        var firstUrl = SampleFactory.Create<UrlRecordDto>() with
        {
            CanonicalUrl = "https://example.test/seed-1",
            Path = "/seed-1"
        };
        await CreateAsync(provider, firstUrl);
        await CreateAsync(provider, firstUrl with
        {
            CanonicalUrl = "https://example.test/seed-2",
            Path = "/seed-2"
        });
        await CreateAsync(provider, SampleFactory.Create<ScheduleDefinitionDto>());
        await CreateAsync(provider, SampleFactory.Create<LogImportDto>());

        // Project-scoped detail rows.
        await CreateAsync(provider, SampleFactory.Create<FindingDto>());
        await CreateAsync(provider, SampleFactory.Create<MetricSummaryDto>());
        await CreateAsync(provider, SampleFactory.Create<UrlElementDto>());

        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        async Task PutAsync(long auditId, long urlId, ContentArtifactKind kind, byte[] payload)
        {
            // The content store writes the content_artifacts index row itself.
            var put = await store.PutAsync(auditId, urlId, kind, new MemoryStream(payload));
            payloads[put.StorageKey] = payload;
        }

        await PutAsync(1, 1, ContentArtifactKind.Body, Encoding.UTF8.GetBytes("<html><body>mysql export body</body></html>"));
        await PutAsync(1, 2, ContentArtifactKind.RenderedDom, Encoding.UTF8.GetBytes("<html><body>rendered</body></html>"));

        // Second audit with its own URL and artifact (multi-audit remapping).
        var secondAudit = await CreateAsync(provider, SampleFactory.Create<AuditDto>() with
        {
            StartedAt = SampleFactory.BaseTime.AddDays(2),
            FinishedAt = SampleFactory.BaseTime.AddDays(2).AddHours(2)
        });
        var thirdUrl = await CreateAsync(provider, SampleFactory.Create<UrlRecordDto>() with
        {
            AuditId = secondAudit.Id,
            CanonicalUrl = "https://example.test/second-audit",
            Path = "/second-audit"
        });
        await PutAsync(secondAudit.Id, thirdUrl.Id, ContentArtifactKind.Body, Encoding.UTF8.GetBytes("second audit body"));

        return payloads;
    }

    private static async Task<TDto> CreateAsync<TDto>(IDbProvider provider, TDto entity)
        where TDto : class
    {
        var repository = new SqlRepository<TDto>(provider);
        return await repository.CreateAsync(entity);
    }

    private static async Task<long> CountAsync(IDbProvider provider, string table)
    {
        await using var connection = await provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteServerAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadAllBytesAsync(CompressedContentStore store, string storageKey)
    {
        await using var stream = await store.OpenReadAsync(storageKey);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
