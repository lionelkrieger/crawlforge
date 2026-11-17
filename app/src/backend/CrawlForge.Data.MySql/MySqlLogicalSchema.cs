namespace CrawlForge.Data.MySql;

/// <summary>Kind of a logical schema object (FS-CRAWL-001 §9.2, FS-CRAWL-002 Appendix A).</summary>
public enum LogicalObjectKind
{
    /// <summary>Plain logical table.</summary>
    Table = 0,

    /// <summary>
    /// Aggregate view materialized as a precomputed read model (precedence rule 6,
    /// §8.1 pattern): the provider refreshes it transactionally instead of relying
    /// on a native view, because native views cannot carry the required indexes.
    /// </summary>
    MaterializedReadModel = 1,
}

/// <summary>One column of a logical index, with optional descending order (MySQL 8.0 native).</summary>
/// <param name="Name">Logical column name.</param>
/// <param name="Descending">Whether the column is ordered descending.</param>
public sealed record LogicalIndexColumn(string Name, bool Descending = false);

/// <summary>
/// A required logical index (FS-CRAWL-001 §9.3 and FS-CRAWL-002 Appendix A). Both
/// providers implement every entry; the MySQL dialect renders descending columns
/// natively (SQLite accepts the same logical definition).
/// </summary>
/// <param name="Name">Index name.</param>
/// <param name="Table">Logical table the index belongs to.</param>
/// <param name="Columns">Ordered index columns.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
public sealed record LogicalIndexDefinition(
    string Name,
    string Table,
    IReadOnlyList<LogicalIndexColumn> Columns,
    bool IsUnique);

/// <summary>A logical schema object: one of the 40 catalog objects both providers implement.</summary>
/// <param name="Name">Logical object name.</param>
/// <param name="Kind">Object kind.</param>
public sealed record LogicalSchemaObject(string Name, LogicalObjectKind Kind);

/// <summary>
/// The provider-neutral logical schema catalog rendered for MySQL (FS-CRAWL-001
/// §9.2/§9.3 plus FS-CRAWL-002 Appendix A; precedence rule 6). Every object is
/// logical: the embedded provider implements the identical set. This catalog is the
/// MySQL provider's conformance surface — the versioned migration runner composes
/// these objects into its dialect-specific migration path.
/// </summary>
/// <remarks>
/// Column names used in indexes are the canonical logical snake_case names shared
/// by both providers (surrogate <c>id</c>, references <c>*_id</c>, UTC timestamps
/// <c>*_at</c>, JSON payloads <c>*_json</c>).
/// </remarks>
public static class MySqlLogicalSchema
{
    /// <summary>
    /// All 40 logical objects: the 25 base tables (§9.2) and the 15 additive objects
    /// (Appendix A). <c>ngram_site_summaries</c> is a materialized read model, not a
    /// native view (rule 6).
    /// </summary>
    public static IReadOnlyList<LogicalSchemaObject> Objects { get; } =
    [
        new("application_settings", LogicalObjectKind.Table),
        new("projects", LogicalObjectKind.Table),
        new("crawl_configurations", LogicalObjectKind.Table),
        new("audits", LogicalObjectKind.Table),
        new("url_records", LogicalObjectKind.Table),
        new("url_elements", LogicalObjectKind.Table),
        new("link_edges", LogicalObjectKind.Table),
        new("findings", LogicalObjectKind.Table),
        new("metric_summaries", LogicalObjectKind.Table),
        new("crawl_checkpoints", LogicalObjectKind.Table),
        new("schema_migrations", LogicalObjectKind.Table),
        new("saved_views", LogicalObjectKind.Table),
        new("schedule_definitions", LogicalObjectKind.Table),
        new("schedule_runs", LogicalObjectKind.Table),
        new("export_destinations", LogicalObjectKind.Table),
        new("user_dictionary", LogicalObjectKind.Table),
        new("content_artifacts", LogicalObjectKind.Table),
        new("url_embeddings", LogicalObjectKind.Table),
        new("extracted_content", LogicalObjectKind.Table),
        new("content_search_results", LogicalObjectKind.Table),
        new("snippet_results", LogicalObjectKind.Table),
        new("technology_detections", LogicalObjectKind.Table),
        new("audit_scores", LogicalObjectKind.Table),
        new("finding_suppressions", LogicalObjectKind.Table),
        new("automation_action_log", LogicalObjectKind.Table),
        new("waf_detections", LogicalObjectKind.Table),
        new("waf_user_signatures", LogicalObjectKind.Table),
        new("host_throttle_events", LogicalObjectKind.Table),
        new("content_scope_profiles", LogicalObjectKind.Table),
        new("ngram_frequencies", LogicalObjectKind.Table),
        new("ngram_site_summaries", LogicalObjectKind.MaterializedReadModel),
        new("user_stopwords", LogicalObjectKind.Table),
        new("ui_plugins", LogicalObjectKind.Table),
        new("redirect_mappings", LogicalObjectKind.Table),
        new("log_imports", LogicalObjectKind.Table),
        new("log_url_aggregates", LogicalObjectKind.Table),
        new("semantic_link_opportunities", LogicalObjectKind.Table),
        new("semantic_clusters", LogicalObjectKind.Table),
        new("governance_matrix", LogicalObjectKind.Table),
        new("analytics_detections", LogicalObjectKind.Table),
    ];

