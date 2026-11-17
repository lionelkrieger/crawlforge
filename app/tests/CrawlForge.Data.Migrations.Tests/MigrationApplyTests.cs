using System.Data.Common;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// Migration application behavior: version reporting, per-provider ledger,
/// incremental application, transactional rollback, cancellation, and the
/// criterion #9 double-apply proof (MigrateAsync twice → identical schema,
/// identical ledger, identical seed, no errors).
/// </summary>
public class MigrationApplyTests : IAsyncLifetime
{
    private readonly MigrationTestDatabase _db = new();

    /// <summary>Creates the per-test database.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public Task InitializeAsync() => _db.InitializeAsync();

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>A fresh database reports version zero and an empty ledger without creating anything.</summary>
    [Fact]
    public async Task FreshDatabaseReportsVersionZeroAndEmptyLedger()
    {
        Assert.Equal(0, await _db.Migrator.GetCurrentVersionAsync());
        Assert.Empty(await _db.Migrator.GetAppliedMigrationsAsync());

        // Read-only probes must not create the ledger or any other object.
        await using var connection = await _db.OpenAsync();
        Assert.Empty(await SchemaInspector.GetTableNamesAsync(connection));
    }

    /// <summary>MigrateAsync applies every catalog version and records the per-provider ledger.</summary>
    [Fact]
    public async Task MigrateAppliesAllVersionsAndRecordsLedger()
    {
        await _db.MigrateAsync();

        Assert.Equal(SchemaCatalog.LatestVersion, await _db.Migrator.GetCurrentVersionAsync());
        var applied = await _db.Migrator.GetAppliedMigrationsAsync();
        Assert.Equal(SchemaCatalog.Migrations.Count, applied.Count);
        Assert.Equal(SchemaCatalog.Migrations.Select(m => m.Version), applied.Select(r => r.Version));
        Assert.All(applied, row =>
        {
            Assert.Equal("sqlite", row.ProviderDialect);
            Assert.True(row.Id > 0);
            Assert.Equal(TimeSpan.Zero, row.AppliedAt.Offset);
        });
    }

    /// <summary>
    /// Criterion #9: applying MigrateAsync twice leaves the schema DDL, the
    /// ledger rows, and the seeded settings row byte-identical, with no errors.
    /// </summary>
    [Fact]
    public async Task DoubleApplyIsIdenticalNoOp()
    {
        await _db.MigrateAsync();
        var schemaBefore = await CaptureSchemaAsync();
        var ledgerBefore = await CaptureLedgerAsync();
        var seedBefore = await CaptureSettingsRowAsync();

        await _db.MigrateAsync();

        Assert.Equal(schemaBefore, await CaptureSchemaAsync());
        Assert.Equal(ledgerBefore, await CaptureLedgerAsync());
        Assert.Equal(seedBefore, await CaptureSettingsRowAsync());
        Assert.Equal(SchemaCatalog.LatestVersion, await _db.Migrator.GetCurrentVersionAsync());
        Assert.Equal(1, await _db.CountRowsAsync("application_settings"));
    }

    /// <summary>Incremental application (version 1 only, then the rest) converges to the full catalog.</summary>
    [Fact]
    public async Task IncrementalApplyConvergesToFullCatalog()
    {
        var firstOnly = new SchemaMigrator(_db.Provider, SchemaCatalog.Migrations.Take(1).ToList());
        await firstOnly.MigrateAsync();

        Assert.Equal(1, await _db.Migrator.GetCurrentVersionAsync());
        await using (var connection = await _db.OpenAsync())
        {
            var tables = await SchemaInspector.GetTableNamesAsync(connection);
            Assert.Equal(SchemaCatalog.BaselineTables.Count, tables.Count);
        }

        // The partial run seeds the baseline settings columns without failing.
        Assert.Equal(1, await _db.CountRowsAsync("application_settings"));

        await _db.MigrateAsync();

        Assert.Equal(SchemaCatalog.LatestVersion, await _db.Migrator.GetCurrentVersionAsync());
        await using (var connection = await _db.OpenAsync())
        {
            var tables = await SchemaInspector.GetTableNamesAsync(connection);
            Assert.Equal(SchemaCatalog.AllTables.Count, tables.Count);
        }

        var applied = await _db.Migrator.GetAppliedMigrationsAsync();
        Assert.Equal(SchemaCatalog.Migrations.Select(m => m.Version), applied.Select(r => r.Version));
    }

    /// <summary>A failing migration rolls back atomically: no partial objects, no ledger row.</summary>
    [Fact]
    public async Task FailingMigrationRollsBackAtomically()
    {
        var duplicateProjects = SchemaCatalog.BaselineTables.First(t => t.Name == "projects");
        var brokenSequence = new List<MigrationDefinition>
        {
            SchemaCatalog.Migrations[0],
            new(2, "broken-migration", [new CreateTableOperation(duplicateProjects)]),
        };
        var migrator = new SchemaMigrator(_db.Provider, brokenSequence);

        await Assert.ThrowsAnyAsync<DbException>(() => migrator.MigrateAsync());

        Assert.Equal(1, await _db.Migrator.GetCurrentVersionAsync());
        var applied = await _db.Migrator.GetAppliedMigrationsAsync();
        Assert.Single(applied);
        Assert.Equal(1, applied[0].Version);
        await using var connection = await _db.OpenAsync();
        Assert.Equal(SchemaCatalog.BaselineTables.Count, (await SchemaInspector.GetTableNamesAsync(connection)).Count);
    }

    /// <summary>A pre-canceled token aborts MigrateAsync before any schema work.</summary>
    [Fact]
    public async Task PreCanceledTokenAbortsMigration()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _db.Migrator.MigrateAsync(cts.Token));
        Assert.Equal(0, await _db.Migrator.GetCurrentVersionAsync());
    }

    /// <summary>Concurrent MigrateAsync calls on one migrator are serialized and safe.</summary>
    [Fact]
    public async Task ConcurrentMigrateCallsAreSafe()
    {
        await Task.WhenAll(_db.Migrator.MigrateAsync(), _db.Migrator.MigrateAsync());

        Assert.Equal(SchemaCatalog.LatestVersion, await _db.Migrator.GetCurrentVersionAsync());
        Assert.Equal(SchemaCatalog.Migrations.Count, (await _db.Migrator.GetAppliedMigrationsAsync()).Count);
        Assert.Equal(1, await _db.CountRowsAsync("application_settings"));
    }

    /// <summary>The ledger enforces one row per (version, provider dialect).</summary>
    [Fact]
    public async Task LedgerRejectsDuplicateVersionPerDialect()
    {
        await _db.MigrateAsync();
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO schema_migrations (version, provider_dialect, applied_at) VALUES (1, 'sqlite', '2026-01-01 00:00:00.000000');"));
    }

    private async Task<string> CaptureSchemaAsync()
    {
        await using var connection = await _db.OpenAsync();
        return await SchemaInspector.GetSchemaSnapshotAsync(connection);
    }

    private async Task<string> CaptureLedgerAsync()
    {
        var rows = await _db.Migrator.GetAppliedMigrationsAsync();
        return string.Join("|", rows.Select(r => $"{r.Id}:{r.Version}:{r.ProviderDialect}:{r.AppliedAt:O}"));
    }

    private async Task<string> CaptureSettingsRowAsync()
    {
        await using var connection = await _db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM application_settings ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields[i] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)) ?? string.Empty;
            }

            rows.Add(string.Join("|", fields));
        }

        return string.Join(";", rows);
    }
}
