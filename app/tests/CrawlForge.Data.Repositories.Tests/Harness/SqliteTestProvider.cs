using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Repositories.Tests.Harness;

/// <summary>
/// Test-local <see cref="IDbProvider"/> double backed by Microsoft.Data.Sqlite.
/// WP-01-04 owns no concrete provider; this harness exercises the provider-agnostic
/// repositories against a real embedded engine with foreign keys and WAL enabled.
/// </summary>
public sealed class SqliteTestProvider : IDbProvider
{
    private readonly string _connectionString;

    /// <summary>Creates the provider for a file-backed database.</summary>
    /// <param name="databasePath">Local database file path.</param>
    public SqliteTestProvider(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    /// <inheritdoc />
    public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;

    /// <inheritdoc />
    public string Dialect => "sqlite";

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc />
    public async Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return new DbConnectionTestResult(true, true, true, null);
    }
}
