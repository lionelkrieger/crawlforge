using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Logical-schema conformance (precedence rule 6): the MySQL provider carries the
/// complete 40-object catalog (§9.2 + Appendix A) and every required index (§9.3 +
/// Appendix A), rendered in the MySQL dialect with the §9.1 conventions.
/// </summary>
public sealed class LogicalSchemaTests
{
    /// <summary>The catalog holds exactly 40 distinct logical objects.</summary>
    [Fact]
    public void Catalog_Has_Forty_Distinct_Objects()
    {
        Assert.Equal(40, MySqlLogicalSchema.Objects.Count);
        Assert.Equal(40, MySqlLogicalSchema.Objects.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Every base table from §9.2 is present.</summary>
    [Theory]
    [InlineData("application_settings")]
    [InlineData("projects")]
    [InlineData("crawl_configurations")]
    [InlineData("audits")]
    [InlineData("url_records")]
    [InlineData("url_elements")]
    [InlineData("link_edges")]
    [InlineData("findings")]
    [InlineData("metric_summaries")]
    [InlineData("crawl_checkpoints")]
    [InlineData("schema_migrations")]
    [InlineData("saved_views")]
    [InlineData("schedule_definitions")]
    [InlineData("schedule_runs")]
    [InlineData("export_destinations")]
    [InlineData("user_dictionary")]
    [InlineData("content_artifacts")]
    [InlineData("url_embeddings")]
    [InlineData("extracted_content")]
    [InlineData("content_search_results")]
    [InlineData("snippet_results")]
    [InlineData("technology_detections")]
    [InlineData("audit_scores")]
    [InlineData("finding_suppressions")]
    [InlineData("automation_action_log")]
    public void Base_Tables_Are_Present(string name)
    {
        LogicalSchemaObject? obj = MySqlLogicalSchema.Objects.SingleOrDefault(o => o.Name == name);
        Assert.NotNull(obj);
        Assert.Equal(LogicalObjectKind.Table, obj.Kind);
    }

    /// <summary>Every additive object from Appendix A is present.</summary>
    [Theory]
    [InlineData("waf_detections")]
    [InlineData("waf_user_signatures")]
    [InlineData("host_throttle_events")]
    [InlineData("content_scope_profiles")]
    [InlineData("ngram_frequencies")]
    [InlineData("ngram_site_summaries")]
    [InlineData("user_stopwords")]
    [InlineData("ui_plugins")]
    [InlineData("redirect_mappings")]
    [InlineData("log_imports")]
    [InlineData("log_url_aggregates")]
    [InlineData("semantic_link_opportunities")]
    [InlineData("semantic_clusters")]
    [InlineData("governance_matrix")]
    [InlineData("analytics_detections")]
    public void Additive_Objects_Are_Present(string name)
    {
        Assert.Contains(MySqlLogicalSchema.Objects, o => o.Name == name);
    }

    /// <summary>The n-gram site summary is a materialized read model, not a native view (rule 6).</summary>
    [Fact]
    public void Ngram_Site_Summaries_Is_Materialized_Read_Model()
    {
        LogicalSchemaObject obj = MySqlLogicalSchema.Objects.Single(o => o.Name == "ngram_site_summaries");
        Assert.Equal(LogicalObjectKind.MaterializedReadModel, obj.Kind);
        Assert.All(
            MySqlLogicalSchema.Objects.Where(o => o.Name != "ngram_site_summaries"),
            o => Assert.Equal(LogicalObjectKind.Table, o.Kind));
    }

    /// <summary>The catalog carries all 35 required indexes (13 parent + 22 additive).</summary>
    [Fact]
    public void Required_Indexes_Are_Complete()
    {
        Assert.Equal(35, MySqlLogicalSchema.RequiredIndexes.Count);
        Assert.Equal(35, MySqlLogicalSchema.RequiredIndexes.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            MySqlLogicalSchema.RequiredIndexes,
            i => Assert.Contains(MySqlLogicalSchema.Objects, o => o.Name == i.Table));
    }

    /// <summary>The six logical uniqueness constraints are unique indexes.</summary>
    [Theory]
    [InlineData("ux_url_records_audit_url")]
    [InlineData("ux_waf_detections_audit_url")]
    [InlineData("ux_ngram_frequencies_audit_url_zone_order_term")]
    [InlineData("ux_redirect_mappings_audit_source")]
    [InlineData("ux_log_url_aggregates_import_url")]
    [InlineData("ux_semantic_link_opportunities_audit_source_target")]
    public void Uniqueness_Constraints_Are_Unique_Indexes(string name)
    {
        LogicalIndexDefinition index = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == name);
        Assert.True(index.IsUnique);
    }

    /// <summary>Exactly the three spec'd descending indexes carry DESC columns.</summary>
    [Fact]
    public void Descending_Indexes_Match_The_Spec()
    {
        string[] descending = MySqlLogicalSchema.RequiredIndexes
            .Where(i => i.Columns.Any(c => c.Descending))
            .Select(i => i.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ix_log_url_aggregates_import_bot_hits",
                "ix_ngram_site_summaries_audit_zone_order_df",
                "ix_semantic_link_opportunities_audit_similarity",
            },
            descending);
    }

