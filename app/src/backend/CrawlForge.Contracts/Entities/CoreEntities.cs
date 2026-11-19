using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Single-row preferences and defaults (schema catalog: application_settings).
/// Seeded at first run with deterministic defaults: local file-based provider,
/// unlimited crawl ceilings, all audit categories enabled, local-only toggles
/// on, default politeness on. No identity, license, or account columns exist
/// (criterion #5). Stored credentials are encrypted at rest.
/// </summary>
/// <param name="Id">Surrogate key (single row).</param>
/// <param name="UiPreferencesJson">UI preferences payload.</param>
/// <param name="DefaultCrawlLimits">Application-level default crawl limits.</param>
/// <param name="AuditTogglesJson">Per-category audit enablement payload.</param>
/// <param name="StorageOptionsJson">Storage options payload.</param>
/// <param name="DatabaseProvider">Selected database provider.</param>
/// <param name="DatabaseConnectionJson">Connection settings payload; credentials encrypted at rest.</param>
/// <param name="Theme">UI theme identifier.</param>
/// <param name="Locale">UI locale tag.</param>
public sealed record ApplicationSettingsDto(
    long Id,
    string UiPreferencesJson,
    CrawlLimitOverrides DefaultCrawlLimits,
    string AuditTogglesJson,
    string StorageOptionsJson,
    DatabaseProviderKind DatabaseProvider,
    string? DatabaseConnectionJson,
    string Theme,
    string Locale)
{
    /// <summary>Additive: application-level AIMD defaults (FS-CRAWL-002 §3).</summary>
    [AdditiveMember]
    public AimdSettings? AimdDefaults { get; init; }

    /// <summary>Additive: WAF detection toggle; null means enabled (FS-CRAWL-002 §2).</summary>
    [AdditiveMember]
    public bool? WafDetectionEnabled { get; init; }

    /// <summary>Additive: status-bar threshold preferences (FS-CRAWL-002 §7).</summary>
    [AdditiveMember]
    public string? StatusBarThresholdsJson { get; init; }

    /// <summary>Additive: local plugin preferences (FS-CRAWL-002 §6).</summary>
    [AdditiveMember]
    public string? PluginPreferencesJson { get; init; }

    /// <summary>Additive: embedding provider configuration (FS-CRAWL-002 §12.2).</summary>
    [AdditiveMember]
    public EmbeddingProviderSettings? EmbeddingProvider { get; init; }

    /// <summary>Additive: user-local bot CIDR additions (FS-CRAWL-002 §11.3).</summary>
    [AdditiveMember]
    public string? UserBotCidrAdditionsJson { get; init; }

    /// <summary>Additive: SERP pixel budget defaults (FS-CRAWL-002 §15.3).</summary>
    [AdditiveMember]
    public SerpBudgetSettings? SerpBudgetDefaults { get; init; }
}

/// <summary>
/// Grouping of audits (schema catalog: projects). Supports soft delete with
/// transactional pruning.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Name">Project name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="TagsJson">User tags/labels payload.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
/// <param name="ScopeSummary">Optional scope summary.</param>
/// <param name="IsDeleted">Soft-delete marker.</param>
public sealed record ProjectDto(
    long Id,
    string Name,
    string? Description,
    string? TagsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ScopeSummary,
    bool IsDeleted)
{
    /// <summary>Additive: project-level content scope (FS-CRAWL-002 §4.4).</summary>
    [AdditiveMember]
    public ContentScopeDefinition? ContentScope { get; init; }

    /// <summary>Additive: project-level URL rewrite rules (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? RewriteRulesJson { get; init; }

    /// <summary>Additive: project-level staging↔production host mapping (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? HostMappingJson { get; init; }

    /// <summary>Additive: project-level consent toggle (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public bool? ConsentModeEnabled { get; init; }

    /// <summary>Additive: project-level beacon patterns (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public string? BeaconPatternsJson { get; init; }

    /// <summary>Additive: project-level zone weights (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public string? ZoneWeightsJson { get; init; }

    /// <summary>Additive: project-level SERP budgets (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public SerpBudgetSettings? SerpBudgets { get; init; }
}

