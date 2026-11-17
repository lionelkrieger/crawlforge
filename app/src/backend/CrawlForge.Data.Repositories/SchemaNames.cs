using CrawlForge.Contracts.Entities;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Explicit logical table names and column-name overrides for the schema catalog
/// (FS-CRAWL-001 §9.2 + FS-CRAWL-002 Appendix A). Table names are the catalog's
/// irregular plurals and cannot be derived mechanically; a few property names
/// collide with SQL keywords and get safe physical names. Both providers use the
/// identical logical layout (precedence rule 6).
/// </summary>
public static class SchemaNames
{
    private static readonly IReadOnlyDictionary<Type, string> TableNameOverrides =
        new Dictionary<Type, string>
        {
            [typeof(ApplicationSettingsDto)] = "application_settings",
            [typeof(ProjectDto)] = "projects",
            [typeof(CrawlConfigurationDto)] = "crawl_configurations",
            [typeof(AuditDto)] = "audits",
            [typeof(SchemaMigrationDto)] = "schema_migrations",
            [typeof(SavedViewDto)] = "saved_views",
            [typeof(FindingDto)] = "findings",
            [typeof(MetricSummaryDto)] = "metric_summaries",
            [typeof(ExtractedContentDto)] = "extracted_content",
            [typeof(ContentSearchResultDto)] = "content_search_results",
            [typeof(SnippetResultDto)] = "snippet_results",
            [typeof(TechnologyDetectionDto)] = "technology_detections",
            [typeof(AuditScoreDto)] = "audit_scores",
            [typeof(FindingSuppressionDto)] = "finding_suppressions",
            [typeof(UrlRecordDto)] = "url_records",
            [typeof(UrlElementDto)] = "url_elements",
            [typeof(LinkEdgeDto)] = "link_edges",
            [typeof(CrawlCheckpointDto)] = "crawl_checkpoints",
            [typeof(ContentArtifactDto)] = "content_artifacts",
            [typeof(UrlEmbeddingDto)] = "url_embeddings",
            [typeof(ScheduleDefinitionDto)] = "schedule_definitions",
            [typeof(ScheduleRunDto)] = "schedule_runs",
            [typeof(ExportDestinationDto)] = "export_destinations",
            [typeof(UserDictionaryEntryDto)] = "user_dictionary",
            [typeof(AutomationActionLogDto)] = "automation_action_log",
            [typeof(ContentScopeProfileDto)] = "content_scope_profiles",
            [typeof(NgramFrequencyDto)] = "ngram_frequencies",
            [typeof(NgramSiteSummaryDto)] = "ngram_site_summaries",
            [typeof(UserStopwordDto)] = "user_stopwords",
            [typeof(UiPluginDto)] = "ui_plugins",
            [typeof(RedirectMappingDto)] = "redirect_mappings",
            [typeof(LogImportDto)] = "log_imports",
            [typeof(LogUrlAggregateDto)] = "log_url_aggregates",
            [typeof(SemanticLinkOpportunityDto)] = "semantic_link_opportunities",
            [typeof(SemanticClusterDto)] = "semantic_clusters",
            [typeof(GovernanceMatrixRowDto)] = "governance_matrix",
            [typeof(AnalyticsDetectionDto)] = "analytics_detections",
            [typeof(WafDetectionDto)] = "waf_detections",
            [typeof(WafUserSignatureDto)] = "waf_user_signatures",
            [typeof(HostThrottleEventDto)] = "host_throttle_events"
        };

    private static readonly IReadOnlyDictionary<(Type Entity, string Property), string> ColumnNameOverrides =
        new Dictionary<(Type, string), string>
        {
            // "order", "key", "class", and "count" collide with SQL keywords or
            // function names on one of the providers; use unambiguous physical names.
            [(typeof(NgramFrequencyDto), nameof(NgramFrequencyDto.Order))] = "ngram_order",
            [(typeof(NgramSiteSummaryDto), nameof(NgramSiteSummaryDto.Order))] = "ngram_order",
            [(typeof(WafUserSignatureDto), nameof(WafUserSignatureDto.Key))] = "signature_key",
            [(typeof(ExportDestinationDto), nameof(ExportDestinationDto.Class))] = "destination_class",
            [(typeof(NgramFrequencyDto), nameof(NgramFrequencyDto.Count))] = "term_count",

            // Complex-type payloads: the DTO property names omit the catalog's
            // _json suffix (WP-01-04 reopen sweep — the catalog is the schema
            // authority; every entry below matches a physical catalog column).
            [(typeof(ApplicationSettingsDto), nameof(ApplicationSettingsDto.DefaultCrawlLimits))] = "default_crawl_limits_json",
            [(typeof(ApplicationSettingsDto), nameof(ApplicationSettingsDto.AimdDefaults))] = "aimd_defaults_json",
            [(typeof(ApplicationSettingsDto), nameof(ApplicationSettingsDto.EmbeddingProvider))] = "embedding_provider_json",
            [(typeof(ApplicationSettingsDto), nameof(ApplicationSettingsDto.SerpBudgetDefaults))] = "serp_budget_defaults_json",
            [(typeof(ProjectDto), nameof(ProjectDto.ContentScope))] = "content_scope_json",
            [(typeof(ProjectDto), nameof(ProjectDto.SerpBudgets))] = "serp_budgets_json",
            [(typeof(CrawlConfigurationDto), nameof(CrawlConfigurationDto.LimitOverrides))] = "limit_overrides_json",
            [(typeof(CrawlConfigurationDto), nameof(CrawlConfigurationDto.ContentScope))] = "content_scope_json",
            [(typeof(CrawlConfigurationDto), nameof(CrawlConfigurationDto.SerpBudgets))] = "serp_budgets_json",
            [(typeof(ScheduleDefinitionDto), nameof(ScheduleDefinitionDto.LimitOverrides))] = "limit_overrides_json",

            // "value" is too generic for the catalog's metric column.
            [(typeof(MetricSummaryDto), nameof(MetricSummaryDto.Value))] = "metric_value"
        };

    /// <summary>Resolves the logical table name for a DTO type.</summary>
    /// <param name="entityType">The DTO record type.</param>
    /// <returns>The catalog table name.</returns>
    /// <exception cref="InvalidOperationException">The entity is not part of the catalog.</exception>
    public static string TableName(Type entityType) =>
        TableNameOverrides.TryGetValue(entityType, out var name)
            ? name
            : throw new InvalidOperationException($"{entityType.Name} is not registered in the schema catalog.");

    /// <summary>Resolves the physical column name for a DTO property.</summary>
    /// <param name="entityType">The DTO record type.</param>
    /// <param name="propertyName">The PascalCase property name.</param>
    /// <returns>The physical column name.</returns>
    public static string ColumnName(Type entityType, string propertyName) =>
        ColumnNameOverrides.TryGetValue((entityType, propertyName), out var name)
            ? name
            : EntityMap.ToColumnName(propertyName);

    /// <summary>All registered entity types in catalog order.</summary>
    public static IReadOnlyList<Type> AllEntities { get; } = TableNameOverrides.Keys.ToList();
}
