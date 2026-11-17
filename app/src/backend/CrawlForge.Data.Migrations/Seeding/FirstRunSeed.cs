using System.Globalization;
using CrawlForge.Data.Migrations.Dialects;

namespace CrawlForge.Data.Migrations.Seeding;

/// <summary>
/// Deterministic first-run seeding (FS-CRAWL-001 §9.4): after all migrations
/// apply, exactly one <c>application_settings</c> row is seeded with documented
/// defaults — the local file-based database provider, unlimited crawl ceilings,
/// all audit categories enabled, local-only feature toggles on, default
/// politeness on, and the local MCP automation interface disabled. No schedules,
/// no export destinations, no external database, and an empty user dictionary
/// are seeded. Initialization performs zero network calls: every value below is
/// a compile-time constant and the only I/O is the local database insert.
/// </summary>
/// <remarks>
/// The row carries no identity, license, or account columns (criterion #5);
/// the schema itself contains none anywhere. Seeding is idempotent: the insert
/// is guarded by a NOT EXISTS check, so re-running migrations never duplicates
/// or mutates the row (criterion #9).
/// </remarks>
public static class FirstRunSeed
{
    /// <summary>Surrogate key of the single settings row.</summary>
    public const long SettingsRowId = 1;

    /// <summary>
    /// UI preferences: default politeness on, every local-only feature toggle on,
    /// and the local MCP automation interface disabled at first run (§9.4, §11).
    /// </summary>
    public const string UiPreferencesJson =
        "{\"defaultPolitenessEnabled\":true,\"localFeatureToggles\":{\"scheduledAudits\":true,\"customExtraction\":true,\"customSearch\":true,\"javascriptSnippets\":true,\"logAnalyzer\":true,\"semanticLinking\":true,\"geoAnalyzer\":true,\"marTechAnalyzer\":true},\"mcpAutomationEnabled\":false}";

    /// <summary>
    /// Application-level default crawl limits: every ceiling null = unlimited,
    /// reproducing the built-in unlimited profile exactly (precedence rule 5).
    /// </summary>
    public const string DefaultCrawlLimitsJson =
        "{\"MaxUrls\":null,\"MaxDepth\":null,\"MaxDurationSeconds\":null,\"MaxConcurrency\":null,\"RequestsPerSecond\":null,\"FetchTimeoutSeconds\":null}";

    /// <summary>
    /// Audit toggles: every category enabled, including the additive Log
    /// Intelligence, AI Search (GEO), and Analytics &amp; Tagging categories.
    /// </summary>
    public const string AuditTogglesJson =
        "{\"Indexability\":true,\"Content\":true,\"Linking\":true,\"StructuredData\":true,\"Accessibility\":true,\"Performance\":true,\"Crawl\":true,\"Custom\":true,\"LogIntelligence\":true,\"AiSearchGeo\":true,\"AnalyticsTagging\":true}";

    /// <summary>
    /// Storage options: the compressed content store lives beside the database
    /// on the local filesystem, independent of the provider (§2.4).
    /// </summary>
    public const string StorageOptionsJson =
        "{\"contentStoreLocation\":\"beside-database\",\"compressionEnabled\":true}";

    /// <summary>
    /// Selected database provider: the embedded file-based provider (Sqlite = 0).
    /// No external database is configured at first run.
    /// </summary>
    public const int DatabaseProviderSqlite = 0;

    /// <summary>Dark-first UI theme default.</summary>
    public const string Theme = "dark";

    /// <summary>Deterministic locale default.</summary>
    public const string Locale = "en";

    /// <summary>
    /// Application-level AIMD defaults (R0, alpha, beta, kappa, floor, ceiling):
    /// local, deterministic, user-tunable; the controller is on by default.
    /// </summary>
    public const string AimdDefaultsJson =
        "{\"InitialRate\":2.0,\"AdditiveIncrease\":0.5,\"MultiplicativeDecrease\":0.5,\"EwmaSmoothing\":0.2,\"MinRate\":0.1,\"MaxConcurrency\":4}";

    /// <summary>WAF detection enabled at first run (detection and reporting only).</summary>
    public const int WafDetectionEnabled = 1;

