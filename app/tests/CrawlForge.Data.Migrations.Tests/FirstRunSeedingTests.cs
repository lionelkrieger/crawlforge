using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Seeding;
using CrawlForge.Data.Migrations.Tests.Support;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// Deterministic first-run seeding (FS-CRAWL-001 §9.4; criteria #9 and #5):
/// exactly one application_settings row with documented defaults, the local
/// file-based provider selected, unlimited ceilings, all categories enabled,
/// local toggles on, MCP automation disabled, zero identity/license/account
/// columns anywhere, and no destinations, schedules, or dictionary entries.
/// Seeding performs zero network calls — every value is a compile-time
/// constant written to the local database only.
/// </summary>
public class FirstRunSeedingTests : IAsyncLifetime
{
    private readonly MigrationTestDatabase _db = new();

    /// <summary>Creates and migrates the per-test database.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.MigrateAsync();
    }

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>Exactly one settings row is seeded, with surrogate key 1.</summary>
    [Fact]
    public async Task ExactlyOneSettingsRowIsSeeded()
    {
        Assert.Equal(1, await _db.CountRowsAsync("application_settings"));
        var id = await _db.ScalarAsync("SELECT id FROM application_settings;");
        Assert.Equal(FirstRunSeed.SettingsRowId, Convert.ToInt64(id));
    }

    /// <summary>Every seeded value equals its documented constant, including the additive preferences.</summary>
    [Fact]
    public async Task SeededRowMatchesDocumentedDefaults()
    {
        var row = await ReadSettingsRowAsync(_db);
        Assert.Equal(FirstRunSeed.SettingsRowId, row.Id);
        Assert.Equal(FirstRunSeed.UiPreferencesJson, row.UiPreferencesJson);
        Assert.Equal(FirstRunSeed.DefaultCrawlLimitsJson, row.DefaultCrawlLimitsJson);
        Assert.Equal(FirstRunSeed.AuditTogglesJson, row.AuditTogglesJson);
        Assert.Equal(FirstRunSeed.StorageOptionsJson, row.StorageOptionsJson);
        Assert.Equal(FirstRunSeed.DatabaseProviderSqlite, row.DatabaseProvider);
        Assert.Null(row.DatabaseConnectionJson);
        Assert.Equal(FirstRunSeed.Theme, row.Theme);
        Assert.Equal(FirstRunSeed.Locale, row.Locale);
        Assert.Equal(FirstRunSeed.AimdDefaultsJson, row.AimdDefaultsJson);
        Assert.Equal(FirstRunSeed.WafDetectionEnabled, row.WafDetectionEnabled);
        Assert.Equal(FirstRunSeed.StatusBarThresholdsJson, row.StatusBarThresholdsJson);
        Assert.Equal(FirstRunSeed.PluginPreferencesJson, row.PluginPreferencesJson);
        Assert.Equal(FirstRunSeed.EmbeddingProviderJson, row.EmbeddingProviderJson);
        Assert.Equal(FirstRunSeed.UserBotCidrAdditionsJson, row.UserBotCidrAdditionsJson);
        Assert.Equal(FirstRunSeed.SerpBudgetDefaultsJson, row.SerpBudgetDefaultsJson);
    }

    /// <summary>The default provider is the local file-based provider; no external database is configured.</summary>
    [Fact]
    public async Task DefaultProviderIsFileBasedWithNoExternalDatabase()
    {
        var row = await ReadSettingsRowAsync(_db);
        Assert.Equal((int)DatabaseProviderKind.Sqlite, row.DatabaseProvider);
        Assert.Null(row.DatabaseConnectionJson);
    }

    /// <summary>Default crawl ceilings are all unlimited (every limit null), reproducing the built-in profile.</summary>
    [Fact]
    public async Task DefaultCrawlCeilingsAreUnlimited()
    {
        var row = await ReadSettingsRowAsync(_db);
        using var document = JsonDocument.Parse(row.DefaultCrawlLimitsJson);
        var properties = document.RootElement.EnumerateObject().ToList();
        Assert.Equal(6, properties.Count);
        Assert.All(properties, property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
    }

    /// <summary>All audit categories are enabled, including the three additive categories.</summary>
    [Fact]
    public async Task AllAuditCategoriesAreEnabled()
    {
        var row = await ReadSettingsRowAsync(_db);
        using var document = JsonDocument.Parse(row.AuditTogglesJson);
        var toggles = document.RootElement.EnumerateObject().ToList();
        var expectedNames = Enum.GetNames<AuditCategory>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedNames, toggles.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(toggles, toggle => Assert.True(toggle.Value.GetBoolean()));
        Assert.Contains("LogIntelligence", expectedNames);
        Assert.Contains("AiSearchGeo", expectedNames);
        Assert.Contains("AnalyticsTagging", expectedNames);
    }

    /// <summary>Local-only feature toggles are on, default politeness is on, MCP automation is disabled.</summary>
    [Fact]
    public async Task LocalTogglesOnPolitenessOnMcpDisabled()
    {
        var row = await ReadSettingsRowAsync(_db);
        using var document = JsonDocument.Parse(row.UiPreferencesJson);
        Assert.True(document.RootElement.GetProperty("defaultPolitenessEnabled").GetBoolean());
        Assert.False(document.RootElement.GetProperty("mcpAutomationEnabled").GetBoolean());
        var toggles = document.RootElement.GetProperty("localFeatureToggles").EnumerateObject().ToList();
        Assert.NotEmpty(toggles);
        Assert.All(toggles, toggle => Assert.True(toggle.Value.GetBoolean()));
    }

    /// <summary>The embedding default is the bundled offline model with no daemon endpoint.</summary>
    [Fact]
    public async Task EmbeddingDefaultIsBundledOfflineModel()
    {
        var row = await ReadSettingsRowAsync(_db);
        using var document = JsonDocument.Parse(row.EmbeddingProviderJson);
        Assert.Equal((int)EmbeddingProviderKind.BundledOnnx, document.RootElement.GetProperty("Kind").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("LoopbackEndpoint").ValueKind);
    }

    /// <summary>No destinations, schedules, projects, dictionary entries, or any other user rows are seeded.</summary>
    [Fact]
    public async Task AllOtherTablesRemainEmpty()
    {
        // The settings row is the only seed; the ledger holds migration records,
        // not user data. Every other table must be empty at first run.
        foreach (var table in SchemaCatalog.AllTables.Where(t =>
            t.Name != "application_settings" && t.Name != SchemaCatalog.LedgerTableName))
        {
            Assert.Equal(0, await _db.CountRowsAsync(table.Name));
        }

        Assert.Equal(SchemaCatalog.Migrations.Count, await _db.CountRowsAsync(SchemaCatalog.LedgerTableName));
    }

    /// <summary>Two fresh databases receive byte-identical settings rows (determinism).</summary>
    [Fact]
    public async Task SeedingIsDeterministicAcrossFreshDatabases()
    {
        var first = await CaptureRowAsync(_db);

        var second = new MigrationTestDatabase();
        await second.InitializeAsync();
        try
        {
            await second.MigrateAsync();
            Assert.Equal(first, await CaptureRowAsync(second));
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    /// <summary>Re-applying migrations never duplicates or mutates the seeded row.</summary>
    [Fact]
    public async Task ReapplyKeepsSeedIntact()
    {
        var before = await CaptureRowAsync(_db);
        await _db.MigrateAsync();
        Assert.Equal(1, await _db.CountRowsAsync("application_settings"));
        Assert.Equal(before, await CaptureRowAsync(_db));
    }

    /// <summary>
    /// Criterion #5: no column anywhere in the 40-object schema carries an
    /// identity, licensing, or account concept. Forbidden concepts are matched
    /// as whole snake_case tokens (so legitimate words such as "serialized"
    /// never false-positive on "serial").
    /// </summary>
    [Fact]
    public async Task SchemaHasNoForbiddenConceptColumns()
    {
        string[] forbiddenConcepts =
        [
            "identity", "license", "licence", "entitlement", "account",
            "password", "login", "signin", "signup", "machine", "device",
            "hardware", "serial", "user_id", "subscription", "trial",
            "seat", "activation", "telemetry",
        ];

        var violations = new List<string>();
        await using var connection = await _db.OpenAsync();
        foreach (var table in SchemaCatalog.AllTables)
        {
            foreach (var column in await SchemaInspector.GetColumnsAsync(connection, table.Name))
            {
                var tokens = column.Name.ToLowerInvariant().Split('_');
                foreach (var concept in forbiddenConcepts)
                {
                    var conceptTokens = concept.Split('_');
                    if (ContainsTokenSequence(tokens, conceptTokens))
                    {
                        violations.Add($"{table.Name}.{column.Name} ~ '{concept}'");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Criterion #5 violations: " + string.Join("; ", violations));
    }

    private static bool ContainsTokenSequence(string[] tokens, string[] sequence)
    {
        for (var i = 0; i <= tokens.Length - sequence.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(tokens[i + j], sequence[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<SettingsRow> ReadSettingsRowAsync(MigrationTestDatabase db)
    {
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, ui_preferences_json, default_crawl_limits_json, audit_toggles_json, storage_options_json, " +
            "database_provider, database_connection_json, theme, locale, aimd_defaults_json, waf_detection_enabled, " +
            "status_bar_thresholds_json, plugin_preferences_json, embedding_provider_json, user_bot_cidr_additions_json, " +
            "serp_budget_defaults_json FROM application_settings;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new SettingsRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private static async Task<string> CaptureRowAsync(MigrationTestDatabase db)
    {
        var row = await ReadSettingsRowAsync(db);
        return string.Join("|",
            row.Id, row.UiPreferencesJson, row.DefaultCrawlLimitsJson, row.AuditTogglesJson,
            row.StorageOptionsJson, row.DatabaseProvider, row.DatabaseConnectionJson ?? "<null>",
            row.Theme, row.Locale, row.AimdDefaultsJson, row.WafDetectionEnabled,
            row.StatusBarThresholdsJson, row.PluginPreferencesJson, row.EmbeddingProviderJson,
            row.UserBotCidrAdditionsJson, row.SerpBudgetDefaultsJson);
    }

    private sealed record SettingsRow(
        long Id,
        string UiPreferencesJson,
        string DefaultCrawlLimitsJson,
        string AuditTogglesJson,
        string StorageOptionsJson,
        int DatabaseProvider,
        string? DatabaseConnectionJson,
        string Theme,
        string Locale,
        string AimdDefaultsJson,
        int WafDetectionEnabled,
        string StatusBarThresholdsJson,
        string PluginPreferencesJson,
        string EmbeddingProviderJson,
        string UserBotCidrAdditionsJson,
        string SerpBudgetDefaultsJson);
}
