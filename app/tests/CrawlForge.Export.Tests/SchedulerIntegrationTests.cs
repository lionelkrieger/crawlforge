using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Export.Delivery;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Scheduler completion-action integration (FS-CRAWL-001 §6.5): the accepted
/// CompletionActionExecutor consumes the locked IExportEngine +
/// IExportDestinationDelivery surfaces end-to-end — export generation,
/// polling, and delivery to a user-configured local folder — and a delivery
/// failure becomes an action outcome, never an audit failure.
/// </summary>
public sealed class SchedulerIntegrationTests
{
    private sealed class NullNotificationSink : ILocalNotificationSink
    {
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullEndpointClient : INotificationEndpointClient
    {
        public Task SendAsync(string endpoint, ScheduleNotificationPayload payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The export-delivery completion action generates and delivers locally.</summary>
    [Fact]
    public async Task ExportDelivery_Action_Generates_And_Delivers()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/");

        var destinationFolder = Path.Combine(Path.GetTempPath(), "cf-sched-dest-" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-sched-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var destinationId = await stack.SeedDestinationAsync(
                ExportDestinationClass.LocalFolder,
                $$"""{"path":"{{destinationFolder.Replace("\\", "\\\\")}}"}""");

            var jobs = new ExportJobRegistry();
            var events = new ExportEventPublisher(stack.Hub);
            using var engine = new ExportEngine(stack.Provider, jobs, events, new ExportOptions { OutputDirectory = outputDirectory });
            var delivery = new ExportDestinationDelivery(stack.Provider, stack.Protector, jobs, events);
            var executor = new CompletionActionExecutor(
                engine, delivery, new NullNotificationSink(), new NullEndpointClient(),
                new SystemSchedulerClock(),
                new SchedulingOptions { ExportPollInterval = TimeSpan.FromMilliseconds(10) });

            var actions = new[]
            {
                new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: destinationId, Format: ExportFormat.Csv),
            };
            var outcomes = await executor.ExecuteAsync(auditId, scheduleDefinitionId: 1, actions);

            var outcome = Assert.Single(outcomes);
            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.True(Directory.Exists(destinationFolder));
            Assert.Single(Directory.GetFiles(destinationFolder, "*.zip"));
        }
        finally
        {
            Directory.Delete(destinationFolder, recursive: true);
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    /// <summary>A disabled destination fails the action, never the audit.</summary>
    [Fact]
    public async Task Disabled_Destination_Fails_Action_Not_Audit()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/");

        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-sched-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var destinationId = await stack.SeedDestinationAsync(
                ExportDestinationClass.LocalFolder, """{"path":"C:\\unused"}""", enabled: false);

            var jobs = new ExportJobRegistry();
            var events = new ExportEventPublisher(stack.Hub);
            using var engine = new ExportEngine(stack.Provider, jobs, events, new ExportOptions { OutputDirectory = outputDirectory });
            var delivery = new ExportDestinationDelivery(stack.Provider, stack.Protector, jobs, events);
            var executor = new CompletionActionExecutor(
                engine, delivery, new NullNotificationSink(), new NullEndpointClient(),
                new SystemSchedulerClock(),
                new SchedulingOptions { ExportPollInterval = TimeSpan.FromMilliseconds(10) });

            var actions = new[]
            {
                new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: destinationId, Format: ExportFormat.Csv),
            };
            var outcomes = await executor.ExecuteAsync(auditId, scheduleDefinitionId: 1, actions);

            var outcome = Assert.Single(outcomes);
            Assert.False(outcome.Succeeded);
            Assert.Contains("disabled", outcome.Message);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
