using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Delivery;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Producer → frozen-consumer contract proofs (WP-09-10, G-F/G-I). The real
/// producer output (hub payloads captured from the real engine + delivery
/// over the real SQLite stack) is fed through the exact shape checks the
/// frontend guard applies (contracts/hub/guards.ts isExportDeliveryPayload:
/// record + string exportId + string destinationId + state ∈
/// {delivered, retrying, failed}). These are the payloads the lead's
/// ExportFormat/ExportDestination conformance suites consume.
/// </summary>
public sealed class ProducerContractTests : IAsyncLifetime
{
    private static readonly string[] DeliveryStates = ["delivered", "retrying", "failed"];
    private static readonly string[] JobStates = ["queued", "running", "completed", "failed"];

    private ExportTestStack _stack = null!;
    private string _filePath = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _stack = new ExportTestStack();
        _filePath = Path.Combine(Path.GetTempPath(), "cf-producer-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(_filePath, "url\nhttps://example.com/\n");
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _stack.DisposeAsync();
        File.Delete(_filePath);
    }

    /// <summary>Every captured export-delivery payload passes the frozen frontend guard shape.</summary>
    [Fact]
    public async Task DeliveryPayloads_Match_Frozen_Frontend_Guard()
    {
        var jobs = new ExportJobRegistry();
        var transport = new ScriptedTransport { FailuresBeforeSuccess = 1 }; // one retry → retrying + delivered
        var record = jobs.Create(1, ExportFormat.Csv, []);
        jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = _filePath });
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""");
        var delivery = new ExportDestinationDelivery(
            _stack.Provider, _stack.Protector, jobs, new ExportEventPublisher(_stack.Hub),
            new ExportOptions { MaxDeliveryAttempts = 3, DeliveryRetryBaseDelay = TimeSpan.FromMilliseconds(1) },
            _ => transport);

        await delivery.DeliverAsync(record.Id, destinationId);

        var events = _stack.Hub.Events.Where(e => e.Name == "export-delivery").ToList();
        Assert.NotEmpty(events);
        foreach (var hubEvent in events)
        {
            using var payload = JsonDocument.Parse(hubEvent.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            // guards.ts: isString(value.exportId) && isString(value.destinationId)
            Assert.Equal(JsonValueKind.String, root.GetProperty("exportId").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("destinationId").ValueKind);
            Assert.Equal(record.Id.ToString(), root.GetProperty("exportId").GetString());
            Assert.Equal(destinationId.ToString(), root.GetProperty("destinationId").GetString());
            // guards.ts: isOneOf(value.state, DELIVERY_STATES)
            Assert.Contains(root.GetProperty("state").GetString(), DeliveryStates);
            // No producer-only drift fields (exportJobId/attempt/timestamp).
            Assert.False(root.TryGetProperty("exportJobId", out _));
            Assert.False(root.TryGetProperty("attempt", out _));
            Assert.False(root.TryGetProperty("timestamp", out _));
        }

        Assert.Contains(events, e => e.PayloadJson.Contains("\"state\":\"retrying\""));
        Assert.Contains(events, e => e.PayloadJson.Contains("\"state\":\"delivered\""));
    }

    /// <summary>Failed delivery (disabled destination) still emits a guard-valid payload.</summary>
    [Fact]
    public async Task DisabledDestination_FailedPayload_Is_GuardValid()
    {
        var jobs = new ExportJobRegistry();
        var record = jobs.Create(1, ExportFormat.Csv, []);
        jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = _filePath });
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""", enabled: false);
        var delivery = new ExportDestinationDelivery(
            _stack.Provider, _stack.Protector, jobs, new ExportEventPublisher(_stack.Hub),
            new ExportOptions { MaxDeliveryAttempts = 1, DeliveryRetryBaseDelay = TimeSpan.FromMilliseconds(1) },
            _ => new ScriptedTransport());

        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(record.Id, destinationId));

        var failed = _stack.Hub.Events.Single(e => e.Name == "export-delivery" && e.PayloadJson.Contains("\"state\":\"failed\""));
        using var payload = JsonDocument.Parse(failed.PayloadJson);
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("exportId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("destinationId").ValueKind);
    }

    /// <summary>
    /// export-progress payloads carry the proposed additive contract shape:
    /// string exportId/auditId, format wire key, state ∈ job vocabulary.
    /// (events.ts entry is the lead handoff; the producer emits it today.)
    /// </summary>
    [Fact]
    public async Task ProgressPayloads_Match_Proposed_Additive_Contract()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-producer-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auditId = await _stack.SeedAuditAsync();
            await _stack.SeedUrlAsync(auditId, "https://example.com/");
            using var engine = new ExportEngine(
                _stack.Provider, new ExportJobRegistry(), new ExportEventPublisher(_stack.Hub),
                new ExportOptions { OutputDirectory = outputDirectory });
            var job = await engine.CreateExportAsync(new Contracts.Api.ExportRequest(auditId, ExportFormat.Csv, []));
            await engine.GetJobTask(job.Id);

            var events = _stack.Hub.Events.Where(e => e.Name == "export-progress").ToList();
            Assert.NotEmpty(events);
            foreach (var hubEvent in events)
            {
                using var payload = JsonDocument.Parse(hubEvent.PayloadJson);
                var root = payload.RootElement;
                Assert.Equal(JsonValueKind.String, root.GetProperty("exportId").ValueKind);
                Assert.Equal(JsonValueKind.String, root.GetProperty("auditId").ValueKind);
                Assert.Equal(job.Id.ToString(), root.GetProperty("exportId").GetString());
                Assert.Equal(auditId.ToString(), root.GetProperty("auditId").GetString());
                Assert.Equal("csv", root.GetProperty("format").GetString());
                Assert.Contains(root.GetProperty("state").GetString(), JobStates);
                Assert.False(root.TryGetProperty("status", out _));
                Assert.False(root.TryGetProperty("exportJobId", out _));
            }

            Assert.Contains(events, e => e.PayloadJson.Contains("\"state\":\"completed\""));
        }
        finally
        {
            try
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>Typed payload records serialize to exactly the contract fields.</summary>
    [Fact]
    public void TypedPayloads_Serialize_To_Exact_Contract_Fields()
    {
        var delivery = new ExportDeliveryPayload("12", "34", "delivered");
        using var deliveryJson = JsonDocument.Parse(JsonSerializer.Serialize(delivery, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(["destinationId", "exportId", "state"], deliveryJson.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());

        var progress = new ExportProgressPayload("12", "7", "json", "completed", @"C:\exports\export-12.json", null);
        using var progressJson = JsonDocument.Parse(JsonSerializer.Serialize(progress, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        var names = progressJson.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["exportId", "auditId", "format", "state", "filePath"], names);
        Assert.Equal(JsonValueKind.String, progressJson.RootElement.GetProperty("exportId").ValueKind);
    }
}