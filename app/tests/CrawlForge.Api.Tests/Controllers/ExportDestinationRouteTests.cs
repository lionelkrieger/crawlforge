using System.Net;
using System.Net.Http.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for export jobs and user-configured
/// destinations: envelope shapes, format/state wire keys, credential masking,
/// subset merge, delivery delegation, and the non-enveloped file stream.
/// </summary>
public sealed class ExportDestinationRouteTests
{
    [Fact]
    public async Task Exports_Create_ReturnsImmediately_WithWireFormatAndState()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.PostAsJsonAsync("/api/exports", new
        {
            auditId = audit.Id.ToString(),
            format = "xlsx",
            datasets = new[] { "urls", "findings", "waf-blocks" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<ExportCreateResponse>();
        Assert.Equal("completed", created.State);
        Assert.True(long.TryParse(created.ExportId, out var exportId));

        var job = await (await host.Client.GetAsync($"/api/exports/{exportId}")).ReadDataAsync<ExportJobWire>();
        Assert.Equal(audit.Id.ToString(), job.AuditId);
        Assert.Equal("xlsx", job.Format);
        Assert.Equal("completed", job.State);
        Assert.Equal(new[] { "urls", "findings", "waf-blocks" }, job.Datasets);
        Assert.NotNull(job.FilePath);
    }

    [Fact]
    public async Task Exports_Create_ValidatesAuditFormatAndDatasets()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var unknownAudit = await host.Client.PostAsJsonAsync("/api/exports", new { auditId = "999", format = "csv", datasets = new[] { "urls" } });
        Assert.Equal(HttpStatusCode.NotFound, unknownAudit.StatusCode);

        var badFormat = await host.Client.PostAsJsonAsync("/api/exports", new { auditId = audit.Id.ToString(), format = "docx", datasets = new[] { "urls" } });
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(badFormat, "validation");

        var emptyDatasets = await host.Client.PostAsJsonAsync("/api/exports", new { auditId = audit.Id.ToString(), format = "csv", datasets = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, emptyDatasets.StatusCode);
    }

    [Fact]
    public async Task Exports_Create_WithDestination_DeliversCompletedJob()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var destination = await CreateDestinationAsync(host, "Local drop", "local-folder", Path.GetTempPath());

        var response = await host.Client.PostAsJsonAsync("/api/exports", new
        {
            auditId = audit.Id.ToString(),
            format = "csv",
            datasets = new[] { "urls" },
            destinationId = destination.Id,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<ExportCreateResponse>();
        Assert.True(host.Fakes.Delivery.Deliveries.TryDequeue(out var delivery));
        Assert.Equal(created.ExportId, delivery.ExportJobId.ToString());
        Assert.Equal(destination.Id, delivery.DestinationId.ToString());
    }

    [Fact]
    public async Task Exports_File_StreamsBytes_WithoutEnvelope()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var created = await (await host.Client.PostAsJsonAsync("/api/exports", new { auditId = audit.Id.ToString(), format = "pdf", datasets = new[] { "urls" } }))
            .ReadDataAsync<ExportCreateResponse>();

        var response = await host.Client.GetAsync($"/api/exports/{created.ExportId}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }

    [Fact]
    public async Task Exports_File_IncompleteJob_ReturnsConflict()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        host.Fakes.Exports.CreateStatus = ExportJobStatus.Running;
        var created = await (await host.Client.PostAsJsonAsync("/api/exports", new { auditId = audit.Id.ToString(), format = "csv", datasets = new[] { "urls" } }))
            .ReadDataAsync<ExportCreateResponse>();

        var response = await host.Client.GetAsync($"/api/exports/{created.ExportId}/file");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "conflict");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/exports/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/exports/999/file")).StatusCode);
    }

    [Fact]
    public async Task Destinations_Create_MasksSecret_AndProjectsTarget()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostAsJsonAsync("/api/destinations", new
        {
            name = "My SFTP",
            kind = "sftp",
            target = "sftp://files.example.org:2222/backups",
            username = "backup-user",
            secret = "super-secret",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wire = await response.ReadDataAsync<DestinationWire>();
        Assert.Equal("sftp", wire.Kind);
        Assert.Equal("files.example.org:2222/backups", wire.Target);
        Assert.Equal("backup-user", wire.Username);
        Assert.Equal("********", wire.SecretMask);
        Assert.True(wire.CreatedAt > DateTimeOffset.UnixEpoch);
        Assert.Equal(wire.CreatedAt, wire.UpdatedAt);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret", body);

        var listed = await (await host.Client.GetAsync("/api/destinations")).ReadDataAsync<List<DestinationWire>>();
        Assert.Single(listed);
        Assert.Equal("********", listed[0].SecretMask);
    }

    [Fact]
    public async Task Destinations_Create_ValidatesKindAndTarget()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var badKind = await host.Client.PostAsJsonAsync("/api/destinations", new { name = "X", kind = "vendor-cloud", target = "C:\\x" });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        var badWebhook = await host.Client.PostAsJsonAsync("/api/destinations", new { name = "X", kind = "webhook", target = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, badWebhook.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(badWebhook, "validation");

        var missingName = await host.Client.PostAsJsonAsync("/api/destinations", new { name = "", kind = "local-folder", target = "C:\\x" });
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
    }

    [Fact]
    public async Task Destinations_Update_MergesSubset_AndClearsSecretOnEmpty()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await CreateDestinationAsync(host, "Webhook", "webhook", "https://hooks.example.org/ingest", secret: "token-1");

        var renamed = await (await host.Client.PutAsJsonAsync($"/api/destinations/{created.Id}", new { name = "Renamed" }))
            .ReadDataAsync<DestinationWire>();
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal("********", renamed.SecretMask);
        Assert.True(renamed.UpdatedAt >= renamed.CreatedAt);

        var cleared = await (await host.Client.PutAsJsonAsync($"/api/destinations/{created.Id}", new { secret = "" }))
            .ReadDataAsync<DestinationWire>();
        Assert.Null(cleared.SecretMask);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync("/api/destinations/999", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Destinations_Enabled_ToggleRoundTrip_AbsentKeeps_ListReflects()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await CreateDestinationAsync(host, "Drop", "local-folder", Path.GetTempPath());
        Assert.True(created.Enabled); // WP-06-A3: creation stays enabled.

        // Toggle off: reflected on the item wire and on the list.
        var disabled = await (await host.Client.PutAsJsonAsync($"/api/destinations/{created.Id}", new { enabled = false }))
            .ReadDataAsync<DestinationWire>();
        Assert.False(disabled.Enabled);
        var listed = await (await host.Client.GetAsync("/api/destinations")).ReadDataAsync<List<DestinationWire>>();
        Assert.False(Assert.Single(listed).Enabled);

        // Absent = keep: a rename-only update preserves enabled=false.
        var renamed = await (await host.Client.PutAsJsonAsync($"/api/destinations/{created.Id}", new { name = "Renamed" }))
            .ReadDataAsync<DestinationWire>();
        Assert.False(renamed.Enabled);
        Assert.Equal("Renamed", renamed.Name);

        // Toggle back on.
        var enabled = await (await host.Client.PutAsJsonAsync($"/api/destinations/{created.Id}", new { enabled = true }))
            .ReadDataAsync<DestinationWire>();
        Assert.True(enabled.Enabled);
    }

    [Fact]
    public async Task Destinations_Test_DelegatesToDelivery()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await CreateDestinationAsync(host, "Folder", "local-folder", Path.GetTempPath());
        host.Fakes.Delivery.TestResult = new Contracts.Api.DestinationTestResult(true, false, "Credentials rejected.");

        var response = await host.Client.PostAsync($"/api/destinations/{created.Id}/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.ReadDataAsync<DestinationTestResponse>();
        Assert.True(verdict.Reachable);
        Assert.False(verdict.CredentialsValid);
        Assert.Equal("Credentials rejected.", verdict.Message);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/api/destinations/999/test", null)).StatusCode);
    }

    [Fact]
    public async Task Destinations_Deliver_ChecksJobAndDestination()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var destination = await CreateDestinationAsync(host, "Folder", "local-folder", Path.GetTempPath());
        var export = await (await host.Client.PostAsJsonAsync("/api/exports", new { auditId = audit.Id.ToString(), format = "csv", datasets = new[] { "urls" } }))
            .ReadDataAsync<ExportCreateResponse>();

        var ok = await host.Client.PostAsJsonAsync($"/api/destinations/{destination.Id}/deliver", new { exportId = export.ExportId });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var verdict = await ok.ReadDataAsync<DestinationDeliverResponse>();
        Assert.Equal("delivered", verdict.State);

        var unknownJob = await host.Client.PostAsJsonAsync($"/api/destinations/{destination.Id}/deliver", new { exportId = "999" });
        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);

        host.Fakes.Delivery.DeliverException = new InvalidOperationException("Export job is not completed.");
        var conflict = await host.Client.PostAsJsonAsync($"/api/destinations/{destination.Id}/deliver", new { exportId = export.ExportId });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Destinations_Delete_RemovesRowAndMetadata()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var created = await CreateDestinationAsync(host, "Folder", "local-folder", Path.GetTempPath());

        var response = await host.Client.DeleteAsync($"/api/destinations/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await response.ReadDataAsync<DeletionConfirmationWire>();
        Assert.True(confirmation.Deleted);
        Assert.Empty(await (await host.Client.GetAsync("/api/destinations")).ReadDataAsync<List<DestinationWire>>());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/destinations/{created.Id}")).StatusCode);
    }

    private static async Task<DestinationWire> CreateDestinationAsync(
        ControllerTestHost host,
        string name,
        string kind,
        string target,
        string? username = null,
        string? secret = null)
    {
        var response = await host.Client.PostAsJsonAsync("/api/destinations", new { name, kind, target, username, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadDataAsync<DestinationWire>();
    }
}
