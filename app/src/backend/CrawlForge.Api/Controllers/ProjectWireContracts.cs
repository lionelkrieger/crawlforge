namespace CrawlForge.Api.Controllers;

/// <summary>Wire crawl limits; null means unlimited (exclusion #8: never license-driven).</summary>
/// <param name="MaxUrls">Maximum URLs, or null for unlimited.</param>
/// <param name="MaxDepth">Maximum depth, or null for unlimited.</param>
/// <param name="MaxDurationSeconds">Maximum duration, or null for unlimited.</param>
/// <param name="MaxResponseBytes">Maximum response bytes, or null for unlimited.</param>
public sealed record CrawlLimitsWire(long? MaxUrls, int? MaxDepth, int? MaxDurationSeconds, long? MaxResponseBytes);

/// <summary>Wire AIMD politeness parameters (FS-CRAWL-002 §3).</summary>
/// <param name="InitialRateR0">Initial requests-per-second rate.</param>
/// <param name="Alpha">Additive increase step.</param>
/// <param name="Beta">Multiplicative decrease factor in (0,1).</param>
/// <param name="Kappa">Concurrency cap.</param>
/// <param name="EwmaTtfbWeight">EWMA smoothing weight for TTFB samples.</param>
public sealed record AimdSettingsWire(double InitialRateR0, double Alpha, double Beta, int Kappa, double EwmaTtfbWeight);

/// <summary>Wire headless rendering options.</summary>
/// <param name="Enabled">Whether rendering is enabled.</param>
/// <param name="DeviceProfile">Device profile identifier.</param>
/// <param name="InfiniteScroll">Whether infinite-scroll expansion runs.</param>
/// <param name="CaptureConsole">Whether console messages are captured.</param>
public sealed record RenderingSettingsWire(bool Enabled, string? DeviceProfile, bool InfiniteScroll, bool CaptureConsole);

/// <summary>Wire content-scope profile row.</summary>
/// <param name="Id">Profile identifier.</param>
/// <param name="Name">Profile name.</param>
/// <param name="IncludeSelectors">Include selectors.</param>
/// <param name="ExcludeSelectors">Exclude selectors.</param>
public sealed record ContentScopeProfileWire(string Id, string Name, IReadOnlyList<string> IncludeSelectors, IReadOnlyList<string> ExcludeSelectors);

/// <summary>Wire content-area scoping settings (FS-CRAWL-002 §4).</summary>
/// <param name="IncludeSelectors">Include selectors.</param>
/// <param name="ExcludeSelectors">Exclude selectors.</param>
/// <param name="Profiles">Saved project profiles, when present.</param>
public sealed record ContentScopeSettingsWire(IReadOnlyList<string> IncludeSelectors, IReadOnlyList<string> ExcludeSelectors, IReadOnlyList<ContentScopeProfileWire>? Profiles);

/// <summary>Wire URL rewrite rule.</summary>
/// <param name="Pattern">Regex pattern.</param>
/// <param name="Replacement">Replacement expression.</param>
/// <param name="Enabled">Whether the rule is active.</param>
public sealed record UrlRewriteRuleWire(string Pattern, string Replacement, bool Enabled);

/// <summary>Wire host mapping entry (staging↔production equivalence).</summary>
/// <param name="FromHost">Source host.</param>
/// <param name="ToHost">Target host.</param>
public sealed record HostMappingWire(string FromHost, string ToHost);

/// <summary>Wire rewrite / host-mapping settings (FS-CRAWL-002 §10.7).</summary>
/// <param name="Rules">Rewrite rules.</param>
/// <param name="HostMappings">Host mappings.</param>
public sealed record RewriteSettingsWire(IReadOnlyList<UrlRewriteRuleWire> Rules, IReadOnlyList<HostMappingWire> HostMappings);

/// <summary>Wire consent / beacon detection settings.</summary>
/// <param name="ConsentModeV2Enabled">Whether Consent Mode v2 detection is enabled.</param>
/// <param name="BeaconDetectionEnabled">Whether beacon detection is enabled.</param>
public sealed record ConsentSettingsWire(bool ConsentModeV2Enabled, bool BeaconDetectionEnabled);

/// <summary>Wire DOM-zone link-equity weights and SERP options.</summary>
/// <param name="ZoneWeights">Zone key → weight map.</param>
/// <param name="SerpMetricsEnabled">Whether SERP metrics are enabled.</param>
public sealed record ZoneWeightSettingsWire(IReadOnlyDictionary<string, double> ZoneWeights, bool SerpMetricsEnabled);

