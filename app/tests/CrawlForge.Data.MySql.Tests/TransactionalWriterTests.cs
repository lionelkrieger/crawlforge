using System.Data;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Transactional write conformance (§2.4): multi-row work runs inside one native
/// transaction, commits on success, rolls back on failure, and the helper manages
/// connection lifetime only when it opened the connection itself.
/// </summary>
public sealed class TransactionalWriterTests
{
    /// <summary>Successful work commits exactly once and closes a helper-opened connection.</summary>
    [Fact]
    public async Task Success_Commits_And_Closes_Helper_Opened_Connection()
    {
        using var connection = new FakeDbConnection();

        int result = await MySqlTransactionalWriter.RunAsync(
            connection,
            (_, _, _) => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(1, connection.OpenCount);
        FakeDbTransaction transaction = Assert.Single(connection.Transactions);
        Assert.Equal(1, transaction.CommitCount);
        Assert.Equal(0, transaction.RollbackCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Failed work rolls back exactly once, never commits, and propagates the failure.</summary>
    [Fact]
    public async Task Failure_Rolls_Back_And_Propagates()
    {
        using var connection = new FakeDbConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(() => MySqlTransactionalWriter.RunAsync<int>(
            connection,
            (_, _, _) => throw new InvalidOperationException("write failed")));

        FakeDbTransaction transaction = Assert.Single(connection.Transactions);
        Assert.Equal(0, transaction.CommitCount);
        Assert.Equal(1, transaction.RollbackCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>An already-open connection is left open and is not re-opened.</summary>
    [Fact]
    public async Task Already_Open_Connection_Is_Not_Closed()
    {
        using var connection = new FakeDbConnection();
        connection.Open();

        int result = await MySqlTransactionalWriter.RunAsync(
            connection,
            (_, _, _) => Task.FromResult(7));

        Assert.Equal(7, result);
        Assert.Equal(1, connection.OpenCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>The work callback receives the connection and its active transaction.</summary>
    [Fact]
    public async Task Work_Receives_Connection_And_Transaction()
    {
        using var connection = new FakeDbConnection();
        System.Data.Common.DbConnection? seenConnection = null;
        System.Data.Common.DbTransaction? seenTransaction = null;

        await MySqlTransactionalWriter.RunAsync(
            connection,
            (c, t, _) =>
            {
                seenConnection = c;
                seenTransaction = t;
                return Task.FromResult(0);
            });

        Assert.Same(connection, seenConnection);
        Assert.Same(Assert.Single(connection.Transactions), seenTransaction);
    }
}
