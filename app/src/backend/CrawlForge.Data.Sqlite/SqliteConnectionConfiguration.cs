using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite;

/// <summary>
/// Applies the provider's durability and capability profile to an opened connection:
/// busy timeout and foreign keys on every connection; WAL journal mode and the
/// synchronous level on writable connections; <c>query_only</c> on read connections;
/// then loads the bundled extension families (FS-CRAWL-001 §2.4).
/// </summary>
internal static class SqliteConnectionConfiguration
{
    /// <summary>Configures one opened connection.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="options">The validated provider options.</param>
    /// <param name="writable">Whether the connection may write (WAL/synchronous) or is read-only.</param>
    /// <param name="loader">The bundled extension loader.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the connection is configured.</returns>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PRAGMA statements embed only validated numeric settings (busy timeout, synchronous level), never user data.")]
    public static async Task ConfigureAsync(
        SqliteConnection connection,
        SqliteProviderOptions options,
        bool writable,
        Extensions.SqliteExtensionLoader loader,
        CancellationToken cancellationToken)
    {
        var busyTimeoutMs = (long)options.BusyTimeout.TotalMilliseconds;
        await ExecuteAsync(connection, "PRAGMA busy_timeout = " + busyTimeoutMs.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);

        if (writable)
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = " + ((int)options.Synchronous).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, "PRAGMA query_only = ON", cancellationToken).ConfigureAwait(false);
        }

        loader.Load(connection);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
