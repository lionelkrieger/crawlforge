using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.MySql;
using CrawlForge.Data.Repositories;
using CrawlForge.E2E.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 criterion #18 — LIVE MySQL parity. The user-configured external
/// server (defaults 127.0.0.1:3306, root/empty password, MySQL 8.0.28 as on the
/// v1.1 sign-off machine; CF_MYSQL_TEST_* overrides honored) is exercised
/// through the locked provider slot-in: <see cref="E2EBackend.StartAsync"/>
/// overrides <c>IDbProvider</c> after module registration, so the composed
/// backend migrates, crawls, analyzes, and scores entirely on MySQL.
/// WP-08-A1: every fact uses <see cref="LiveMySqlParityFactAttribute"/> and skips
/// gracefully when no live server is configured (same convention as the
/// Data.MySql.Tests live-server suites); all assertions run unchanged when one is.
///
/// Proven here:
/// • connection test verdicts (positive / bad credentials / missing database);
/// • a full audit on MySQL produces IDENTICAL report output (precomputed
///   metric summaries + audit scores, findings, URL dataset) to the embedded
///   SQLite provider for the same origin;
/// • archive export → import switch to MySQL and the explicit revert back to
///   the embedded provider, with full artifact fidelity;
/// • the MySQL migration ledger is idempotent and dialect-pure.
///
/// The dedicated <c>crawlforge_e2e</c> database is reset per test so surrogate
/// keys are deterministic and report dimensions compare exactly. VERSION() is
/// recorded for every live test.
/// </summary>
public sealed class MySqlParityE2ETests
{
    private const string DatabaseName = "crawlforge_e2e";

