using System.Data.Common;
using System.Runtime.CompilerServices;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Streaming read side of a CQRS repository. Rows are yielded one at a time from a
/// forward-only reader so ingestion, aggregation, and archiving stay bounded-memory
/// at 500k+ URL scale (precedence rule 7) — never a full materialization.
/// </summary>
/// <typeparam name="TDto">Entity DTO type served by this repository.</typeparam>
public interface IStreamingReadRepository<TDto> : IReadRepository<TDto>
    where TDto : class
{
    /// <summary>Streams all entities matching a query, one row at a time.</summary>
    /// <param name="query">Filter and sort specification (pagination is ignored).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of matching entities.</returns>
    IAsyncEnumerable<TDto> StreamAsync(ListQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider-agnostic CQRS repository over <see cref="IDbProvider"/>. One generic
/// implementation serves every catalog entity through its <see cref="EntityMap{TDto}"/>:
/// parameterized SQL only, whitelisted filter/sort columns, transactional writes,
/// soft delete where the pattern applies, and forward-only streaming reads.
/// </summary>
/// <typeparam name="TDto">Entity DTO type served by this repository.</typeparam>
public sealed class SqlRepository<TDto> : IStreamingReadRepository<TDto>, IWriteRepository<TDto>
    where TDto : class
{
    private readonly IDbProvider _provider;

    /// <summary>Creates a repository for the mapped entity.</summary>
    /// <param name="provider">The configured database provider.</param>
    public SqlRepository(IDbProvider provider)
    {
        _provider = provider;
        Map = EntityMap<TDto>.Instance.Map;
    }

    /// <summary>The entity map driving this repository.</summary>
    public EntityMap Map { get; }

    /// <inheritdoc />
    public async Task<TDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        var where = $" WHERE {Map.KeyColumn.ColumnName} = @id" + SoftDeleteClause();
        await using var command = BuildSelect(connection, where, null, orderByKey: true);
        command.Parameters.Add(Parameter(command, "@id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? EntityMap<TDto>.Instance.Materialize(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PagedResult<TDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        var (where, parameters) = BuildFilter(query);
        var limit = Math.Clamp(query.Limit, 1, 10_000);
        var offset = Math.Max(0, query.Offset);

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        long totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM {Map.TableName}{where}";
            AddParameters(countCommand, connection, parameters);
            totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<TDto>(limit);
        var orderBy = ResolveOrderBy(query);
        await using (var command = BuildSelect(connection, where, orderBy, orderByKey: false))
        {
            AddParameters(command, connection, parameters);
            command.Parameters.Add(Parameter(command, "@__limit", limit));
            command.Parameters.Add(Parameter(command, "@__offset", offset));
            command.CommandText += " LIMIT @__limit OFFSET @__offset";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(EntityMap<TDto>.Instance.Materialize(reader));
            }
        }

        return new PagedResult<TDto>(items, totalCount, offset, limit);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TDto> StreamAsync(ListQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (where, parameters) = BuildFilter(query);
        var orderBy = ResolveOrderBy(query);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = BuildSelect(connection, where, orderBy, orderByKey: false);
        AddParameters(command, connection, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return EntityMap<TDto>.Instance.Materialize(reader);
        }
    }

    /// <summary>
    /// Streams rows matching a trusted internal predicate (used by aggregation and
    /// archiving). The clause is composed exclusively from catalog constants; values
    /// are always parameterized.
    /// </summary>
    /// <param name="whereClause">WHERE clause without the WHERE keyword, or null for all rows.</param>
    /// <param name="parameters">Parameter values keyed by name (without the @ prefix).</param>
    /// <param name="connection">Optional shared connection (not disposed when supplied).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of matching entities.</returns>
    public async IAsyncEnumerable<TDto> StreamWhereAsync(
        string? whereClause,
        IReadOnlyDictionary<string, object?>? parameters = null,
        DbConnection? connection = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var owned = connection is null;
        var conn = connection ?? await _provider.OpenConnectionAsync(cancellationToken);
        try
        {
            var where = string.IsNullOrEmpty(whereClause) ? string.Empty : $" WHERE {whereClause}";
            await using var command = conn.CreateCommand();
            command.CommandText = $"SELECT {ColumnList()} FROM {Map.TableName}{where} ORDER BY {Map.KeyColumn.ColumnName}";
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.Add(Parameter(command, "@" + name, value ?? DBNull.Value));
                }
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                yield return EntityMap<TDto>.Instance.Materialize(reader);
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

    /// <inheritdoc />
    public async Task<TDto> CreateAsync(TDto entity, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var created = await CreateAsync(entity, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Creates an entity inside a caller-owned transaction (used by archive import so
    /// the whole import commits or rolls back atomically).
    /// </summary>
    /// <param name="entity">Entity payload.</param>
    /// <param name="connection">The shared open connection.</param>
    /// <param name="transaction">The shared transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entity with its new identifier.</returns>
    public async Task<TDto> CreateAsync(TDto entity, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        var columns = Map.ValueColumns;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Map.TableName} ({string.Join(", ", columns.Select(c => c.ColumnName))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => "@v" + i))})";
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(Parameter(command, "@v" + i, EntityMap.ToStorageValue(columns[i], columns[i].GetValue(entity))));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = LastInsertIdSql();
        var newId = Convert.ToInt64(await idCommand.ExecuteScalarAsync(cancellationToken));
        return WithId(entity, newId);
    }

    /// <inheritdoc />
    public async Task<TDto> UpdateAsync(TDto entity, CancellationToken cancellationToken = default)
    {
        var columns = Map.ValueColumns;
        var id = Convert.ToInt64(Map.KeyColumn.GetValue(entity));

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {Map.TableName} SET {string.Join(", ", columns.Select((c, i) => $"{c.ColumnName} = @v{i}"))} " +
            $"WHERE {Map.KeyColumn.ColumnName} = @id";
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(Parameter(command, "@v" + i, EntityMap.ToStorageValue(columns[i], columns[i].GetValue(entity))));
        }

        command.Parameters.Add(Parameter(command, "@id", id));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return affected == 0
            ? throw new KeyNotFoundException($"{Map.TableName} row {id} does not exist.")
            : entity;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (Map.SoftDeleteColumn is not null)
        {
            command.CommandText =
                $"UPDATE {Map.TableName} SET {Map.SoftDeleteColumn.ColumnName} = 1 " +
                $"WHERE {Map.KeyColumn.ColumnName} = @id AND {Map.SoftDeleteColumn.ColumnName} = 0";
        }
        else
        {
            command.CommandText = $"DELETE FROM {Map.TableName} WHERE {Map.KeyColumn.ColumnName} = @id";
        }

        command.Parameters.Add(Parameter(command, "@id", id));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>Counts rows matching a trusted internal predicate.</summary>
    /// <param name="whereClause">WHERE clause without the WHERE keyword, or null for all rows.</param>
    /// <param name="parameters">Parameter values keyed by name (without the @ prefix).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching row count.</returns>
    public async Task<long> CountWhereAsync(string? whereClause, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Map.TableName}" + (string.IsNullOrEmpty(whereClause) ? string.Empty : $" WHERE {whereClause}");
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(Parameter(command, "@" + name, value ?? DBNull.Value));
            }
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private string LastInsertIdSql() => _provider.Kind switch
    {
        Contracts.Enums.DatabaseProviderKind.Sqlite => "SELECT last_insert_rowid()",
        Contracts.Enums.DatabaseProviderKind.MySql => "SELECT LAST_INSERT_ID()",
        _ => throw new InvalidOperationException($"Unsupported provider kind {_provider.Kind}.")
    };

    private string SoftDeleteClause() =>
        Map.SoftDeleteColumn is null ? string.Empty : $" AND {Map.SoftDeleteColumn.ColumnName} = 0";

    private string ColumnList() => string.Join(", ", Map.Columns.Select(c => c.ColumnName));

    private DbCommand BuildSelect(DbConnection connection, string? where, string? orderBy, bool orderByKey)
    {
        var command = connection.CreateCommand();
        var order = orderBy ?? (orderByKey ? Map.KeyColumn.ColumnName : null);
        command.CommandText =
            $"SELECT {ColumnList()} FROM {Map.TableName}{where}" +
            (order is null ? string.Empty : $" ORDER BY {order}");
        return command;
    }

    private (string Where, Dictionary<string, object?> Parameters) BuildFilter(ListQuery query)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (Map.SoftDeleteColumn is not null)
        {
            clauses.Add($"{Map.SoftDeleteColumn.ColumnName} = 0");
        }

        if (query.Filters is not null)
        {
            var index = 0;
            foreach (var (field, value) in query.Filters)
            {
                var column = Map.Columns.FirstOrDefault(c => string.Equals(c.PropertyName, field, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown filter field '{field}' for {Map.TableName}.");
                var name = "@f" + index++;
                clauses.Add($"{column.ColumnName} = {name}");
                parameters[name[1..]] = EntityMap.FilterValueToStorage(column, value);
            }
        }

        return (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses), parameters);
    }

    private string ResolveOrderBy(ListQuery query)
    {
        if (string.IsNullOrEmpty(query.SortBy))
        {
            return Map.KeyColumn.ColumnName;
        }

        var column = Map.Columns.FirstOrDefault(c => string.Equals(c.PropertyName, query.SortBy, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown sort field '{query.SortBy}' for {Map.TableName}.");
        return column.ColumnName + (query.SortDescending ? " DESC" : " ASC");
    }

    private static void AddParameters(DbCommand command, DbConnection connection, Dictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(Parameter(command, "@" + name, value ?? DBNull.Value));
        }
    }

    private static DbParameter Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    private static TDto WithId(TDto entity, long id)
    {
        var map = EntityMap<TDto>.Instance.Map;
        map.KeyColumn.SetValue(entity, id);
        return entity;
    }
}
