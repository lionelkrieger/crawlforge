using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for saved queries (body/query identifiers,
/// rule-tree validation), the user dictionary (duplicate 409, scoped removal),
/// and the local plugin registry (grants, rescan discovery, deletion).
/// </summary>
public sealed class QueryDictionaryPluginRouteTests
{
    private static readonly JsonElement ValidTree = JsonDocument.Parse(
        "{\"kind\":\"group\",\"operator\":\"and\",\"children\":[{\"kind\":\"condition\",\"field\":\"statusCode\",\"operator\":\"gte\",\"value\":400}]}").RootElement;

    [Fact]
    public async Task Queries_CreateListUpdateDelete_RoundTripRuleTree()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostAsJsonAsync("/api/queries", new { name = "Error pages", ruleTree = ValidTree });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<SavedQueryWire>();
        Assert.Equal("Error pages", created.Name);
        Assert.Equal("group", created.RuleTree.GetProperty("kind").GetString());
        Assert.True(created.CreatedAt > DateTimeOffset.UnixEpoch);

        var listed = await host.Client.GetAsync("/api/queries?search=error");
        using var document = await listed.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);

        var updated = await (await host.Client.PutAsJsonAsync("/api/queries", new { id = created.Id, name = "Renamed" }))
            .ReadDataAsync<SavedQueryWire>();
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("group", updated.RuleTree.GetProperty("kind").GetString());
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);

        var deleted = await host.Client.DeleteAsync($"/api/queries?id={created.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await deleted.ReadDataAsync<DeletionConfirmationWire>()).Deleted);
        Assert.Empty(await (await host.Client.GetAsync("/api/queries")).ReadDataAsync<List<SavedQueryWire>>());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/queries?id={created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Queries_ValidateRuleTree_AndIdentifier()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var badOperator = await host.Client.PostAsJsonAsync("/api/queries", new
        {
            name = "Bad",
            ruleTree = new { kind = "condition", field = "x", @operator = "regex", value = 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badOperator.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(badOperator, "validation");

        var badGroup = await host.Client.PostAsJsonAsync("/api/queries", new
        {
            name = "Bad",
            ruleTree = new { kind = "group", @operator = "xor", children = Array.Empty<object>() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badGroup.StatusCode);

        var missingId = await host.Client.PutAsJsonAsync("/api/queries", new { name = "No id" });
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);

        var unknownId = await host.Client.PutAsJsonAsync("/api/queries", new { id = "999", name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);

        var missingDeleteId = await host.Client.DeleteAsync("/api/queries");
        Assert.Equal(HttpStatusCode.BadRequest, missingDeleteId.StatusCode);
    }

    [Fact]
    public async Task Dictionary_AddListRemove_WithLanguageFilter()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var added = await host.Client.PostAsJsonAsync("/api/dictionary", new { word = "crawlforge", language = "en" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var confirmation = await added.ReadDataAsync<DictionaryMutationResponse>();
        Assert.True(confirmation.Added);
        Assert.Equal("crawlforge", confirmation.Word);

        await host.Client.PostAsJsonAsync("/api/dictionary", new { word = "crawlforge", language = "de" });
        await host.Client.PostAsJsonAsync("/api/dictionary", new { word = "audit", language = "en" });

        var english = await (await host.Client.GetAsync("/api/dictionary?language=en")).ReadDataAsync<List<DictionaryEntryWire>>();
        Assert.Equal(2, english.Count);
        Assert.All(english, e => Assert.Equal("en", e.Language));

        var duplicate = await host.Client.PostAsJsonAsync("/api/dictionary", new { word = "crawlforge", language = "en" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(duplicate, "conflict");

        var removedScoped = await host.Client.DeleteAsJsonAsync("/api/dictionary", new { word = "crawlforge", language = "de" });
        Assert.Equal(HttpStatusCode.OK, removedScoped.StatusCode);
        Assert.True((await removedScoped.ReadDataAsync<DictionaryMutationResponse>()).Removed);
        Assert.Equal(2, (await (await host.Client.GetAsync("/api/dictionary")).ReadDataAsync<List<DictionaryEntryWire>>()).Count);

        var removedAll = await host.Client.DeleteAsJsonAsync("/api/dictionary", new { word = "crawlforge" });
        Assert.Equal(HttpStatusCode.OK, removedAll.StatusCode);
        var remaining = await (await host.Client.GetAsync("/api/dictionary")).ReadDataAsync<List<DictionaryEntryWire>>();
        Assert.Single(remaining);
        Assert.Equal("audit", remaining[0].Word);

        var unknown = await host.Client.DeleteAsJsonAsync("/api/dictionary", new { word = "absent" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var missingWord = await host.Client.PostAsJsonAsync("/api/dictionary", new { word = "", language = "en" });
        Assert.Equal(HttpStatusCode.BadRequest, missingWord.StatusCode);
    }

    [Fact]
    public async Task Plugins_UpdateGrants_AndRejectCommands()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var pluginId = await SeedPluginAsync(host, "sample-plugin");

        var granted = await (await host.Client.PutAsJsonAsync($"/api/plugins/{pluginId}", new { grantPermissions = new[] { "read-data", "ui-contributions" } }))
            .ReadDataAsync<PluginRegistryEntryWire>();
        Assert.Equal(new[] { "read-data", "ui-contributions" }, granted.Permissions);

        var revoked = await (await host.Client.PutAsJsonAsync($"/api/plugins/{pluginId}", new { revokePermissions = new[] { "ui-contributions" }, enabled = false }))
            .ReadDataAsync<PluginRegistryEntryWire>();
        Assert.Equal(new[] { "read-data" }, revoked.Permissions);
        Assert.False(revoked.Enabled);
        Assert.Equal("disabled", revoked.State);

        var commands = await host.Client.PutAsJsonAsync($"/api/plugins/{pluginId}", new { grantPermissions = new[] { "commands" } });
        Assert.Equal(HttpStatusCode.BadRequest, commands.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(commands, "validation");

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync("/api/plugins/999", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task Plugins_Rescan_DiscoversManifests_AndPrunesVanished()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var root = PluginsController.PluginRoot;
        var pluginDir = Path.Combine(root, $"rescan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(pluginDir, "plugin.json"),
                "{\"name\":\"rescan-sample\",\"version\":\"1.2.3\",\"permissions\":[\"read-data\"]}");

            var response = await host.Client.PostAsync("/api/plugins/rescan", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var verdict = await response.ReadDataAsync<PluginRescanResponse>();
            Assert.Equal(1, verdict.Added);
            Assert.Equal(0, verdict.Removed);
            var entry = Assert.Single(verdict.Plugins);
            Assert.Equal("rescan-sample", entry.Name);
            Assert.Equal("1.2.3", entry.Version);
            Assert.Equal("pending", entry.State);
            Assert.True(entry.Enabled);
            Assert.Equal(new[] { "read-data" }, entry.Permissions);
            Assert.Equal(pluginDir, entry.Directory);

            var again = await (await host.Client.PostAsync("/api/plugins/rescan", null)).ReadDataAsync<PluginRescanResponse>();
            Assert.Equal(0, again.Added);
            Assert.Equal(0, again.Removed);

            Directory.Delete(pluginDir, recursive: true);
            var pruned = await (await host.Client.PostAsync("/api/plugins/rescan", null)).ReadDataAsync<PluginRescanResponse>();
            Assert.Equal(0, pruned.Added);
            Assert.Equal(1, pruned.Removed);
            Assert.Empty(pruned.Plugins);
        }
        finally
        {
            if (Directory.Exists(pluginDir))
            {
                Directory.Delete(pluginDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Plugins_Delete_RemovesEntry()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var pluginId = await SeedPluginAsync(host, "delete-me");

        var response = await host.Client.DeleteAsync($"/api/plugins/{pluginId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadDataAsync<DeletionConfirmationWire>()).Deleted);
        Assert.Empty(await (await host.Client.GetAsync("/api/plugins")).ReadDataAsync<List<PluginRegistryEntryWire>>());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/plugins/{pluginId}")).StatusCode);
    }

    private static async Task<string> SeedPluginAsync(ControllerTestHost host, string name)
    {
        var created = await host.Get<CrawlForge.Contracts.Persistence.IWriteRepository<CrawlForge.Contracts.Entities.UiPluginDto>>()
            .CreateAsync(new CrawlForge.Contracts.Entities.UiPluginDto(
                0, name, @"C:\plugins\" + name, "1.0.0", true, CrawlForge.Contracts.Enums.PluginPermission.None, "hash", "loaded", DateTimeOffset.UtcNow));
        return created.Id.ToString();
    }
}

/// <summary>DELETE-with-body helper for the dictionary routes.</summary>
internal static class DictionaryHttpExtensions
{
    public static async Task<HttpResponseMessage> DeleteAsJsonAsync<T>(this HttpClient client, string requestUri, T value)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, requestUri)
        {
            Content = JsonContent.Create(value),
        };
        return await client.SendAsync(request);
    }
}