    // WP-08-A1: defaults match the v1.1 sign-off machine (127.0.0.1:3306,
    // root/empty); CF_MYSQL_TEST_* overrides follow the Data.MySql.Tests
    // live-server convention. The dedicated E2E database name is fixed.
    private static MySqlDatabaseSettings Settings => new()
    {
        Host = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST") ?? "127.0.0.1",
        Port = int.TryParse(Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PORT"), out var port) ? port : 3306,
        Database = DatabaseName,
        UserId = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_USER") ?? "root",
        Password = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PASSWORD") ?? string.Empty
    };

    private readonly ITestOutputHelper _output;

    public MySqlParityE2ETests(ITestOutputHelper output) => _output = output;

    [LiveMySqlParityFact]
    public async Task ConnectionTest_ReportsLiveVerdicts_AndRecordsServerVersion()
    {
        await ResetDatabaseAsync();

        var provider = new MySqlDbProvider(Settings);
        var positive = await provider.TestConnectionAsync();
        Assert.True(positive.IsReachable, positive.Message);
        Assert.True(positive.AuthenticationOk, positive.Message);
        Assert.True(positive.MigrationCompatible, positive.Message);

        await using (var connection = await provider.OpenConnectionAsync())
        {
            var version = await ScalarAsync(connection, "SELECT VERSION()");
            _output.WriteLine($"MySQL live server VERSION(): {version}");
            Assert.True(Version.TryParse(version.Split('-')[0], out var parsed), $"Unparseable VERSION(): {version}");
            Assert.True(parsed >= new Version(8, 0), $"MySQL 8.0+ required, got {version}");
        }

        // Bad credentials: reachable, not authenticated.
        var badCredentials = new MySqlDbProvider(Settings with { Password = "cf-wrong-password" });
        var negative = await badCredentials.TestConnectionAsync();
        Assert.True(negative.IsReachable);
        Assert.False(negative.AuthenticationOk);

        // Missing database: authenticated, not migration-compatible.
        var missingDatabase = new MySqlDbProvider(Settings with { Database = DatabaseName + "_cf_missing" });
        var missing = await missingDatabase.TestConnectionAsync();
        Assert.True(missing.IsReachable);
        Assert.True(missing.AuthenticationOk);
        Assert.False(missing.MigrationCompatible);
    }

    [LiveMySqlParityFact]
    public async Task FullAudit_OnMySql_ProducesIdenticalReportOutput_AsSqlite()
    {
        await ResetDatabaseAsync();
        await using var origin = await ParitySiteAsync();

        // Embedded baseline: full crawl + analysis + scoring on SQLite.
        await using var sqlite = await E2EBackend.StartAsync();
        var (sqliteProject, sqliteConfig) = await sqlite.SeedProjectAsync(origin.BaseUrl);
        var sqliteAudit = await sqlite.StartAuditAsync(sqliteProject, sqliteConfig);
        await sqlite.WaitForStatusAsync(sqliteAudit, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // External target: the SAME composed backend with the provider slot-in.
        await using var mysql = await StartMySqlBackendAsync();
        Assert.Equal(DatabaseProviderKind.MySql, mysql.Service<IDbProvider>().Kind);

        var migrator = mysql.Service<ISchemaMigrator>();
        var schemaVersion = await migrator.GetCurrentVersionAsync();
        Assert.True(schemaVersion > 0, "MySQL schema was not migrated at startup.");
        var ledger = await migrator.GetAppliedMigrationsAsync();
        Assert.NotEmpty(ledger);
        Assert.All(ledger, row => Assert.Equal(MySqlDialect.DialectName, row.ProviderDialect));

        var (mysqlProject, mysqlConfig) = await mysql.SeedProjectAsync(origin.BaseUrl);
        var mysqlAudit = await mysql.StartAuditAsync(mysqlProject, mysqlConfig);
        await mysql.WaitForStatusAsync(mysqlAudit, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        await using (var connection = await mysql.Service<IDbProvider>().OpenConnectionAsync())
        {
            _output.WriteLine($"MySQL live server VERSION(): {await ScalarAsync(connection, "SELECT VERSION()")}");
        }

        // Identical report output: precomputed metrics + scores (criterion #18).
        var (sourceMetrics, sourceScores) = await ReadReportAsync(sqlite, sqliteAudit);
        var (targetMetrics, targetScores) = await ReadReportAsync(mysql, mysqlAudit);
        var parity = ProviderSwitchOrchestrator.CompareReports(sourceMetrics, targetMetrics, sourceScores, targetScores);
        Assert.True(parity.Identical,
            $"Report output differs across providers ({parity.MetricsCompared} metrics, {parity.ScoresCompared} scores): " +
            string.Join(" | ", parity.Differences));
        Assert.True(parity.MetricsCompared > 0, "No metric rows were compared.");
        Assert.True(parity.ScoresCompared > 0, "No score rows were compared.");

        // The URL dataset and finding rule keys are identical too.
        var sqliteUrls = await sqlite.ListByAuditAsync<UrlRecordDto>(sqliteAudit);
        var mysqlUrls = await mysql.ListByAuditAsync<UrlRecordDto>(mysqlAudit);
        Assert.Equal(
            sqliteUrls.Select(u => (u.CanonicalUrl, u.StatusClass)).OrderBy(x => x.CanonicalUrl, StringComparer.Ordinal),
            mysqlUrls.Select(u => (u.CanonicalUrl, u.StatusClass)).OrderBy(x => x.CanonicalUrl, StringComparer.Ordinal));

        var sqliteFindings = await sqlite.ListByAuditAsync<FindingDto>(sqliteAudit);
        var mysqlFindings = await mysql.ListByAuditAsync<FindingDto>(mysqlAudit);
        Assert.Equal(
            sqliteFindings.Select(f => f.RuleKey).OrderBy(k => k, StringComparer.Ordinal),
            mysqlFindings.Select(f => f.RuleKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    [LiveMySqlParityFact]
    public async Task ArchiveSwitch_ToMySql_AndExplicitRevert_ToEmbedded()
    {
        await ResetDatabaseAsync();
        await using var origin = await ParitySiteAsync();

        // Source: embedded provider with a completed audit.
        await using var sqlite = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await sqlite.SeedProjectAsync(origin.BaseUrl);
        var sourceAudit = await sqlite.StartAuditAsync(projectId, configurationId);
        await sqlite.WaitForStatusAsync(sourceAudit, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        var (sourceMetrics, sourceScores) = await ReadReportAsync(sqlite, sourceAudit);

        // Target: MySQL backend via the provider slot-in.
        await using var mysql = await StartMySqlBackendAsync();
        var forward = new ProviderSwitchOrchestrator(Endpoint(sqlite), Endpoint(mysql));

        // Explicit user-initiated switch: connection test → export → migrate → import.
        var switched = await forward.SwitchAsync(projectId);
        Assert.True(switched.Success, switched.FailureReason);
        Assert.True(switched.SchemaVersion > 0);
        Assert.NotNull(switched.ImportResult);
        Assert.True(switched.ImportResult!.AuditsImported >= 1);
        Assert.True(switched.ImportResult.UrlsImported >= 3);

        // The imported audit's report output is identical to the source's.
        var importedAudit = await SingleAuditOfProjectAsync(mysql, switched.ImportResult.ProjectId);
        var (importedMetrics, importedScores) = await ReadReportAsync(mysql, importedAudit);
        var parity = ProviderSwitchOrchestrator.CompareReports(sourceMetrics, importedMetrics, sourceScores, importedScores);
        Assert.True(parity.Identical,
            "Imported report output differs: " + string.Join(" | ", parity.Differences));

        // Full fidelity: the compressed bodies travelled with the archive and
        // are readable from the target's content store (one per crawled page).
        var artifacts = await mysql.ListByAuditAsync<ContentArtifactDto>(importedAudit);
        var bodies = artifacts.Where(a => a.Kind == ContentArtifactKind.Body).ToList();
        Assert.Equal(3, bodies.Count);
        foreach (var body in bodies)
        {
            var bodyStream = await mysql.Service<IContentStore>().OpenReadAsync(body.StorageKey);
            Assert.NotNull(bodyStream);
            await using (bodyStream)
            using (var reader = new StreamReader(bodyStream))
            {
                var html = await reader.ReadToEndAsync();
                Assert.Contains("Parity fixture", html);
            }
        }

        // Revert: export from MySQL, import back into a FRESH embedded backend.
        using var revertBundle = new MemoryStream();
        await Endpoint(mysql).Archive.ExportAsync(switched.ImportResult.ProjectId, revertBundle);
        revertBundle.Position = 0;

        await using var reverted = await E2EBackend.StartAsync();
        var revert = new ProviderSwitchOrchestrator(Endpoint(reverted), Endpoint(mysql));
        var revertedResult = await revert.RevertAsync(revertBundle);
        Assert.True(revertedResult.Success, revertedResult.FailureReason);
        Assert.NotNull(revertedResult.ImportResult);
        Assert.True(revertedResult.ImportResult!.AuditsImported >= 1);

        var revertedAudit = await SingleAuditOfProjectAsync(reverted, revertedResult.ImportResult.ProjectId);
        var (revertedMetrics, revertedScores) = await ReadReportAsync(reverted, revertedAudit);
        var revertParity = ProviderSwitchOrchestrator.CompareReports(sourceMetrics, revertedMetrics, sourceScores, revertedScores);
        Assert.True(revertParity.Identical,
            "Reverted report output differs: " + string.Join(" | ", revertParity.Differences));

        // Bodies survive the round trip too.
        var revertedArtifacts = await reverted.ListByAuditAsync<ContentArtifactDto>(revertedAudit);
        Assert.Contains(revertedArtifacts, a => a.Kind == ContentArtifactKind.Body);
    }

    [LiveMySqlParityFact]
    public async Task MySqlMigrations_AreIdempotent_AndDialectPure()
    {
        await ResetDatabaseAsync();
        await using var mysql = await StartMySqlBackendAsync();
        var migrator = mysql.Service<ISchemaMigrator>();

        var firstVersion = await migrator.GetCurrentVersionAsync();
        var firstLedger = await migrator.GetAppliedMigrationsAsync();
        Assert.True(firstVersion > 0);
        Assert.NotEmpty(firstLedger);

        // Re-running migrations changes nothing (idempotent ledger).
        await migrator.MigrateAsync();
        var secondVersion = await migrator.GetCurrentVersionAsync();
        var secondLedger = await migrator.GetAppliedMigrationsAsync();
        Assert.Equal(firstVersion, secondVersion);
        Assert.Equal(firstLedger.Count, secondLedger.Count);
        Assert.All(secondLedger, row => Assert.Equal(MySqlDialect.DialectName, row.ProviderDialect));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static Task<E2EBackend> StartMySqlBackendAsync() =>
        E2EBackend.StartAsync(builder =>
            builder.Services.AddSingleton<IDbProvider>(new MySqlDbProvider(Settings)));

    private static ProviderSwitchEndpoint Endpoint(E2EBackend backend) => new(
        backend.Service<IDbProvider>(),
        backend.Service<ISchemaMigrator>(),
        new ProjectArchiveService(backend.Service<IDbProvider>(), backend.Service<IArtifactFileStore>()));

    private static async Task<(IReadOnlyList<MetricSummaryRow> Metrics, IReadOnlyList<AuditScoreRow> Scores)> ReadReportAsync(
        E2EBackend backend, long auditId)
    {
        await using var connection = await backend.Service<IDbProvider>().OpenConnectionAsync();
        return await ProviderSwitchOrchestrator.ReadReportRowsAsync(connection, auditId);
    }

    private static async Task<long> SingleAuditOfProjectAsync(E2EBackend backend, long projectId)
    {
        var audits = await backend.Service<IReadRepository<AuditDto>>().ListAsync(
            new ListQuery(0, 10, new Dictionary<string, string> { ["projectId"] = projectId.ToString() }));
        return Assert.Single(audits.Items).Id;
    }

    /// <summary>
    /// Resets the dedicated E2E database so every test starts from an empty
    /// schema with deterministic surrogate keys. Server-level connection only;
    /// no other database is ever touched. DROP and CREATE run as SEPARATE
    /// commands (a multi-statement batch left inconsistent post-reset state),
    /// and pooled sessions are cleared afterwards so no stale connection can
    /// resurrect the dropped database.
    /// </summary>
    private static async Task ResetDatabaseAsync()
    {
        var factory = new MySqlConnectionFactory();
        await using (var connection = factory.Create(Settings with { Database = "mysql" }))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{DatabaseName}`";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var connection = factory.Create(Settings with { Database = "mysql" }))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE `{DatabaseName}` CHARACTER SET utf8mb4";
            await create.ExecuteNonQueryAsync();
        }

        await MySqlConnector.MySqlConnection.ClearAllPoolsAsync();
    }

    private static async Task<string> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Deterministic three-page parity site (no WAF, no redirects).</summary>
    private static Task<LoopbackOrigin> ParitySiteAsync() =>
        LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Parity fixture index",
                "<p>Welcome to the parity fixture site.</p>" + FixturePages.Links(["/p/1", "/p/2"]))));
            routes.Map("/p/1", OriginResponse.Html(FixturePages.Page(
                "Parity fixture page one",
                "<p>First page with enough unique content to analyze and score reliably.</p>")));
            routes.Map("/p/2", OriginResponse.Html(FixturePages.Page(
                "Parity fixture page two",
                "<p>Second page with different unique content to analyze and score reliably.</p>")));
        });
}
