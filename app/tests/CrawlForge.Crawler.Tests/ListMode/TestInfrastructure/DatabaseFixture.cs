using System.Data.Common;
using System.Globalization;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Integration fixture over the real completed persistence stack: a temporary
/// file-based <see cref="SqliteDbProvider"/> migrated by the real
/// <see cref="SchemaMigrator"/> (full 40-object catalog), with helper seeding
/// of project → configuration → audit rows so foreign keys hold. Fully
/// offline; the temp directory is deleted on disposal.
/// </summary>
internal sealed class DatabaseFixture : IDisposable
{
    private static readonly string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    private DatabaseFixture(string directory, SqliteDbProvider provider)
    {
        Directory = directory;
        Provider = provider;
    }

    public string Directory { get; }

    public SqliteDbProvider Provider { get; }

    public static async Task<DatabaseFixture> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-listmode-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(directory, "crawlforge.db"),
        });

        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();
        return new DatabaseFixture(directory, provider);
    }

    /// <summary>Seeds a project, configuration, and audit; returns the audit id.</summary>
    public async Task<long> SeedAuditAsync(string name = "list-mode-test")
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        var projectId = await InsertAsync(connection, transaction,
            "INSERT INTO `projects` (`name`, `created_at`, `updated_at`, `is_deleted`) VALUES (@name, @now, @now, 0); SELECT last_insert_rowid();",
            ("@name", name), ("@now", now));

        var configurationId = await InsertAsync(connection, transaction,
            "INSERT INTO `crawl_configurations` (`project_id`, `name`, `seeds_json`, `rendering_mode`, `created_at`, `updated_at`) " +
            "VALUES (@projectId, @name, '[]', 0, @now, @now); SELECT last_insert_rowid();",
            ("@projectId", projectId), ("@name", name), ("@now", now));

        var auditId = await InsertAsync(connection, transaction,
            "INSERT INTO `audits` (`project_id`, `configuration_id`, `configuration_snapshot_json`, `status`, `phase`, `counters_json`, `engine_version`, `is_deleted`) " +
            "VALUES (@projectId, @configurationId, '{}', 0, 0, '{}', 'test', 0); SELECT last_insert_rowid();",
            ("@projectId", projectId), ("@configurationId", configurationId));

        transaction.Commit();
        return auditId;
    }

    /// <summary>Opens a plain verification connection over the same database file.</summary>
    public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        Provider.OpenConnectionAsync(cancellationToken);

    private static async Task<long> InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
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

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Provider.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test over it.
        }
    }
}
