using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Entities;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Real completed persistence stack for integration-faithful tests: a temp
/// embedded SQLite database, the full 40-object migration catalog, and the
/// locked generic repositories for the WP-02-01 surfaces (host_throttle_events,
/// crawl_checkpoints). The project → configuration → audit prerequisite chain
/// is inserted with raw parameterized SQL because the completed phase-01
/// repository mapping disagrees with the migration catalog on some additive
/// column names (projects.content_scope_json vs mapped content_scope;
/// crawl_configurations.limit_overrides_json vs limit_overrides;
/// metric_summaries.metric_value vs mapped value) — reported to the orchestrator
/// as a completed-stack defect; the chain rows only satisfy foreign keys here.
/// </summary>
public sealed class SqliteStackFixture : IAsyncLifetime
{
    private string _directory = null!;

    public SqliteDbProvider Provider { get; private set; } = null!;

    public SqlRepository<HostThrottleEventDto> ThrottleEvents { get; private set; } = null!;

    public SqlRepository<CrawlCheckpointDto> Checkpoints { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-02-01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db")
        });
        await new SchemaMigrator(Provider).MigrateAsync();
        ThrottleEvents = new SqlRepository<HostThrottleEventDto>(Provider);
        Checkpoints = new SqlRepository<CrawlCheckpointDto>(Provider);
    }

    public Task DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; WAL handles may linger briefly on Windows.
        }

        return Task.CompletedTask;
    }

    /// <summary>Creates the project → configuration → audit chain required by foreign keys.</summary>
    /// <returns>The new audit identifier.</returns>
    public async Task<long> CreateAuditAsync()
    {
        await using var connection = await Provider.OpenConnectionAsync();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var projectId = await InsertAsync(
            connection,
            "INSERT INTO projects (name, created_at, updated_at, is_deleted) VALUES (@name, @now, @now, 0)",
            ("name", "limits-tests"),
            ("now", now));
        var configurationId = await InsertAsync(
            connection,
            "INSERT INTO crawl_configurations (project_id, name, seeds_json, rendering_mode, created_at, updated_at) VALUES (@projectId, @name, @seeds, 0, @now, @now)",
            ("projectId", projectId),
            ("name", "cfg"),
            ("seeds", "[\"https://example.com/\"]"),
            ("now", now));
        return await InsertAsync(
            connection,
            "INSERT INTO audits (project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) VALUES (@projectId, @configId, '{}', 0, 0, '{}', 'tests', 0)",
            ("projectId", projectId),
            ("configId", configurationId));
    }

    private static async Task<long> InsertAsync(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@" + name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync();
        }

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(await idCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
