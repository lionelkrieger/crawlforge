using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 — REST envelope conformance across the ENTIRE route surface
/// (FS-CRAWL-001 §10.1/§10.4): the inventory is discovered by reflection over
/// the composed API controllers (88 locked baseline + additive WP-06 growth),
/// every route is exercised against the live backend, and every response must
/// carry the uniform envelope — data always present on success,
/// pagination/error omitted when null, pagination.total, error.code from the
/// stable set, and fieldErrors as a field→reasons map. Enum wire values are
/// lowercase without hyphens across every contract enum, with the single
/// locked exception catch-up. The full-sweep and fieldErrors tests flip green
/// when defects D8 (ProblemDetails bypass) and D9 (tools activation 500) land.
/// </summary>
public sealed class RestEnvelopeConformanceE2ETests
{
    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "validation", "not-found", "conflict", "invalid-state", "unsupported", "internal"
    };

    [Fact]
    public void RouteInventory_CoversTheLockedSurface_AndAdditiveGrowth()
    {
        var routes = DiscoverRoutes();
        // 88 locked baseline routes (WP-05-01) plus additive WP-06 surfaces
        // (explorer, matrix, semantics, governance, tools, diagnostics...).
        Assert.True(routes.Count >= 88,
            $"The locked 88-route baseline shrank: found {routes.Count}.");
        Assert.True(routes.Count == routes.Distinct().Count(), "Duplicate route definitions.");
    }

    [Fact]
    public async Task EveryRoute_ReturnsTheUniformEnvelope()
    {
        await using var backend = await E2EBackend.StartAsync();
        var violations = new List<string>();
        var binaryRoutes = 0;

        foreach (var (method, path) in DiscoverRoutes())
        {
            var concretePath = Regex.Replace(path, @"\{[^}]*\}", "999999");
            var url = "/" + concretePath.TrimStart('/');
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = JsonContent.Create(new { });
                }

                using var response = await backend.Http.SendAsync(request);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var body = await response.Content.ReadAsStringAsync();

                // Binary artifact routes (downloads) intentionally bypass the
                // JSON envelope; they are verified in the export-validity suite.
                if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    if (url.Contains("download", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("archive", StringComparison.OrdinalIgnoreCase))
                    {
                        binaryRoutes++;
                        if ((int)response.StatusCode >= 500)
                        {
                            violations.Add($"{method} {url}: binary route returned {(int)response.StatusCode}");
                        }
                        continue;
                    }
                    violations.Add($"{method} {url}: {(int)response.StatusCode} non-JSON content '{contentType}' on a non-binary route (body: {Truncate(body)})");
                    continue;
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    violations.Add($"{method} {url}: envelope root is {root.ValueKind}");
                    continue;
                }

                var status = (int)response.StatusCode;
                if (status is >= 200 and < 300)
                {
                    if (!root.TryGetProperty("data", out _))
                    {
                        violations.Add($"{method} {url}: success envelope missing data");
                    }
                    if (root.TryGetProperty("error", out var successError) && successError.ValueKind != JsonValueKind.Null)
                    {
                        violations.Add($"{method} {url}: success envelope carries an error");
                    }
                    if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in new[] { "offset", "limit", "total" })
                        {
                            if (!pagination.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number)
                            {
                                violations.Add($"{method} {url}: pagination missing numeric {field}");
                            }
                        }
                    }
                }
                else if (status is >= 400 and < 600)
                {
                    if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                    {
                        violations.Add($"{method} {url}: {(int)status} without an error object (body: {Truncate(body)})");
                        continue;
                    }
                    if (!error.TryGetProperty("code", out var code) || !ErrorCodes.Contains(code.GetString() ?? ""))
                    {
                        violations.Add($"{method} {url}: unknown error code '{code.GetString()}'");
                    }
                    if (!error.TryGetProperty("message", out var message) ||
                        message.ValueKind != JsonValueKind.String ||
                        string.IsNullOrEmpty(message.GetString()))
                    {
                        violations.Add($"{method} {url}: error message missing");
                    }
                    if (error.TryGetProperty("fieldErrors", out var fieldErrors) && fieldErrors.ValueKind != JsonValueKind.Null)
                    {
                        if (fieldErrors.ValueKind != JsonValueKind.Object)
                        {
                            violations.Add($"{method} {url}: fieldErrors is {fieldErrors.ValueKind}, expected field→reasons map");
                        }
                        else
                        {
                            foreach (var entry in fieldErrors.EnumerateObject())
                            {
                                if (entry.Value.ValueKind != JsonValueKind.Array ||
                                    entry.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                                {
                                    violations.Add($"{method} {url}: fieldErrors['{entry.Name}'] is not a string array");
                                }
                            }
                        }
                    }
                }
                else
                {
                    violations.Add($"{method} {url}: unexpected status {status}");
                }
            }
            catch (Exception ex)
            {
                violations.Add($"{method} {url}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(violations.Count == 0,
            $"{violations.Count} envelope violations ({binaryRoutes} binary routes exempt):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void EveryContractEnum_SerializesLowercaseNoHyphen_ExceptCatchUp()
    {
        var contractAssembly = typeof(CrawlForge.Contracts.Common.ApiEnvelope<>).Assembly;
        var enumTypes = contractAssembly.GetTypes().Where(t => t.IsEnum).ToList();
        Assert.NotEmpty(enumTypes);

        foreach (var enumType in enumTypes)
        {
            var converterType = typeof(ApiEnumConverter<>).MakeGenericType(enumType);
            var toWire = converterType.GetMethod("ToWireValue", BindingFlags.Public | BindingFlags.Static)!;
            var tryParse = converterType.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static)!;

            foreach (var value in Enum.GetValues(enumType))
            {
                var wire = (string)toWire.Invoke(null, [value])!;
                if (enumType == typeof(ScheduleTriggerType) && (ScheduleTriggerType)value == ScheduleTriggerType.CatchUp)
                {
                    Assert.Equal("catch-up", wire);
                }
                else
                {
                    Assert.True(Regex.IsMatch(wire, "^[a-z0-9]+$"),
                        $"{enumType.Name}.{value} wire value '{wire}' is not lowercase-no-hyphen.");
                }

                // Round-trip: every wire value parses back to the same enum value.
                var parameters = new object?[] { wire, null };
                var parsed = (bool)tryParse.Invoke(null, parameters)!;
                Assert.True(parsed, $"{enumType.Name} wire value '{wire}' does not round-trip.");
                Assert.Equal(value, parameters[1]);
            }
        }
    }

    [Fact]
    public async Task LiveWire_PaginationTotal_OmittedNulls_AndLowercaseEnums()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Envelope index", "<p>Envelope fixture.</p>"))));
        await using var backend = await E2EBackend.StartAsync();

        // Empty list: pagination.total present, error omitted.
        var projects = await backend.Http.GetAsync("/api/projects");
        var projectsBody = await projects.Content.ReadAsStringAsync();
        Assert.True(projects.IsSuccessStatusCode, projectsBody);
        using (var document = JsonDocument.Parse(projectsBody))
        {
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("data", out _));
            Assert.False(root.TryGetProperty("error", out _), "null error must be omitted.");
            var pagination = root.GetProperty("pagination");
            Assert.Equal(0, pagination.GetProperty("total").GetInt64());
        }

        // Not-found: stable code, no data payload.
        var missing = await backend.Http.GetAsync("/api/audits/999999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        var missingEnvelope = await WireJson.EnvelopeAsync(missing);
        Assert.Equal("not-found", missingEnvelope.GetProperty("error").GetProperty("code").GetString());

        // Live audit: state/severity wire values are lowercase-no-hyphen.
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var audits = await WireJson.EnvelopeAsync(await backend.Http.GetAsync("/api/audits"));
        var state = audits.GetProperty("data").EnumerateArray()
            .First(a => a.GetProperty("id").GetString() == auditId.ToString())
            .GetProperty("state").GetString();
        Assert.Equal("completed", state);

        var findings = await WireJson.EnvelopeAsync(await backend.Http.GetAsync($"/api/audits/{auditId}/findings?limit=100"));
        foreach (var finding in findings.GetProperty("data").EnumerateArray())
        {
            Assert.Matches("^[a-z0-9]+$", finding.GetProperty("severity").GetString()!);
            Assert.Matches("^[a-z0-9]+$", finding.GetProperty("category").GetString()!);
        }
        Assert.True(findings.GetProperty("pagination").GetProperty("total").GetInt64() >= 0);
    }

    [Fact]
    public async Task ValidationErrors_UseTheEnvelopeFieldErrorsMap()
    {
        // §10.4 over the live surface: binding/validation failures must carry
        // error.code=validation with a field→reasons map. Flips green when
        // defect D8 (framework ProblemDetails bypass on 18 routes) is fixed.
        await using var backend = await E2EBackend.StartAsync();

        var invalid = await backend.Http.PostAsJsonAsync("/api/projects", new { });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidEnvelope = await WireJson.EnvelopeAsync(invalid);
        var error = invalidEnvelope.GetProperty("error");
        Assert.Equal("validation", error.GetProperty("code").GetString());
        var fieldErrors = error.GetProperty("fieldErrors");
        Assert.Equal(JsonValueKind.Object, fieldErrors.ValueKind);
        foreach (var entry in fieldErrors.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Array, entry.Value.ValueKind);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<(string Method, string Path)> DiscoverRoutes()
    {
        var routes = new List<(string Method, string Path)>();
        foreach (var type in typeof(ApiControllerBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ApiControllerBase).IsAssignableFrom(t)))
        {
            var controllerTemplate = type.GetCustomAttribute<RouteAttribute>()?.Template ?? "";
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes())
                {
                    if (attribute is HttpMethodAttribute http)
                    {
                        var path = Combine(controllerTemplate, http.Template ?? "");
                        routes.Add((http.HttpMethods.First(), path));
                    }
                }
            }
        }
        return routes
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToList();
    }

    private static string Combine(string controller, string action)
    {
        var left = controller.Trim('/');
        var right = action.Trim('/');
        return right.Length == 0 ? left : $"{left}/{right}";
    }

    private static string Truncate(string body) => body.Length <= 200 ? body : body[..200];
}
