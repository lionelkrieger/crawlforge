using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the system surface: health, version,
/// metrics, settings (criterion #8), diagnostics, and the additive system
/// health/compact routes (FS-CRAWL-002 §7.2).
/// </summary>
public sealed class SystemSurfaceRouteTests
{
    [Fact]
    public async Task Health_ReturnsReadyFlagAndUptime_InUniformEnvelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("ready").GetBoolean());
        Assert.True(data.GetProperty("uptimeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task Version_ReturnsProductSchemaAndRuntimeProfile()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.ReadDataAsync<VersionResponse>();
        Assert.False(string.IsNullOrWhiteSpace(data.ProductVersion));
        Assert.True(data.SchemaVersion > 0);
        Assert.Equal("local-default", data.RuntimeProfile);
    }

    [Fact]
    public async Task Metrics_ReturnsRawTextExposition_NotEnveloped()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Metrics.Text = "# local technical metrics\ncrawlforge_test_family 7\n";

        var response = await host.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(host.Fakes.Metrics.Text, body);
        Assert.False(body.TrimStart().StartsWith('{'), "Metrics must not be JSON-enveloped.");
    }

    [Fact]
    public async Task Settings_Get_ReturnsGuaranteedDefaults_WithNoSecretsInClear()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("dark", data.GetProperty("theme").GetString());
        Assert.Equal("en", data.GetProperty("uiLanguage").GetString());
        Assert.Equal("sqlite", data.GetProperty("database").GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("database").GetProperty("password").ValueKind);
        Assert.False(data.GetProperty("proxy").GetProperty("enabled").GetBoolean());
        Assert.Equal("sse", data.GetProperty("mcp").GetProperty("transport").GetString());
        Assert.Equal("onnx", data.GetProperty("embeddings").GetProperty("provider").GetString());
        Assert.True(data.GetProperty("statusBar").GetProperty("workingSetAdvisoryBytes").GetInt64() > 0);
        Assert.True(data.GetProperty("statusBar").GetProperty("walAdvisoryBytes").GetInt64() > 0);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("license", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entitlement", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_PutSubset_MergesFields_AndMasksOutboundSecrets()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var update = new SettingsUpdateRequest(
            Theme: "light",
            UiLanguage: null,
            Database: null,
            Proxy: new ProxyUpdateRequest(true, "127.0.0.1", 8080, "proxy-user", "proxy-secret"),
            Mcp: new McpUpdateRequest(true, "stdio", "local-token-123"),
            Embeddings: null,
            StatusBar: null);
        var response = await host.Client.PutJsonAsync("/api/settings", update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.ReadDataAsync<AppSettingsWire>();
        Assert.Equal("light", data.Theme);
        Assert.Equal("en", data.UiLanguage);
        Assert.True(data.Proxy.Enabled);
        Assert.Equal("127.0.0.1", data.Proxy.Host);
        Assert.Equal(SettingsController.PasswordMask, data.Proxy.Password);
        Assert.True(data.Mcp.Enabled);
        Assert.Equal("stdio", data.Mcp.Transport);
        Assert.Equal("local-token-123", data.Mcp.Token);

        // The stored row never carries the clear secret; unrelated storage options survive.
        var settings = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        Assert.NotNull(settings);
        Assert.DoesNotContain("proxy-secret", settings!.StorageOptionsJson);
        Assert.Contains("enc:", settings.StorageOptionsJson);
        Assert.Contains("contentStoreLocation", settings.StorageOptionsJson);

        // A later subset update keeps the stored secret when the mask is sent back.
        var keep = await host.Client.PutJsonAsync("/api/settings", new SettingsUpdateRequest(
            null, null, null, new ProxyUpdateRequest(null, "127.0.0.2", null, null, SettingsController.PasswordMask), null, null, null));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var kept = await keep.ReadDataAsync<AppSettingsWire>();
        Assert.Equal("127.0.0.2", kept.Proxy.Host);
        Assert.Equal(SettingsController.PasswordMask, kept.Proxy.Password);
        var stored = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        Assert.Contains("enc:", stored!.StorageOptionsJson);
    }

    [Fact]
    public async Task Settings_DatabaseSubset_StoresEncryptedPassword_AndMasksOnRead()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PutJsonAsync("/api/settings", new SettingsUpdateRequest(
            null, null,
            new DatabaseUpdateRequest("mysql", "db.internal", 3306, "crawlforge", "db-user", "db-secret"),
            null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.ReadDataAsync<AppSettingsWire>();
        Assert.Equal("mysql", data.Database.Provider);
        Assert.Equal("db.internal", data.Database.Host);
        Assert.Equal(SettingsController.PasswordMask, data.Database.Password);

        var stored = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        Assert.Equal(DatabaseProviderKind.MySql, stored!.DatabaseProvider);
        Assert.DoesNotContain("db-secret", stored.DatabaseConnectionJson);
        Assert.Contains("enc:", stored.DatabaseConnectionJson);
    }

    [Fact]
    public async Task DatabaseTest_Sqlite_ReportsReachableAuthenticatedCompatible()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/settings/database/test", new DatabaseTestRequest("sqlite", null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<DatabaseTestResponse>();
        Assert.True(verdict.Reachable);
        Assert.True(verdict.Authenticated);
        Assert.True(verdict.MigrationsCompatible);
    }

    [Fact]
    public async Task DatabaseTest_MysqlIncomplete_ReturnsValidationWithFieldMap()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/settings/database/test", new DatabaseTestRequest("mysql", null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document, expectError: true);
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        var fieldErrors = document.RootElement.GetProperty("error").GetProperty("fieldErrors");
        Assert.True(fieldErrors.TryGetProperty("host", out _));
        Assert.True(fieldErrors.TryGetProperty("database", out _));
        Assert.True(fieldErrors.TryGetProperty("username", out _));
    }

    [Fact]
    public async Task DatabaseTest_UnknownProvider_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/settings/database/test", new DatabaseTestRequest("oracle", null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
    }

    [Fact]
    public async Task EmbeddingTest_NonLoopbackEndpoint_IsRejectedWithoutContact()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/settings/embedding/test", new EmbeddingTestRequest("http://example.com:11434", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "validation");
        Assert.True(document.RootElement.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("endpoint", out _));
    }

    [Fact]
    public async Task EmbeddingTest_LoopbackEndpoint_ReturnsVerdict()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        // Port 9 (discard) on loopback: no daemon listens, so the verdict is
        // reachable=false — a deterministic local outcome, never a remote call.
        var response = await host.Client.PostJsonAsync("/api/settings/embedding/test", new EmbeddingTestRequest("http://127.0.0.1:9", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<EmbeddingTestResponse>();
        Assert.True(verdict.IsLoopback);
        Assert.False(verdict.Reachable);
        Assert.False(verdict.ModelAvailable);
    }

    [Fact]
    public async Task Diagnostics_ListAndPurge_UseKindFilter_AndWireShape()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Diagnostics.Artifacts.Add(new(1, DiagnosticArtifactKind.Log, DateTimeOffset.UtcNow, 10, @"C:\logs\app.log"));
        host.Fakes.Diagnostics.Artifacts.Add(new(2, DiagnosticArtifactKind.Minidump, DateTimeOffset.UtcNow, 2048, @"C:\dumps\crash.dmp"));

        var all = await host.Client.GetAsync("/api/diagnostics/artifacts");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var items = await all.ReadDataAsync<List<DiagnosticArtifactWire>>();
        Assert.Equal(2, items.Count);
        Assert.Equal("app.log", items[0].FileName);
        Assert.Equal("log", items[0].Kind);
        Assert.Equal("minidump", items[1].Kind);

        var filtered = await host.Client.GetAsync("/api/diagnostics/artifacts?kind=minidump");
        var filteredItems = await filtered.ReadDataAsync<List<DiagnosticArtifactWire>>();
        Assert.Single(filteredItems);

        var invalid = await host.Client.GetAsync("/api/diagnostics/artifacts?kind=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var purge = await host.Client.DeleteAsync("/api/diagnostics/artifacts?kind=log");
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        var confirmation = await purge.ReadDataAsync<DiagnosticPurgeResponse>();
        Assert.Equal(1, confirmation.PurgedCount);
        Assert.Single(host.Fakes.Diagnostics.Artifacts);
    }

    [Fact]
    public async Task SystemHealth_ReturnsSnapshot_WithRunningAuditReference()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var idle = await host.Client.GetAsync("/api/system/health");
        Assert.Equal(HttpStatusCode.OK, idle.StatusCode);
        var idleData = await idle.ReadDataAsync<SystemHealthWire>();
        Assert.True(idleData.BackendWorkingSetBytes > 0);
        Assert.True(idleData.ManagedHeapBytes > 0);
        Assert.Null(idleData.AuditId);

        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);

        var running = await host.Client.GetAsync("/api/system/health");
        var runningData = await running.ReadDataAsync<SystemHealthWire>();
        Assert.Equal(audit.Id.ToString(), runningData.AuditId);
    }

    [Fact]
    public async Task SystemCompact_ReturnsBeforeAfterAndActions()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostAsync("/api/system/compact", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.ReadDataAsync<SystemCompactResponse>();
        Assert.True(data.Before.BackendWorkingSetBytes >= data.After.BackendWorkingSetBytes);
        Assert.Equal(
            new[] { "managed-gc-compaction", "wal-checkpoint-truncate", "working-set-trim" },
            data.ActionsPerformed);
        Assert.Equal(1, host.Fakes.Compact.Calls);

        // Locked wire shape (frozen system.ts SystemCompactResponse): before/after
        // snapshots with the spec field names, and a non-empty actionsPerformed array.
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document);
        var root = document.RootElement.GetProperty("data");
        AssertCompactSnapshot(root.GetProperty("before"));
        AssertCompactSnapshot(root.GetProperty("after"));
        var actions = root.GetProperty("actionsPerformed");
        Assert.Equal(JsonValueKind.Array, actions.ValueKind);
        Assert.True(actions.GetArrayLength() > 0);
        Assert.Equal("managed-gc-compaction", actions[0].GetString());
        Assert.Equal("wal-checkpoint-truncate", actions[1].GetString());
        Assert.Equal("working-set-trim", actions[2].GetString());
    }

    [Fact]
    public async Task SystemCompact_EmitsServiceActions_NotHardcoded()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var before = new SystemHealthSnapshot(100_000_000, 50_000_000, 1_000_000, DateTimeOffset.UtcNow);
        var after = new SystemHealthSnapshot(80_000_000, 40_000_000, 1_000_000, DateTimeOffset.UtcNow);
        // Degraded run: only the GC action performed (no WAL checkpoint, no trim).
        host.Fakes.Compact.Result = new CompactResult(before, after, DateTimeOffset.UtcNow, new[] { "managed-gc-compaction" });

        var response = await host.Client.PostAsync("/api/system/compact", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.ReadDataAsync<SystemCompactResponse>();
        Assert.Equal(new[] { "managed-gc-compaction" }, data.ActionsPerformed);
    }

    [Fact]
    public async Task SystemCompact_NullActions_NormalizesToEmptyArray()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var before = new SystemHealthSnapshot(100_000_000, 50_000_000, 1_000_000, DateTimeOffset.UtcNow);
        var after = new SystemHealthSnapshot(90_000_000, 45_000_000, 0, DateTimeOffset.UtcNow);
        host.Fakes.Compact.Result = new CompactResult(before, after, DateTimeOffset.UtcNow, ActionsPerformed: null);

        var response = await host.Client.PostAsync("/api/system/compact", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        var actions = document.RootElement.GetProperty("data").GetProperty("actionsPerformed");
        Assert.Equal(JsonValueKind.Array, actions.ValueKind);
        Assert.Equal(0, actions.GetArrayLength());
    }

    private static void AssertCompactSnapshot(JsonElement snapshot)
    {
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.True(snapshot.TryGetProperty("timestamp", out _));
        Assert.True(snapshot.TryGetProperty("backendWorkingSetBytes", out _));
        Assert.True(snapshot.TryGetProperty("managedHeapBytes", out _));
        Assert.True(snapshot.TryGetProperty("gcGen0Count", out _));
        Assert.True(snapshot.TryGetProperty("gcGen1Count", out _));
        Assert.True(snapshot.TryGetProperty("gcGen2Count", out _));
        Assert.True(snapshot.TryGetProperty("sqliteWalBytes", out _));
    }

    [Fact]
    public async Task SystemCompact_GuardRefusal_ReturnsConflict()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        host.Fakes.Compact.GuardException = new InvalidOperationException("Compaction is rate-limited; retry after 10 seconds.");

        var response = await host.Client.PostAsync("/api/system/compact", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertEnvelopeShape(document, expectError: true);
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "conflict");
    }
}
