using System.Data.Common;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// WAL checkpoint hook for the manual compact action (FS-CRAWL-002 §7.2).
/// The embedded provider implements it with <c>PRAGMA wal_checkpoint(TRUNCATE)</c>;
/// external providers supply no checkpointer (the action is simply absent).
/// </summary>
public interface IWalCheckpointer
{
    /// <summary>
    /// Attempts a truncating WAL checkpoint without blocking in-flight
    /// writers: a busy database reports failure instead of waiting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the WAL was checkpointed and truncated.</returns>
    Task<bool> TryCheckpointTruncateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite truncating checkpoint over the accepted provider. Uses a short
/// command timeout so an in-flight audit transaction is never interrupted:
/// a busy checkpoint returns false and the compact action list omits it.
/// </summary>
public sealed class SqliteWalCheckpointer : IWalCheckpointer
{
    private readonly SqliteDbProvider _provider;

    /// <summary>Creates the checkpointer.</summary>
    /// <param name="provider">Embedded SQLite provider (accepted phase-01 surface).</param>
    public SqliteWalCheckpointer(SqliteDbProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task<bool> TryCheckpointTruncateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.CommandTimeout = 2;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Row: (busy, log, checkpointed). busy=0 means success.
                return reader.GetInt32(0) == 0;
            }

            return false;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            return false; // Busy or unavailable: never interrupt the audit.
        }
    }
}
