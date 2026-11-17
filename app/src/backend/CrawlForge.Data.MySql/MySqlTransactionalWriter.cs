using System.Data;
using System.Data.Common;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Transactional write helper for the external provider (FS-CRAWL-001 §2.4: all
/// multi-row writes occur inside transactions on either provider; the external
/// provider uses its native InnoDB transactional durability). Opens the connection
/// when needed, runs the work inside one transaction, commits on success, and
/// rolls back on any failure.
/// </summary>
public static class MySqlTransactionalWriter
{
    /// <summary>Runs work inside a single transaction on the given connection.</summary>
    /// <typeparam name="T">Work result type.</typeparam>
    /// <param name="connection">Connection to the user-configured server.</param>
    /// <param name="work">Write work receiving the connection and its transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work result after a successful commit.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<T> RunAsync<T>(
        DbConnection connection,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(work);

        bool openedHere = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            openedHere = true;
        }

        try
        {
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    T result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
