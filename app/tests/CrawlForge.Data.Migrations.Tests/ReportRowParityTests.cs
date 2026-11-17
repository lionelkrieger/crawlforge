using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations.Tests.Support;
using CrawlForge.Data.MySql;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// DEFECT D10 regression facts: report-row reading returns the correct
/// contributing counts on BOTH providers. The locked audit_scores schema stores
/// four integer count columns (no contributing_counts_json column); the reader
/// composes the deterministic camelCase payload from them and maps the nullable
/// integer category to its AuditCategory name or the overall marker. Score rows
/// are inserted with the EXACT statement AuditScoreRecorder persists.
/// </summary>
public class ReportRowParityTests : IAsyncLifetime
{
    private const string ExpectedContentCounts = "{\"errorCount\":1,\"warningCount\":2,\"noticeCount\":3,\"passCount\":4}";
    private const string ExpectedIndexabilityCounts = "{\"errorCount\":0,\"warningCount\":1,\"noticeCount\":2,\"passCount\":10}";
    private const string ExpectedOverallCounts = "{\"errorCount\":1,\"warningCount\":3,\"noticeCount\":5,\"passCount\":14}";

    private readonly MigrationTestDatabase _db = new();
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test with output capture.</summary>
    /// <param name="output">Test output sink.</param>
    public ReportRowParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Creates and migrates the per-test embedded database with the parent graph.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.MigrateAsync();
        await TestGraph.InsertFullGraphAsync(_db);
        await InsertScoreRowsAsync(await _db.OpenAsync());
    }

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>Embedded provider: counts compose correctly, categories map faithfully.</summary>
    [Fact]
    public async Task Sqlite_ReportRows_CarryCorrectContributingCounts()
    {
        await using var connection = await _db.OpenAsync();
        var (metrics, scores) = await ProviderSwitchOrchestrator.ReadReportRowsAsync(connection, auditId: 1);

        Assert.Single(metrics);
        Assert.Equal("scoring.findings.total", metrics[0].MetricKey);
        Assert.Equal(23, metrics[0].Value);

        Assert.Equal(3, scores.Count);
        AssertScore(scores, "Content", 87.5, ExpectedContentCounts);
        AssertScore(scores, "Indexability", 92.0, ExpectedIndexabilityCounts);
        AssertScore(scores, "overall", 89.75, ExpectedOverallCounts);
    }

    /// <summary>
    /// Live MySQL: the same rows read identically, and the cross-provider parity
    /// comparison is exact (criterion #18 report output, reader level).
    /// </summary>
    [LiveMySqlFact]
    public async Task MySql_ReportRows_IdenticalToSqlite()
    {
        var settings = LiveMySqlSettings.Create() with { Database = "crawlforge_d10" };
        await using var serverConnection = new MySqlConnection(settings.ServerConnectionString);
        await serverConnection.OpenAsync();

        string serverVersion;
        await using (var versionCommand = serverConnection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT VERSION();";
            serverVersion = (string)(await versionCommand.ExecuteScalarAsync())!;
        }

        _output.WriteLine($"MySQL VERSION(): {serverVersion}; scratch database: {settings.Database}");

        try
        {
            await ExecuteAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
            await ExecuteAsync(serverConnection, $"CREATE DATABASE `{settings.Database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

            var provider = new MySqlTestProvider(settings.DatabaseConnectionString);
            await new SchemaMigrator(provider).MigrateAsync();

            await using (var connection = await provider.OpenConnectionAsync())
            {
                await InsertParentGraphAsync(connection);
                await InsertScoreRowsAsync(connection);
            }

            IReadOnlyList<MetricSummaryRow> mysqlMetrics;
            IReadOnlyList<AuditScoreRow> mysqlScores;
            await using (var connection = await provider.OpenConnectionAsync())
            {
                (mysqlMetrics, mysqlScores) = await ProviderSwitchOrchestrator.ReadReportRowsAsync(connection, auditId: 1);
            }

            Assert.Single(mysqlMetrics);
            Assert.Equal(3, mysqlScores.Count);
            AssertScore(mysqlScores, "Content", 87.5, ExpectedContentCounts);
            AssertScore(mysqlScores, "Indexability", 92.0, ExpectedIndexabilityCounts);
            AssertScore(mysqlScores, "overall", 89.75, ExpectedOverallCounts);

            // Cross-provider parity: the embedded rows read in InitializeAsync
            // compare exactly against the live MySQL rows.
            await using var sqliteConnection = await _db.OpenAsync();
            var (sqliteMetrics, sqliteScores) = await ProviderSwitchOrchestrator.ReadReportRowsAsync(sqliteConnection, auditId: 1);
            var parity = ProviderSwitchOrchestrator.CompareReports(sqliteMetrics, mysqlMetrics, sqliteScores, mysqlScores);
            Assert.True(parity.Identical, "Reader-level report parity differs: " + string.Join(" | ", parity.Differences));

            _output.WriteLine($"D10 LIVE PROOF PASS — MySQL {serverVersion}: contributing counts correct on both providers, cross-provider parity identical.");
        }
        finally
        {
            await ExecuteAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
        }
    }

    private static void AssertScore(IReadOnlyList<AuditScoreRow> scores, string category, double score, string countsJson)
    {
        var row = scores.Single(s => s.Category == category);
        Assert.Equal(score, row.Score);
        Assert.Equal(countsJson, row.ContributingCountsJson);
    }

    /// <summary>
    /// Inserts score rows with the EXACT column list and values shape
    /// AuditScoreRecorder.RecordAsync persists (category ordinal or NULL).
    /// </summary>
    private static async Task InsertScoreRowsAsync(DbConnection connection)
    {
        await using (var metric = connection.CreateCommand())
        {
            metric.CommandText =
                "INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) "
                + "VALUES (1, 'scoring.findings.total', '{}', 23)";
            await metric.ExecuteNonQueryAsync();
        }

        var rows = new (int? Category, double Score, int Errors, int Warnings, int Notices, int Passes)[]
        {
            ((int)AuditCategory.Content, 87.5, 1, 2, 3, 4),
            ((int)AuditCategory.Indexability, 92.0, 0, 1, 2, 10),
            (null, 89.75, 1, 3, 5, 14),
        };

        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO audit_scores (audit_id, category, score, error_count, warning_count, notice_count, pass_count) "
                + "VALUES (@auditId, @category, @score, @errors, @warnings, @notices, @passes)";
            AddParameter(insert, "@auditId", 1L);
            AddParameter(insert, "@category", row.Category is { } category ? category : DBNull.Value);
            AddParameter(insert, "@score", row.Score);
            AddParameter(insert, "@errors", row.Errors);
            AddParameter(insert, "@warnings", row.Warnings);
            AddParameter(insert, "@notices", row.Notices);
            AddParameter(insert, "@passes", row.Passes);
            await insert.ExecuteNonQueryAsync();
        }
    }

    /// <summary>MySQL parent graph equivalent to TestGraph (FK parents for audit 1).</summary>
    private static async Task InsertParentGraphAsync(DbConnection connection)
    {
        string[] statements =
        [
            "INSERT INTO projects (id, name, created_at, updated_at, is_deleted) "
            + "VALUES (1, 'Test Project', '2026-01-01 00:00:00.000000', '2026-01-01 00:00:00.000000', 0)",
            "INSERT INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) "
            + "VALUES (1, 1, 'Test Configuration', '[\"https://example.com/\"]', 0, '2026-01-01 00:00:00.000000', '2026-01-01 00:00:00.000000')",
            "INSERT INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) "
            + "VALUES (1, 1, 1, '{}', 1, 1, '{}', '0.0.1', 0)",
        ];
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
