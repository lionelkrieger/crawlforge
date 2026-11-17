using System.Net;
using CrawlForge.Api.Controllers;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for project CRUD, configuration CRUD, and
/// finding suppressions (FS-CRAWL-001 §10.2, §7.5; additive extension settings
/// per FS-CRAWL-002 Appendix B).
/// </summary>
public sealed class ProjectRouteTests
{
    [Fact]
    public async Task Projects_Create_ReturnsCreatedProject_WithWireShape()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest(
            "Example Site", "Migration audit", new[] { "seo", "migration" }, "https://example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Example Site", data.GetProperty("name").GetString());
        Assert.Equal("Migration audit", data.GetProperty("description").GetString());
        Assert.Equal(2, data.GetProperty("tags").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("id").GetString()));
        Assert.True(data.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Projects_CreateEmptyName_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest("  ", null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Projects_List_Paginates_WithFrozenMeta()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest($"Project {index}", null, null, null));
        }

        var response = await host.Client.GetAsync("/api/projects?offset=0&limit=2&sort=name&order=asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 2, 3);
        var items = document.RootElement.GetProperty("data");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Project 0", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Projects_ListSearch_FiltersByName()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest("Alpha Site", null, null, null));
        await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest("Beta Site", null, null, null));

        var response = await host.Client.GetAsync("/api/projects?search=alpha");

        var items = await response.ReadDataAsync<List<ProjectSummaryWire>>();
        Assert.Single(items);
        Assert.Equal("Alpha Site", items[0].Name);
    }

    [Fact]
    public async Task Projects_ListInvalidSortOrOrder_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var badSort = await host.Client.GetAsync("/api/projects?sort=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);

        var badOrder = await host.Client.GetAsync("/api/projects?order=sideways");
        Assert.Equal(HttpStatusCode.BadRequest, badOrder.StatusCode);
    }

    [Fact]
    public async Task Projects_GetUnknown_ReturnsNotFoundEnvelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.GetAsync("/api/projects/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document, expectError: true);
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "not-found");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task Projects_UpdateSubset_AppliesExtensionSettings()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await (await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest("Site", null, null, null)))
            .ReadDataAsync<ProjectWire>();

        var update = new ProjectUpdateRequest(
            Name: "Renamed",
            Description: null,
            Tags: new[] { "new-tag" },
            ScopeSummary: null,
            ContentScope: new ContentScopeSettingsWire(new[] { "main" }, new[] { "nav", "footer" }, null),
            Rewrite: new RewriteSettingsWire(
                new[] { new UrlRewriteRuleWire("^https://old\\.example/", "https://new.example/", true) },
                new[] { new HostMappingWire("staging.example.com", "www.example.com") }),
            Consent: new ConsentSettingsWire(true, true),
            ZoneWeights: new ZoneWeightSettingsWire(new Dictionary<string, double> { ["mainContent"] = 1.0, ["footer"] = 0.2 }, true));
        var response = await host.Client.PutJsonAsync($"/api/projects/{created.Id}", update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadDataAsync<ProjectWire>();
        Assert.Equal("Renamed", updated.Name);
        Assert.Single(updated.Tags);
        Assert.NotNull(updated.ContentScope);
        Assert.Equal(new[] { "main" }, updated.ContentScope!.IncludeSelectors);
        Assert.NotNull(updated.Rewrite);
        Assert.Single(updated.Rewrite!.Rules);
        Assert.Equal("staging.example.com", updated.Rewrite.HostMappings[0].FromHost);
        Assert.NotNull(updated.Consent);
        Assert.True(updated.Consent!.ConsentModeV2Enabled);
        Assert.True(updated.Consent.BeaconDetectionEnabled);
        Assert.NotNull(updated.ZoneWeights);
        Assert.Equal(0.2, updated.ZoneWeights!.ZoneWeights["footer"]);
        Assert.True(updated.ZoneWeights.SerpMetricsEnabled);

        // A second subset update keeps untouched extension settings.
        var keep = await (await host.Client.PutJsonAsync($"/api/projects/{created.Id}", new ProjectUpdateRequest(
            null, null, null, null, null, null, null, null))).ReadDataAsync<ProjectWire>();
        Assert.NotNull(keep.ContentScope);
        Assert.NotNull(keep.Rewrite);
        Assert.Equal("Renamed", keep.Name);
    }

    [Fact]
    public async Task Projects_Delete_SoftDeletes_AndHidesFromList()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await (await host.Client.PostJsonAsync("/api/projects", new ProjectCreateRequest("Doomed", null, null, null)))
            .ReadDataAsync<ProjectWire>();

        var delete = await host.Client.DeleteAsync($"/api/projects/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var confirmation = await delete.ReadDataAsync<DeletionConfirmationWire>();
        Assert.Equal(created.Id, confirmation.Id);
        Assert.True(confirmation.Deleted);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/projects/{created.Id}")).StatusCode);
        var list = await (await host.Client.GetAsync("/api/projects")).ReadDataAsync<List<ProjectSummaryWire>>();
        Assert.Empty(list);
    }

    [Fact]
    public async Task Configurations_Create_ReturnsFullConfiguration_WithDefaults()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/configurations", new ConfigurationCreateRequest(
            "Full crawl",
            "Baseline configuration",
            new[] { "https://example.com/" },
            new CrawlLimitsWire(500, 3, 600, 2_000_000),
            null,
            new RenderingSettingsWire(true, "desktop", true, false),
            null,
            null,
            null, null, null, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var configuration = await response.ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal(project.Id.ToString(), configuration.ProjectId);
        Assert.Equal("Full crawl", configuration.Name);
        Assert.Equal("Baseline configuration", configuration.Description);
        Assert.Equal(new[] { "https://example.com/" }, configuration.StartUrls);
        Assert.Equal(500, configuration.Limits.MaxUrls);
        Assert.Equal(3, configuration.Limits.MaxDepth);
        Assert.Equal(600, configuration.Limits.MaxDurationSeconds);
        Assert.Equal(2_000_000, configuration.Limits.MaxResponseBytes);
        Assert.Equal(2.0, configuration.Aimd.InitialRateR0);
        Assert.Equal(4, configuration.Aimd.Kappa);
        Assert.True(configuration.Rendering.Enabled);
        Assert.Equal("desktop", configuration.Rendering.DeviceProfile);
        Assert.True(configuration.Rendering.InfiniteScroll);
        Assert.True(configuration.RespectRobotsTxt);
    }

    [Fact]
    public async Task Configurations_CreateInvalidSeeds_ReturnsFieldValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/configurations", new ConfigurationCreateRequest(
            "Bad", null, new[] { "not-a-url" }, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("startUrls[0]", out _));
    }

    [Fact]
    public async Task Configurations_CreateInvalidAimd_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/configurations", new ConfigurationCreateRequest(
            "Bad aimd", null, new[] { "https://example.com/" }, null,
            new AimdSettingsWire(2.0, 0.5, 1.5, 4, 0.2),
            null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("aimd.beta", out _));
    }

    [Fact]
    public async Task Configurations_CreateUnderUnknownProject_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/projects/999/configurations", new ConfigurationCreateRequest(
            "Orphan", null, new[] { "https://example.com/" }, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Configurations_ListAndUpdate_RoundTripSubset()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var created = await (await host.Client.PostJsonAsync($"/api/projects/{project.Id}/configurations", new ConfigurationCreateRequest(
            "Config", null, new[] { "https://example.com/" }, null, null, null, null, null, null, null, null, null)))
            .ReadDataAsync<AuditConfigurationWire>();

        var list = await (await host.Client.GetAsync($"/api/projects/{project.Id}/configurations")).ReadDataAsync<List<ConfigurationSummaryWire>>();
        Assert.Contains(list, c => c.Id == created.Id);

        var update = new ConfigurationUpdateRequest(
            Name: null,
            Description: "Updated description",
            StartUrls: null,
            Limits: new CrawlLimitsWire(null, null, null, 4096),
            Aimd: new AimdSettingsWire(1.5, 0.25, 0.7, 2, 0.3),
            Rendering: new RenderingSettingsWire(false, null, false, false),
            RespectRobotsTxt: false,
            VirtualRobotsTxt: "User-agent: *\nDisallow: /private",
            ContentScope: null, Rewrite: null, Consent: null, ZoneWeights: null);
        var response = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}", update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal("Updated description", updated.Description);
        Assert.Equal(4096, updated.Limits.MaxResponseBytes);
        Assert.Null(updated.Limits.MaxUrls);
        Assert.Equal(1.5, updated.Aimd.InitialRateR0);
        Assert.Equal(0.7, updated.Aimd.Beta);
        Assert.False(updated.Rendering.Enabled);
        Assert.False(updated.RespectRobotsTxt);
        Assert.Equal("User-agent: *\nDisallow: /private", updated.VirtualRobotsTxt);
        Assert.Equal(new[] { "https://example.com/" }, updated.StartUrls);
    }

    [Fact]
    public async Task Configurations_Delete_RemovesItem()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();

        var delete = await host.Client.DeleteAsync($"/api/configurations/{configuration.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var confirmation = await delete.ReadDataAsync<DeletionConfirmationWire>();
        Assert.True(confirmation.Deleted);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/configurations/{configuration.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/configurations/{configuration.Id}")).StatusCode);
    }

    [Fact]
    public async Task Suppressions_CreateAndDelete_AreReversible_AndIdempotent()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var create = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/suppressions", new SuppressionCreateRequest("title.missing", null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var state = await create.ReadDataAsync<SuppressionStateWire>();
        Assert.Equal(project.Id.ToString(), state.ProjectId);
        Assert.Equal("title.missing", state.RuleKey);
        Assert.True(state.Suppressed);
        Assert.Null(state.Scope);

        // Idempotent: the same active suppression returns the same key.
        var again = await (await host.Client.PostJsonAsync($"/api/projects/{project.Id}/suppressions", new SuppressionCreateRequest("title.missing", null)))
            .ReadDataAsync<SuppressionStateWire>();
        Assert.Equal(state.Key, again.Key);

        var delete = await host.Client.DeleteAsync($"/api/projects/{project.Id}/suppressions/{Uri.EscapeDataString(state.Key)}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var inactive = await delete.ReadDataAsync<SuppressionStateWire>();
        Assert.Equal(state.Key, inactive.Key);
        Assert.False(inactive.Suppressed);

        var secondDelete = await host.Client.DeleteAsync($"/api/projects/{project.Id}/suppressions/{Uri.EscapeDataString(state.Key)}");
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
    }

    [Fact]
    public async Task Suppressions_WithUrlPatternScope_RoundTripsScope()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/suppressions", new SuppressionCreateRequest(
            null, new SuppressionScopeWire(null, "/blog/*")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var state = await response.ReadDataAsync<SuppressionStateWire>();
        Assert.NotNull(state.Scope);
        Assert.Equal("/blog/*", state.Scope!.UrlPattern);
        Assert.Null(state.RuleKey);
    }

    [Fact]
    public async Task Suppressions_EmptyTarget_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync($"/api/projects/{project.Id}/suppressions", new SuppressionCreateRequest(null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Suppressions_UnknownProject_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/projects/999/suppressions", new SuppressionCreateRequest("rule", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
