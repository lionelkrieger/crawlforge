using System.Data.Common;
using System.Runtime.CompilerServices;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Non-generic repository operations over the entity catalog. Used by the project
/// archive service, which iterates every catalog table without compile-time knowledge
/// of each DTO type. All SQL is parameterized and composed from catalog constants.
/// </summary>
public static class RepositoryOps
{
    /// <summary>
    /// Inserts one entity inside a caller-owned transaction and returns it with the
    /// new surrogate key assigned.
    /// </summary>
    /// <param name="map">The entity map.</param>
    /// <param name="providerKind">Provider kind (selects the last-insert-id dialect).</param>
    /// <param name="entity">The entity payload.</param>
    /// <param name="connection">The shared open connection.</param>
    /// <param name="transaction">The shared transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entity with its identifier set.</returns>
    public static async Task<object> CreateAsync(
        EntityMap map, DatabaseProviderKind providerKind, object entity,
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        var columns = map.ValueColumns;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {map.TableName} ({string.Join(", ", columns.Select(c => c.ColumnName))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => "@v" + i))})";
        for (var i = 0; i < columns.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@v" + i;
            parameter.Value = EntityMap.ToStorageValue(columns[i], columns[i].GetValue(entity));
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = providerKind switch
        {
            DatabaseProviderKind.Sqlite => "SELECT last_insert_rowid()",
            DatabaseProviderKind.MySql => "SELECT LAST_INSERT_ID()",
            _ => throw new InvalidOperationException($"Unsupported provider kind {providerKind}.")
        };
        var newId = Convert.ToInt64(await idCommand.ExecuteScalarAsync(cancellationToken));
        map.KeyColumn.SetValue(entity, newId);
        return entity;
    }

    /// <summary>
    /// Streams rows matching a trusted internal predicate, one at a time
    /// (bounded memory; precedence rule 7).
    /// </summary>
    /// <remarks>
    /// DEFECT D12: when the caller supplies a shared connection with an ambient
    /// transaction, the streaming command MUST be enlisted in that transaction.
    /// MySqlConnector rejects any command whose transaction does not match the
    /// connection's active transaction; the embedded provider is lenient, which
    /// masked the defect on SQLite. Explicit enlistment keeps the streaming read
    /// inside the caller's consistent snapshot on every provider.
    /// </remarks>
    /// <param name="map">The entity map.</param>
    /// <param name="provider">The database provider (used when no shared connection is supplied).</param>
    /// <param name="whereClause">WHERE clause without the WHERE keyword, or null for all rows.</param>
    /// <param name="parameters">Parameter values keyed by name (without the @ prefix).</param>
    /// <param name="connection">Optional shared connection (not disposed when supplied).</param>
    /// <param name="transaction">
    /// Optional ambient transaction on the shared connection; the streaming command
    /// is enlisted in it (required by MySqlConnector when a transaction is active).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of materialized entities.</returns>
    public static async IAsyncEnumerable<object> StreamAsync(
        EntityMap map, IDbProvider provider, string? whereClause,
        IReadOnlyDictionary<string, object?>? parameters = null,
        DbConnection? connection = null,
        DbTransaction? transaction = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialize = map.MaterializeObject
            ?? throw new InvalidOperationException($"No materializer registered for {map.TableName}.");
        if (transaction is not null && connection is null)
        {
            throw new ArgumentException(
                "A transaction requires the shared connection it belongs to.", nameof(transaction));
        }

        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The supplied transaction belongs to a different connection.", nameof(transaction));
        }

        var owned = connection is null;
        var conn = connection ?? await provider.OpenConnectionAsync(cancellationToken);
        try
        {
            var where = string.IsNullOrEmpty(whereClause) ? string.Empty : $" WHERE {whereClause}";
            await using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT {string.Join(", ", map.Columns.Select(c => c.ColumnName))} FROM {map.TableName}{where} " +
                $"ORDER BY {map.KeyColumn.ColumnName}";
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@" + name;
                    parameter.Value = value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                yield return materialize(reader);
            }
        }
        finally
        {
            if (owned)
            {
                await conn.DisposeAsync();
            }
        }
    }
}
