using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Crawler.Tests.Frontier.Support;

/// <summary>
/// Real persistence stack for frontier integration tests: an embedded SQLite
/// provider on a temporary local file, migrated through the production
/// <see cref="SchemaMigrator"/>. No mocks of the database layer.
/// </summary>
public sealed class FrontierTestDatabase : IAsyncDisposable
{
    private FrontierTestDatabase(SqliteDbProvider provider, string directory)
    {
        Provider = provider;
        DirectoryPath = directory;
    }

    /// <summary>The migrated SQLite provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>Temporary local directory holding the database and seen-set files.</summary>
    public string DirectoryPath { get; }

    /// <summary>Creates a fresh migrated database in a unique temporary directory.</summary>
    /// <returns>The initialized test database.</returns>
    public static async Task<FrontierTestDatabase> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-frontier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(directory, "frontier.db")
        });
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();
        return new FrontierTestDatabase(provider, directory);
    }

    /// <summary>Seeds a url_records row and returns its identifier.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <returns>The new url_records identifier.</returns>
    public async Task<long> InsertUrlAsync(long auditId, string canonicalUrl)
    {
        var uri = new Uri(canonicalUrl);
        var repository = new SqlRepository<UrlRecordDto>(Provider);
        var record = new UrlRecordDto(
            0, auditId, canonicalUrl, uri.Host, uri.AbsolutePath, 0,
            DiscoverySource.Seed, CrawlStatusClass.NotCrawled,
            null, null, null, null, null,
            RobotsState.Unknown, IndexabilityFlags.None, false);
        return (await repository.CreateAsync(record)).Id;
    }

    /// <summary>
    /// Seeds the foreign-key parent chain (project → configuration → audit) so an
    /// audit with the given explicit identifier exists. The real migrated schema
    /// enforces FKs, so checkpoint/url/link rows need a live parent audit.
    /// Idempotent: safe to call repeatedly for the same or different audit ids.
    /// </summary>
    /// <param name="auditId">The audit identifier to make available.</param>
    /// <returns>A task that completes when the parent chain is seeded.</returns>
    public async Task SeedAuditAsync(long auditId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO projects (id, name, created_at, updated_at, is_deleted) " +
            "VALUES (1, 'frontier-test', @now, @now, 0)", ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) " +
            "VALUES (1, 1, 'frontier-test', '[]', 0, @now, @now)", ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) " +
            "VALUES (@id, 1, 1, '{}', 1, 1, '{}', 'test', 0)", ("@id", auditId), ("@now", now));

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temporary directory.
        }

        return ValueTask.CompletedTask;
    }
}
