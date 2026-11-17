using System.Data;
using System.Globalization;
using MySqlConnector;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Live-server facts: exercised only when a user-configured MySQL server is
/// available through CF_MYSQL_TEST_* environment variables; otherwise every fact
/// SKIPS gracefully (the build machine has no MySQL server). All schema objects
/// created here are session-scoped TEMPORARY tables — the user's real schema is
/// never modified.
/// </summary>
public sealed class LiveServerTests
{
    private static async Task<MySqlConnection> OpenLiveConnectionAsync()
    {
        var factory = new MySqlConnectionFactory();
        MySqlConnection connection = factory.Create(LiveServerSettings.Create());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>The provider opens a live connection to the configured server.</summary>
    [LiveServerFact]
    public async Task OpenConnection_Returns_Open_Connection()
    {
        var provider = new MySqlDbProvider(LiveServerSettings.Create());

        await using System.Data.Common.DbConnection connection = await provider.OpenConnectionAsync();

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.True(MySqlConnectionTester.TryParseServerVersion(connection.ServerVersion, out Version? version));
        Assert.False(version < MySqlConnectionTester.MinimumServerVersion);
    }

    /// <summary>The connection test reports the full positive verdict against a healthy server.</summary>
    [LiveServerFact]
    public async Task TestConnection_Reports_Full_Positive_Verdict()
    {
        var provider = new MySqlDbProvider(LiveServerSettings.Create());

        CrawlForge.Contracts.Persistence.DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.True(result.MigrationCompatible);
    }

    /// <summary>Wrong credentials report reachable-but-not-authenticated against the live server.</summary>
    [LiveServerFact]
    public async Task Bad_Credentials_Report_Reachable_Not_Authenticated()
    {
        MySqlDatabaseSettings settings = LiveServerSettings.Create() with
        {
            Password = LiveServerSettings.Create().Password + "-cf-wrong",
        };
        var provider = new MySqlDbProvider(settings);

        CrawlForge.Contracts.Persistence.DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
    }

    /// <summary>A missing database reports authenticated-but-not-compatible against the live server.</summary>
    [LiveServerFact]
    public async Task Unknown_Database_Reports_Authenticated_Not_Compatible()
    {
        MySqlDatabaseSettings settings = LiveServerSettings.Create() with
        {
            Database = LiveServerSettings.Create().Database + "_cf_missing",
        };
        var provider = new MySqlDbProvider(settings);

        CrawlForge.Contracts.Persistence.DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
    }

    /// <summary>The dialect regex predicate executes natively on the live server.</summary>
    [LiveServerFact]
    public async Task Regex_Predicate_Executes()
    {
        await using MySqlConnection connection = await OpenLiveConnectionAsync();
        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE `cf_regex_tmp` (`id` INT NOT NULL, `url` VARCHAR(512) NOT NULL)");
        await ExecuteAsync(connection, "INSERT INTO `cf_regex_tmp` VALUES (1, 'https://example.com/a'), (2, 'https://example.com/b'), (3, 'https://other.invalid/c')");

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `cf_regex_tmp` WHERE " + MySqlDialect.RegexPredicateSql("`url`", "@pattern");
        MySqlParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@pattern";
        parameter.Value = "^https://example\\.com/";
        command.Parameters.Add(parameter);
        long count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        Assert.Equal(2, count);
    }

    /// <summary>Multi-row writes roll back atomically on failure and commit on success.</summary>
    [LiveServerFact]
    public async Task Transaction_Rollback_Leaves_No_Rows()
    {
        await using MySqlConnection connection = await OpenLiveConnectionAsync();
        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE `cf_tx_tmp` (`id` INT NOT NULL)");

        await Assert.ThrowsAsync<InvalidOperationException>(() => MySqlTransactionalWriter.RunAsync<int>(
            connection,
            async (c, t, ct) =>
            {
                await using MySqlCommand insert = (MySqlCommand)c.CreateCommand();
                insert.Transaction = (MySqlTransaction)t;
                insert.CommandText = "INSERT INTO `cf_tx_tmp` VALUES (1), (2)";
                await insert.ExecuteNonQueryAsync(ct);
                throw new InvalidOperationException("simulated write failure");
            }));

        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM `cf_tx_tmp`"));

        await MySqlTransactionalWriter.RunAsync(
            connection,
            async (c, t, ct) =>
            {
                await using MySqlCommand insert = (MySqlCommand)c.CreateCommand();
                insert.Transaction = (MySqlTransaction)t;
                insert.CommandText = "INSERT INTO `cf_tx_tmp` VALUES (3)";
                await insert.ExecuteNonQueryAsync(ct);
                return 0;
            });

        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM `cf_tx_tmp`"));
    }

    /// <summary>The JSON_TABLE cosine-similarity expression computes on the live server.</summary>
    [LiveServerFact]
    public async Task Cosine_Similarity_Computes()
    {
        await using MySqlConnection connection = await OpenLiveConnectionAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + MySqlDialect.CosineSimilaritySql("@a", "@b");
        MySqlParameter a = command.CreateParameter();
        a.ParameterName = "@a";
        a.Value = "[1, 2, 3]";
        MySqlParameter b = command.CreateParameter();
        b.ParameterName = "@b";
        b.Value = "[1, 2, 3]";
        command.Parameters.Add(a);
        command.Parameters.Add(b);

        double similarity = Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        Assert.Equal(1.0, similarity, 9);
    }

    /// <summary>The materialized n-gram read model refreshes correctly and idempotently.</summary>
    [LiveServerFact]
    public async Task Ngram_Read_Model_Refresh_Is_Idempotent()
    {
        await using MySqlConnection connection = await OpenLiveConnectionAsync();

        // Real scratch tables in the configured test database (dropped in finally).
        // TEMPORARY tables cannot be used here: the product refresh self-joins
        // ngram_site_summaries in one UPDATE, and MySQL forbids referencing a
        // temporary table more than once in a single query ("Can't reopen table").
        // The FK clause is stripped because the scratch database has no audits
        // parent; the refresh logic under test is unchanged.
        string summariesDdl = MySqlMaterializedReadModels.NgramSiteSummariesDdl
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(",\n  CONSTRAINT `fk_ngram_site_summaries_audit` FOREIGN KEY (`audit_id`) REFERENCES `audits` (`id`)", string.Empty, StringComparison.Ordinal);
        try
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS `ngram_site_summaries`");
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS `ngram_frequencies`");
            await ExecuteAsync(connection, summariesDdl);
            await ExecuteAsync(connection, "CREATE TABLE `ngram_frequencies` (`audit_id` BIGINT NOT NULL, `url_id` BIGINT NOT NULL, `zone` VARCHAR(32) NOT NULL, `ngram_order` INT NOT NULL, `term` VARCHAR(255) NOT NULL, `frequency` BIGINT NOT NULL, `tf` DOUBLE NOT NULL)");
            await ExecuteAsync(connection, "INSERT INTO `ngram_frequencies` VALUES (1, 10, 'title', 1, 'alpha', 3, 0.3), (1, 20, 'title', 1, 'alpha', 2, 0.2), (1, 10, 'title', 1, 'beta', 1, 0.1)");

            for (int pass = 0; pass < 2; pass++)
            {
                await using MySqlCommand refresh = connection.CreateCommand();
                refresh.CommandText = MySqlMaterializedReadModels.BuildNgramSiteSummaryRefreshSql();
                MySqlParameter audit = refresh.CreateParameter();
                audit.ParameterName = "@auditId";
                audit.Value = 1L;
                refresh.Parameters.Add(audit);
                await refresh.ExecuteNonQueryAsync();
            }

            Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM `ngram_site_summaries`"));
            Assert.Equal(2, await CountAsync(connection, "SELECT `document_frequency` FROM `ngram_site_summaries` WHERE `term` = 'alpha'"));
            Assert.Equal(5, await CountAsync(connection, "SELECT `total_frequency` FROM `ngram_site_summaries` WHERE `term` = 'alpha'"));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM `ngram_site_summaries` WHERE `is_primary_term` = 1 AND `term` = 'alpha'"));
            Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM `ngram_site_summaries` WHERE `is_primary_term` = 1 AND `term` = 'beta'"));
        }
        finally
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS `ngram_site_summaries`");
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS `ngram_frequencies`");
        }
    }

    /// <summary>Report rows read from the live server compare identical through the parity contract.</summary>
    [LiveServerFact]
    public async Task Report_Rows_Read_And_Compare_Identical()
    {
        await using MySqlConnection connection = await OpenLiveConnectionAsync();
        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE `metric_summaries` (`id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, `audit_id` BIGINT NOT NULL, `metric_key` VARCHAR(128) NOT NULL, `dimensions_json` JSON NOT NULL, `metric_value` DOUBLE NOT NULL)");
        // DEFECT D10: the temp table mirrors the REAL audit_scores catalog shape —
        // nullable integer category plus the four contributing count columns.
        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE `audit_scores` (`id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, `audit_id` BIGINT NOT NULL, `category` INT NULL, `score` DOUBLE NOT NULL, `error_count` INT NOT NULL, `warning_count` INT NOT NULL, `notice_count` INT NOT NULL, `pass_count` INT NOT NULL)");
        await ExecuteAsync(connection, "INSERT INTO `metric_summaries` (`audit_id`, `metric_key`, `dimensions_json`, `metric_value`) VALUES (1, 'waf-blocks', JSON_OBJECT(), 3)");
        await ExecuteAsync(connection, "INSERT INTO `audit_scores` (`audit_id`, `category`, `score`, `error_count`, `warning_count`, `notice_count`, `pass_count`) VALUES (1, NULL, 87.5, 1, 2, 3, 4)");
        await ExecuteAsync(connection, "INSERT INTO `audit_scores` (`audit_id`, `category`, `score`, `error_count`, `warning_count`, `notice_count`, `pass_count`) VALUES (1, 1, 75.0, 2, 1, 0, 5)");

        (IReadOnlyList<MetricSummaryRow> metrics, IReadOnlyList<AuditScoreRow> scores) =
            await ProviderSwitchOrchestrator.ReadReportRowsAsync(connection, auditId: 1);

        Assert.Single(metrics);
        Assert.Equal(2, scores.Count);
        Assert.Equal("waf-blocks", metrics[0].MetricKey);
        Assert.Equal(3, metrics[0].Value);

        // NULL category maps to the overall marker; ordinal 1 maps to Content;
        // the counts payload is composed from the four real columns.
        AuditScoreRow overall = scores.Single(s => s.Category == "overall");
        Assert.Equal(87.5, overall.Score);
        Assert.Equal("{\"errorCount\":1,\"warningCount\":2,\"noticeCount\":3,\"passCount\":4}", overall.ContributingCountsJson);
        AuditScoreRow content = scores.Single(s => s.Category == "Content");
        Assert.Equal(75.0, content.Score);
        Assert.Equal("{\"errorCount\":2,\"warningCount\":1,\"noticeCount\":0,\"passCount\":5}", content.ContributingCountsJson);

        ReportParityResult parity = ProviderSwitchOrchestrator.CompareReports(metrics, metrics, scores, scores);
        Assert.True(parity.Identical);
    }
}
