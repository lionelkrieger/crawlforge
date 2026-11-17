namespace CrawlForge.Data.Sqlite;

/// <summary>
/// Passive write-ahead-log size observability hook (FS-CRAWL-001 §2.4 durability
/// surface). Consumed by the system-health heartbeat (<c>SystemHealthSnapshot.WalSizeBytes</c>)
/// and the pull-only /metrics families in later phases; this provider only exposes the
/// measurement and never publishes it anywhere. The measurement is a local file stat —
/// no checkpoint, no write, no network, and no identity dimensions.
/// </summary>
public interface IWalSizeSource
{
    /// <summary>
    /// Reads the current WAL size in bytes, or <see langword="null"/> when no WAL file
    /// exists (for example after a truncating checkpoint or before the first write).
    /// </summary>
    /// <returns>The WAL size in bytes, when present.</returns>
    long? TryGetWalSizeBytes();
}

/// <summary>
/// Default WAL size hook: stats the <c>&lt;database&gt;-wal</c> sidecar file beside the
/// embedded database. Safe to call concurrently with readers and the writer; a file
/// that disappears mid-read is reported as <see langword="null"/>.
/// </summary>
public sealed class SqliteWalMonitor : IWalSizeSource
{
    private readonly string _walFilePath;

    /// <summary>Creates a monitor for the given database file.</summary>
    /// <param name="databasePath">The embedded database file path.</param>
    public SqliteWalMonitor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _walFilePath = databasePath + "-wal";
    }

    /// <summary>The WAL sidecar file path observed by this monitor.</summary>
    public string WalFilePath => _walFilePath;

    /// <inheritdoc/>
    public long? TryGetWalSizeBytes()
    {
        try
        {
            var info = new FileInfo(_walFilePath);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asynchronous facade over the passive file stat for heartbeat/metrics consumers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The WAL size in bytes, or <see langword="null"/> when no WAL file exists.</returns>
    public Task<long?> GetWalSizeBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryGetWalSizeBytes());
    }
}