/// <summary>
/// Reusable input set (schema catalog: crawl_configurations). Sensitive optional
/// values (headers/cookies, proxy credentials) are encrypted at rest. Additive
/// AIMD and WAF settings ride the existing politeness/limit override fields
/// (FS-CRAWL-002 §5.1 integration).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="Name">Configuration name.</param>
/// <param name="SeedsJson">Seed URLs payload.</param>
/// <param name="ScopeRulesJson">Crawl scope rules payload.</param>
/// <param name="Depth">Optional depth limit.</param>
/// <param name="LimitOverrides">Optional limit overrides.</param>
/// <param name="RenderingMode">Rendering strategy.</param>
/// <param name="PolitenessJson">Politeness settings payload (carries additive AIMD settings).</param>
/// <param name="ProxyRoutingJson">Proxy routing payload (credentials encrypted).</param>
/// <param name="CustomExtractionRulesJson">Custom extraction rules payload.</param>
/// <param name="HeadersJson">Optional headers payload (sensitive values encrypted).</param>
/// <param name="CookiesJson">Optional cookies payload (sensitive values encrypted).</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
public sealed record CrawlConfigurationDto(
    long Id,
    long ProjectId,
    string Name,
    string SeedsJson,
    string? ScopeRulesJson,
    int? Depth,
    CrawlLimitOverrides? LimitOverrides,
    RenderMode RenderingMode,
    string? PolitenessJson,
    string? ProxyRoutingJson,
    string? CustomExtractionRulesJson,
    string? HeadersJson,
    string? CookiesJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Additive: inline content scope (FS-CRAWL-002 §4.4).</summary>
    [AdditiveMember]
    public ContentScopeDefinition? ContentScope { get; init; }

    /// <summary>Additive: content scope profile reference (FS-CRAWL-002 §4.4).</summary>
    [AdditiveMember]
    public long? ContentScopeProfileId { get; init; }

    /// <summary>Additive: list-mode flag (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public bool? IsListMode { get; init; }

    /// <summary>Additive: list-mode URL list payload (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? UrlListJson { get; init; }

    /// <summary>Additive: URL rewrite rules payload (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? RewriteRulesJson { get; init; }

    /// <summary>Additive: staging↔production host mapping payload (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? HostMappingJson { get; init; }

    /// <summary>Additive: consent toggle (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public bool? ConsentModeEnabled { get; init; }

    /// <summary>Additive: primary-container override (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public string? PrimaryContainerOverride { get; init; }

    /// <summary>Additive: dataLayer schema payload (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public string? DataLayerSchemaJson { get; init; }

    /// <summary>Additive: beacon patterns payload (FS-CRAWL-002 §14.4).</summary>
    [AdditiveMember]
    public string? BeaconPatternsJson { get; init; }

    /// <summary>Additive: zone weights payload (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public string? ZoneWeightsJson { get; init; }

    /// <summary>Additive: SERP budgets (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public SerpBudgetSettings? SerpBudgets { get; init; }

    /// <summary>Additive (WP-10-A1, D-021): capture mode (FS-CRAWL-001 §5.6.1,
    /// CR-005). Null keeps the baseline non-JS behaviour; new configurations
    /// default to non-JS. Wire field: captureMode ("non-js"/"js"/"dual"/"adaptive").</summary>
    [AdditiveMember]
    public CaptureMode? CaptureMode { get; init; }

    /// <summary>Additive (WP-10-A1, D-021): capture-mode settings document
    /// (FS-CRAWL-001 §5.6.1/§5.6.2 knobs: adaptive sample size, material-change
    /// threshold, dual storage warning threshold, per-pattern overrides, render
    /// wait rule, rendering pool size). Null applies the engine defaults
    /// (<see cref="Crawl.CaptureSettings.Default"/>). Wire field: captureSettings.</summary>
    [AdditiveMember]
    public CaptureSettings? CaptureSettings { get; init; }

    /// <summary>Additive (WP-10-A1, D-021): inappropriate-redirect check toggles
    /// (CR-029: cross-host, temporary 302/303/307, non-indexable final target per
    /// CR-024, HTTPS-to-HTTP downgrade). Null means all four checks enabled
    /// (the default when absent). Wire field: redirectChecks.</summary>
    [AdditiveMember]
    public RedirectCheckSettings? RedirectChecks { get; init; }
}

