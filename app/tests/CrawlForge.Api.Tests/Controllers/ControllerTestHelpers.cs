using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>Shared helpers for route-by-route contract tests.</summary>
internal static class ControllerTestHelpers
{
    /// <summary>Deserializes an enveloped payload with the API wire conventions.</summary>
    public static async Task<T> ReadDataAsync<T>(this HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(ApiJsonOptions.Default);
        Assert.NotNull(envelope);
        Assert.Null(envelope!.Error);
        return envelope.Data!;
    }

    /// <summary>Reads the raw JSON body of a response.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Asserts the frozen envelope wire shape: a data member is always present;
    /// pagination and error members are absent when null (never written as null).
    /// </summary>
    public static void AssertEnvelopeShape(JsonDocument document, bool expectError = false)
    {
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("data", out _), "The envelope must always carry a data member.");
        Assert.True(root.TryGetProperty("error", out _) == expectError, "The error member must be present exactly on error responses.");
        if (!expectError)
        {
            Assert.False(root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Null,
                "Null pagination must be omitted, not written as null.");
        }
    }

    /// <summary>Asserts a paged envelope carries the frozen pagination meta (offset/limit/total).</summary>
    public static void AssertPagination(JsonElement root, int offset, int limit, long total)
    {
        Assert.True(root.TryGetProperty("pagination", out var pagination), "Paged responses must carry pagination.");
        Assert.Equal(offset, pagination.GetProperty("offset").GetInt32());
        Assert.Equal(limit, pagination.GetProperty("limit").GetInt32());
        Assert.Equal(total, pagination.GetProperty("total").GetInt64());
        Assert.False(pagination.TryGetProperty("totalCount", out _), "The wire meta uses total, not totalCount.");
    }

    /// <summary>Asserts the frozen error wire shape: code, message, field→reasons map.</summary>
    public static void AssertErrorShape(JsonElement root, string code)
    {
        var error = root.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        if (error.TryGetProperty("fieldErrors", out var fieldErrors))
        {
            Assert.Equal(JsonValueKind.Object, fieldErrors.ValueKind);
            foreach (var property in fieldErrors.EnumerateObject())
            {
                Assert.Equal(JsonValueKind.Array, property.Value.ValueKind);
            }
        }
    }

    /// <summary>Asserts the frozen error wire shape directly from a response.</summary>
    public static async Task AssertErrorShape(HttpResponseMessage response, string code)
    {
        using var document = await response.ReadJsonAsync();
        AssertErrorShape(document.RootElement, code);
    }

    /// <summary>Posts a JSON body serialized with the API wire conventions.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiJsonOptions.Default);

    /// <summary>Puts a JSON body serialized with the API wire conventions.</summary>
    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, ApiJsonOptions.Default);

    /// <summary>Seeds a project with one default configuration through the real repositories.</summary>
    public static async Task<(ProjectDto Project, CrawlConfigurationDto Configuration)> SeedProjectAsync(
        this ControllerTestHost host, string name = "Test Project")
    {
        var now = DateTimeOffset.UtcNow;
        var project = await host.Get<IWriteRepository<ProjectDto>>().CreateAsync(
            new ProjectDto(0, name, null, null, now, now, null, false));
        var configuration = await host.Get<IWriteRepository<CrawlConfigurationDto>>().CreateAsync(
            new CrawlConfigurationDto(0, project.Id, "Default", "[\"https://example.com/\"]", null, null, null,
                RenderMode.None, null, null, null, null, null, now, now));
        return (project, configuration);
    }

    /// <summary>Seeds an audit row in the given status through the real repository.</summary>
    public static Task<AuditDto> SeedAuditAsync(
        this ControllerTestHost host,
        long projectId,
        long configurationId,
        AuditStatus status = AuditStatus.Completed,
        AuditPhase phase = AuditPhase.Finished,
        string countersJson = "{}")
    {
        var started = status == AuditStatus.Queued ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddMinutes(-5);
        var finished = status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed
            ? DateTimeOffset.UtcNow
            : (DateTimeOffset?)null;
        return host.Get<IWriteRepository<AuditDto>>().CreateAsync(
            new AuditDto(0, projectId, configurationId, "{}", status, phase, started, finished, countersJson, "test-engine", false));
    }

    /// <summary>Seeds a URL record through the real repository.</summary>
    public static Task<UrlRecordDto> SeedUrlAsync(
        this ControllerTestHost host,
        long auditId,
        string canonicalUrl,
        CrawlStatusClass statusClass = CrawlStatusClass.Success,
        int? statusCode = 200,
        int depth = 0)
    {
        var uri = new Uri(canonicalUrl);
        return host.Get<IWriteRepository<UrlRecordDto>>().CreateAsync(new UrlRecordDto(
            0, auditId, canonicalUrl, uri.Host, uri.AbsolutePath, depth, DiscoverySource.Seed,
            statusClass, statusCode, null, "text/html", 1000, null, RobotsState.Allowed,
            IndexabilityFlags.None, statusClass == CrawlStatusClass.Success));
    }

    /// <summary>Seeds a finding through the real repository.</summary>
    public static Task<FindingDto> SeedFindingAsync(
        this ControllerTestHost host,
        long auditId,
        string ruleKey,
        AuditCategory category = AuditCategory.Content,
        FindingSeverity severity = FindingSeverity.Warning,
        long? urlId = null,
        string evidenceJson = "{}")
    {
        return host.Get<IWriteRepository<FindingDto>>().CreateAsync(new FindingDto(
            0, auditId, ruleKey, category, severity, urlId, evidenceJson, null, false));
    }
}
