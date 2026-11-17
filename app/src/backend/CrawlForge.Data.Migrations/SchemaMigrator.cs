using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Migrations.Dialects;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Seeding;

namespace CrawlForge.Data.Migrations;

/// <summary>
/// Versioned, provider-agnostic schema migrator (FS-CRAWL-001 §2.4, §9.4).
/// Applies the logical migration catalog to the configured provider through
/// its dialect, records every applied version in the per-provider
/// <c>schema_migrations</c> ledger, and seeds deterministic first-run defaults
/// with zero network calls. Re-running <see cref="MigrateAsync"/> is a no-op:
/// applied versions are skipped and seeding is guarded (criterion #9).
/// </summary>
/// <remarks>
/// <para>
/// Each migration executes inside a transaction together with its ledger
/// insert on providers with transactional DDL (the embedded provider). On the
/// external provider, DDL statements implicit-commit by engine design; the
/// ledger insert still fences re-application, and every migration is a single
/// ordered unit so a failure is diagnosable from the ledger state.
/// </para>
/// <para>
/// All schema work happens on connections supplied by the configured
/// <see cref="IDbProvider"/> — the local embedded database or the user's own
/// external server. No other endpoint is ever contacted.
/// </para>
/// </remarks>
public sealed class SchemaMigrator : ISchemaMigrator
{
    private readonly IDbProvider _provider;
    private readonly IReadOnlyList<MigrationDefinition> _migrations;
    private readonly SqlDialect _dialect;
    private readonly SqlRenderer _renderer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates a migrator for the configured provider using the complete
    /// 40-object migration catalog.
    /// </summary>
    /// <param name="provider">The configured database provider.</param>
    public SchemaMigrator(IDbProvider provider)
        : this(provider, SchemaCatalog.Migrations)
    {
    }

    /// <summary>
    /// Creates a migrator with an explicit migration sequence (used to prove
    /// incremental application and re-run safety).
    /// </summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="migrations">Ordered migrations to apply.</param>
    public SchemaMigrator(IDbProvider provider, IReadOnlyList<MigrationDefinition> migrations)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Select(m => m.Version).Distinct().Count() != migrations.Count)
        {
            throw new ArgumentException("Migration versions must be unique.", nameof(migrations));
        }

        _provider = provider;
        _migrations = [.. migrations.OrderBy(m => m.Version)];
        _dialect = provider.Kind switch
        {
            DatabaseProviderKind.Sqlite => new SqliteDialect(),
            DatabaseProviderKind.MySql => new MySqlDialect(),
            _ => throw new ArgumentException($"Unsupported provider kind '{provider.Kind}'.", nameof(provider)),
        };
        _renderer = new SqlRenderer(_dialect);
    }

    /// <inheritdoc />
    public async Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await _dialect.TableExistsAsync(connection, SchemaCatalog.LedgerTableName, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"SELECT COALESCE(MAX(version), 0) FROM {_dialect.Quote(SchemaCatalog.LedgerTableName)} WHERE provider_dialect = @dialect;");
        AddParameter(command, "@dialect", _provider.Dialect);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var statement in _dialect.ConnectionInitializationStatements)
            {
                await ExecuteAsync(connection, transaction: null, statement, parameters: null, cancellationToken).ConfigureAwait(false);
            }

            // Bootstrap the per-provider ledger before any version applies.
            await ExecuteAsync(connection, transaction: null, _renderer.RenderLedgerBootstrap(), parameters: null, cancellationToken).ConfigureAwait(false);

            var applied = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var migration in _migrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                await using var transaction = connection.BeginTransaction();
                foreach (var statement in _renderer.RenderMigration(migration))
                {
                    await ExecuteAsync(connection, transaction, statement, parameters: null, cancellationToken).ConfigureAwait(false);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    string.Create(CultureInfo.InvariantCulture,
                        $"INSERT INTO {_dialect.Quote(SchemaCatalog.LedgerTableName)} (version, provider_dialect, applied_at) VALUES (@version, @dialect, @applied);"),
                    [
                        ("@version", migration.Version),
                        ("@dialect", _provider.Dialect),
                        ("@applied", _dialect.WriteTimestamp(DateTimeOffset.UtcNow)),
                    ],
                    cancellationToken).ConfigureAwait(false);
                transaction.Commit();
            }

            await SeedFirstRunAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchemaMigrationDto>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await _dialect.TableExistsAsync(connection, SchemaCatalog.LedgerTableName, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"SELECT id, version, provider_dialect, applied_at FROM {_dialect.Quote(SchemaCatalog.LedgerTableName)} WHERE provider_dialect = @dialect ORDER BY version;");
        AddParameter(command, "@dialect", _provider.Dialect);

        var rows = new List<SchemaMigrationDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SchemaMigrationDto(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                _dialect.ReadTimestamp(reader.GetValue(3))));
        }

        return rows;
    }

    private async Task<HashSet<int>> ReadAppliedVersionsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"SELECT version FROM {_dialect.Quote(SchemaCatalog.LedgerTableName)} WHERE provider_dialect = @dialect;");
        AddParameter(command, "@dialect", _provider.Dialect);

        var versions = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private async Task SeedFirstRunAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // Seed only columns that exist on the settings table: incremental
        // migration sequences may not have applied the additive preference
        // columns yet. The full catalog always receives every documented default.
        var existingColumns = await _dialect
            .GetColumnNamesAsync(connection, "application_settings", cancellationToken)
            .ConfigureAwait(false);
        var existing = new HashSet<string>(existingColumns, StringComparer.Ordinal);
        var pairs = FirstRunSeed.Columns
            .Zip(FirstRunSeed.Values, (column, value) => (Column: column, Value: value))
            .Where(pair => existing.Contains(pair.Column))
            .ToList();

        await using var transaction = connection.BeginTransaction();
        var parameters = pairs
            .Select((pair, index) => (Name: string.Create(CultureInfo.InvariantCulture, $"@p{index}"), pair.Value))
            .ToList();
        var statement = FirstRunSeed.RenderSeedStatement(_dialect, pairs.Select(p => p.Column).ToList(), pairs.Count);
        await ExecuteAsync(connection, transaction, statement, parameters, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)>? parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                AddParameter(command, name, value);
            }
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
