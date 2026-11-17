using CrawlForge.Data.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>Unit tests for the SQLite provider beyond the shared conformance cases.</summary>
public class SqliteDbProviderTests
{
    [Fact(DisplayName = "OpenConnectionAsync registers the bundled capability families")]
    public async Task OpenConnectionRegistersExtensions()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var regexResult = await TestSql.ScalarAsync<long>(connection, "SELECT regexp('^a', 'abc')");
        var medianResult = await TestSql.ScalarAsync<double>(connection, "SELECT median(column1) FROM (VALUES (1.0), (3.0))");

        Assert.Equal(1L, regexResult);
        Assert.Equal(2.0, medianResult, 10);
    }

    [Fact(DisplayName = "Custom busy timeout and synchronous level are applied")]
    public async Task CustomDurabilitySettingsApplied()
    {
        using var temp = new TempDatabase();
        var options = new SqliteProviderOptions
        {
            DatabasePath = temp.DatabasePath,
            BusyTimeout = TimeSpan.FromMilliseconds(1234),
            Synchronous = SqliteSynchronousMode.Full,
        };
        using var provider = new SqliteDbProvider(options);

        await using var connection = await provider.OpenConnectionAsync();
        var busyTimeout = await TestSql.ScalarAsync<long>(connection, "PRAGMA busy_timeout");
        var synchronous = await TestSql.ScalarAsync<long>(connection, "PRAGMA synchronous");

        Assert.Equal(1234L, busyTimeout);
        Assert.Equal(2L, synchronous);
    }

    [Fact(DisplayName = "Capability report is cached across calls")]
    public async Task CapabilityReportIsCached()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        var first = await provider.GetCapabilitiesAsync();
        var second = await provider.GetCapabilitiesAsync();

        Assert.Same(first, second);
        Assert.True(first.AllAvailable);
        Assert.Empty(first.NativeExtensionErrors);
    }

    [Fact(DisplayName = "Disposed provider rejects new connections")]
    public async Task DisposedProviderRejectsConnections()
    {
        using var temp = new TempDatabase();
        var provider = temp.CreateProvider();
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.OpenConnectionAsync());
    }

    [Fact(DisplayName = "Migration compatibility detects a dialect column under an alternative name")]
    public async Task MigrationCompatibilityDetectsAlternativeDialectColumnName()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL, dialect TEXT NOT NULL, applied_at TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO schema_migrations (version, dialect, applied_at) VALUES (1, 'mysql', '2026-11-14T00:00:00.0000000+00:00')");
        }

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.False(result.MigrationCompatible);
    }

    [Fact(DisplayName = "Migration compatibility fails closed when the ledger has no dialect column")]
    public async Task MigrationCompatibilityFailsClosedWithoutDialectColumn()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO schema_migrations (version, applied_at) VALUES (1, '2026-11-14T00:00:00.0000000+00:00')");
        }

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("dialect", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Empty migration ledger is compatible")]
    public async Task EmptyMigrationLedgerIsCompatible()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL, provider_dialect TEXT NOT NULL, applied_at TEXT NOT NULL)");
        }

        var result = await provider.TestConnectionAsync();

        Assert.True(result.MigrationCompatible);
        Assert.Contains("0 applied version", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "ConnectionFactory and WalMonitor expose the configured database path")]
    public void FactoryAndMonitorExposeConfiguredPath()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        Assert.Equal(temp.DatabasePath, provider.DatabasePath);
        Assert.Equal(temp.DatabasePath, provider.ConnectionFactory.DatabasePath);
        Assert.Equal(temp.DatabasePath + "-wal", provider.WalMonitor.WalFilePath);
    }

    [Fact(DisplayName = "Native bundle load errors surface in the capability report without breaking families")]
    public async Task NativeBundleErrorsSurfaceInCapabilityReport()
    {
        using var temp = new TempDatabase();
        var nativeDirectory = Path.Combine(temp.DirectoryPath, "native-bundle");
        Directory.CreateDirectory(nativeDirectory);
        await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "broken.dll"), "this is not a native library");

        var options = new SqliteProviderOptions
        {
            DatabasePath = temp.DatabasePath,
            NativeExtensionDirectory = nativeDirectory,
        };
        using var provider = new SqliteDbProvider(options);

        var report = await provider.GetCapabilitiesAsync();

        Assert.True(report.AllAvailable);
        Assert.NotEmpty(report.NativeExtensionErrors);
    }
}
