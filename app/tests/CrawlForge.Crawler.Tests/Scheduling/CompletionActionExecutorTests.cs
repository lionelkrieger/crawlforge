using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Scheduling;
using Xunit;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>
/// Completion-action execution (FS-CRAWL-001 §6.5): only pre-authorized actions run,
/// failures become per-run outcomes and never fail the audit, and destinations are
/// exclusively user-configured (local notification, export destination, user endpoint).
/// </summary>
public sealed class CompletionActionExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private static (CompletionActionExecutor Executor, FakeExportEngine Exports, FakeDestinationDelivery Delivery, RecordingNotificationSink Sink, FakeNotificationEndpointClient Client) CreateExecutor()
    {
        var exports = new FakeExportEngine();
        var delivery = new FakeDestinationDelivery();
        var sink = new RecordingNotificationSink();
        var client = new FakeNotificationEndpointClient();
        var executor = new CompletionActionExecutor(exports, delivery, sink, client, new FakeSchedulerClock(Now), new SchedulingOptions());
        return (executor, exports, delivery, sink, client);
    }

    [Fact(DisplayName = "Local notification action surfaces through the sink")]
    public async Task LocalNotificationSurfacesThroughSink()
    {
        var (executor, _, _, sink, _) = CreateExecutor();

        var outcomes = await executor.ExecuteAsync(1, 2, [new ScheduleCompletionAction(CompletionActionKinds.LocalNotification, Title: "Audit done", Message: "All green")]);

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Succeeded);
        Assert.True(sink.Notifications.TryPeek(out var notification));
        Assert.Equal("Audit done", notification.Title);
        Assert.Equal("All green", notification.Message);
    }

    [Fact(DisplayName = "Export delivery creates the export and delivers to the configured destination")]
    public async Task ExportDeliveryCreatesAndDelivers()
    {
        var (executor, exports, delivery, _, _) = CreateExecutor();
        var action = new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: 7, Format: ExportFormat.Csv, Datasets: ["findings", "urls"]);

        var outcomes = await executor.ExecuteAsync(11, 2, [action]);

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Succeeded);
        Assert.True(exports.Requests.TryPeek(out var request));
        Assert.Equal(11, request.AuditId);
        Assert.Equal(ExportFormat.Csv, request.Format);
        Assert.Equal(["findings", "urls"], request.Datasets);
        Assert.True(delivery.Deliveries.TryPeek(out var delivered));
        Assert.Equal(7, delivered.DestinationId);
    }

    [Fact(DisplayName = "A failed export job becomes a failed outcome, not an exception")]
    public async Task FailedExportJobBecomesFailedOutcome()
    {
        var (executor, exports, delivery, _, _) = CreateExecutor();
        exports.Status = ExportJobStatus.Failed;
        var action = new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: 7, Format: ExportFormat.Excel);

        var outcomes = await executor.ExecuteAsync(11, 2, [action]);

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Succeeded);
        Assert.NotNull(outcomes[0].Message);
        Assert.Empty(delivery.Deliveries);
    }

    [Fact(DisplayName = "Delivery failure is captured and later actions still run")]
    public async Task DeliveryFailureDoesNotStopLaterActions()
    {
        var (executor, _, delivery, sink, _) = CreateExecutor();
        delivery.DeliverException = new IOException("destination unreachable");
        var actions = new ScheduleCompletionAction[]
        {
            new(CompletionActionKinds.ExportDelivery, DestinationId: 7, Format: ExportFormat.Pdf),
            new(CompletionActionKinds.LocalNotification),
        };

        var outcomes = await executor.ExecuteAsync(11, 2, actions);

        Assert.Equal(2, outcomes.Count);
        Assert.False(outcomes[0].Succeeded);
        Assert.Contains("destination unreachable", outcomes[0].Message);
        Assert.True(outcomes[1].Succeeded);
        Assert.Single(sink.Notifications);
    }

    [Fact(DisplayName = "Notification endpoint receives the typed completion payload")]
    public async Task NotificationEndpointReceivesPayload()
    {
        var (executor, _, _, _, client) = CreateExecutor();
        var action = new ScheduleCompletionAction(CompletionActionKinds.NotificationEndpoint, Endpoint: "https://notify.example.internal/hook", Title: "Finished");

        var outcomes = await executor.ExecuteAsync(11, 2, [action]);

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Succeeded);
        Assert.True(client.Messages.TryPeek(out var message));
        Assert.Equal("https://notify.example.internal/hook", message.Endpoint);
        Assert.Equal(11, message.Payload.AuditId);
        Assert.Equal(2, message.Payload.ScheduleDefinitionId);
        Assert.Equal(Now, message.Payload.CompletedAt);
    }

    [Fact(DisplayName = "Endpoint failure is captured as an outcome")]
    public async Task EndpointFailureBecomesFailedOutcome()
    {
        var (executor, _, _, _, client) = CreateExecutor();
        client.SendException = new HttpRequestException("endpoint rejected the message");
        var action = new ScheduleCompletionAction(CompletionActionKinds.NotificationEndpoint, Endpoint: "https://notify.example.internal/hook");

        var outcomes = await executor.ExecuteAsync(11, 2, [action]);

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Succeeded);
        Assert.Contains("endpoint rejected", outcomes[0].Message);
    }

    [Fact(DisplayName = "No configured actions means nothing runs")]
    public async Task NoConfiguredActionsMeansNothingRuns()
    {
        var (executor, exports, delivery, sink, client) = CreateExecutor();

        var outcomes = await executor.ExecuteAsync(11, 2, []);

        Assert.Empty(outcomes);
        Assert.Empty(exports.Requests);
        Assert.Empty(delivery.Deliveries);
        Assert.Empty(sink.Notifications);
        Assert.Empty(client.Messages);
    }

    [Fact(DisplayName = "HTTP endpoint client posts JSON to the user-configured endpoint only")]
    public async Task HttpClientPostsJsonToUserEndpoint()
    {
        using var handler = new FakeHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new HttpNotificationEndpointClient(httpClient);
        var payload = new ScheduleNotificationPayload(11, 2, "Finished", "Audit complete", Now);

        await client.SendAsync("https://notify.example.internal/hook", payload);

        Assert.True(handler.Requests.TryPeek(out var request));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://notify.example.internal/hook", request.RequestUri?.ToString());
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"auditId\":11", body);
        Assert.Contains("\"scheduleDefinitionId\":2", body);
    }

    [Fact(DisplayName = "HTTP endpoint client surfaces non-success responses")]
    public async Task HttpClientSurfacesNonSuccessResponses()
    {
        using var handler = new FakeHttpMessageHandler { StatusCode = System.Net.HttpStatusCode.InternalServerError };
        using var httpClient = new HttpClient(handler);
        var client = new HttpNotificationEndpointClient(httpClient);
        var payload = new ScheduleNotificationPayload(11, 2, "Finished", "Audit complete", Now);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("https://notify.example.internal/hook", payload));
    }
}

