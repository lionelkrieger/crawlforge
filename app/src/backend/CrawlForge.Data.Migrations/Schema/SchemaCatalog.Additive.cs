namespace CrawlForge.Data.Migrations.Schema;

/// <summary>
/// Additive schema objects (FS-CRAWL-002 Appendix A): 14 additive tables, the
/// materialized <c>ngram_site_summaries</c> read model, and the additive columns
/// on the parent tables.
/// </summary>
public static partial class SchemaCatalog
{
    private static IReadOnlyList<TableDefinition> BuildAdditiveTables() =>
    [
        // §2.6: per-URL WAF challenge detections (detection and reporting only).
        Table("waf_detections",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("provider", LogicalType.Int32),
            Col("challenge_class", LogicalType.Int32),
            Col("evidence_json", LogicalType.Json),
            Col("signature_key", LogicalType.Varchar, length: 191, nullable: true),
            Col("resolved_by_rendering", LogicalType.Boolean),
            Col("detected_at", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("waf_detections", Asc("audit_id"), Asc("provider")),
            Ux("waf_detections", Asc("audit_id"), Asc("url_id")),
        ]),

        // §2.6: user-local signature catalogue entries (bundled assets + local additions).
        Table("waf_user_signatures",
        [
            Col("signature_key", LogicalType.Varchar, length: 191),
            Col("provider", LogicalType.Int32),
            Col("challenge_class", LogicalType.Int32),
            Col("header_markers_json", LogicalType.Json),
            Col("body_markers_json", LogicalType.Json),
            Col("is_enabled", LogicalType.Boolean),
            Col("created_at", LogicalType.Timestamp),
            Col("updated_at", LogicalType.Timestamp),
        ],
        indexes: [Ux("waf_user_signatures", Asc("signature_key"))]),

        // §3.6: bounded AIMD adjustment history per (audit, host).
        Table("host_throttle_events",
        [
            Col("audit_id", LogicalType.Int64),
            Col("host", LogicalType.Varchar, length: 253),
            Col("previous_rate", LogicalType.Double),
            Col("new_rate", LogicalType.Double),
            Col("reason", LogicalType.Varchar, length: 191),
            Col("timestamp", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes: [Idx("host_throttle_events", Asc("audit_id"), Asc("host"), Asc("timestamp"))]),

        // §4.4: reusable project-level include/exclude content scopes.
        Table("content_scope_profiles",
        [
            Col("project_id", LogicalType.Int64),
            Col("name", LogicalType.Varchar, length: 255),
            Col("include_selectors_json", LogicalType.Json),
            Col("exclude_selectors_json", LogicalType.Json),
            Col("created_at", LogicalType.Timestamp),
            Col("updated_at", LogicalType.Timestamp),
        ],
        foreignKeys: [Fk("project_id", "projects")],
        indexes: [Idx("content_scope_profiles", Asc("project_id"))]),

        // §5.6: per-URL, per-zone, per-order n-gram counts and TF.
        Table("ngram_frequencies",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("zone", LogicalType.Int32),
            Col("ngram_order", LogicalType.Int32),
            Col("term", LogicalType.Varchar, length: 256),
            Col("term_count", LogicalType.Int64),
            Col("term_frequency", LogicalType.Double),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Ux("ngram_frequencies", Asc("audit_id"), Asc("url_id"), Asc("zone"), Asc("ngram_order"), Asc("term")),
            Idx("ngram_frequencies", Asc("audit_id"), Asc("zone"), Asc("ngram_order"), Asc("term")),
            Idx("ngram_frequencies", Asc("audit_id"), Asc("url_id")),
        ]),

        // §5.6: per-language user stop-word additions (local only).
        Table("user_stopwords",
        [
            Col("language", LogicalType.Varchar, length: 16),
            Col("word", LogicalType.Varchar, length: 191),
            Col("added_at", LogicalType.Timestamp),
        ],
        indexes: [Ux("user_stopwords", Asc("language"), Asc("word"))]),

        // §6.7: local plugin registry with read-only grants and integrity hashes.
        Table("ui_plugins",
        [
            Col("name", LogicalType.Varchar, length: 255),
            Col("directory_path", LogicalType.Text),
            Col("version", LogicalType.Varchar, length: 64),
            Col("is_enabled", LogicalType.Boolean),
            Col("grants", LogicalType.Int32),
            Col("integrity_hash", LogicalType.Varchar, length: 128),
            Col("load_state", LogicalType.Varchar, length: 64, nullable: true),
            Col("registered_at", LogicalType.Timestamp),
        ],
        indexes: [Ux("ui_plugins", Asc("name"))]),

        // §10.6: migration matrix rows (source URL, expected target URL) per audit.
        Table("redirect_mappings",
        [
            Col("audit_id", LogicalType.Int64),
            Col("source_url", LogicalType.Varchar, length: 512),
            Col("expected_target_url", LogicalType.Text),
            Col("ordinal", LogicalType.Int32),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes:
        [
            Ux("redirect_mappings", Asc("audit_id"), Asc("source_url")),
            Idx("redirect_mappings", Asc("audit_id"), Asc("ordinal")),
        ]),

        // §11.6: log import records (offline ingestion from local disk only).
        Table("log_imports",
        [
            Col("project_id", LogicalType.Int64),
            Col("format", LogicalType.Int32),
            Col("window_start", LogicalType.Timestamp),
            Col("window_end", LogicalType.Timestamp),
            Col("counters_json", LogicalType.Json),
            Col("verification_mode", LogicalType.Int32),
            Col("status", LogicalType.Int32),
            Col("imported_at", LogicalType.Timestamp),
            Col("source_file_path", LogicalType.Text, nullable: true),
        ],
        foreignKeys: [Fk("project_id", "projects")],
        indexes: [Idx("log_imports", Asc("project_id"), Asc("imported_at"))]),

        // §11.6: per-(import, canonical URL) hit aggregates by bot family and status.
        Table("log_url_aggregates",
        [
            Col("log_import_id", LogicalType.Int64),
            Col("canonical_url", LogicalType.Varchar, length: 512),
            Col("host", LogicalType.Varchar, length: 253),
            Col("total_hits", LogicalType.Int64),
            Col("search_bot_hits", LogicalType.Int64),
            Col("bot_family_breakdown_json", LogicalType.Json, nullable: true),
            Col("status_breakdown_json", LogicalType.Json, nullable: true),
        ],
        foreignKeys: [Fk("log_import_id", "log_imports")],
        indexes:
        [
            Ux("log_url_aggregates", Asc("log_import_id"), Asc("canonical_url")),
            Idx("log_url_aggregates", Asc("log_import_id"), Asc("host")),
            Idx("log_url_aggregates", Asc("log_import_id"), Desc("search_bot_hits")),
        ]),

        // §12.7: bounded top-K semantic link recommendations with anchor candidates.
        Table("semantic_link_opportunities",
        [
            Col("audit_id", LogicalType.Int64),
            Col("source_url_id", LogicalType.Int64),
            Col("target_url_id", LogicalType.Int64),
            Col("similarity", LogicalType.Double),
            Col("anchor_candidates_json", LogicalType.Json, nullable: true),
        ],
        foreignKeys:
        [
            Fk("audit_id", "audits"),
            Fk("source_url_id", "url_records"),
            Fk("target_url_id", "url_records"),
        ],
        indexes:
        [
            // Explicit short name (DEFECT-11/D6): the synthesized name is 67
            // chars (> MySQL's 64 limit). Aligned with MySqlLogicalSchema.
            Ux("ux_semantic_link_opportunities_audit_source_target", "semantic_link_opportunities", Asc("audit_id"), Asc("source_url_id"), Asc("target_url_id")),
            Idx("semantic_link_opportunities", Asc("audit_id"), Asc("source_url_id")),
            Idx("semantic_link_opportunities", Asc("audit_id"), Desc("similarity")),
        ]),

        // §12.7: semantic cannibalization cluster membership and centroid similarity.
        Table("semantic_clusters",
        [
            Col("audit_id", LogicalType.Int64),
            Col("cluster_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("centroid_similarity", LogicalType.Double),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("semantic_clusters", Asc("audit_id"), Asc("cluster_id")),
            Ux("semantic_clusters", Asc("audit_id"), Asc("cluster_id"), Asc("url_id")),
        ]),

        // §13.4: agent x scope x purpose allow/deny verdicts with evidence.
        Table("governance_matrix",
        [
            Col("audit_id", LogicalType.Int64),
            Col("agent_name", LogicalType.Varchar, length: 191),
            Col("scope_value", LogicalType.Varchar, length: 191),
            Col("purpose", LogicalType.Int32),
            Col("verdict", LogicalType.Int32),
            Col("evidence_json", LogicalType.Json),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes:
        [
            Idx("governance_matrix", Asc("audit_id"), Asc("agent_name")),
            Idx("governance_matrix", Asc("audit_id"), Asc("scope_value")),
        ]),

        // §14.4: per-URL tag/container detections (vendor, ID, source, placement).
        Table("analytics_detections",
        [
            Col("audit_id", LogicalType.Int64),
            Col("url_id", LogicalType.Int64),
            Col("vendor", LogicalType.Varchar, length: 191),
            Col("container_id", LogicalType.Varchar, length: 191, nullable: true),
            Col("detection_source", LogicalType.Varchar, length: 32),
            Col("placement", LogicalType.Varchar, length: 191, nullable: true),
        ],
        foreignKeys: [Fk("audit_id", "audits"), Fk("url_id", "url_records")],
        indexes:
        [
            Idx("analytics_detections", Asc("audit_id"), Asc("vendor")),
            Idx("analytics_detections", Asc("audit_id"), Asc("container_id")),
        ]),
    ];

    private static IReadOnlyList<TableDefinition> BuildReadModelTables() =>
    [
        // §5.6 aggregate view, materialized as a precomputed read model on every
        // provider (§8.1 pattern; §1.4 rule 6): site-wide DF, total frequency,
        // mean TF, and primary-term counts per (audit, zone, order, term).
        // Maintained by the aggregation write path; reads are indexed lookups.
        Table("ngram_site_summaries",
        [
            Col("audit_id", LogicalType.Int64),
            Col("zone", LogicalType.Int32),
            Col("ngram_order", LogicalType.Int32),
            Col("term", LogicalType.Varchar, length: 256),
            Col("document_frequency", LogicalType.Int64),
            Col("total_frequency", LogicalType.Int64),
            Col("mean_term_frequency", LogicalType.Double),
            Col("primary_term_count", LogicalType.Int64),
        ],
        foreignKeys: [Fk("audit_id", "audits")],
        indexes:
        [
            Ux("ngram_site_summaries", Asc("audit_id"), Asc("zone"), Asc("ngram_order"), Asc("term")),
            // Explicit short name (DEFECT-11/D6): the synthesized name is 68
            // chars (> MySQL's 64 limit). Aligned with MySqlLogicalSchema and
            // MySqlMaterializedReadModels.
            Idx("ix_ngram_site_summaries_audit_zone_order_df", "ngram_site_summaries", Asc("audit_id"), Asc("zone"), Asc("ngram_order"), Desc("document_frequency")),
        ]),
    ];

    private static IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>> BuildAdditiveColumns() =>
        new Dictionary<string, IReadOnlyList<ColumnDefinition>>(StringComparer.Ordinal)
        {
            // §2.6 / §10.6: block reason, WAF provider, challenge class. The
            // discovery-source vocabulary gains `list` as an enum value on the
            // existing discovery_source column (no schema change required).
            ["url_records"] =
            [
                Col("block_reason", LogicalType.Text, nullable: true),
                Col("waf_provider", LogicalType.Int32, nullable: true),
                Col("challenge_class", LogicalType.Int32, nullable: true),
            ],

            // §15.5: six-zone DOM classification with zone evidence; weighted equity.
            ["link_edges"] =
            [
                Col("dom_zone", LogicalType.Int32, nullable: true),
                Col("zone_evidence", LogicalType.Text, nullable: true),
                Col("weighted_equity", LogicalType.Double, nullable: true),
            ],

            // §2.6, §4.4, §10.6, §12.7, §13.4: additive audit snapshot fields.
            ["audits"] =
            [
                Col("waf_catalogue_version", LogicalType.Varchar, length: 64, nullable: true),
                Col("waf_catalogue_revision", LogicalType.Varchar, length: 64, nullable: true),
                Col("detection_policy_json", LogicalType.Json, nullable: true),
                Col("resolved_content_scope_json", LogicalType.Json, nullable: true),
                Col("is_list_mode", LogicalType.Boolean, nullable: true),
                Col("url_list_json", LogicalType.Json, nullable: true),
                Col("rewrite_rules_json", LogicalType.Json, nullable: true),
                Col("host_mapping_json", LogicalType.Json, nullable: true),
                Col("redirect_matrix_json", LogicalType.Json, nullable: true),
                Col("embedding_provider", LogicalType.Int32, nullable: true),
                Col("embedding_model_tag", LogicalType.Varchar, length: 191, nullable: true),
                Col("embedding_similarity_threshold", LogicalType.Double, nullable: true),
                Col("agent_catalogue_version", LogicalType.Varchar, length: 64, nullable: true),
                Col("geo_toggles_json", LogicalType.Json, nullable: true),
            ],

            // §4.4, §10.6, §14.4, §15.5: additive configuration fields. AIMD and
            // WAF settings ride the existing politeness/limit-override payloads
            // (§5.1), so no dedicated columns are added for them here.
            ["crawl_configurations"] =
            [
                Col("content_scope_json", LogicalType.Json, nullable: true),
                Col("content_scope_profile_id", LogicalType.Int64, nullable: true),
                Col("is_list_mode", LogicalType.Boolean, nullable: true),
                Col("url_list_json", LogicalType.Json, nullable: true),
                Col("rewrite_rules_json", LogicalType.Json, nullable: true),
                Col("host_mapping_json", LogicalType.Json, nullable: true),
                Col("consent_mode_enabled", LogicalType.Boolean, nullable: true),
                Col("primary_container_override", LogicalType.Varchar, length: 191, nullable: true),
                Col("data_layer_schema_json", LogicalType.Json, nullable: true),
                Col("beacon_patterns_json", LogicalType.Json, nullable: true),
                Col("zone_weights_json", LogicalType.Json, nullable: true),
                Col("serp_budgets_json", LogicalType.Json, nullable: true),
            ],

            // §3.6: serialized per-host AIMD controller state.
            ["crawl_checkpoints"] =
            [
                Col("aimd_state_json", LogicalType.Json, nullable: true),
            ],

            // §2, §3, §6, §7, §11.3, §12.2, §15.3: additive preference fields.
            ["application_settings"] =
            [
                Col("aimd_defaults_json", LogicalType.Json, nullable: true),
                Col("waf_detection_enabled", LogicalType.Boolean, nullable: true),
                Col("status_bar_thresholds_json", LogicalType.Json, nullable: true),
                Col("plugin_preferences_json", LogicalType.Json, nullable: true),
                Col("embedding_provider_json", LogicalType.Json, nullable: true),
                Col("user_bot_cidr_additions_json", LogicalType.Json, nullable: true),
                Col("serp_budget_defaults_json", LogicalType.Json, nullable: true),
            ],

            // Project-level counterparts of the additive configuration fields
            // (locked ProjectDto additive members; §4.4, §10.6, §14.4, §15.5).
            ["projects"] =
            [
                Col("content_scope_json", LogicalType.Json, nullable: true),
                Col("rewrite_rules_json", LogicalType.Json, nullable: true),
                Col("host_mapping_json", LogicalType.Json, nullable: true),
                Col("consent_mode_enabled", LogicalType.Boolean, nullable: true),
                Col("beacon_patterns_json", LogicalType.Json, nullable: true),
                Col("zone_weights_json", LogicalType.Json, nullable: true),
                Col("serp_budgets_json", LogicalType.Json, nullable: true),
            ],
        };
}
