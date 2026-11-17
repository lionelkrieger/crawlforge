using System.Net;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.Entitlement;

/// <summary>
/// Acceptance criterion #5: no license, account, or entitlement concept exists
/// in any API payload or settings row. Every fresh-install read surface and the
/// MCP automation catalog are scanned for the forbidden concept vocabulary.
/// The UI-screen static layer is covered by the governance analyzer's NR-02
/// full-tree scan (zero violations; see governance evidence).
/// </summary>
public sealed class NoEntitlementConceptTests
{
    private static readonly string[] ForbiddenConcepts =
    [
        "license", "licensing", "entitlement", "subscription", "account", "signin", "sign-in",
        "signup", "sign-up", "login", "log-in", "activation", "trial", "vendor", "paywall",
        "purchase", "billing",
    ];

    [Fact]
    public async Task FreshInstall_ApiPayloadsAndSettingsRows_CarryNoEntitlementConcepts()
    {
        await using var fixture = await BackendFixture.StartAsync();

        string[] routes =
        [
            "/api/health",
            "/api/version",
            "/api/projects",
            "/api/audits",
            "/api/settings",
            "/api/destinations",
            "/api/plugins",
            "/api/schedules",
            "/api/queries",
            "/api/diagnostics/version",
            "/api/diagnostics/artifacts",
            "/api/system/info",
            "/hub",
            "/metrics",
        ];

        var hits = new List<string>();
        var scanned = 0;
        foreach (var route in routes)
        {
            using var response = await fixture.Http.GetAsync(route);
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"Route {route} returned {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync();
            scanned++;
            foreach (var concept in ForbiddenConcepts)
            {
                if (body.Contains(concept, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{route}: '{concept}'");
                }
            }
        }

        // Settings rows directly from the local database (raw storage scan).
        var dbFiles = Directory.EnumerateFiles(fixture.DataDirectory, "*.db", SearchOption.AllDirectories).ToList();
        foreach (var file in dbFiles)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            foreach (var concept in ForbiddenConcepts)
            {
                if (text.Contains(concept, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"settings-db {Path.GetFileName(file)}: '{concept}'");
                }
            }
        }

        // MCP automation catalog: tools, resources, prompts (session handshake first).
        var (endpoint, token) = ReadMcpDescriptor(fixture.DataDirectory);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        string? sessionId;
        using (var initialize = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            initialize.Content = new StringContent(
                """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
                Encoding.UTF8, "application/json");
            initialize.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var initializeResponse = await client.SendAsync(initialize);
            Assert.Equal(HttpStatusCode.OK, initializeResponse.StatusCode);
            sessionId = initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var values)
                ? values.FirstOrDefault()
                : null;
        }

        foreach (var method in new[] { "tools/list", "resources/list", "prompts/list" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(
                $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}"}""", Encoding.UTF8, "application/json");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (sessionId is not null)
            {
                request.Headers.Add("Mcp-Session-Id", sessionId);
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            scanned++;
            foreach (var concept in ForbiddenConcepts)
            {
                if (body.Contains(concept, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"mcp {method}: '{concept}'");
                }
            }
        }

        EvidenceWriter.WriteMarkdown(
            "no-entitlement.md",
            $"""
            # Criterion #5 — no license/account/entitlement concept

            - API surfaces scanned: {routes.Length} (fresh install)
            - MCP catalogs scanned: tools/list, resources/list, prompts/list
            - Settings database raw scan: {dbFiles.Count} file(s)
            - Forbidden vocabulary: {string.Join(", ", ForbiddenConcepts)}
            - Concept hits: **{hits.Count}** (required: 0)
            - UI-screen static layer: governance NR-02 full-tree scan, zero violations

            Hits: {(hits.Count == 0 ? "none" : string.Join("; ", hits))}
            """);

        Assert.Empty(hits);
    }

    private static (string Endpoint, string Token) ReadMcpDescriptor(string dataDirectory)
    {
        using var descriptor = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "mcp-endpoint.json")));
        return (
            descriptor.RootElement.GetProperty("endpoint").GetString()!,
            descriptor.RootElement.GetProperty("token").GetString()!);
    }
}
