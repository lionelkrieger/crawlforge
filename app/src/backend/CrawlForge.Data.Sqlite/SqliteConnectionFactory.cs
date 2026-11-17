using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite;

/// <summary>
/// CQRS connection factory for the embedded provider (FS-CRAWL-001 §2.4
/// command/query separation): report, dashboard, and list reads are served from
/// read connections that cannot write, while all ingestion flows through the
/// transactional write path guarded by a single-writer discipline.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>The resolved database file path served by this factory.</summary>
    string DatabasePath { get; }

    /// <summary>
    /// Opens a read connection: SQLite read-only mode plus <c>PRAGMA query_only</c>,
    /// with the bundled capability families registered. Many read connections may be
    /// open concurrently, including while a write lease is held (WAL).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open read connection owned by the caller.</returns>
    Task<DbConnection> OpenReadConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the write connection: WAL journal mode, configured synchronous level,
    /// busy timeout, enforced foreign keys, and the bundled capability families.
    /// At most one write lease is outstanding per factory; the caller must dispose
    /// the returned connection to release the writer gate for the next writer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open write connection owned by the caller.</returns>
    Task<DbConnection> OpenWriteConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="ISqliteConnectionFactory"/> implementation. Read connections use
/// SQLite read-only mode; write connections are serialized through a per-factory
/// single-writer gate (in-process discipline on top of SQLite's own file locking and
/// the configured busy timeout). The database file is created and switched to WAL on
/// first use, entirely offline.
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory, IDisposable
{
    private readonly SqliteProviderOptions _options;
    private readonly Extensions.SqliteExtensionLoader _loader;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _disposed;

    /// <summary>Creates a factory for the configured database file.</summary>
    /// <param name="options">Provider options; <see langword="null"/> uses the baseline defaults.</param>
    public SqliteConnectionFactory(SqliteProviderOptions? options = null)
    {
        _options = options ?? new SqliteProviderOptions();
        _options.Validate();
        _loader = new Extensions.SqliteExtensionLoader(_options.NativeExtensionDirectory);
    }

    /// <inheritdoc/>
    public string DatabasePath => _options.ResolveDatabasePath();

    /// <inheritdoc/>
    public async Task<DbConnection> OpenReadConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await EnsureDatabaseFileAsync(cancellationToken).ConfigureAwait(false);

        var connectionString = BuildConnectionString(SqliteOpenMode.ReadOnly);
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration.ConfigureAsync(connection, _options, writable: false, _loader, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<DbConnection> OpenWriteConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        SqliteConnection? connection = null;
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadWriteCreate));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration.ConfigureAsync(connection, _options, writable: true, _loader, cancellationToken).ConfigureAwait(false);
            return new SingleWriterConnection(connection, _writerGate);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            _writerGate.Release();
            throw;
        }
    }

    /// <summary>Releases the writer gate resources. Outstanding leases remain usable until disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _writerGate.Dispose();
        }
    }

    private string BuildConnectionString(SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
        };
        return builder.ToString();
    }

    private async Task EnsureDatabaseFileAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(DatabasePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // First use: create the file and persist WAL before any read connection opens.
        await using var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteConnectionConfiguration.ConfigureAsync(connection, _options, writable: true, _loader, cancellationToken).ConfigureAwait(false);
    }
}
