using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-09-06 D3: configuration CRUD round-trips over the real SQLite stack,
/// including the additive editor fields handed off by WP-09-07 (§3):
/// scopeRules (crawl-scope document, behavior-key-preserving merge, parser
/// errors → validation envelope) and enabledCategories (PascalCase enum
/// names, null = all, config-level preference at audit snapshot freeze).
/// AIMD round-trips through PolitenessJson {"aimd":{...}}.
/// </summary>
public sealed class ConfigurationCrudRouteTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ConfigurationCreateRequest CreateRequest(
        string name = "Editor config",
        JsonElement? scopeRules = null,
        IReadOnlyList<string>? enabledCategories = null,
        bool? respectRobotsTxt = null) =>
        new(
            name,
            "Round-trip configuration",
            new[] { "https://example.com/" },
            new CrawlLimitsWire(500, 3, 600, 2_000_000),
            new AimdSettingsWire(1.5, 0.25, 0.7, 2, 0.3),
            new RenderingSettingsWire(true, "desktop", true, false),
            respectRobotsTxt,
            "User-agent: *\nDisallow: /private",
            null, null, null, null,
            scopeRules,
            enabledCategories);

    [Fact]
    public async Task Crud_RoundTrip_AimdAndEditorFields_PersistThroughRealSqlite()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var scopeRules = Json("""
        {
          "canonicalization": { "queryPolicy": "keepWhitelist", "queryWhitelist": ["page"] },
          "scope": { "includePatterns": ["^https://example\\.com/"] },
          "cookieJar": { "enabled": false },
          "userAgent": { "simulateGooglebot": true }
        }
        """);
        var response = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(scopeRules: scopeRules, enabledCategories: new[] { "Content", "Indexability" }, respectRobotsTxt: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<AuditConfigurationWire>();

        // GET round-trip: AIMD through PolitenessJson {"aimd":{...}}.
        var read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal(1.5, read.Aimd.InitialRateR0);
        Assert.Equal(0.25, read.Aimd.Alpha);
        Assert.Equal(0.7, read.Aimd.Beta);
        Assert.Equal(2, read.Aimd.Kappa);
        Assert.Equal(0.3, read.Aimd.EwmaTtfbWeight);
        var stored = await host.Get<IReadRepository<CrawlConfigurationDto>>().GetByIdAsync(long.Parse(created.Id));
        Assert.Contains("\"aimd\"", stored!.PolitenessJson);
        Assert.Contains("\"initialRateR0\":1.5", stored.PolitenessJson);

        // Editor document round-trip (camelCase, verbatim sections).
        Assert.NotNull(read.ScopeRules);
        var document = read.ScopeRules!.Value;
        Assert.False(document.GetProperty("cookieJar").GetProperty("enabled").GetBoolean());
        Assert.Equal("page", document.GetProperty("canonicalization").GetProperty("queryWhitelist")[0].GetString());
        Assert.True(document.GetProperty("userAgent").GetProperty("simulateGooglebot").GetBoolean());

        // Behavior keys preserved inside the same document.
        Assert.Equal("Round-trip configuration", document.GetProperty("description").GetString());
        Assert.False(document.GetProperty("respectRobotsTxt").GetBoolean());
        Assert.Equal(2_000_000, document.GetProperty("maxResponseBytes").GetInt64());
        Assert.True(document.GetProperty("rendering").GetProperty("infiniteScroll").GetBoolean());
        Assert.Equal("desktop", document.GetProperty("rendering").GetProperty("deviceProfile").GetString());
        Assert.Contains("Disallow: /private", document.GetProperty("virtualRobotsTxt").GetString());

        // enabledCategories subset, canonical PascalCase.
        Assert.Equal(new[] { "Content", "Indexability" }, read.EnabledCategories);
        Assert.False(read.RespectRobotsTxt);

        // DELETE then GET → 404.
        var deletion = await host.Client.DeleteAsync($"/api/configurations/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        Assert.True((await deletion.ReadDataAsync<DeletionConfirmationWire>()).Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/configurations/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Put_MergesScopeRulesShallowly_PreservingBehaviorKeysAndOtherSections()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var created = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(scopeRules: Json("""
            {
              "canonicalization": { "queryWhitelist": ["page"] },
              "scope": { "includePatterns": ["^https://example\\.com/"] },
              "cookieJar": { "enabled": false }
            }
            """)))).ReadDataAsync<AuditConfigurationWire>();

        var response = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            Name: null,
            Description: "Updated description",
            StartUrls: null,
            Limits: null,
            Aimd: null,
            Rendering: null,
            RespectRobotsTxt: null,
            VirtualRobotsTxt: null,
            ContentScope: null, Rewrite: null, Consent: null, ZoneWeights: null,
            ScopeRules: Json("""{ "cookieJar": { "enabled": true }, "canonicalization": { "queryWhitelist": [] } }""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        var document = read.ScopeRules!.Value;
        Assert.True(document.GetProperty("cookieJar").GetProperty("enabled").GetBoolean());
        Assert.Equal(0, document.GetProperty("canonicalization").GetProperty("queryWhitelist").GetArrayLength());
        // Untouched section survives the shallow top-level merge.
        Assert.Equal("^https://example\\.com/", document.GetProperty("scope").GetProperty("includePatterns")[0].GetString());
        // Behavior keys survive; the explicit description delta applied.
        Assert.Equal("Updated description", read.Description);
        Assert.Equal("Updated description", document.GetProperty("description").GetString());
        Assert.True(document.GetProperty("respectRobotsTxt").GetBoolean());
        Assert.Contains("Disallow: /private", document.GetProperty("virtualRobotsTxt").GetString());
        Assert.Equal(1.5, read.Aimd.InitialRateR0); // untouched AIMD survives
    }

    [Fact]
    public async Task Put_ExplicitRequestFields_WinOverScopeRulesBehaviorKeys()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var created = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest())).ReadDataAsync<AuditConfigurationWire>();

        var response = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            Name: null, Description: null, StartUrls: null, Limits: null, Aimd: null, Rendering: null,
            RespectRobotsTxt: true,
            VirtualRobotsTxt: null,
            ContentScope: null, Rewrite: null, Consent: null, ZoneWeights: null,
            ScopeRules: Json("""{ "respectRobotsTxt": false, "cookieJar": { "enabled": true } }""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = await response.ReadDataAsync<AuditConfigurationWire>();
        Assert.True(read.RespectRobotsTxt); // explicit field wins over the document
        Assert.True(read.ScopeRules!.Value.GetProperty("cookieJar").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Create_RespectRobotsTxt_FallsBackToDocumentWhenRequestOmitsIt()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(scopeRules: Json("""{ "respectRobotsTxt": false }"""), respectRobotsTxt: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var read = await response.ReadDataAsync<AuditConfigurationWire>();
        Assert.False(read.RespectRobotsTxt);
    }

    [Fact]
    public async Task EnabledCategories_NullMeansAll_SubsetNormalizes_EmptyClears()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var created = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest())).ReadDataAsync<AuditConfigurationWire>();
        Assert.Null(created.EnabledCategories); // absent = all enabled

        // Lowercase input normalizes to canonical PascalCase enum names.
        var subset = await (await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            null, null, null, null, null, null, null, null, null, null, null, null,
            EnabledCategories: new[] { "content", "indexability" }))).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal(new[] { "Content", "Indexability" }, subset.EnabledCategories);

        // Empty array = all enabled (null on the wire).
        var cleared = await (await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            null, null, null, null, null, null, null, null, null, null, null, null,
            EnabledCategories: Array.Empty<string>()))).ReadDataAsync<AuditConfigurationWire>();
        Assert.Null(cleared.EnabledCategories);

        // Omitted member leaves the persisted value unchanged.
        await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            null, null, null, null, null, null, null, null, null, null, null, null,
            EnabledCategories: new[] { "Content" }));
        var unchanged = await (await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", new ConfigurationUpdateRequest(
            "Renamed", null, null, null, null, null, null, null, null, null, null, null))).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal(new[] { "Content" }, unchanged.EnabledCategories);
    }

    [Fact]
    public async Task Create_InvalidScopeRegex_ReturnsValidationEnvelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(scopeRules: Json("""{ "scope": { "includePatterns": ["("] } }""")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("scopeRules", out _));
    }

    [Fact]
    public async Task Create_UnknownCategory_ReturnsValidationEnvelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(enabledCategories: new[] { "Bogus" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("enabledCategories", out _));
    }

    [Fact]
    public async Task AuditStart_Snapshot_PrefersConfigLevelCategories_OverAppSettings()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        // App-settings fallback subset (would win before WP-09-06). The test
        // host seeds settings row 1; update it (the resolver reads the first row).
        var settings = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        await host.Get<IWriteRepository<ApplicationSettingsDto>>().UpdateAsync(settings! with
        {
            AuditTogglesJson = "{\"Content\":true,\"Indexability\":true,\"Linking\":false}",
        });

        var config = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(enabledCategories: new[] { "Content", "Linking" }))).ReadDataAsync<AuditConfigurationWire>();

        var start = await (await host.Client.PostJsonAsync("/api/audits",
            new AuditStartRequest(config.Id, null, null, null))).ReadDataAsync<AuditStartResponse>();

        var audit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        using var snapshot = JsonDocument.Parse(audit!.ConfigurationSnapshotJson);
        var categories = snapshot.RootElement.GetProperty("enabledCategories").EnumerateArray()
            .Select(element => element.GetString()).ToArray();
        Assert.Equal(new[] { "Content", "Linking" }, categories); // config-level wins
    }

    [Fact]
    public async Task AuditStart_Snapshot_FallsBackToAppSettings_WhenConfigHasNoPreference()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var settings = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        await host.Get<IWriteRepository<ApplicationSettingsDto>>().UpdateAsync(settings! with
        {
            AuditTogglesJson = "{\"Content\":true,\"Indexability\":true,\"Linking\":false}",
        });

        var config = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest())).ReadDataAsync<AuditConfigurationWire>();

        var start = await (await host.Client.PostJsonAsync("/api/audits",
            new AuditStartRequest(config.Id, null, null, null))).ReadDataAsync<AuditStartResponse>();

        var audit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        using var snapshot = JsonDocument.Parse(audit!.ConfigurationSnapshotJson);
        var categories = snapshot.RootElement.GetProperty("enabledCategories").EnumerateArray()
            .Select(element => element.GetString()).ToArray();
        Assert.Equal(new[] { "Content", "Indexability" }, categories);
    }
}