/// <summary>Explicit per-URL-pattern capture override (FS-CRAWL-001 §5.6.1;
/// WP-10-A1). Overrides win over adaptive switching and apply on top of every
/// capture mode.</summary>
/// <param name="Pattern">URL path pattern (for example "/blog/*").</param>
/// <param name="Capture">Pattern capture wire token: "rendered" or "non-rendered".</param>
public sealed record CapturePatternOverrideWire(string Pattern, string Capture);

/// <summary>Capture-mode settings document (FS-CRAWL-001 §5.6.1/§5.6.2;
/// WP-10-A1). Every knob optional: null applies the engine default documented
/// per field. Mirrors Contracts.Crawl.CaptureSettings and the frontend
/// CaptureSettings interface. The dual storage threshold warns only — never a
/// hard cap (exclusion #8).</summary>
/// <param name="AdaptiveSampleSizePerGroup">Adaptive samples per pattern group; default 4 (spec range 3–5).</param>
/// <param name="MaterialChangeWordCountThreshold">Material-change word-count fraction; default 0.20.</param>
/// <param name="DualStorageWarningThresholdBytes">Dual-mode storage warning threshold; default 1 GiB.</param>
/// <param name="RenderWaitQuietMs">Render wait quiet period; default 500.</param>
/// <param name="RenderWaitHardCapMs">Render wait hard cap; default 20000.</param>
/// <param name="RenderingPoolSize">Concurrent contexts; null = half logical processors, minimum 2.</param>
/// <param name="PatternOverrides">Explicit per-pattern overrides; null/empty = none.</param>
public sealed record CaptureSettingsWire(
    int? AdaptiveSampleSizePerGroup = null,
    double? MaterialChangeWordCountThreshold = null,
    long? DualStorageWarningThresholdBytes = null,
    int? RenderWaitQuietMs = null,
    int? RenderWaitHardCapMs = null,
    int? RenderingPoolSize = null,
    IReadOnlyList<CapturePatternOverrideWire>? PatternOverrides = null);

/// <summary>Inappropriate-redirect check toggles (CR-029; WP-10-A1). Four
/// independent cases, all ON by default; null/absent means every check
/// enabled. Mirrors Contracts.Crawl.RedirectCheckSettings.</summary>
/// <param name="CrossHost">Case 1: hop redirects to a different host. Default true.</param>
/// <param name="TemporaryStatus">Case 2: hop uses 302/303/307. Default true.</param>
/// <param name="NonIndexableTarget">Case 3: final target non-indexable per CR-024. Default true.</param>
/// <param name="HttpsDowngrade">Case 4: HTTPS URL redirects to HTTP. Default true.</param>
public sealed record RedirectCheckSettingsWire(
    bool? CrossHost = null,
    bool? TemporaryStatus = null,
    bool? NonIndexableTarget = null,
    bool? HttpsDowngrade = null);

/// <summary>Wire project summary row.</summary>
/// <param name="Id">Project identifier.</param>
/// <param name="Name">Project name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Project tags.</param>
/// <param name="ScopeSummary">Optional scope summary.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
public sealed record ProjectSummaryWire(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    string? ScopeSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Wire full project record including additive extension settings.</summary>
/// <param name="Id">Project identifier.</param>
/// <param name="Name">Project name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Project tags.</param>
/// <param name="ScopeSummary">Optional scope summary.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
/// <param name="ContentScope">Content scoping settings, when configured.</param>
/// <param name="Rewrite">Rewrite / host-mapping settings, when configured.</param>
/// <param name="Consent">Consent / beacon settings, when configured.</param>
/// <param name="ZoneWeights">Zone weights / SERP settings, when configured.</param>
public sealed record ProjectWire(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    string? ScopeSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ContentScopeSettingsWire? ContentScope,
    RewriteSettingsWire? Rewrite,
    ConsentSettingsWire? Consent,
    ZoneWeightSettingsWire? ZoneWeights);

/// <summary>POST /api/projects — creation payload.</summary>
/// <param name="Name">Project name (required).</param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Optional tags.</param>
/// <param name="ScopeSummary">Optional scope summary.</param>
public sealed record ProjectCreateRequest(string Name, string? Description, IReadOnlyList<string>? Tags, string? ScopeSummary);

/// <summary>PUT /api/projects/{id} — subset payload with extension settings.</summary>
/// <param name="Name">Optional name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Optional tags.</param>
/// <param name="ScopeSummary">Optional scope summary.</param>
/// <param name="ContentScope">Optional content scoping settings.</param>
/// <param name="Rewrite">Optional rewrite settings.</param>
/// <param name="Consent">Optional consent settings.</param>
/// <param name="ZoneWeights">Optional zone weights.</param>
public sealed record ProjectUpdateRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? Tags,
    string? ScopeSummary,
    ContentScopeSettingsWire? ContentScope,
    RewriteSettingsWire? Rewrite,
    ConsentSettingsWire? Consent,
    ZoneWeightSettingsWire? ZoneWeights);

