namespace CrawlForge.Data.MySql;

/// <summary>
/// Dialect adaptation for aggregate views (FS-CRAWL-002 §1.4 rule 6, FS-CRAWL-001
/// §8.1 pattern): where a native view is insufficient — MySQL views cannot carry
/// indexes and cannot be precomputed — aggregate views are materialized as indexed,
/// transactionally refreshed read models. Dashboard reads stay constant-cost
/// lookups keyed by audit and metric, never full scans over raw crawl data.
/// </summary>
public static class MySqlMaterializedReadModels
{
    /// <summary>Logical name of the site-wide n-gram aggregate read model (Appendix A).</summary>
    public const string NgramSiteSummariesTable = "ngram_site_summaries";

    /// <summary>
    /// MySQL DDL for <c>ngram_site_summaries</c>: site-wide document frequency (DF),
    /// total frequency, mean TF, and primary-term markers per (audit, zone, order,
    /// term), with the required descending-DF index (Appendix A) and a uniqueness
    /// key that makes refreshes idempotent. InnoDB enforces the audit foreign key.
    /// </summary>
    public static string NgramSiteSummariesDdl { get; } =
        """
        CREATE TABLE IF NOT EXISTS `ngram_site_summaries` (
          `id` BIGINT NOT NULL AUTO_INCREMENT,
          `audit_id` BIGINT NOT NULL,
          `zone` VARCHAR(32) NOT NULL,
          `ngram_order` INT NOT NULL,
          `term` VARCHAR(255) NOT NULL,
          `document_frequency` BIGINT NOT NULL,
          `total_frequency` BIGINT NOT NULL,
          `mean_tf` DOUBLE NOT NULL,
          `is_primary_term` TINYINT(1) NOT NULL DEFAULT 0,
          `computed_at` DATETIME(6) NOT NULL,
          PRIMARY KEY (`id`),
          UNIQUE KEY `ux_ngram_site_summaries_audit_zone_order_term` (`audit_id`, `zone`, `ngram_order`, `term`),
          KEY `ix_ngram_site_summaries_audit_zone_order_df` (`audit_id`, `zone`, `ngram_order`, `document_frequency` DESC),
          CONSTRAINT `fk_ngram_site_summaries_audit` FOREIGN KEY (`audit_id`) REFERENCES `audits` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    /// <summary>
    /// Builds the transactional refresh for one audit's n-gram site summaries:
    /// delete the audit's previous rows, recompute DF / total frequency / mean TF
    /// from <c>ngram_frequencies</c> with a grouped aggregate, then mark the primary
    /// term per (zone, order) using a window function (MySQL 8.0 native). The whole
    /// refresh is one transaction (§2.4: all multi-row writes are transactional) and
    /// is idempotent — re-running it reproduces the identical read model.
    /// The caller supplies the <c>@auditId</c> parameter.
    /// </summary>
    /// <returns>The multi-statement refresh SQL.</returns>
    public static string BuildNgramSiteSummaryRefreshSql() =>
        """
        START TRANSACTION;
        DELETE FROM `ngram_site_summaries` WHERE `audit_id` = @auditId;
        INSERT INTO `ngram_site_summaries`
          (`audit_id`, `zone`, `ngram_order`, `term`, `document_frequency`, `total_frequency`, `mean_tf`, `is_primary_term`, `computed_at`)
        SELECT @auditId, `zone`, `ngram_order`, `term`,
               COUNT(DISTINCT `url_id`), SUM(`frequency`), AVG(`tf`), 0, UTC_TIMESTAMP(6)
        FROM `ngram_frequencies`
        WHERE `audit_id` = @auditId
        GROUP BY `zone`, `ngram_order`, `term`;
        UPDATE `ngram_site_summaries` AS s
        JOIN (
          SELECT `id`, ROW_NUMBER() OVER (PARTITION BY `zone`, `ngram_order` ORDER BY `document_frequency` DESC, `term`) AS rn
          FROM `ngram_site_summaries`
          WHERE `audit_id` = @auditId
        ) AS ranked ON ranked.`id` = s.`id`
        SET s.`is_primary_term` = 1
        WHERE ranked.rn = 1;
        COMMIT;
        """;

    /// <summary>
    /// MySQL key adaptation for <c>metric_summaries</c> (§8.1: aggregates keyed by
    /// audit and metric with a dimension payload). MySQL cannot place a unique key
    /// on a JSON column, so the logical key (audit_id, metric_key, dimensions_json)
    /// is carried by a stored generated SHA-256 hash column. The logical schema is
    /// unchanged; this is the dialect-specific physical rendering both providers
    /// agree on semantically.
    /// </summary>
    public static string MetricSummariesDimensionsKeyDdl { get; } =
        """
        ALTER TABLE `metric_summaries`
          ADD COLUMN `dimensions_hash` CHAR(64) GENERATED ALWAYS AS (SHA2(`dimensions_json`, 256)) STORED,
          ADD UNIQUE KEY `ux_metric_summaries_audit_metric_dimensions` (`audit_id`, `metric_key`, `dimensions_hash`)
        """;

    /// <summary>
    /// Builds the idempotent upsert for one precomputed metric row (§8.1 pattern):
    /// INSERT keyed by (audit, metric, dimensions) with an ON DUPLICATE KEY UPDATE
    /// fallback on the value. The caller supplies <c>@auditId</c>, <c>@metricKey</c>,
    /// <c>@dimensionsJson</c>, and <c>@metricValue</c> parameters.
    /// </summary>
    /// <returns>The upsert SQL.</returns>
    public static string BuildMetricSummaryUpsertSql() =>
        MySqlDialect.UpsertSql(
            "metric_summaries",
            keyColumns: ["audit_id", "metric_key", "dimensions_json"],
            valueColumns: ["metric_value"]);
}
