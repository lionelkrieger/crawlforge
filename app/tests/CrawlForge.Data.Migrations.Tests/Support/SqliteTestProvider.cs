using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Migrations.Tests.Support;

/// <summary>
/// Test-local <see cref="IDbProvider"/> double backed directly by
/// Microsoft.Data.Sqlite (dispatch packet: sibling provider projects are
/// in-progress and must not be referenced). Serves a file-based database in a
/// per-test temporary directory with foreign-key enforcement enabled, matching
/// the production embedded-provider behavior the migrator relies on.
/// </summary>
public sealed class SqliteTestProvider : IDbProvider, IDisposable
{
    private readonly string _databasePath;

    /// <summary>Creates a provider double for a file-based test database.</summary>
    /// <param name="databasePath">Absolute path of the SQLite database file.</param>
    public SqliteTestProvider(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <inheritdoc />
    public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;

    /// <inheritdoc />
    public string Dialect => "sqlite";

    /// <summary>Connection string of the test database (pooling disabled for clean teardown).</summary>
    public string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc />
    public async Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return new DbConnectionTestResult(true, true, true, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            return new DbConnectionTestResult(false, false, false, ex.Message);
        }
    }

    /// <summary>Deletes the test database file (best effort).</summary>
    public void Dispose()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // Teardown is best effort; the temp directory is per-test.
        }
    }
}