/// <summary>Wire configuration summary row.</summary>
/// <param name="Id">Configuration identifier.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="Name">Configuration name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
public sealed record ConfigurationSummaryWire(string Id, string ProjectId, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Wire full audit configuration.</summary>
/// <param name="Id">Configuration identifier.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="Name">Configuration name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
/// <param name="StartUrls">Seed URLs.</param>
/// <param name="Limits">Crawl limit overrides.</param>
/// <param name="Aimd">AIMD politeness parameters.</param>
/// <param name="Rendering">Rendering options.</param>
/// <param name="RespectRobotsTxt">Whether robots.txt is honored.</param>
/// <param name="VirtualRobotsTxt">Virtual robots.txt body, when configured.</param>
/// <param name="ContentScope">Content scoping settings, when configured.</param>
/// <param name="Rewrite">Rewrite settings, when configured.</param>
/// <param name="Consent">Consent settings, when configured.</param>
/// <param name="ZoneWeights">Zone weights, when configured.</param>
/// <param name="ScopeRules">Additive (WP-09-06, WP-09-07 G18): the persisted crawl-scope document
/// (camelCase; canonicalization/scope/directives/resources/cookieJar/userAgent/userAgentComparison/headers/cookies
/// plus the behavior keys), projected from <c>ScopeRulesJson</c>. Null when nothing is persisted.</param>
/// <param name="EnabledCategories">Additive (WP-09-06, WP-09-07): per-configuration analysis categories
/// (PascalCase <c>AuditCategory</c> names, case-insensitive on input); null = all enabled.</param>
/// <param name="CaptureMode">Additive (WP-10-A1, D-021): capture-mode wire token
/// (non-js/js/dual/adaptive); null = baseline non-JS behaviour.</param>
/// <param name="CaptureSettings">Additive (WP-10-A1): capture-mode settings document; null = engine defaults.</param>
/// <param name="RedirectChecks">Additive (WP-10-A1, CR-029): inappropriate-redirect toggles; null = all four on.</param>
public sealed record AuditConfigurationWire(
    string Id,
    string ProjectId,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> StartUrls,
    CrawlLimitsWire Limits,
    AimdSettingsWire Aimd,
    RenderingSettingsWire Rendering,
    bool RespectRobotsTxt,
    string? VirtualRobotsTxt,
    ContentScopeSettingsWire? ContentScope,
    RewriteSettingsWire? Rewrite,
    ConsentSettingsWire? Consent,
    ZoneWeightSettingsWire? ZoneWeights,
    System.Text.Json.JsonElement? ScopeRules = null,
    IReadOnlyList<string>? EnabledCategories = null,
    string? CaptureMode = null,
    CaptureSettingsWire? CaptureSettings = null,
    RedirectCheckSettingsWire? RedirectChecks = null);

/// <summary>POST /api/projects/{id}/configurations — creation payload.</summary>
/// <param name="Name">Configuration name (required).</param>
/// <param name="Description">Optional description.</param>
/// <param name="StartUrls">Seed URLs (required, at least one).</param>
/// <param name="Limits">Optional limit overrides.</param>
/// <param name="Aimd">Optional AIMD parameters.</param>
/// <param name="Rendering">Optional rendering options.</param>
/// <param name="RespectRobotsTxt">Optional robots honoring flag.</param>
/// <param name="VirtualRobotsTxt">Optional virtual robots.txt body.</param>
/// <param name="ContentScope">Optional content scoping.</param>
/// <param name="Rewrite">Optional rewrite settings.</param>
/// <param name="Consent">Optional consent settings.</param>
/// <param name="ZoneWeights">Optional zone weights.</param>
/// <param name="ScopeRules">Additive crawl-scope document (WP-09-07 §3 shape); merged into
/// <c>ScopeRulesJson</c> preserving the behavior keys. Invalid regexes are validation errors.</param>
/// <param name="EnabledCategories">Additive per-configuration analysis categories; null = all enabled.</param>
/// <param name="CaptureMode">Additive (WP-10-A1): capture-mode wire token; absent/null = baseline non-JS.</param>
/// <param name="CaptureSettings">Additive (WP-10-A1): capture settings; absent/null = engine defaults.</param>
/// <param name="RedirectChecks">Additive (WP-10-A1, CR-029): redirect toggles; absent/null = all four on.</param>
public sealed record ConfigurationCreateRequest(
    string Name,
    string? Description,
    IReadOnlyList<string> StartUrls,
    CrawlLimitsWire? Limits,
    AimdSettingsWire? Aimd,
    RenderingSettingsWire? Rendering,
    bool? RespectRobotsTxt,
    string? VirtualRobotsTxt,
    ContentScopeSettingsWire? ContentScope,
    RewriteSettingsWire? Rewrite,
    ConsentSettingsWire? Consent,
    ZoneWeightSettingsWire? ZoneWeights,
    System.Text.Json.JsonElement? ScopeRules = null,
    IReadOnlyList<string>? EnabledCategories = null,
    string? CaptureMode = null,
    CaptureSettingsWire? CaptureSettings = null,
    RedirectCheckSettingsWire? RedirectChecks = null);

/// <summary>PUT /api/configurations/{id} — subset payload.</summary>
/// <param name="Name">Optional name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="StartUrls">Optional seed URLs.</param>
/// <param name="Limits">Optional limit overrides.</param>
/// <param name="Aimd">Optional AIMD parameters.</param>
/// <param name="Rendering">Optional rendering options.</param>
/// <param name="RespectRobotsTxt">Optional robots honoring flag.</param>
/// <param name="VirtualRobotsTxt">Optional virtual robots.txt body.</param>
/// <param name="ContentScope">Optional content scoping.</param>
/// <param name="Rewrite">Optional rewrite settings.</param>
/// <param name="Consent">Optional consent settings.</param>
/// <param name="ZoneWeights">Optional zone weights.</param>
/// <param name="ScopeRules">Additive crawl-scope document; shallow top-level merge into the
/// persisted <c>ScopeRulesJson</c> (explicit request fields win over behavior keys). Omitted = unchanged.</param>
/// <param name="EnabledCategories">Additive per-configuration analysis categories. Non-null replaces the
/// persisted set (empty array = all enabled); null/omitted = unchanged.</param>
/// <param name="CaptureMode">Additive (WP-10-A1): capture-mode wire token; non-null replaces the persisted
/// mode; null/omitted = unchanged.</param>
/// <param name="CaptureSettings">Additive (WP-10-A1): capture settings document; non-null replaces the
/// persisted document (absent knobs take engine defaults); null/omitted = unchanged.</param>
/// <param name="RedirectChecks">Additive (WP-10-A1, CR-029): redirect toggles; non-null replaces the
/// persisted document (absent toggles default ON); null/omitted = unchanged.</param>
public sealed record ConfigurationUpdateRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? StartUrls,
    CrawlLimitsWire? Limits,
    AimdSettingsWire? Aimd,
    RenderingSettingsWire? Rendering,
    bool? RespectRobotsTxt,
    string? VirtualRobotsTxt,
    ContentScopeSettingsWire? ContentScope,
    RewriteSettingsWire? Rewrite,
    ConsentSettingsWire? Consent,
    ZoneWeightSettingsWire? ZoneWeights,
    System.Text.Json.JsonElement? ScopeRules = null,
    IReadOnlyList<string>? EnabledCategories = null,
    string? CaptureMode = null,
    CaptureSettingsWire? CaptureSettings = null,
    RedirectCheckSettingsWire? RedirectChecks = null);

/// <summary>Wire suppression scope.</summary>
/// <param name="UrlId">Optional URL scope.</param>
/// <param name="UrlPattern">Optional URL pattern scope.</param>
public sealed record SuppressionScopeWire(string? UrlId, string? UrlPattern);

/// <summary>POST /api/projects/{id}/suppressions — suppression target.</summary>
/// <param name="RuleKey">Optional rule key.</param>
/// <param name="Scope">Optional finding scope.</param>
public sealed record SuppressionCreateRequest(string? RuleKey, SuppressionScopeWire? Scope);

/// <summary>Wire suppression state.</summary>
/// <param name="Key">Stable suppression key.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="RuleKey">Suppressed rule key, when rule-scoped.</param>
/// <param name="Scope">Suppression scope, when scoped.</param>
/// <param name="Suppressed">Whether the suppression is active.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
public sealed record SuppressionStateWire(string Key, string ProjectId, string? RuleKey, SuppressionScopeWire? Scope, bool Suppressed, DateTimeOffset CreatedAt);

/// <summary>Generic deletion confirmation.</summary>
/// <param name="Id">Deleted entity identifier.</param>
/// <param name="Deleted">Whether the entity was deleted.</param>
public sealed record DeletionConfirmationWire(string Id, bool Deleted);