/// <summary>
/// One crawl-and-audit run (schema catalog: audits). The configuration is
/// snapshotted so later edits never mutate history. Supports soft delete.
/// Additive snapshot fields freeze extension configuration at audit start.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="ConfigurationId">Source configuration reference.</param>
/// <param name="ConfigurationSnapshotJson">Frozen configuration snapshot.</param>
/// <param name="Status">Run status.</param>
/// <param name="Phase">Coarse processing phase.</param>
/// <param name="StartedAt">Start timestamp (UTC), when started.</param>
/// <param name="FinishedAt">Finish timestamp (UTC), when finished.</param>
/// <param name="CountersJson">Run counters payload.</param>
/// <param name="EngineVersion">Engine version that produced the run.</param>
/// <param name="IsDeleted">Soft-delete marker.</param>
public sealed record AuditDto(
    long Id,
    long ProjectId,
    long ConfigurationId,
    string ConfigurationSnapshotJson,
    AuditStatus Status,
    AuditPhase Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string CountersJson,
    string EngineVersion,
    bool IsDeleted)
{
    /// <summary>Additive: WAF catalogue version snapshot (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public string? WafCatalogueVersion { get; init; }

    /// <summary>Additive: WAF catalogue revision snapshot (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public string? WafCatalogueRevision { get; init; }

    /// <summary>Additive: detection policy snapshot (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public string? DetectionPolicyJson { get; init; }

    /// <summary>Additive: resolved content scope snapshot (FS-CRAWL-002 §4.4).</summary>
    [AdditiveMember]
    public string? ResolvedContentScopeJson { get; init; }

    /// <summary>Additive: list-mode flag snapshot (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public bool? IsListMode { get; init; }

    /// <summary>Additive: URL list snapshot (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? UrlListJson { get; init; }

    /// <summary>Additive: rewrite rules snapshot (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? RewriteRulesJson { get; init; }

    /// <summary>Additive: host mapping snapshot (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? HostMappingJson { get; init; }

    /// <summary>Additive: redirect matrix snapshot (FS-CRAWL-002 §10.6).</summary>
    [AdditiveMember]
    public string? RedirectMatrixJson { get; init; }

    /// <summary>Additive: embedding provider snapshot (FS-CRAWL-002 §12.7).</summary>
    [AdditiveMember]
    public EmbeddingProviderKind? EmbeddingProvider { get; init; }

    /// <summary>Additive: embedding model tag snapshot (FS-CRAWL-002 §12.7).</summary>
    [AdditiveMember]
    public string? EmbeddingModelTag { get; init; }

    /// <summary>Additive: embedding similarity threshold snapshot (FS-CRAWL-002 §12.7).</summary>
    [AdditiveMember]
    public double? EmbeddingSimilarityThreshold { get; init; }

    /// <summary>Additive: agent catalogue version snapshot (FS-CRAWL-002 §13.4).</summary>
    [AdditiveMember]
    public string? AgentCatalogueVersion { get; init; }

    /// <summary>Additive: GEO toggles snapshot (FS-CRAWL-002 §13.4).</summary>
    [AdditiveMember]
    public string? GeoTogglesJson { get; init; }
}

/// <summary>
/// Applied migration ledger row (schema catalog: schema_migrations).
/// Idempotent per provider.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Version">Applied schema version.</param>
/// <param name="ProviderDialect">Provider dialect the migration ran on.</param>
/// <param name="AppliedAt">Application timestamp (UTC).</param>
public sealed record SchemaMigrationDto(
    long Id,
    int Version,
    string ProviderDialect,
    DateTimeOffset AppliedAt);

/// <summary>
/// User filter preset (schema catalog: saved_views, optional). Local
/// convenience only.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Name">Preset name.</param>
/// <param name="TargetSurface">Surface the preset applies to.</param>
/// <param name="FilterJson">Filter definition payload.</param>
public sealed record SavedViewDto(
    long Id,
    string Name,
    string TargetSurface,
    string FilterJson);
