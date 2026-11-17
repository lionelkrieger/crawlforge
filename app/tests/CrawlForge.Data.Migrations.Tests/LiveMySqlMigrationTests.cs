using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Tests.Support;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// DEFECT-11 (D6) LIVE MySQL migration proof against the user-configured test
/// server (CF_MYSQL_TEST_* environment variables; skipped gracefully when no
/// server is configured, matching the repo live-server convention). Creates a
/// scratch database, applies the FULL migration catalog through the real
/// <see cref="SchemaMigrator"/> + <see cref="Dialects.MySqlDialect"/>, asserts
/// success, the D6 short index names, the 64-char bound on every physical
/// index, and an idempotent no-op re-run; records VERSION(); drops the scratch
/// database on teardown.
/// </summary>
public sealed class LiveMySqlMigrationTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test with xunit output capture.</summary>
    /// <param name="output">Test output sink.</param>
    public LiveMySqlMigrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Full catalog migrates cleanly on live MySQL and re-runs as a no-op.</summary>
    [LiveMySqlFact]
    public async Task FullCatalog_MigratesOnLiveMySql_IdempotentReRun()
    {
        var settings = LiveMySqlSettings.Create();
        await using var serverConnection = new MySqlConnection(settings.ServerConnectionString);
        await serverConnection.OpenAsync();

        string serverVersion;
        await using (var versionCommand = serverConnection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT VERSION();";
            serverVersion = (string)(await versionCommand.ExecuteScalarAsync())!;
        }

        _output.WriteLine($"MySQL VERSION(): {serverVersion}");
        _output.WriteLine($"Scratch database: {settings.Database}");

        try
        {
            // Fresh scratch database (pre-release proof; never touches real data).
            await ExecuteServerAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
            await ExecuteServerAsync(
                serverConnection,
                $"CREATE DATABASE `{settings.Database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

            var provider = new MySqlTestProvider(settings.DatabaseConnectionString);
            var migrator = new SchemaMigrator(provider);

            // First apply: the full catalog must succeed (D6: no identifier > 64).
            await migrator.MigrateAsync();

            var version = await migrator.GetCurrentVersionAsync();
            Assert.Equal(SchemaCatalog.LatestVersion, version);
            _output.WriteLine($"Schema version after first apply: {version}");

            var ledgerCount = await ScalarAsync(provider, "SELECT COUNT(*) FROM schema_migrations;");
            Assert.Equal(SchemaCatalog.Migrations.Count, (int)ledgerCount);

            var tableCount = await ScalarAsync(
                provider,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE';");
            Assert.Equal(40, (int)tableCount);
            _output.WriteLine($"Physical tables: {tableCount}");

            // The two D6 indexes exist under the exact MySQL logical-schema short names.
            var semanticUnique = await ScalarAsync(
                provider,
                "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() "
                + "AND table_name = 'semantic_link_opportunities' AND index_name = 'ux_semantic_link_opportunities_audit_source_target';");
            Assert.True((int)semanticUnique > 0, "D6 unique index missing on live MySQL.");

            var ngramDf = await ScalarAsync(
                provider,
                "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() "
                + "AND table_name = 'ngram_site_summaries' AND index_name = 'ix_ngram_site_summaries_audit_zone_order_df';");
            Assert.True((int)ngramDf > 0, "D6 DF-descending index missing on live MySQL.");

            // Every physical index name on the live server fits the 64-char bound.
            var overLong = await ScalarAsync(
                provider,
                "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() "
                + "AND CHAR_LENGTH(index_name) > 64;");
            Assert.Equal(0, (int)overLong);

            // First-run seed: exactly one settings row.
            var settingsRows = await ScalarAsync(provider, "SELECT COUNT(*) FROM application_settings;");
            Assert.Equal(1, (int)settingsRows);

            // Idempotent re-run: a complete no-op (criterion #9).
            await migrator.MigrateAsync();
            Assert.Equal(SchemaCatalog.LatestVersion, await migrator.GetCurrentVersionAsync());
            Assert.Equal(ledgerCount, await ScalarAsync(provider, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(tableCount, await ScalarAsync(
                provider,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE';"));
            Assert.Equal(1, (int)await ScalarAsync(provider, "SELECT COUNT(*) FROM application_settings;"));

            _output.WriteLine("Idempotent re-run: version, ledger, tables, and seed row unchanged (no-op).");
            _output.WriteLine($"D6 LIVE PROOF PASS — MySQL {serverVersion}, scratch {settings.Database}, "
                + $"version {version}, 40 tables, both short indexes present, 0 over-long identifiers.");
        }
        finally
        {
            await ExecuteServerAsync(serverConnection, $"DROP DATABASE IF EXISTS `{settings.Database}`;");
        }
    }

    private static async Task ExecuteServerAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(MySqlTestProvider provider, string sql)
    {
        await using var connection = await provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Fact that runs only when a live MySQL server is configured through
/// CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_DATABASE,
/// CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD); otherwise skipped gracefully.
/// </summary>
public sealed class LiveMySqlFactAttribute : FactAttribute
{
    /// <summary>Creates the fact, skipping when no live server is configured.</summary>
    public LiveMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST")))
        {
            Skip = "No live MySQL server configured. Set CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_DATABASE, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD) to enable the D6 live migration proof.";
        }
    }
}

/// <summary>Live-server configuration for the D6 scratch-database proof.</summary>
internal sealed record LiveMySqlSettings(string Host, int Port, string Database, string User, string Password)
{
    public string ServerConnectionString =>
        $"Server={Host};Port={Port};User ID={User};Password={Password};Pooling=false;ConnectionTimeout=10;DefaultCommandTimeout=120";

    public string DatabaseConnectionString =>
        $"Server={Host};Port={Port};Database={Database};User ID={User};Password={Password};Pooling=false;ConnectionTimeout=10;DefaultCommandTimeout=120";

    public static LiveMySqlSettings Create()
    {
        var host = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST") ?? "127.0.0.1";
        var portText = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PORT");
        var port = int.TryParse(portText, out var parsed) ? parsed : 3306;
        var database = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_DATABASE") ?? "crawlforge_d6";
        var user = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_USER") ?? "root";
        var password = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PASSWORD") ?? string.Empty;

        // The scratch database name is interpolated into DDL: restrict it to a
        // safe identifier alphabet (test hygiene, never user input in product).
        if (!Regex.IsMatch(database, "^[A-Za-z0-9_]+$"))
        {
            throw new InvalidOperationException($"Scratch database name '{database}' must match ^[A-Za-z0-9_]+$.");
        }

        return new LiveMySqlSettings(host, port, database, user, password);
    }
}