    /// <summary>
    /// Every required index: the 13 parent indexes (§9.3) and the 22 additive
    /// indexes (Appendix A). Parent indexes are unchanged by the extension catalog.
    /// </summary>
    public static IReadOnlyList<LogicalIndexDefinition> RequiredIndexes { get; } =
    [
        // §9.3 parent indexes.
        new("ux_url_records_audit_url", "url_records", [new("audit_id"), new("canonical_url")], IsUnique: true),
        new("ix_url_records_audit_status", "url_records", [new("audit_id"), new("status_class")], IsUnique: false),
        new("ix_findings_audit_severity_category", "findings", [new("audit_id"), new("severity"), new("category")], IsUnique: false),
        new("ix_findings_audit_rule", "findings", [new("audit_id"), new("rule_key")], IsUnique: false),
        new("ix_link_edges_audit_source", "link_edges", [new("audit_id"), new("source_url_id")], IsUnique: false),
        new("ix_link_edges_audit_target", "link_edges", [new("audit_id"), new("target_url_id")], IsUnique: false),
        new("ix_metric_summaries_audit_metric", "metric_summaries", [new("audit_id"), new("metric_key")], IsUnique: false),
        new("ix_schedule_definitions_next_due", "schedule_definitions", [new("next_due_at")], IsUnique: false),
        new("ix_schedule_runs_schedule_started", "schedule_runs", [new("schedule_id"), new("started_at")], IsUnique: false),
        new("ix_content_artifacts_audit_url_kind", "content_artifacts", [new("audit_id"), new("url_id"), new("artifact_kind")], IsUnique: false),
        new("ix_extracted_content_audit_rule", "extracted_content", [new("audit_id"), new("rule_name")], IsUnique: false),
        new("ix_extracted_content_audit_url", "extracted_content", [new("audit_id"), new("url_id")], IsUnique: false),
        new("ix_automation_action_log_timestamp", "automation_action_log", [new("created_at")], IsUnique: false),

        // Appendix A additive indexes.
        new("ix_waf_detections_audit_provider", "waf_detections", [new("audit_id"), new("provider")], IsUnique: false),
        new("ux_waf_detections_audit_url", "waf_detections", [new("audit_id"), new("url_id")], IsUnique: true),
        new("ix_host_throttle_events_audit_host_ts", "host_throttle_events", [new("audit_id"), new("host"), new("occurred_at")], IsUnique: false),
        new("ux_ngram_frequencies_audit_url_zone_order_term", "ngram_frequencies", [new("audit_id"), new("url_id"), new("zone"), new("ngram_order"), new("term")], IsUnique: true),
        new("ix_ngram_frequencies_audit_zone_order_term", "ngram_frequencies", [new("audit_id"), new("zone"), new("ngram_order"), new("term")], IsUnique: false),
        new("ix_ngram_frequencies_audit_url", "ngram_frequencies", [new("audit_id"), new("url_id")], IsUnique: false),
        new("ix_ngram_site_summaries_audit_zone_order_df", "ngram_site_summaries", [new("audit_id"), new("zone"), new("ngram_order"), new("document_frequency", Descending: true)], IsUnique: false),
        new("ux_redirect_mappings_audit_source", "redirect_mappings", [new("audit_id"), new("source_url")], IsUnique: true),
        new("ix_redirect_mappings_audit_ordinal", "redirect_mappings", [new("audit_id"), new("ordinal")], IsUnique: false),
        new("ix_log_imports_project_imported", "log_imports", [new("project_id"), new("imported_at")], IsUnique: false),
        new("ux_log_url_aggregates_import_url", "log_url_aggregates", [new("import_id"), new("canonical_url")], IsUnique: true),
        new("ix_log_url_aggregates_import_host", "log_url_aggregates", [new("import_id"), new("host")], IsUnique: false),
        new("ix_log_url_aggregates_import_bot_hits", "log_url_aggregates", [new("import_id"), new("search_bot_hits", Descending: true)], IsUnique: false),
        new("ux_semantic_link_opportunities_audit_source_target", "semantic_link_opportunities", [new("audit_id"), new("source_url_id"), new("target_url_id")], IsUnique: true),
        new("ix_semantic_link_opportunities_audit_source", "semantic_link_opportunities", [new("audit_id"), new("source_url_id")], IsUnique: false),
        new("ix_semantic_link_opportunities_audit_similarity", "semantic_link_opportunities", [new("audit_id"), new("similarity", Descending: true)], IsUnique: false),
        new("ix_semantic_clusters_audit_cluster", "semantic_clusters", [new("audit_id"), new("cluster_id")], IsUnique: false),
        new("ix_governance_matrix_audit_agent", "governance_matrix", [new("audit_id"), new("agent_key")], IsUnique: false),
        new("ix_governance_matrix_audit_scope", "governance_matrix", [new("audit_id"), new("scope_value")], IsUnique: false),
        new("ix_analytics_detections_audit_vendor", "analytics_detections", [new("audit_id"), new("vendor")], IsUnique: false),
        new("ix_analytics_detections_audit_container", "analytics_detections", [new("audit_id"), new("container_id")], IsUnique: false),
        new("ix_link_edges_audit_dom_zone", "link_edges", [new("audit_id"), new("dom_zone")], IsUnique: false),
    ];

