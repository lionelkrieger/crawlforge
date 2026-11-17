using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using MySqlConnector;

namespace CrawlForge.Data.Migrations.Tests.Support;

/// <summary>
/// Test-local <see cref="IDbProvider"/> double backed directly by MySqlConnector
/// (same pattern as <see cref="SqliteTestProvider"/>: the migrator and dialect
/// under test are the real product code; the double only supplies connections
/// to the scratch database on the user-configured test server).
/// </summary>
public sealed class MySqlTestProvider : IDbProvider
{
    /// <summary>Creates a provider double for a server-scoped or database-scoped connection.</summary>
    /// <param name="connectionString">MySqlConnector connection string.</param>
    public MySqlTestProvider(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>Connection string of the scratch database (pooling disabled for clean teardown).</summary>
    public string ConnectionString { get; }

    /// <inheritdoc />
    public DatabaseProviderKind Kind => DatabaseProviderKind.MySql;

    /// <inheritdoc />
    public string Dialect => "mysql";

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
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
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
        {
            return new DbConnectionTestResult(false, false, false, ex.Message);
        }
    }
}
