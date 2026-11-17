using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.E2E.Tests.Fixtures;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 — local plugin lifecycle fixture (FS-CRAWL-002 §6.7) against the
/// composed backend's richer loader: discovery is local-only (plugin.json
/// manifests under the plugin root), rescan records SHA-256 integrity hashes,
/// enablement/grants are explicit user actions, the "commands" grant is
/// rejected (no locked grant flag), rescans are idempotent, and vanished
/// plugin directories are removed from the registry. Fixture manifests live
/// in unique directories and are removed after each test.
/// </summary>
public sealed class PluginLifecycleE2ETests
{
    [Fact]
    public async Task FullLifecycle_Discover_Grant_Revoke_CommandsRejected_RescanIdempotent_VanishRemoves()
    {
        var pluginName = "cf-e2e-lifecycle-" + Guid.NewGuid().ToString("N")[..8];
        var pluginDirectory = Path.Combine(PluginsController.PluginRoot, pluginName);
        Directory.CreateDirectory(pluginDirectory);
        var manifest = $$"""
        {
          "name": "{{pluginName}}",
          "version": "1.2.3",
          "entry": "index.js",
          "permissions": ["read-data", "ui-contributions"]
        }
        """;
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        await File.WriteAllBytesAsync(Path.Combine(pluginDirectory, "plugin.json"), manifestBytes);

        try
        {
            await using var backend = await E2EBackend.StartAsync();

            // Local discovery registers the manifest with its integrity hash.
            var rescan = await backend.Http.PostAsync("/api/plugins/rescan", content: null);
            var rescanBody = await rescan.Content.ReadAsStringAsync();
            Assert.True(rescan.IsSuccessStatusCode, rescanBody);
            using (var rescanDocument = JsonDocument.Parse(rescanBody))
            {
                var data = rescanDocument.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("added").GetInt32() >= 1);
                Assert.Contains(pluginName, data.GetProperty("plugins").EnumerateArray()
                    .Select(p => p.GetProperty("name").GetString()));
            }

            var entry = await FindPluginAsync(backend, pluginName);
            Assert.Equal("1.2.3", entry.GetProperty("version").GetString());
            Assert.Equal(pluginDirectory, entry.GetProperty("directory").GetString());
            var pluginId = entry.GetProperty("id").GetString()!;

            // The stored integrity hash matches the manifest bytes (SHA-256).
            var stored = await backend.Service<IReadRepository<UiPluginDto>>().GetByIdAsync(long.Parse(pluginId));
            Assert.NotNull(stored);
            var expectedHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            Assert.Equal(expectedHash, stored!.IntegrityHash, StringComparer.OrdinalIgnoreCase);

            // Explicit disablement reflects in the wire state.
            var disabled = await PutPluginAsync(backend, pluginId, new { enabled = false });
            Assert.False(disabled.GetProperty("enabled").GetBoolean());
            Assert.Equal("disabled", disabled.GetProperty("state").GetString());

            // Revocation removes exactly the revoked grant.
            var revoked = await PutPluginAsync(backend, pluginId, new { revokePermissions = new[] { "ui-contributions" } });
            Assert.Equal(
                new[] { "read-data" },
                revoked.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());

            // The "commands" grant has no locked flag and must be rejected.
            var commands = await backend.Http.PutAsJsonAsync($"/api/plugins/{pluginId}", new
            {
                grantPermissions = new[] { "commands" }
            });
            Assert.Equal(HttpStatusCode.BadRequest, commands.StatusCode);
            var commandsEnvelope = await WireJson.EnvelopeAsync(commands);
            Assert.Equal("validation", commandsEnvelope.GetProperty("error").GetProperty("code").GetString());

            // Unknown grant values are rejected too.
            var unknown = await backend.Http.PutAsJsonAsync($"/api/plugins/{pluginId}", new
            {
                grantPermissions = new[] { "remote-code" }
            });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

            // Re-enable and grant again.
            var enabled = await PutPluginAsync(backend, pluginId, new
            {
                enabled = true,
                grantPermissions = new[] { "ui-contributions" }
            });
            Assert.True(enabled.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                new[] { "read-data", "ui-contributions" },
                enabled.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());

            // Rescan is idempotent: no duplicate, grants/enablement preserved.
            var rescanAgain = await backend.Http.PostAsync("/api/plugins/rescan", content: null);
            Assert.True(rescanAgain.IsSuccessStatusCode);
            using (var againDocument = JsonDocument.Parse(await rescanAgain.Content.ReadAsStringAsync()))
            {
                Assert.Equal(0, againDocument.RootElement.GetProperty("data").GetProperty("added").GetInt32());
            }
            var list = await WireJson.EnvelopeAsync(await backend.Http.GetAsync("/api/plugins"));
            var matches = list.GetProperty("data").EnumerateArray()
                .Where(p => p.GetProperty("name").GetString() == pluginName)
                .ToList();
            Assert.Single(matches);
            Assert.True(matches[0].GetProperty("enabled").GetBoolean());
            Assert.Equal(
                new[] { "read-data", "ui-contributions" },
                matches[0].GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());

            // Vanished directory: the next rescan removes the registry entry.
            Directory.Delete(pluginDirectory, recursive: true);
            var rescanAfterVanish = await backend.Http.PostAsync("/api/plugins/rescan", content: null);
            Assert.True(rescanAfterVanish.IsSuccessStatusCode);
            using (var vanishDocument = JsonDocument.Parse(await rescanAfterVanish.Content.ReadAsStringAsync()))
            {
                var data = vanishDocument.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("removed").GetInt32() >= 1);
                Assert.DoesNotContain(pluginName, data.GetProperty("plugins").EnumerateArray()
                    .Select(p => p.GetProperty("name").GetString()));
            }
        }
        finally
        {
            if (Directory.Exists(pluginDirectory))
            {
                Directory.Delete(pluginDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rescan_SkipsMalformedManifests_AndUnknownPluginsAreNotFound()
    {
        var goodName = "cf-e2e-good-" + Guid.NewGuid().ToString("N")[..8];
        var badName = "cf-e2e-bad-" + Guid.NewGuid().ToString("N")[..8];
        var goodDirectory = Path.Combine(PluginsController.PluginRoot, goodName);
        var badDirectory = Path.Combine(PluginsController.PluginRoot, badName);
        Directory.CreateDirectory(goodDirectory);
        Directory.CreateDirectory(badDirectory);
        await File.WriteAllTextAsync(Path.Combine(goodDirectory, "plugin.json"),
            $$"""{"name":"{{goodName}}","version":"0.1.0"}""");
        await File.WriteAllTextAsync(Path.Combine(badDirectory, "plugin.json"), "{ not valid json !!");

        try
        {
            await using var backend = await E2EBackend.StartAsync();
            var rescan = await backend.Http.PostAsync("/api/plugins/rescan", content: null);
            var rescanBody = await rescan.Content.ReadAsStringAsync();
            Assert.True(rescan.IsSuccessStatusCode, rescanBody);
            using (var document = JsonDocument.Parse(rescanBody))
            {
                var names = document.RootElement.GetProperty("data").GetProperty("plugins")
                    .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
                Assert.Contains(goodName, names);
                Assert.DoesNotContain(badName, names);
            }

            var missing = await backend.Http.PutAsJsonAsync("/api/plugins/999999", new { enabled = true });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var missingEnvelope = await WireJson.EnvelopeAsync(missing);
            Assert.Equal("not-found", missingEnvelope.GetProperty("error").GetProperty("code").GetString());

            var missingDelete = await backend.Http.DeleteAsync("/api/plugins/999999");
            Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        }
        finally
        {
            Directory.Delete(goodDirectory, recursive: true);
            Directory.Delete(badDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Discovery_NetworkDeclarations_NeverBecomeActiveGrants()
    {
        // Locked consent invariant (§6.3/§6.4/§6.7/§6.8): a discovered plugin
        // declaring manifest network destinations must hold NO active network
        // grant — consent is shell-local and default-deny. The backend registry
        // has no network grant flag at all (PluginPermission: read-data /
        // ui-contributions only), so discovery cannot auto-activate network.
        var pluginName = "cf-e2e-net-" + Guid.NewGuid().ToString("N")[..8];
        var pluginDirectory = Path.Combine(PluginsController.PluginRoot, pluginName);
        Directory.CreateDirectory(pluginDirectory);
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "plugin.json"), $$"""
        {
          "name": "{{pluginName}}",
          "version": "1.0.0",
          "entry": "index.js",
          "permissions": {
            "read-data": true,
            "network": { "mode": "destinations", "destinationRefs": ["dest-1"] }
          }
        }
        """);

        try
        {
            await using var backend = await E2EBackend.StartAsync();
            var rescan = await backend.Http.PostAsync("/api/plugins/rescan", content: null);
            Assert.True(rescan.IsSuccessStatusCode, await rescan.Content.ReadAsStringAsync());

            var entry = await FindPluginAsync(backend, pluginName);
            var permissions = entry.GetProperty("permissions").EnumerateArray()
                .Select(p => p.GetString()).ToList();
            Assert.DoesNotContain("network", permissions);
            Assert.DoesNotContain("network-destinations", permissions);
            Assert.All(permissions, p => Assert.Contains(p, new[] { "read-data", "ui-contributions" }));
            var pluginId = entry.GetProperty("id").GetString()!;

            // No REST path can activate a network grant: no locked flag exists.
            foreach (var key in new[] { "network", "network-destinations", "destinations" })
            {
                var attempt = await backend.Http.PutAsJsonAsync($"/api/plugins/{pluginId}", new
                {
                    grantPermissions = new[] { key }
                });
                Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
                var envelope = await WireJson.EnvelopeAsync(attempt);
                Assert.Equal("validation", envelope.GetProperty("error").GetProperty("code").GetString());
            }
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<JsonElement> FindPluginAsync(E2EBackend backend, string name)
    {
        var list = await WireJson.EnvelopeAsync(await backend.Http.GetAsync("/api/plugins"));
        return list.GetProperty("data").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == name)
            .Clone();
    }

    private static async Task<JsonElement> PutPluginAsync(E2EBackend backend, string pluginId, object request)
    {
        var response = await backend.Http.PutAsJsonAsync($"/api/plugins/{pluginId}", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Plugin update failed: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }
}
