using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Sqlite.Extensions;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite;

/// <summary>
/// Embedded file-based SQLite provider — the default persistence provider
/// (FS-CRAWL-001 §2.4): zero configuration, zero network, fully offline, WAL
/// durability, enforced foreign keys, and the four required capability families
/// bundled locally. Implements the locked <see cref="IDbProvider"/> contract;
/// the dialect identifier is recorded in the provider-agnostic migration ledger.
/// </summary>
public sealed class SqliteDbProvider : IDbProvider, IDisposable
{
    private readonly SqliteProviderOptions _options;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteExtensionLoader _loader;
    private readonly Lazy<SqliteCapabilityReport> _capabilities;
    private int _disposed;

    /// <summary>Creates the provider for the configured database file.</summary>
    /// <param name="options">Provider options; <see langword="null"/> uses the baseline defaults.</param>
    public SqliteDbProvider(SqliteProviderOptions? options = null)
    {
        _options = options ?? new SqliteProviderOptions();
        _options.Validate();
        _connectionFactory = new SqliteConnectionFactory(_options);
        _loader = new SqliteExtensionLoader(_options.NativeExtensionDirectory);
        _capabilities = new Lazy<SqliteCapabilityReport>(() => SqliteCapabilityProbe.Probe(_loader));
        WalMonitor = new SqliteWalMonitor(_options.ResolveDatabasePath());
    }

    /// <inheritdoc/>
    public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;

    /// <inheritdoc/>
    public string Dialect => SqliteDialect.Name;

    /// <summary>The resolved embedded database file path.</summary>
    public string DatabasePath => _options.ResolveDatabasePath();

    /// <summary>
    /// CQRS connection factory: read connections for reports/dashboards/lists and a
    /// single-writer transactional write path for ingestion (FS-CRAWL-001 §2.4).
    /// </summary>
    public ISqliteConnectionFactory ConnectionFactory => _connectionFactory;

    /// <summary>
    /// Passive WAL size observability hook consumed by the system-health heartbeat and
    /// the /metrics families in later phases.
    /// </summary>
    public SqliteWalMonitor WalMonitor { get; }

    /// <summary>
    /// Capability probe verdict for the four required extension families, computed once
    /// against a scratch in-memory database with the bundled extensions loaded.
    /// </summary>
    public SqliteCapabilityReport Capabilities => _capabilities.Value;

    /// <summary>Releases the connection factory resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connectionFactory.Dispose();
        }
    }

    /// <summary>
    /// Runs the provider-capability probe (cached after the first call).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-family capability report.</returns>
    public Task<SqliteCapabilityReport> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Capabilities);
    }

    /// <summary>
    /// Opens a general-purpose read/write connection with the full provider profile
    /// (WAL, synchronous level, busy timeout, foreign keys, bundled extensions).
    /// Used for migrations and administration; ingestion flows use the single-writer
    /// <see cref="ConnectionFactory"/> write path instead.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection owned by the caller.</returns>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var databasePath = DatabasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration.ConfigureAsync(connection, _options, writable: true, _loader, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Tests the local embedded database. Reachability means the file can be opened
    /// (created on demand) and queried; authentication is trivially local for the
    /// embedded provider. Migration compatibility inspects the provider-agnostic
    /// <c>schema_migrations</c> ledger: a fresh database is compatible (migrations will
    /// initialize it), a ledger carrying this dialect is compatible, and a ledger
    /// carrying another provider's dialect is incompatible — switching providers is an
    /// explicit archive export/import operation, never automatic (FS-CRAWL-001 §2.4).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test verdict.</returns>
    public async Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        DbConnection? connection = null;
        try
        {
            connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT 1";
                await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            string? integrity;
            await using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA quick_check";
                integrity = (string?)await integrityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new DbConnectionTestResult(
                    IsReachable: true,
                    AuthenticationOk: true,
                    MigrationCompatible: false,
                    Message: string.Create(CultureInfo.InvariantCulture, $"Database integrity check failed: {integrity}"));
            }

            var (compatible, ledgerDetail) = await CheckMigrationCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            var capabilities = Capabilities;
            var capabilityDetail = capabilities.AllAvailable
                ? "all four capability families available"
                : "capability probe degraded";

            var message = compatible
                ? string.Create(CultureInfo.InvariantCulture, $"Local embedded database OK (dialect '{Dialect}'); {capabilityDetail}; {ledgerDetail}")
                : ledgerDetail;

            return new DbConnectionTestResult(
                IsReachable: true,
                AuthenticationOk: true,
                MigrationCompatible: compatible,
                Message: message);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new DbConnectionTestResult(
                IsReachable: false,
                AuthenticationOk: false,
                MigrationCompatible: false,
                Message: string.Create(CultureInfo.InvariantCulture, $"Local embedded database unreachable: {ex.Message}"));
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<(bool Compatible, string Detail)> CheckMigrationCompatibilityAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        string? ledgerTable;
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'";
            ledgerTable = (string?)await tableCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ledgerTable is null)
        {
            return (true, "no migration ledger yet; migrations will initialize it at startup");
        }

        var dialectColumn = await FindDialectColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        if (dialectColumn is null)
        {
            return (false, "migration ledger 'schema_migrations' has no provider dialect column; cannot verify migration compatibility");
        }

        var foreignDialects = new List<string>();
        var rowCount = 0;
        await using (var dialectCommand = connection.CreateCommand())
        {
            dialectCommand.CommandText = "SELECT DISTINCT \"" + dialectColumn + "\" FROM schema_migrations";
            await using var reader = await dialectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowCount++;
                var dialect = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.Equals(dialect, SqliteDialect.Name, StringComparison.Ordinal))
                {
                    foreignDialects.Add(dialect ?? "(null)");
                }
            }
        }

        if (foreignDialects.Count > 0)
        {
            return (false, string.Create(
                CultureInfo.InvariantCulture,
                $"database carries migrations for dialect(s) [{string.Join(", ", foreignDialects)}]; provider switching requires the archive export/import mechanism"));
        }

        return (true, string.Create(
            CultureInfo.InvariantCulture,
            $"migration ledger present ({rowCount.ToString(CultureInfo.InvariantCulture)} applied version(s), dialect '{SqliteDialect.Name}')"));
    }

    private static async Task<string?> FindDialectColumnAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(schema_migrations)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var columnName = reader.GetString(1);
            if (columnName.Contains("dialect", StringComparison.OrdinalIgnoreCase) && IsSafeIdentifier(columnName))
            {
                return columnName;
            }
        }

        return null;
    }

    private static bool IsSafeIdentifier(string name)
    {
        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return name.Length > 0;
    }
}