    /// <summary>Key parent indexes carry the exact spec'd columns.</summary>
    [Fact]
    public void Parent_Index_Columns_Match_The_Spec()
    {
        LogicalIndexDefinition urlUnique = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ux_url_records_audit_url");
        Assert.Equal(new[] { "audit_id", "canonical_url" }, urlUnique.Columns.Select(c => c.Name));

        LogicalIndexDefinition metrics = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ix_metric_summaries_audit_metric");
        Assert.Equal(new[] { "audit_id", "metric_key" }, metrics.Columns.Select(c => c.Name));

        LogicalIndexDefinition nextDue = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ix_schedule_definitions_next_due");
        Assert.Equal(new[] { "next_due_at" }, nextDue.Columns.Select(c => c.Name));

        LogicalIndexDefinition domZone = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ix_link_edges_audit_dom_zone");
        Assert.Equal(new[] { "audit_id", "dom_zone" }, domZone.Columns.Select(c => c.Name));
    }

    /// <summary>Index DDL renders UNIQUE and DESC natively with backtick quoting.</summary>
    [Fact]
    public void Index_Ddl_Rendering()
    {
        LogicalIndexDefinition unique = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ux_url_records_audit_url");
        string uniqueSql = MySqlLogicalSchema.RenderCreateIndexSql(unique);
        Assert.StartsWith("CREATE UNIQUE INDEX `ux_url_records_audit_url` ON `url_records`", uniqueSql, StringComparison.Ordinal);
        Assert.Contains("(`audit_id`, `canonical_url`)", uniqueSql, StringComparison.Ordinal);

        LogicalIndexDefinition descending = MySqlLogicalSchema.RequiredIndexes.Single(i => i.Name == "ix_ngram_site_summaries_audit_zone_order_df");
        string descendingSql = MySqlLogicalSchema.RenderCreateIndexSql(descending);
        Assert.StartsWith("CREATE INDEX", descendingSql, StringComparison.Ordinal);
        Assert.Contains("`document_frequency` DESC", descendingSql, StringComparison.Ordinal);
    }

    /// <summary>Table DDL enforces the §9.1/§2.4 conventions: surrogate key, InnoDB, utf8mb4.</summary>
    [Fact]
    public void Table_Ddl_Rendering()
    {
        string sql = MySqlLogicalSchema.RenderCreateTableSql(
            "url_records",
            new KeyValuePair<string, MySqlLogicalColumnType>[]
            {
                new("id", MySqlLogicalColumnType.SurrogateKey),
                new("audit_id", MySqlLogicalColumnType.ForeignKey),
                new("canonical_url", MySqlLogicalColumnType.Text),
                new("discovered_at", MySqlLogicalColumnType.TimestampUtc),
                new("flags_json", MySqlLogicalColumnType.Json),
            },
            new KeyValuePair<string, string>[] { new("audit_id", "`audits` (`id`)") });

        Assert.Contains("CREATE TABLE IF NOT EXISTS `url_records`", sql, StringComparison.Ordinal);
        Assert.Contains("`id` BIGINT NOT NULL AUTO_INCREMENT", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (`id`)", sql, StringComparison.Ordinal);
        Assert.Contains("`discovered_at` DATETIME(6) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("`flags_json` JSON NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (`audit_id`) REFERENCES `audits` (`id`)", sql, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB", sql, StringComparison.Ordinal);
        Assert.Contains("utf8mb4", sql, StringComparison.Ordinal);
    }

    /// <summary>Table DDL requires the surrogate key first.</summary>
    [Fact]
    public void Table_Ddl_Requires_Surrogate_Key_First()
    {
        Assert.Throws<ArgumentException>(() => MySqlLogicalSchema.RenderCreateTableSql(
            "bad",
            new KeyValuePair<string, MySqlLogicalColumnType>[] { new("name", MySqlLogicalColumnType.Text) }));
    }
}
