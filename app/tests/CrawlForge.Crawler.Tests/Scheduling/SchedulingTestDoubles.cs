using System.Collections.Concurrent;
using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>Hub double: records every event and lets tests await a named event as a signal.</summary>
internal sealed class RecordingHub : IRealtimeHub
{
    private readonly ConcurrentQueue<HubEvent> _events = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();

    public IReadOnlyList<HubEvent> Events => _events.ToArray();

    public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(hubEvent);
        if (_signals.TryRemove(hubEvent.Name, out var signal))
        {
            signal.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public async Task WaitForEventAsync(string name, TimeSpan? timeout = null)
    {
        if (_events.Any(e => e.Name == name))
        {
            return;
        }

        var signal = _signals.GetOrAdd(name, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        Assert.Same(signal.Task, completed);
    }

    public IReadOnlyList<HubEvent> EventsNamed(string name) => _events.Where(e => e.Name == name).ToList();
}

/// <summary>
/// Audit-start double: creates real audit rows through the repository so terminal-state
/// observation runs against the real stack. Configurable start failures.
/// </summary>
internal sealed class FakeAuditExecutionService : IAuditExecutionService
{
    private readonly SqlRepository<AuditDto> _audits;
    private readonly FakeSchedulerClock _clock;

    public FakeAuditExecutionService(SqlRepository<AuditDto> audits, FakeSchedulerClock clock)
    {
        _audits = audits;
        _clock = clock;
    }

    public ConcurrentQueue<(long ProjectId, long ConfigurationId, ScheduleTriggerType Trigger, CrawlLimitOverrides? Overrides)> Starts { get; } = new();

    public Exception? StartException { get; set; }

    public async Task<long> StartAuditAsync(long projectId, long configurationId, ScheduleTriggerType trigger, CrawlLimitOverrides? limitOverrides, CancellationToken cancellationToken = default)
    {
        Starts.Enqueue((projectId, configurationId, trigger, limitOverrides));
        if (StartException is not null)
        {
            throw StartException;
        }

        var audit = await _audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Running, AuditPhase.Crawling, _clock.UtcNow, null, "{}", "test-engine", false), cancellationToken);
        return audit.Id;
    }
}

/// <summary>Completion-executor double: records calls and returns configured outcomes.</summary>
internal sealed class RecordingCompletionExecutor : ICompletionActionExecutor
{
    public ConcurrentQueue<(long AuditId, long ScheduleId, IReadOnlyList<ScheduleCompletionAction> Actions)> Calls { get; } = new();

    public IReadOnlyList<CompletionActionOutcome> Outcomes { get; set; } = [];

    public Task<IReadOnlyList<CompletionActionOutcome>> ExecuteAsync(long auditId, long scheduleDefinitionId, IReadOnlyList<ScheduleCompletionAction> actions, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((auditId, scheduleDefinitionId, actions));
        return Task.FromResult(Outcomes);
    }
}

/// <summary>Retention-pruner double: records the policies it receives.</summary>
internal sealed class RecordingPruner : IRetentionPruner
{
    public ConcurrentQueue<RetentionPolicy> Policies { get; } = new();

    public Task<RetentionPruneResult> PruneAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        Policies.Enqueue(policy);
        return Task.FromResult(new RetentionPruneResult(0, []));
    }
}

/// <summary>Export-engine double: jobs complete immediately unless configured otherwise.</summary>
internal sealed class FakeExportEngine : IExportEngine
{
    private readonly ConcurrentDictionary<long, ExportJobDto> _jobs = new();
    private long _nextId;

    public ExportJobStatus Status { get; set; } = ExportJobStatus.Completed;

    public ConcurrentQueue<ExportRequest> Requests { get; } = new();

    public Task<ExportJobDto> CreateExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        var id = Interlocked.Increment(ref _nextId);
        var job = new ExportJobDto(id, request.AuditId, request.Format, Status, DateTimeOffset.UtcNow, @"E:\exports\test.xlsx", request.Datasets);
        _jobs[id] = job;
        return Task.FromResult(job);
    }

    public Task<ExportJobDto?> GetExportAsync(long exportJobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryGetValue(exportJobId, out var job) ? job : null);

    public Task<Stream> OpenExportFileAsync(long exportJobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
}

/// <summary>Destination-delivery double: records deliveries; configurable failure.</summary>
internal sealed class FakeDestinationDelivery : IExportDestinationDelivery
{
    public ConcurrentQueue<(long ExportJobId, long DestinationId)> Deliveries { get; } = new();

    public Exception? DeliverException { get; set; }

    public Task<DestinationTestResult> TestAsync(long destinationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DestinationTestResult(true, true, null));

    public Task DeliverAsync(long exportJobId, long destinationId, CancellationToken cancellationToken = default)
    {
        if (DeliverException is not null)
        {
            throw DeliverException;
        }

        Deliveries.Enqueue((exportJobId, destinationId));
        return Task.CompletedTask;
    }
}

