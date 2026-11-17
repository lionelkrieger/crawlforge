using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Defects D8 + D11 (task-47): framework client errors never bypass the locked
/// uniform envelope (validation fieldErrors map, 415 unsupported), and export
/// download labels match the locked writer bytes (§8.4 csv-is-zip, html is a
/// single document).
/// </summary>
public sealed class EnvelopeAndExportLabelTests
{
    [Fact]
    public async Task ValidationPost_ReturnsEnvelopeFieldErrorsMap_NotProblemDetails()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/projects", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Locked envelope shape — never RFC 9110 ProblemDetails.
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(root.TryGetProperty("traceId", out _));
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("status", out _));

        Assert.Equal("validation", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // fieldErrors is the frozen field→reasons map.
        var fieldErrors = error.GetProperty("fieldErrors");
        Assert.Equal(JsonValueKind.Object, fieldErrors.ValueKind);
        Assert.True(fieldErrors.EnumerateObject().Any());
        foreach (var entry in fieldErrors.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Array, entry.Value.ValueKind);
            Assert.True(entry.Value.GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task DeleteWithoutBody_Returns415Envelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        // DELETE /api/dictionary carries a required body; no content type must
        // surface as the envelope's unsupported error, not ProblemDetails.
        var response = await host.Client.DeleteAsync("/api/dictionary");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(root.TryGetProperty("traceId", out _));
        Assert.Equal("unsupported", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CsvDownload_LabelsMatchTheZipBytes()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var create = await host.Client.PostJsonAsync("/api/exports", new ExportCreateRequest(
            audit.Id.ToString(), "csv", ["urls"], null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var job = await create.ReadDataAsync<ExportCreateResponse>();

        var download = await host.Client.GetAsync($"/api/exports/{job.ExportId}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        // §8.4 locked writer: the CSV export is a ZIP archive of dataset CSVs.
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        var disposition = download.Content.Headers.ContentDisposition?.ToString() ?? "";
        Assert.Contains(".zip", disposition);
    }

    [Fact]
    public async Task HtmlDownload_LabelsMatchHtmlBytes()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var create = await host.Client.PostJsonAsync("/api/exports", new ExportCreateRequest(
            audit.Id.ToString(), "html", ["urls"], null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var job = await create.ReadDataAsync<ExportCreateResponse>();

        var download = await host.Client.GetAsync($"/api/exports/{job.ExportId}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        // The HTML export is a single self-contained document.
        Assert.Equal("text/html", download.Content.Headers.ContentType?.MediaType);
        var disposition = download.Content.Headers.ContentDisposition?.ToString() ?? "";
        Assert.Contains(".html", disposition);
    }
}
