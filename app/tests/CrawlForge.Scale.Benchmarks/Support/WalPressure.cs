using System.Data.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>
/// Keeps a real WAL present for telemetry measurement: writes probe rows
/// through the product repository inside one shared transaction, commits, and
/// holds the connection open. SQLite removes the WAL file when the last
/// connection closes, so an idle database legitimately reports a null WAL
/// size; this harness makes the WAL observable without faking anything.
/// </summary>
public sealed class WalPressure : IAsyncDisposable
{
    private readonly DbConnection _connection;

    private WalPressure(DbConnection connection) => _connection = connection;

    /// <summary>The open connection keeping the WAL alive.</summary>
    public DbConnection Connection => _connection;

    /// <summary>Creates committed WAL pressure through the real persistence stack.</summary>
    /// <param name="services">Product service provider.</param>
    /// <param name="rows">Number of probe rows to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Held-open pressure handle.</returns>
    public static async Task<WalPressure> CreateAsync(IServiceProvider services, int rows, CancellationToken cancellationToken)
    {
        var provider = services.GetRequiredService<IDbProvider>();
        var connection = await provider.OpenConnectionAsync(cancellationToken);
        var repository = new SqlRepository<MetricSummaryDto>(provider);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            for (var i = 0; i < rows; i++)
            {
                await repository.CreateAsync(
                    new MetricSummaryDto(0, 1, "wal-pressure-probe", $$"""{"ordinal":{{i}}}""", i),
                    connection, transaction, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
        return new WalPressure(connection);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
