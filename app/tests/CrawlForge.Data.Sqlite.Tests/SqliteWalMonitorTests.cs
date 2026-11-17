using Xunit;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>
/// Unit tests for the passive WAL size observability hook consumed by the
/// system-health heartbeat and /metrics families in later phases.
/// </summary>
public class SqliteWalMonitorTests
{
    [Fact(DisplayName = "Monitor observes the database WAL sidecar path")]
    public void MonitorObservesWalSidecarPath()
    {
        var monitor = new SqliteWalMonitor(@"E:\data\crawlforge.db");

        Assert.Equal(@"E:\data\crawlforge.db-wal", monitor.WalFilePath);
    }

    [Fact(DisplayName = "Missing WAL file reports null")]
    public void MissingWalFileReportsNull()
    {
        using var temp = new TempDatabase();
        var monitor = new SqliteWalMonitor(temp.DatabasePath);

        Assert.Null(monitor.TryGetWalSizeBytes());
    }

    [Fact(DisplayName = "WAL size matches the sidecar file length after writes")]
    public async Task WalSizeMatchesSidecarFileLength()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        var monitor = new SqliteWalMonitor(temp.DatabasePath);

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, payload TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (payload) VALUES ('some wal content')");

            // Measure while the database is open: SQLite checkpoints and removes the
            // WAL sidecar when the last connection closes.
            var size = monitor.TryGetWalSizeBytes();

            Assert.NotNull(size);
            Assert.True(size > 0);
            Assert.Equal(new FileInfo(temp.DatabasePath + "-wal").Length, size);
        }
    }

    [Fact(DisplayName = "Async facade agrees with the synchronous measurement")]
    public async Task AsyncFacadeAgreesWithSyncMeasurement()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        var monitor = new SqliteWalMonitor(temp.DatabasePath);

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items DEFAULT VALUES");

            var syncSize = monitor.TryGetWalSizeBytes();
            var asyncSize = await monitor.GetWalSizeBytesAsync();

            Assert.NotNull(syncSize);
            Assert.Equal(syncSize, asyncSize);
        }
    }

    [Fact(DisplayName = "Async facade honors cancellation")]
    public async Task AsyncFacadeHonorsCancellation()
    {
        var monitor = new SqliteWalMonitor(@"E:\data\crawlforge.db");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.GetWalSizeBytesAsync(cancellation.Token));
    }

    [Fact(DisplayName = "Truncating checkpoint collapses the reported WAL size")]
    public async Task TruncatingCheckpointCollapsesWalSize()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        var monitor = new SqliteWalMonitor(temp.DatabasePath);

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, payload TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (payload) VALUES ('content')");
            Assert.NotNull(monitor.TryGetWalSizeBytes());

            await TestSql.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        var size = monitor.TryGetWalSizeBytes();
        Assert.True(size is null or 0);
    }

    [Fact(DisplayName = "Monitor rejects invalid database paths")]
    public void MonitorRejectsInvalidDatabasePaths()
    {
        Assert.Throws<ArgumentException>(() => new SqliteWalMonitor(string.Empty));
        Assert.Throws<ArgumentException>(() => new SqliteWalMonitor("   "));
        Assert.Throws<ArgumentNullException>(() => new SqliteWalMonitor(null!));
    }
}
