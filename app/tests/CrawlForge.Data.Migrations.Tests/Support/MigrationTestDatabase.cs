using CrawlForge.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests.Support;

/// <summary>
/// Per-test fixture: a fresh file-based SQLite database in a temporary
/// directory, the test-local provider double, and a migrator bound to the full
/// catalog. Implements <see cref="IAsyncLifetime"/> so every test receives an
/// isolated database (xunit constructs a new class instance per test).
/// </summary>
public sealed class MigrationTestDatabase : IAsyncLifetime
{
    private string _directory = string.Empty;

    /// <summary>The test-local provider double.</summary>
    public SqliteTestProvider Provider { get; private set; } = null!;

    /// <summary>Migrator bound to the complete migration catalog.</summary>
    public SchemaMigrator Migrator { get; private set; } = null!;

    /// <summary>Absolute path of the per-test database file.</summary>
    public string DatabasePath => Path.Combine(_directory, "crawlforge-migrations-tests.db");

    /// <summary>Creates the temporary directory and binds the provider and migrator.</summary>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-01-03-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteTestProvider(DatabasePath);
        Migrator = new SchemaMigrator(Provider);
        return Task.CompletedTask;
    }

    /// <summary>Disposes the provider and removes the temporary directory.</summary>
    /// <returns>A completed task.</returns>
    public Task DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort teardown.
        }

        return Task.CompletedTask;
    }

    /// <summary>Applies all pending migrations and first-run seeding.</summary>
    /// <returns>A task that completes when the schema is up to date.</returns>
    public Task MigrateAsync() => Migrator.MigrateAsync();

    /// <summary>Opens a raw connection with foreign-key enforcement enabled.</summary>
    /// <returns>An open connection owned by the caller.</returns>
    public async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(Provider.ConnectionString);
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();
        return connection;
    }

    /// <summary>Counts rows of a catalog table.</summary>
    /// <param name="table">Catalog table name.</param>
    /// <returns>The row count.</returns>
    public async Task<long> CountRowsAsync(string table)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    /// <summary>Executes a scalar query and returns the raw value.</summary>
    /// <param name="sql">The query text.</param>
    /// <returns>The scalar result (DBNull when NULL).</returns>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Executes a non-query statement.</summary>
    /// <param name="sql">The statement text.</param>
    /// <returns>A task that completes when the statement has run.</returns>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