/// <summary>Local-notification double.</summary>
internal sealed class RecordingNotificationSink : ILocalNotificationSink
{
    public ConcurrentQueue<(string Title, string Message)> Notifications { get; } = new();

    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        Notifications.Enqueue((title, message));
        return Task.CompletedTask;
    }
}

/// <summary>User-endpoint client double: records messages; configurable failure.</summary>
internal sealed class FakeNotificationEndpointClient : INotificationEndpointClient
{
    public ConcurrentQueue<(string Endpoint, ScheduleNotificationPayload Payload)> Messages { get; } = new();

    public Exception? SendException { get; set; }

    public Task SendAsync(string endpoint, ScheduleNotificationPayload payload, CancellationToken cancellationToken = default)
    {
        if (SendException is not null)
        {
            throw SendException;
        }

        Messages.Enqueue((endpoint, payload));
        return Task.CompletedTask;
    }
}

/// <summary>In-memory artifact file store for the real retention pruner.</summary>
internal sealed class FakeArtifactFileStore : IArtifactFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public bool Exists(string storageKey) => _files.ContainsKey(storageKey);

    public Task<Stream?> OpenCompressedAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_files.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

    public Task<bool> SaveCompressedAsync(string storageKey, Stream compressedContent, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        compressedContent.CopyTo(buffer);
        return Task.FromResult(_files.TryAdd(storageKey, buffer.ToArray()));
    }

    public Task<bool> DeleteCompressedAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryRemove(storageKey, out _));

    public Task<IReadOnlyList<string>> ListStorageKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());
}

/// <summary>HTTP handler double: captures requests and returns a configured status code.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public System.Net.HttpStatusCode StatusCode { get; set; } = System.Net.HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new HttpResponseMessage(StatusCode));
    }
}

/// <summary>
/// Composition root for scheduler behavior tests: real persistence stack + real
/// service with double execution/hub/executor/pruner and a controllable clock.
/// </summary>
internal sealed class SchedulerHarness : IAsyncDisposable
{
    private SchedulerHarness(SchedulingDatabase database, SchedulingStack stack, FakeSchedulerClock clock, SchedulingOptions options)
    {
        Database = database;
        Stack = stack;
        Clock = clock;
        Options = options;
        Hub = new RecordingHub();
        Execution = new FakeAuditExecutionService(stack.Audits, clock);
        Completion = new RecordingCompletionExecutor();
        Pruner = new RecordingPruner();
        Service = CreateService();
    }

    public SchedulingDatabase Database { get; }

    public SchedulingStack Stack { get; }

    public FakeSchedulerClock Clock { get; }

    public SchedulingOptions Options { get; }

    public RecordingHub Hub { get; }

    public FakeAuditExecutionService Execution { get; }

    public RecordingCompletionExecutor Completion { get; }

    public RecordingPruner Pruner { get; }

    public ScheduledAuditService Service { get; }

    public static async Task<SchedulerHarness> CreateAsync(DateTimeOffset? start = null, SchedulingOptions? options = null)
    {
        var database = new SchedulingDatabase();
        var stack = await SchedulingStack.CreateAsync(database);
        var clock = new FakeSchedulerClock(start ?? new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var effectiveOptions = options ?? new SchedulingOptions
        {
            TickInterval = TimeSpan.FromMinutes(30),
            AuditPollInterval = TimeSpan.FromMilliseconds(1),
        };

        return new SchedulerHarness(database, stack, clock, effectiveOptions);
    }

    /// <summary>Builds a second service over the same stack (restart reconciliation scenarios).</summary>
    public ScheduledAuditService CreateService() => new(
        Stack.Schedules,
        Stack.Schedules,
        Stack.Runs,
        Stack.Runs,
        Stack.Audits,
        Execution,
        Clock,
        new ScheduleEventPublisher(Hub),
        Completion,
        Pruner,
        Options);

    public async Task<ScheduleDefinitionDto> CreateScheduleAsync(
        string rule = "interval:01:00:00",
        DateTimeOffset? nextDue = null,
        bool enabled = true,
        string? completionActionsJson = null,
        CrawlLimitOverrides? overrides = null)
    {
        return await Stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, Stack.ProjectId, Stack.ConfigurationId, rule, enabled, overrides, completionActionsJson, nextDue));
    }

    public async Task CompleteAuditAsync(long auditId, AuditStatus status = AuditStatus.Completed)
    {
        var audit = await Stack.Audits.GetByIdAsync(auditId);
        Assert.NotNull(audit);
        await Stack.Audits.UpdateAsync(audit with { Status = status, FinishedAt = Clock.UtcNow });
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();
        await Stack.DisposeAsync();
        Database.Dispose();
    }
}