    /// <summary>
    /// Renders CREATE INDEX for a logical index in the MySQL dialect. Descending
    /// columns are rendered natively (MySQL 8.0); uniqueness becomes UNIQUE KEY.
    /// </summary>
    /// <param name="index">Logical index definition.</param>
    /// <returns>The DDL statement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public static string RenderCreateIndexSql(LogicalIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);

        string columns = string.Join(
            ", ",
            index.Columns.Select(c => MySqlDialect.QuoteIdentifier(c.Name) + (c.Descending ? " DESC" : string.Empty)));
        string unique = index.IsUnique ? "UNIQUE " : string.Empty;
        return $"CREATE {unique}INDEX {MySqlDialect.QuoteIdentifier(index.Name)} ON {MySqlDialect.QuoteIdentifier(index.Table)} ({columns})";
    }

    /// <summary>
    /// Renders CREATE TABLE for a logical table in the MySQL dialect: surrogate
    /// auto-increment primary key, InnoDB engine (foreign keys enforced, native
    /// transactional durability — §2.4), utf8mb4 storage, and optional foreign-key
    /// constraints. Multi-row writes against these tables run inside transactions.
    /// </summary>
    /// <param name="table">Logical table name.</param>
    /// <param name="columns">Ordered columns; the first must be the surrogate key.</param>
    /// <param name="foreignKeys">Optional foreign-key constraints.</param>
    /// <returns>The DDL statement.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string RenderCreateTableSql(
        string table,
        IReadOnlyList<KeyValuePair<string, MySqlLogicalColumnType>> columns,
        IReadOnlyList<KeyValuePair<string, string>>? foreignKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0 || columns[0].Value != MySqlLogicalColumnType.SurrogateKey)
        {
            throw new ArgumentException("The first column must be the surrogate key.", nameof(columns));
        }

        var body = new List<string>(columns.Count + 2);
        foreach ((string name, MySqlLogicalColumnType type) in columns)
        {
            body.Add($"  {MySqlDialect.QuoteIdentifier(name)} {MySqlDialect.MapColumnType(type)}");
        }

        body.Add($"  PRIMARY KEY ({MySqlDialect.QuoteIdentifier(columns[0].Key)})");

        if (foreignKeys is not null)
        {
            foreach ((string column, string reference) in foreignKeys)
            {
                string constraintName = "fk_" + table + "_" + column;
                body.Add($"  CONSTRAINT {MySqlDialect.QuoteIdentifier(constraintName)} FOREIGN KEY ({MySqlDialect.QuoteIdentifier(column)}) REFERENCES {reference}");
            }
        }

        return $"CREATE TABLE IF NOT EXISTS {MySqlDialect.QuoteIdentifier(table)} (\n"
            + string.Join(",\n", body)
            + "\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci";
    }
}
