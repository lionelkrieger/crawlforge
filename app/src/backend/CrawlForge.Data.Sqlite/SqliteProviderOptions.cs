namespace CrawlForge.Data.Sqlite;

/// <summary>
/// SQLite <c>PRAGMA synchronous</c> levels for the write-ahead-logging durability
/// profile (FS-CRAWL-001 §2.4: the file-based provider uses write-ahead logging).
/// Numeric values match the SQLite pragma encoding.
/// </summary>
public enum SqliteSynchronousMode
{
    /// <summary>No fsync; fastest, not durable across power loss.</summary>
    Off = 0,

    /// <summary>WAL-safe default: sync at critical moments; durable across application crashes.</summary>
    Normal = 1,

    /// <summary>Sync on every commit; durable across power loss.</summary>
    Full = 2,

    /// <summary>Like <see cref="Full"/> with additional directory syncs.</summary>
    Extra = 3,
}

/// <summary>
/// Configuration of the embedded SQLite provider (FS-CRAWL-001 §2.4). Unconfigured
/// options reproduce the baseline exactly: a database file in the per-user application
/// data directory, WAL journal mode, WAL-safe synchronous level, a five-second busy
/// timeout, and enforced foreign keys. Zero configuration, zero network, fully offline.
/// </summary>
public sealed class SqliteProviderOptions
{
    /// <summary>Application folder name inside the per-user application data directory.</summary>
    public const string ApplicationFolderName = "CrawlForge";

    /// <summary>Default embedded database file name.</summary>
    public const string DatabaseFileName = "crawlforge.db";

    /// <summary>
    /// Explicit database file path. When <see langword="null"/>, the default per-user
    /// application data location is used (<see cref="GetDefaultDatabasePath"/>).
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// Busy timeout applied to every connection (<c>PRAGMA busy_timeout</c>).
    /// Defaults to five seconds so concurrent readers and the single writer retry
    /// instead of failing immediately on lock contention.
    /// </summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// Synchronous level applied to write connections (<c>PRAGMA synchronous</c>).
    /// Defaults to <see cref="SqliteSynchronousMode.Normal"/>, the WAL-safe profile.
    /// </summary>
    public SqliteSynchronousMode Synchronous { get; init; } = SqliteSynchronousMode.Normal;

    /// <summary>
    /// Optional local directory holding platform-specific native SQLite extension
    /// binaries bundled with the application (self-contained packaging, FS-CRAWL-001
    /// §2.4/§13). When <see langword="null"/> or absent, the four required capability
    /// families are supplied by the bundled managed extensions alone. Binaries are only
    /// ever loaded from this local directory — never downloaded.
    /// </summary>
    public string? NativeExtensionDirectory { get; init; }

    /// <summary>
    /// Resolves the effective database file path: the explicit <see cref="DatabasePath"/>
    /// when set, otherwise the default per-user application data location.
    /// </summary>
    /// <returns>The absolute database file path.</returns>
    public string ResolveDatabasePath() => DatabasePath ?? GetDefaultDatabasePath();

    /// <summary>
    /// Default embedded database location: <c>CrawlForge/crawlforge.db</c> inside the
    /// per-user local application data directory (FS-CRAWL-001 §2.4). This is a directory
    /// location only — no machine or OS identity is read or derived.
    /// </summary>
    /// <returns>The default absolute database file path.</returns>
    public static string GetDefaultDatabasePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName,
            DatabaseFileName);

    /// <summary>
    /// Validates the option values, throwing when a setting is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The busy timeout is negative.</exception>
    /// <exception cref="ArgumentException">The explicit database path is empty or whitespace.</exception>
    public void Validate()
    {
        if (BusyTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BusyTimeout), BusyTimeout, "The busy timeout must not be negative.");
        }

        if (DatabasePath is not null && string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("The database path must not be empty or whitespace.", nameof(DatabasePath));
        }
    }
}
