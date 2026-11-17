namespace CrawlForge.Data.Migrations.Schema;

/// <summary>Baseline table definitions (FS-CRAWL-001 §9.2, §9.3).</summary>
public static partial class SchemaCatalog
{
    private static IReadOnlyList<TableDefinition> BuildBaselineTables() =>
    [
        // Single-row preferences and defaults; seeded at first run (§9.4).
        // No identity, license, or account columns exist (criterion #5).
        Table("application_settings",
        [
            Col("ui_preferences_json", LogicalType.Json),
            Col("default_crawl_limits_json", LogicalType.Json),
            Col("audit_toggles_json", LogicalType.Json),
            Col("storage_options_json", LogicalType.Json),
            Col("database_provider", LogicalType.Int32),
            Col("database_connection_json", LogicalType.Json, nullable: true),
            Col("theme", LogicalType.Varchar, length: 32),
            Col("locale", LogicalType.Varchar, length: 16),
        ]),

        // Grouping of audits; soft delete with transactional pruning.
        Table("projects",
        [
            Col("name", LogicalType.Varchar, length: 255),
            Col("description", LogicalType.Text, nullable: true),
            Col("tags_json", LogicalType.Json, nullable: true),
            Col("created_at", LogicalType.Timestamp),
            Col("updated_at", LogicalType.Timestamp),
            Col("scope_summary", LogicalType.Text, nullable: true),
            Col("is_deleted", LogicalType.Boolean),
        ]),

        // Reusable input sets; sensitive optional values encrypted at rest.
        Table("crawl_configurations",
        [
            Col("project_id", LogicalType.Int64),
            Col("name", LogicalType.Varchar, length: 255),
            Col("seeds_json", LogicalType.Json),
            Col("scope_rules_json", LogicalType.Json, nullable: true),
            Col("depth", LogicalType.Int32, nullable: true),
            Col("limit_overrides_json", LogicalType.Json, nullable: true),
            Col("rendering_mode", LogicalType.Int32),
            Col("politeness_json", LogicalType.Json, nullable: true),
            Col("proxy_routing_json", LogicalType.Json, nullable: true),
            Col("custom_extraction_rules_json", LogicalType.Json, nullable: true),
            Col("headers_json", LogicalType.Json, nullable: true),
            Col("cookies_json", LogicalType.Json, nullable: true),
            Col("created_at", LogicalType.Timestamp),
            Col("updated_at", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("project_id", "projects")],
        indexes: [Idx("crawl_configurations", Asc("project_id"))]),

        // One crawl-and-audit run; configuration snapshotted at start.
        Table("audits",
        [
            Col("project_id", LogicalType.Int64),
            Col("configuration_id", LogicalType.Int64),
            Col("configuration_snapshot_json", LogicalType.Json),
            Col("status", LogicalType.Int32),
            Col("phase", LogicalType.Int32),
            Col("started_at", LogicalType.Timestamp, nullable: true),
            Col("finished_at", LogicalType.Timestamp, nullable: true),
            Col("counters_json", LogicalType.Json),
            Col("engine_version", LogicalType.Varchar, length: 64),
            Col("is_deleted", LogicalType.Boolean),
        ],
        foreignKeys: [Fk("project_id", "projects"), Fk("configuration_id", "crawl_configurations")],
        indexes: [Idx("audits", Asc("project_id"))]),

        // Per-URL crawl facts; unique per (audit, canonical URL).
        Table("url_records",
        [
            Col("audit_id", LogicalType.Int64),
            Col("canonical_url", LogicalType.Varchar, length: 512),
            Col("host", LogicalType.Varchar, length: 253),
            Col("path", LogicalType.Text),
            Col("depth", LogicalType.Int32),
            Col("discovery_source", LogicalType.Int32),
            Col("status_class", LogicalType.Int32),
            Col("final_status_code", LogicalType.Int32, nullable: true),
            Col("redirect_target_url", LogicalType.Text, nullable: true),
            Col("content_type", LogicalType.Varchar, length: 255, nullable: true),
            Col("size_bytes", LogicalType.Int64, nullable: true),
            Col("timing_json", LogicalType.Json, nullable: true),
            Col("robots_state", LogicalType.Int32),
            Col("indexability_flags", LogicalType.Int32),
            Col("is_indexable", LogicalType.Boolean),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes:
        [
            Ux("url_records", Asc("audit_id"), Asc("canonical_url")),
            Idx("url_records", Asc("audit_id"), Asc("status_class")),
        ]),

        // Extracted DOM inventory; one row per element group instance.
        Table("url_elements",
        [
            Col("url_id", LogicalType.Int64),
            Col("element_group", LogicalType.Varchar, length: 191),
            Col("payload_json", LogicalType.Json),
            Col("ordinal", LogicalType.Int32),
        ],
        foreignKeys: [Fk("url_id", "url_records")],
        indexes: [Idx("url_elements", Asc("url_id"), Asc("element_group"))]),

        // Directed link graph; internal edges reference URL records on both ends.
        Table("link_edges",
        [
            Col("audit_id", LogicalType.Int64),
            Col("source_url_id", LogicalType.Int64),
            Col("canonical_target_url", LogicalType.Text),
            Col("target_url_id", LogicalType.Int64, nullable: true),
            Col("anchor_text", LogicalType.Text, nullable: true),
            Col("relation_flags", LogicalType.Int32),
            Col("is_internal", LogicalType.Boolean),
        ],
        foreignKeys:
        [
            Fk("audit_id", "audits"),
            Fk("source_url_id", "url_records"),
            Fk("target_url_id", "url_records"),
        ],
        indexes:
        [
            Idx("link_edges", Asc("audit_id"), Asc("source_url_id")),
            Idx("link_edges", Asc("audit_id"), Asc("target_url_id")),
        ]),

        // Audit judgments; site-wide findings carry a null URL reference.
        Table("findings",
        [
            Col("audit_id", LogicalType.Int64),
            Col("rule_key", LogicalType.Varchar, length: 191),
            Col("category", LogicalType.Int32),
            Col("severity", LogicalType.Int32),
            Col("url_id", LogicalType.Int64, nullable: true),
            Col("evidence_json", LogicalType.Json),
            Col("recommendation_key", LogicalType.Varchar, length: 191, nullable: true),
            Col("is_suppressed", LogicalType.Boolean),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("findings", Asc("audit_id"), Asc("severity"), Asc("category")),
            Idx("findings", Asc("audit_id"), Asc("rule_key")),
        ]),

        // Precomputed aggregates powering dashboards and charts (§8.1).
        Table("metric_summaries",
        [
            Col("audit_id", LogicalType.Int64),
            Col("metric_key", LogicalType.Varchar, length: 191),
            Col("dimensions_json", LogicalType.Json),
            Col("metric_value", LogicalType.Double),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes: [Idx("metric_summaries", Asc("audit_id"), Asc("metric_key"))]),

        // Resume state written on pause and periodically during runs.
        Table("crawl_checkpoints",
        [
            Col("audit_id", LogicalType.Int64),
            Col("serialized_frontier", LogicalType.Json),
            Col("seen_set_reference", LogicalType.Text, nullable: true),
            Col("counters_json", LogicalType.Json),
            Col("saved_at", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes: [Idx("crawl_checkpoints", Asc("audit_id"))]),

        // Idempotent migration ledger, per provider (§9.2). Bootstrapped by the
        // migrator before version 1 applies; recorded here for full coverage.
        Table(LedgerTableName,
        [
            Col("version", LogicalType.Int32),
            Col("provider_dialect", LogicalType.Varchar, length: 32),
            Col("applied_at", LogicalType.Timestamp),
        ],
        indexes: [Ux(LedgerTableName, Asc("version"), Asc("provider_dialect"))]),

        // User filter presets; local convenience only (optional table).
        Table("saved_views",
        [
            Col("name", LogicalType.Varchar, length: 255),
            Col("target_surface", LogicalType.Varchar, length: 191),
            Col("filter_json", LogicalType.Json),
        ]),

        // Recurring audit rules; local only, none seeded at first run.
        Table("schedule_definitions",
        [
            Col("project_id", LogicalType.Int64),
            Col("configuration_id", LogicalType.Int64),
            Col("recurrence_rule", LogicalType.Varchar, length: 255),
            Col("is_enabled", LogicalType.Boolean),
            Col("limit_overrides_json", LogicalType.Json, nullable: true),
            Col("completion_actions_json", LogicalType.Json, nullable: true),
            Col("next_due_at", LogicalType.Timestamp, nullable: true),
        ],
        foreignKeys: [Fk("project_id", "projects"), Fk("configuration_id", "crawl_configurations")],
        indexes: [Idx("schedule_definitions", Asc("next_due_at"))]),

        // Schedule firing history; pruned by retention.
        Table("schedule_runs",
        [
            Col("schedule_definition_id", LogicalType.Int64),
            Col("audit_id", LogicalType.Int64, nullable: true),
            Col("trigger_type", LogicalType.Int32),
            Col("outcome", LogicalType.Int32),
            Col("started_at", LogicalType.Timestamp),
            Col("finished_at", LogicalType.Timestamp, nullable: true),
        ],
        foreignKeys: [Fk("schedule_definition_id", "schedule_definitions"), Fk("audit_id", "audits")],
        indexes: [Idx("schedule_runs", Asc("schedule_definition_id"), Asc("started_at"))]),

        // User-configured delivery targets; off by default, none seeded.
        Table("export_destinations",
        [
            Col("name", LogicalType.Varchar, length: 255),
            Col("destination_class", LogicalType.Int32),
            Col("endpoint_details_json", LogicalType.Json),
            Col("encrypted_credentials_json", LogicalType.Json, nullable: true),
            Col("is_enabled", LogicalType.Boolean),
        ]),

        // Accepted spellings; empty at first run.
        Table("user_dictionary",
        [
            Col("language", LogicalType.Varchar, length: 16),
            Col("word", LogicalType.Varchar, length: 191),
            Col("added_at", LogicalType.Timestamp),
        ],
        indexes: [Ux("user_dictionary", Asc("language"), Asc("word"))]),

        // Content-store index pointing into the compressed artifact store.
        Table("content_artifacts",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("kind", LogicalType.Int32),
            Col("storage_key", LogicalType.Varchar, length: 512),
            Col("size_bytes", LogicalType.Int64),
            Col("content_hash", LogicalType.Varchar, length: 128),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes: [Idx("content_artifacts", Asc("audit_id"), Asc("url_id"), Asc("kind"))]),

        // Similarity artifacts; activated parent table (FS-CRAWL-002 §12.7),
        // populated by the local embedding pipeline without schema change.
        Table("url_embeddings",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("embedding_payload_json", LogicalType.Json),
            Col("model_tag", LogicalType.Varchar, length: 191),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes: [Idx("url_embeddings", Asc("audit_id"), Asc("url_id"))]),

        // Custom extraction values; one row per captured value.
        Table("extracted_content",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("rule_name", LogicalType.Varchar, length: 191),
            Col("ordinal", LogicalType.Int32),
            Col("typed_value_json", LogicalType.Json, nullable: true),
            Col("value_type", LogicalType.Int32),
            Col("is_invalid", LogicalType.Boolean),
            Col("is_truncated", LogicalType.Boolean),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("extracted_content", Asc("audit_id"), Asc("rule_name")),
            Idx("extracted_content", Asc("audit_id"), Asc("url_id")),
        ]),

        // Custom search matches; per-item aggregates for the overview.
        Table("content_search_results",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("search_item_key", LogicalType.Varchar, length: 191),
            Col("match_count", LogicalType.Int32),
            Col("snippets_json", LogicalType.Json, nullable: true),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes: [Idx("content_search_results", Asc("audit_id"), Asc("url_id"))]),

        // Custom JavaScript snippet outputs; dynamic columns, bounded rows.
        Table("snippet_results",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("snippet_key", LogicalType.Varchar, length: 191),
            Col("columns_json", LogicalType.Json),
            Col("rows_json", LogicalType.Json),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes: [Idx("snippet_results", Asc("audit_id"), Asc("url_id"))]),

        // Detected technologies feeding the technology explorer and aggregate.
        Table("technology_detections",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("technology_key", LogicalType.Varchar, length: 191),
            Col("category", LogicalType.Varchar, length: 191),
            Col("confidence", LogicalType.Double),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("technology_detections", Asc("audit_id"), Asc("url_id")),
            Idx("technology_detections", Asc("audit_id"), Asc("technology_key")),
        ]),

        // Category and overall scores; comparable across audits.
        Table("audit_scores",
        [
            Col("audit_id", LogicalType.Int64),
            Col("category", LogicalType.Int32, nullable: true),
            Col("score", LogicalType.Double),
            Col("error_count", LogicalType.Int32),
            Col("warning_count", LogicalType.Int32),
            Col("notice_count", LogicalType.Int32),
            Col("pass_count", LogicalType.Int32),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes: [Idx("audit_scores", Asc("audit_id"))]),

        // User-suppressed findings/rules; project-scoped and reversible.
        Table("finding_suppressions",
        [
            Col("project_id", LogicalType.Int64),
            Col("suppression_key", LogicalType.Varchar, length: 191),
            Col("rule_key", LogicalType.Varchar, length: 191, nullable: true),
            Col("url_id", LogicalType.Int64, nullable: true),
            Col("finding_scope", LogicalType.Text, nullable: true),
            Col("is_suppressed", LogicalType.Boolean),
            Col("created_at", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("project_id", "projects"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("finding_suppressions", Asc("project_id")),
            Ux("finding_suppressions", Asc("project_id"), Asc("suppression_key")),
        ]),

        // Local agent invocation history; user-viewable and purgeable.
        Table("automation_action_log",
        [
            Col("timestamp", LogicalType.Timestamp),
            Col("transport", LogicalType.Int32),
            Col("target_name", LogicalType.Varchar, length: 255),
            Col("arguments_summary", LogicalType.Text),
            Col("outcome", LogicalType.Int32),
        ],
        indexes: [Idx("automation_action_log", Asc("timestamp"))]),
    ];
}
