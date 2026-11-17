using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Aggregate-view dialect adaptation (§8.1 pattern, rule 6): materialized read
/// models are indexed, transactionally refreshed, audit-scoped, and idempotent;
/// metric summaries upsert on the logical (audit, metric, dimensions) key.
/// </summary>
public sealed class ReadModelTests
{
    /// <summary>The n-gram read model DDL is an indexed InnoDB table with the descending-DF index.</summary>
    [Fact]
    public void Ngram_Read_Model_Ddl_Shape()
    {
        string ddl = MySqlMaterializedReadModels.NgramSiteSummariesDdl;

        Assert.Contains("CREATE TABLE IF NOT EXISTS `ngram_site_summaries`", ddl, StringComparison.Ordinal);
        Assert.Contains("`id` BIGINT NOT NULL AUTO_INCREMENT", ddl, StringComparison.Ordinal);
        Assert.Contains("`document_frequency` BIGINT NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("`total_frequency` BIGINT NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("`mean_tf` DOUBLE NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("`is_primary_term` TINYINT(1) NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("`computed_at` DATETIME(6) NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY `ux_ngram_site_summaries_audit_zone_order_term`", ddl, StringComparison.Ordinal);
        Assert.Contains("`document_frequency` DESC", ddl, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (`audit_id`) REFERENCES `audits` (`id`)", ddl, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB", ddl, StringComparison.Ordinal);
    }

    /// <summary>The refresh is one transaction: delete, recompute, mark primary terms, commit.</summary>
    [Fact]
    public void Ngram_Refresh_Is_Transactional_And_Audit_Scoped()
    {
        string sql = MySqlMaterializedReadModels.BuildNgramSiteSummaryRefreshSql();

        Assert.StartsWith("START TRANSACTION;", sql.TrimStart(), StringComparison.Ordinal);
        Assert.EndsWith("COMMIT;", sql.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("DELETE FROM `ngram_site_summaries` WHERE `audit_id` = @auditId", sql, StringComparison.Ordinal);
        Assert.Contains("FROM `ngram_frequencies`", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE `audit_id` = @auditId", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY `zone`, `ngram_order`, `term`", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT `url_id`)", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(`frequency`)", sql, StringComparison.Ordinal);
        Assert.Contains("AVG(`tf`)", sql, StringComparison.Ordinal);
        Assert.Contains("UTC_TIMESTAMP(6)", sql, StringComparison.Ordinal);
    }

    /// <summary>Primary-term marking uses a MySQL 8 window function partitioned by zone and order.</summary>
    [Fact]
    public void Ngram_Refresh_Marks_Primary_Terms_With_Window_Function()
    {
        string sql = MySqlMaterializedReadModels.BuildNgramSiteSummaryRefreshSql();

        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY `zone`, `ngram_order` ORDER BY `document_frequency` DESC, `term`)", sql, StringComparison.Ordinal);
        Assert.Contains("SET s.`is_primary_term` = 1", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE ranked.rn = 1", sql, StringComparison.Ordinal);
    }

    /// <summary>The refresh is idempotent by construction: delete precedes insert, values are parameterized.</summary>
    [Fact]
    public void Ngram_Refresh_Is_Idempotent_By_Construction()
    {
        string sql = MySqlMaterializedReadModels.BuildNgramSiteSummaryRefreshSql();

        int deleteIndex = sql.IndexOf("DELETE FROM", StringComparison.Ordinal);
        int insertIndex = sql.IndexOf("INSERT INTO", StringComparison.Ordinal);
        Assert.True(deleteIndex >= 0);
        Assert.True(insertIndex > deleteIndex);
        Assert.DoesNotContain("@auditId =", sql, StringComparison.Ordinal);
        Assert.Contains("@auditId", sql, StringComparison.Ordinal);
    }

    /// <summary>The metric-summaries key adaptation carries the JSON dimension key via a stored hash column.</summary>
    [Fact]
    public void Metric_Summaries_Key_Adaptation()
    {
        string ddl = MySqlMaterializedReadModels.MetricSummariesDimensionsKeyDdl;

        Assert.Contains("ALTER TABLE `metric_summaries`", ddl, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS (SHA2(`dimensions_json`, 256)) STORED", ddl, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY `ux_metric_summaries_audit_metric_dimensions` (`audit_id`, `metric_key`, `dimensions_hash`)", ddl, StringComparison.Ordinal);
    }

    /// <summary>Metric upserts are keyed by (audit, metric, dimensions) and refresh the value on conflict.</summary>
    [Fact]
    public void Metric_Summary_Upsert_Shape()
    {
        string sql = MySqlMaterializedReadModels.BuildMetricSummaryUpsertSql();

        Assert.Contains("INSERT INTO `metric_summaries` (`audit_id`, `metric_key`, `dimensions_json`, `metric_value`)", sql, StringComparison.Ordinal);
        Assert.Contains("VALUES (@audit_id, @metric_key, @dimensions_json, @metric_value)", sql, StringComparison.Ordinal);
        Assert.Contains("ON DUPLICATE KEY UPDATE `metric_value` = VALUES(`metric_value`)", sql, StringComparison.Ordinal);
    }
}
