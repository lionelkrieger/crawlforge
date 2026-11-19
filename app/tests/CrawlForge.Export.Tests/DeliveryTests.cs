using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Delivery;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Delivery proofs (FS-CRAWL-001 §8.5): the delivered/retrying/failed state
/// machine with bounded backoff, hub emission per transition, encrypted
/// credentials at rest via the real AppEntropySecretProtector, and disabled
/// destinations that never reach a transport.
/// </summary>
public sealed class DeliveryTests : IAsyncLifetime
{
    private ExportTestStack _stack = null!;
    private ExportJobRegistry _jobs = null!;
    private ScriptedTransport _transport = null!;
    private string _filePath = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _stack = new ExportTestStack();
        _jobs = new ExportJobRegistry();
        _transport = new ScriptedTransport();
        _filePath = Path.Combine(Path.GetTempPath(), "cf-delivery-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(_filePath, "url\nhttps://example.com/\n");
        var record = _jobs.Create(1, ExportFormat.Csv, []);
        _jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = _filePath });
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _stack.DisposeAsync();
        File.Delete(_filePath);
    }

    private ExportDestinationDelivery CreateDelivery(ExportOptions? options = null)
        => new(_stack.Provider, _stack.Protector, _jobs, new ExportEventPublisher(_stack.Hub),
            options ?? new ExportOptions { MaxDeliveryAttempts = 3, DeliveryRetryBaseDelay = TimeSpan.FromMilliseconds(1) },
            _ => _transport);

    private long JobId => _jobs.Get(1)!.Id;

    /// <summary>Successful delivery: ledger delivering → delivered; hub carries
    /// only the frozen frontend states with string identifiers (G-F fix).</summary>
    [Fact]
    public async Task Deliver_Succeeds_With_State_Transitions()
    {
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""");
        var delivery = CreateDelivery();

        await delivery.DeliverAsync(JobId, destinationId);

        var entry = delivery.GetLedgerEntry(JobId, destinationId);
        Assert.NotNull(entry);
        Assert.Equal(ExportDeliveryStates.Delivered, entry.State);
        Assert.Equal(1, entry.Attempt);
        Assert.Single(_transport.Sends);
        var events = _stack.Hub.Events.Where(e => e.Name == ExportHubEventNames.ExportDelivery).Select(e => e.PayloadJson).ToList();
        Assert.Contains(events, p => p.Contains("\"state\":\"delivered\""));
        // G-F: "delivering" is internal ledger state — the frozen frontend
        // guard (guards.ts DELIVERY_STATES) rejects it, so it never hits the hub.
        Assert.DoesNotContain(events, p => p.Contains("\"state\":\"delivering\""));
        // Frozen wire shape: exportId + destinationId are STRINGS (EntityId).
        Assert.Contains(events, p => p.Contains($"\"exportId\":\"{JobId}\"") && p.Contains($"\"destinationId\":\"{destinationId}\""));
        Assert.DoesNotContain(events, p => p.Contains("exportJobId"));
    }

    /// <summary>Transient failures retry with bounded backoff, then deliver.</summary>
    [Fact]
    public async Task Deliver_Retries_Then_Succeeds()
    {
        _transport.FailuresBeforeSuccess = 2;
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""");
        var delivery = CreateDelivery();

        await delivery.DeliverAsync(JobId, destinationId);

        Assert.Equal(3, _transport.Sends.Count);
        Assert.Equal(ExportDeliveryStates.Delivered, delivery.GetLedgerEntry(JobId, destinationId)!.State);
        var events = _stack.Hub.Events.Where(e => e.Name == ExportHubEventNames.ExportDelivery).Select(e => e.PayloadJson).ToList();
        Assert.Equal(2, events.Count(p => p.Contains("\"state\":\"retrying\"")));
    }

    /// <summary>Exhausted attempts terminate failed and surface the failure.</summary>
    [Fact]
    public async Task Deliver_Fails_After_Max_Attempts()
    {
        _transport.FailuresBeforeSuccess = 10;
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.Ftp, """{"host":"ftp.user.example","remotePath":"/exports"}""");
        var delivery = CreateDelivery();

        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(JobId, destinationId));

        Assert.Equal(3, _transport.Sends.Count);
        var entry = delivery.GetLedgerEntry(JobId, destinationId);
        Assert.Equal(ExportDeliveryStates.Failed, entry!.State);
        Assert.Contains(_stack.Hub.Events, e => e.Name == ExportHubEventNames.ExportDelivery && e.PayloadJson.Contains("\"state\":\"failed\""));
    }

    /// <summary>Disabled destinations never reach the transport (criterion #14).</summary>
    [Fact]
    public async Task Disabled_Destination_Never_Transfers()
    {
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.Sftp, """{"host":"sftp.user.example"}""", enabled: false);
        var delivery = CreateDelivery();

        var test = await delivery.TestAsync(destinationId);
        Assert.False(test.IsReachable);
        Assert.Equal(0, _transport.TestCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(JobId, destinationId));
        Assert.Empty(_transport.Sends);
        Assert.Equal(ExportDeliveryStates.Failed, delivery.GetLedgerEntry(JobId, destinationId)!.State);
    }

    /// <summary>Credentials are encrypted at rest and decrypted only at use.</summary>
    [Fact]
    public async Task Credentials_Encrypted_At_Rest_And_Used_Decrypted()
    {
        var protectedPayload = DestinationCredentials.Protect(_stack.Protector, new DestinationCredentials("user", "s3cret-password", "tok"));
        Assert.StartsWith("cfsec1:", protectedPayload);
        Assert.DoesNotContain("s3cret-password", protectedPayload);

        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.Ftp, """{"host":"ftp.user.example"}""", protectedPayload);
        var stored = (string)(await _stack.ScalarAsync("SELECT `encrypted_credentials_json` FROM `export_destinations` WHERE `id` = @id", ("@id", destinationId)))!;
        Assert.DoesNotContain("s3cret-password", stored);

        var delivery = CreateDelivery();
        await delivery.DeliverAsync(JobId, destinationId);

        Assert.NotNull(_transport.LastCredentials);
        Assert.Equal("user", _transport.LastCredentials!.Username);
        Assert.Equal("s3cret-password", _transport.LastCredentials.Password);
        Assert.Equal("tok", _transport.LastCredentials.Token);
    }

    /// <summary>Test verdicts map reachability and credential acceptance.</summary>
    [Fact]
    public async Task Test_Maps_Probe_Verdict()
    {
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""");
        var delivery = CreateDelivery();

        var result = await delivery.TestAsync(destinationId);
        Assert.True(result.IsReachable);
        Assert.True(result.CredentialsValid);
        Assert.Equal(1, _transport.TestCalls);

        var missing = await delivery.TestAsync(999);
        Assert.False(missing.IsReachable);
    }

    /// <summary>Delivery requires a completed job.</summary>
    [Fact]
    public async Task Deliver_Rejects_Incomplete_Job()
    {
        var destinationId = await _stack.SeedDestinationAsync(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""");
        var delivery = CreateDelivery();
        var queued = _jobs.Create(2, ExportFormat.Csv, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(queued.Id, destinationId));
        Assert.Empty(_transport.Sends);
    }
}