/// <summary>
/// Completion-actions payload validation: misconfiguration is rejected at parse time
/// so unconfigured or malformed actions can never run mid-audit.
/// </summary>
public sealed class ScheduleCompletionActionsTests
{
    [Fact(DisplayName = "Null or empty payload means no actions")]
    public void NullOrEmptyPayloadMeansNoActions()
    {
        Assert.Empty(ScheduleCompletionActions.Parse(null));
        Assert.Empty(ScheduleCompletionActions.Parse(string.Empty));
        Assert.Empty(ScheduleCompletionActions.Parse("   "));
    }

    [Fact(DisplayName = "Actions round-trip through the stored JSON shape")]
    public void ActionsRoundTrip()
    {
        var actions = new ScheduleCompletionAction[]
        {
            new(CompletionActionKinds.LocalNotification, Title: "Done"),
            new(CompletionActionKinds.ExportDelivery, DestinationId: 3, Format: ExportFormat.Html, Datasets: ["findings"]),
            new(CompletionActionKinds.NotificationEndpoint, Endpoint: "https://notify.example.internal/hook"),
        };

        var json = ScheduleCompletionActions.Serialize(actions);
        var parsed = ScheduleCompletionActions.Parse(json);

        Assert.Equal(actions.Length, parsed.Count);
        for (var i = 0; i < actions.Length; i++)
        {
            Assert.Equal(actions[i].Kind, parsed[i].Kind);
            Assert.Equal(actions[i].DestinationId, parsed[i].DestinationId);
            Assert.Equal(actions[i].Format, parsed[i].Format);
            Assert.Equal(actions[i].Endpoint, parsed[i].Endpoint);
            Assert.Equal(actions[i].Title, parsed[i].Title);
            Assert.Equal(actions[i].Message, parsed[i].Message);
            Assert.Equal(actions[i].Datasets ?? [], parsed[i].Datasets ?? []);
        }
    }

    [Fact(DisplayName = "Unknown action kinds are rejected")]
    public void UnknownKindsAreRejected()
    {
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"vendor-telemetry\"}]"));
    }

    [Fact(DisplayName = "Export delivery requires a destination and format")]
    public void ExportDeliveryRequiresDestinationAndFormat()
    {
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"export-delivery\"}]"));
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"export-delivery\",\"destinationId\":3}]"));
    }

    [Fact(DisplayName = "Notification endpoint requires an absolute http(s) URL")]
    public void NotificationEndpointRequiresAbsoluteHttpUrl()
    {
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"notification-endpoint\"}]"));
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"notification-endpoint\",\"endpoint\":\"not-a-url\"}]"));
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("[{\"kind\":\"notification-endpoint\",\"endpoint\":\"ftp://files.example.internal\"}]"));
    }

    [Fact(DisplayName = "Malformed JSON is rejected with a clear error")]
    public void MalformedJsonIsRejected()
    {
        Assert.Throws<FormatException>(() => ScheduleCompletionActions.Parse("{not json"));
    }
}
