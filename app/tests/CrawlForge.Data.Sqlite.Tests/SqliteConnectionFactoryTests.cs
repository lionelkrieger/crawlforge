using Xunit;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>Unit tests for the CQRS connection factory and single-writer lease.</summary>
public class SqliteConnectionFactoryTests
{
    [Fact(DisplayName = "Read connection on a fresh database initializes the file first")]
    public async Task ReadConnectionInitializesFreshDatabase()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        Assert.False(File.Exists(temp.DatabasePath));

        await using var reader = await factory.OpenReadConnectionAsync();
        var value = await TestSql.ScalarAsync<long>(reader, "SELECT 1");

        Assert.Equal(1L, value);
        Assert.True(File.Exists(temp.DatabasePath));
    }

    [Fact(DisplayName = "Read connections are query-only; write connections are not")]
    public async Task QueryOnlyAppliedToReadConnections()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using var reader = await factory.OpenReadConnectionAsync();
        var readerQueryOnly = await TestSql.ScalarAsync<long>(reader, "PRAGMA query_only");
        Assert.Equal(1L, readerQueryOnly);

        await using var writer = await factory.OpenWriteConnectionAsync();
        var writerQueryOnly = await TestSql.ScalarAsync<long>(writer, "PRAGMA query_only");
        Assert.Equal(0L, writerQueryOnly);
    }

    [Fact(DisplayName = "Disposing a write lease releases the gate for the next writer")]
    public async Task WriteLeaseDisposalReleasesGate()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        var first = await factory.OpenWriteConnectionAsync();
        await first.DisposeAsync();

        // A second lease must be obtainable without deadlock once the first is disposed.
        await using var second = await factory.OpenWriteConnectionAsync();
        var value = await TestSql.ScalarAsync<long>(second, "SELECT 1");
        Assert.Equal(1L, value);
    }

    [Fact(DisplayName = "Double disposal of a write lease releases the gate exactly once")]
    public async Task DoubleDisposalReleasesGateOnce()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        var lease = await factory.OpenWriteConnectionAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // If the gate had been released twice, a second lease could be acquired while
        // the first is still held. It must not be acquirable until the holder disposes.
        await using var second = await factory.OpenWriteConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenWriteConnectionAsync(timeout.Token));

        await second.DisposeAsync();
        await using var third = await factory.OpenWriteConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, third.State);
    }

    [Fact(DisplayName = "Write lease exposes working commands and transactions")]
    public async Task WriteLeaseSupportsCommandsAndTransactions()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using var writer = await factory.OpenWriteConnectionAsync();
        await TestSql.ExecuteAsync(writer, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");

        await using (var transaction = writer.BeginTransaction())
        {
            await TestSql.ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('leased')");
            await transaction.CommitAsync();
        }

        var count = await TestSql.ScalarAsync<long>(writer, "SELECT COUNT(*) FROM items");
        Assert.Equal(1L, count);
        Assert.Equal(System.Data.ConnectionState.Open, writer.State);
        Assert.Equal("main", writer.Database);
    }

    [Fact(DisplayName = "Cancelled write-lease acquisition throws and leaves the gate usable")]
    public async Task CancelledWriteAcquisitionLeavesGateUsable()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenWriteConnectionAsync(cancellation.Token));

        await using var writer = await factory.OpenWriteConnectionAsync();
        var value = await TestSql.ScalarAsync<long>(writer, "SELECT 1");
        Assert.Equal(1L, value);
    }

    [Fact(DisplayName = "Disposed factory rejects new connections")]
    public async Task DisposedFactoryRejectsConnections()
    {
        using var temp = new TempDatabase();
        var factory = temp.CreateFactory();
        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.OpenReadConnectionAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.OpenWriteConnectionAsync());
    }

    [Fact(DisplayName = "Read connections carry the capability families")]
    public async Task ReadConnectionsCarryCapabilities()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using var reader = await factory.OpenReadConnectionAsync();
        var field = await TestSql.ScalarAsync<string>(reader, "SELECT split_part('a,b', ',', 2)");
        Assert.Equal("b", field);
    }
}