    /// <summary>Status-bar threshold preferences (local defaults, user-tunable).</summary>
    public const string StatusBarThresholdsJson =
        "{\"ttfbWarningMilliseconds\":500,\"ttfbErrorMilliseconds\":1500}";

    /// <summary>Local plugin preferences: none registered at first run.</summary>
    public const string PluginPreferencesJson = "{}";

    /// <summary>
    /// Embedding provider: the bundled offline ONNX model (Kind = 0); no
    /// loopback daemon endpoint is configured (exclusion E7).
    /// </summary>
    public const string EmbeddingProviderJson =
        "{\"Kind\":0,\"LoopbackEndpoint\":null,\"ModelTag\":null}";

    /// <summary>User-local bot CIDR additions: empty; bundled assets only.</summary>
    public const string UserBotCidrAdditionsJson = "[]";

    /// <summary>SERP pixel budget defaults (local defaults, user-tunable).</summary>
    public const string SerpBudgetDefaultsJson =
        "{\"TitleBudgetPixels\":600.0,\"DescriptionBudgetPixels\":960.0,\"FontProfileKey\":\"default\"}";

    /// <summary>
    /// Ordered column names of the seeded settings row (baseline columns followed
    /// by the additive preference columns).
    /// </summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "id",
        "ui_preferences_json",
        "default_crawl_limits_json",
        "audit_toggles_json",
        "storage_options_json",
        "database_provider",
        "database_connection_json",
        "theme",
        "locale",
        "aimd_defaults_json",
        "waf_detection_enabled",
        "status_bar_thresholds_json",
        "plugin_preferences_json",
        "embedding_provider_json",
        "user_bot_cidr_additions_json",
        "serp_budget_defaults_json",
    ];

    /// <summary>
    /// Ordered parameter values matching <see cref="Columns"/>. Every value is a
    /// deterministic constant; the external-database connection payload is NULL
    /// (no external database configured at first run).
    /// </summary>
    public static IReadOnlyList<object?> Values { get; } =
    [
        SettingsRowId,
        UiPreferencesJson,
        DefaultCrawlLimitsJson,
        AuditTogglesJson,
        StorageOptionsJson,
        DatabaseProviderSqlite,
        null,
        Theme,
        Locale,
        AimdDefaultsJson,
        WafDetectionEnabled,
        StatusBarThresholdsJson,
        PluginPreferencesJson,
        EmbeddingProviderJson,
        UserBotCidrAdditionsJson,
        SerpBudgetDefaultsJson,
    ];

    /// <summary>
    /// Renders the idempotent seed statement for a dialect: a guarded insert
    /// that creates the single settings row only when the table is empty.
    /// </summary>
    /// <param name="dialect">The target dialect.</param>
    /// <returns>The parameterized INSERT statement (parameters @p0..@p15).</returns>
    public static string RenderSeedStatement(SqlDialect dialect) =>
        RenderSeedStatement(dialect, Columns, Values.Count);

    /// <summary>
    /// Renders the guarded seed statement for a subset of the seed columns.
    /// Used when migrations apply incrementally: only columns that already
    /// exist on the settings table are seeded, so partial schema states never
    /// fail and the full catalog always receives every documented default.
    /// </summary>
    /// <param name="dialect">The target dialect.</param>
    /// <param name="columns">Ordered column names to seed (a subset of <see cref="Columns"/>).</param>
    /// <param name="valueCount">Number of parameter values matching the columns.</param>
    /// <returns>The parameterized INSERT statement (parameters @p0..@pN).</returns>
    public static string RenderSeedStatement(SqlDialect dialect, IReadOnlyList<string> columns, int valueCount)
    {
        var table = dialect.Quote("application_settings");
        var columnList = string.Join(", ", columns.Select(dialect.Quote));
        var parameters = string.Join(", ", Enumerable.Range(0, valueCount).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"@p{i}")));
        return $"INSERT INTO {table} ({columnList}) SELECT {parameters} WHERE NOT EXISTS (SELECT 1 FROM {table});";
    }
}
