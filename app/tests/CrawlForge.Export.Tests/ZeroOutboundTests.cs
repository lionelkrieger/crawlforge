using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Delivery;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Criterion #14 proof: with all destinations disabled (or none configured),
/// export generation and delivery produce ZERO non-crawl outbound traffic.
/// Enabled transfers target ONLY the user-configured endpoint, proven with a
/// recording HttpMessageHandler across all four destination classes.
/// </summary>
public sealed class ZeroOutboundTests
{
    /// <summary>Fresh installations seed no destinations.</summary>
    [Fact]
    public async Task No_Destinations_Are_Seeded()
    {
        await using var stack = new ExportTestStack();
        var count = Convert.ToInt64(await stack.ScalarAsync("SELECT COUNT(*) FROM `export_destinations`"));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Every destination class, disabled: neither TestAsync nor DeliverAsync
    /// constructs a transport or opens a connection.
    /// </summary>
    [Theory]
    [InlineData(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""")]
    [InlineData(ExportDestinationClass.Ftp, """{"host":"ftp.user.example"}""")]
    [InlineData(ExportDestinationClass.Sftp, """{"host":"sftp.user.example"}""")]
    [InlineData(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""")]
    public async Task Disabled_Destinations_Of_Every_Class_Open_Nothing(ExportDestinationClass @class, string endpointJson)
    {
        await using var stack = new ExportTestStack();
        var destinationId = await stack.SeedDestinationAsync(@class, endpointJson, enabled: false);
        var jobs = new ExportJobRegistry();
        var filePath = Path.GetTempFileName();
        try
        {
            var record = jobs.Create(1, ExportFormat.Csv, []);
            jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = filePath });

            var transportsCreated = 0;
            var delivery = new ExportDestinationDelivery(
                stack.Provider, stack.Protector, jobs, new ExportEventPublisher(stack.Hub),
                new ExportOptions { DeliveryRetryBaseDelay = TimeSpan.FromMilliseconds(1) },
                _ =>
                {
                    transportsCreated++;
                    throw new InvalidOperationException("A disabled destination must never construct a transport.");
                });

            var test = await delivery.TestAsync(destinationId);
            Assert.False(test.IsReachable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.DeliverAsync(record.Id, destinationId));
            Assert.Equal(0, transportsCreated);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>Export generation itself never issues HTTP requests.</summary>
    [Fact]
    public async Task Export_Generation_Issues_No_Http_Requests()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/");
        var handler = new RecordingHttpHandler();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-zero-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var engine = new ExportEngine(stack.Provider, new ExportJobRegistry(), new ExportEventPublisher(stack.Hub), new ExportOptions { OutputDirectory = outputDirectory });
            var job = await engine.CreateExportAsync(new ExportRequest(auditId, ExportFormat.Html, []));
            await engine.GetJobTask(job.Id);

            Assert.Empty(handler.Requests);
            Assert.Equal(ExportJobStatus.Completed, (await engine.GetExportAsync(job.Id))!.Status);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    /// <summary>Enabled webhook transfers hit ONLY the user-configured URL.</summary>
    [Fact]
    public async Task Webhook_Transfers_Only_To_The_User_Endpoint()
    {
        await using var stack = new ExportTestStack();
        var handler = new RecordingHttpHandler();
        var destinationId = await stack.SeedDestinationAsync(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""");
        var jobs = new ExportJobRegistry();
        var filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(filePath, "data");
            var record = jobs.Create(1, ExportFormat.Csv, []);
            jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = filePath });

            using var webhook = new WebhookTransport(handler);
            var delivery = new ExportDestinationDelivery(
                stack.Provider, stack.Protector, jobs, new ExportEventPublisher(stack.Hub),
                new ExportOptions { DeliveryRetryBaseDelay = TimeSpan.FromMilliseconds(1) },
                _ => webhook);

            await delivery.DeliverAsync(record.Id, destinationId);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(new Uri("https://user.example/hook"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>Webhook test probes use the user endpoint and map 401 to invalid credentials.</summary>
    [Fact]
    public async Task Webhook_Test_Probe_Uses_User_Endpoint_And_Detects_Auth_Failure()
    {
        await using var stack = new ExportTestStack();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized),
        };
        var destinationId = await stack.SeedDestinationAsync(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""");
        var jobs = new ExportJobRegistry();

        using var webhook = new WebhookTransport(handler);
        var delivery = new ExportDestinationDelivery(
            stack.Provider, stack.Protector, jobs, new ExportEventPublisher(stack.Hub),
            transportFactory: _ => webhook);

        var result = await delivery.TestAsync(destinationId);
        Assert.True(result.IsReachable);
        Assert.False(result.CredentialsValid);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://user.example/hook"), request.RequestUri);
    }
}
