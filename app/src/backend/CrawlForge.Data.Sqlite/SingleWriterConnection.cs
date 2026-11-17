using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite;

/// <summary>
/// A write connection lease enforcing the single-writer discipline (FS-CRAWL-001 §2.4
/// command/query separation): the connection factory hands out at most one writable
/// lease at a time, and disposing the lease releases the writer gate. All
/// <see cref="DbConnection"/> behavior delegates to the underlying connection.
/// </summary>
internal sealed class SingleWriterConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly SemaphoreSlim _writerGate;
    private int _released;

    /// <summary>Wraps an open write connection with gate release on dispose.</summary>
    /// <param name="inner">The underlying open connection.</param>
    /// <param name="writerGate">The gate acquired before this lease was created.</param>
    public SingleWriterConnection(DbConnection inner, SemaphoreSlim writerGate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(writerGate);
        _inner = inner;
        _writerGate = writerGate;
        _inner.StateChange += OnInnerStateChange;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    /// <inheritdoc/>
    public override string Database => _inner.Database;

    /// <inheritdoc/>
    public override string DataSource => _inner.DataSource;

    /// <inheritdoc/>
    public override string ServerVersion => _inner.ServerVersion;

    /// <inheritdoc/>
    public override ConnectionState State => _inner.State;

    /// <summary>The underlying SQLite provider factory (the base member is protected).</summary>
    protected override DbProviderFactory DbProviderFactory => SqliteFactory.Instance;

    /// <inheritdoc/>
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    /// <inheritdoc/>
    public override void Open() => _inner.Open();

    /// <inheritdoc/>
    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    /// <inheritdoc/>
    public override void Close() => _inner.Close();

    /// <inheritdoc/>
    public override Task CloseAsync() => _inner.CloseAsync();

    /// <inheritdoc/>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        _inner.BeginTransaction(isolationLevel);

    /// <inheritdoc/>
    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        _inner.BeginTransactionAsync(isolationLevel, cancellationToken);

    /// <inheritdoc/>
    protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseGate();
            _inner.StateChange -= OnInnerStateChange;
            _inner.Dispose();
            GC.SuppressFinalize(this);
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        ReleaseGate();
        _inner.StateChange -= OnInnerStateChange;
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void ReleaseGate()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _writerGate.Release();
        }
    }

    private void OnInnerStateChange(object sender, StateChangeEventArgs e) => OnStateChange(e);
}
