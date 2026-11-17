using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Host;
using CrawlForge.Host.Composition;
using CrawlForge.Host.Execution;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Security.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-06-01 reopen batch wiring proof (task-44): AIMD observability
/// (criterion #36), logs DI forwarding, content-explorer repository
/// registration, live AIMD router, validator registrations, GEO aggregate
/// persistence with failure isolation, identity-free structured logging, and
/// the offline REST reaudit routing (criterion #15).
/// </summary>
public sealed class ReopenBatchWiringTests
{
    private const string SessionHeader = "X-CrawlForge-Session";

    [Fact]
    public async Task AimdObservability_PersistsHistory_PublishesHubEvents_WritesSummary()
    {
        var channel = new RecordingHubChannel();
        await using var backend = await StartBackendAsync(configureBuilder: builder =>
            builder.Services.AddSingleton<IHubClientChannel>(channel));
        // Strain pages (429) force multiplicative-decrease adjustments.
        var baseUrl = await StartOriginAsync(strainPaths: 6);
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
            var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
            var audit = await WaitForCompletionAsync(backend, auditId);
            Assert.Equal(AuditStatus.Completed, audit.Status);

            // Durable history rows persisted through the accepted recorder.
            var events = await backend.App.Services.GetRequiredService<IReadRepository<HostThrottleEventDto>>()
                .ListAsync(new ListQuery(0, 1000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            Assert.NotEmpty(events.Items);
            Assert.All(events.Items, e => Assert.Equal(auditId, e.AuditId));

            // Typed host-throttle hub events published (coalesced ≤1/s/host).
            var throttleEvents = channel.Events.Where(e => e.Name == HubEventNames.HostThrottle).ToList();
            Assert.NotEmpty(throttleEvents);
            using var payload = JsonDocument.Parse(throttleEvents[0].PayloadJson);
            Assert.Equal(auditId, payload.RootElement.GetProperty("auditId").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("host").GetString()));
            // HOTFIX-01: wire names follow the frontend hub contract.
            Assert.True(payload.RootElement.TryGetProperty("rateAfter", out _));
            Assert.True(payload.RootElement.TryGetProperty("ttfbSampleMs", out _));

            // Completion summary under the locked metric key.
            var metrics = await backend.App.Services.GetRequiredService<IReadRepository<MetricSummaryDto>>()
                .ListAsync(new ListQuery(0, 1000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            var summary = Assert.Single(metrics.Items, m => m.MetricKey == MetricKeys.HostThrottle);
            Assert.True(summary.Value >= 1);
            using var dimensions = JsonDocument.Parse(summary.DimensionsJson);
            Assert.True(dimensions.RootElement.GetProperty("hostCount").GetInt32() >= 1);

            // Host explorer route activates through the live router (idle now).
            using var hostsResponse = await GetAsync(backend, $"/api/audits/{auditId}/hosts");
            Assert.Equal(HttpStatusCode.OK, hostsResponse.StatusCode);
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task LogsDi_ForwardsBothInterfaces_ToOneInstance_AndReadRoutesActivate()
    {
        await using var backend = await StartBackendAsync();
        var engine = backend.App.Services.GetRequiredService<ILogCorrelationEngine>();
        var readModel = backend.App.Services.GetRequiredService<ILogCorrelationReadModel>();
        Assert.Same(engine, readModel);

        var (project, configuration) = await SeedAsync(backend, "http://127.0.0.1:1/");
        var audit = await SeedAuditRowAsync(backend, project.Id, configuration.Id);

        using var imports = await GetAsync(backend, $"/api/projects/{project.Id}/logs");
        Assert.Equal(HttpStatusCode.OK, imports.StatusCode);
        using var correlation = await GetAsync(backend, $"/api/audits/{audit.Id}/logs/correlation");
        Assert.Equal(HttpStatusCode.OK, correlation.StatusCode);
    }

    [Fact]
    public async Task ContentExplorer_Activates_ExtractedContentRoutesReturn200()
    {
        await using var backend = await StartBackendAsync();
        var (project, configuration) = await SeedAsync(backend, "http://127.0.0.1:1/");
        var audit = await SeedAuditRowAsync(backend, project.Id, configuration.Id);

        using var extracted = await GetAsync(backend, $"/api/audits/{audit.Id}/extracted-content");
        Assert.Equal(HttpStatusCode.OK, extracted.StatusCode);
        using var summary = await GetAsync(backend, $"/api/audits/{audit.Id}/extracted-content/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        using var searches = await GetAsync(backend, $"/api/audits/{audit.Id}/search-results");
        Assert.Equal(HttpStatusCode.OK, searches.StatusCode);
        using var snippets = await GetAsync(backend, $"/api/audits/{audit.Id}/snippet-results");
        Assert.Equal(HttpStatusCode.OK, snippets.StatusCode);
    }

    [Fact]
    public async Task ValidatorsAndAimdRouter_Resolve_IdleFallbackWorks()
    {
        await using var backend = await StartBackendAsync();
        Assert.NotNull(backend.App.Services.GetRequiredService<IRedirectMatrixValidator>());
        Assert.NotNull(backend.App.Services.GetRequiredService<IStructuredDataValidator>());

        var aimd = backend.App.Services.GetRequiredService<IAimdPolitenessController>();
        Assert.IsType<LiveAimdControllerRouter>(aimd);
        // Idle fallback: empty state, harmless override round-trip.
        Assert.Empty(aimd.CreateCheckpointState().Hosts);
        aimd.SetManualOverride("idle.example", 2.5);
        Assert.Equal(2.5, aimd.GetState("idle.example").ManualOverrideRate);
        Assert.Null(backend.App.Services.GetRequiredService<AuditExecutionEngine>().ActiveAimdController);
    }

    [Fact]
    public async Task AimdRouter_DelegatesToLiveAuditController()
    {
        await using var backend = await StartBackendAsync();
        // Slow origin keeps the crawl alive long enough to observe delegation.
        var baseUrl = await StartOriginAsync(strainPaths: 0, delayMilliseconds: 400, pageCount: 12);
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
            var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);

            var engine = backend.App.Services.GetRequiredService<AuditExecutionEngine>();
            var router = backend.App.Services.GetRequiredService<IAimdPolitenessController>();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            var delegated = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (engine.ActiveAimdController is { } live)
                {
                    // The router addresses the SAME live controller state.
                    var liveHosts = live.CreateCheckpointState().Hosts.Select(h => h.Host).OrderBy(h => h, StringComparer.Ordinal).ToArray();
                    var routerHosts = router.CreateCheckpointState().Hosts.Select(h => h.Host).OrderBy(h => h, StringComparer.Ordinal).ToArray();
                    Assert.Equal(liveHosts, routerHosts);
                    var host = new Uri(baseUrl).Host;
                    Assert.Equal(live.GetState(host).CurrentRate, router.GetState(host).CurrentRate);
                    delegated = true;
                    break;
                }

                await Task.Delay(50);
            }

            Assert.True(delegated, "The running audit never exposed a live AIMD controller.");
            await WaitForCompletionAsync(backend, auditId);
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task GeoAggregates_PersistedAtFinalize()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartOriginAsync(strainPaths: 0);
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
            var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
            var audit = await WaitForCompletionAsync(backend, auditId);
            Assert.Equal(AuditStatus.Completed, audit.Status);

            var metrics = await backend.App.Services.GetRequiredService<IReadRepository<MetricSummaryDto>>()
                .ListAsync(new ListQuery(0, 2000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            Assert.Contains(metrics.Items, m => m.MetricKey == MetricKeys.GeoReadiness);
            Assert.Contains(metrics.Items, m => m.MetricKey == MetricKeys.GovernanceCoverage);

            var matrix = await backend.App.Services.GetRequiredService<IReadRepository<GovernanceMatrixRowDto>>()
                .ListAsync(new ListQuery(0, 2000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            Assert.NotEmpty(matrix.Items);
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task AggregateRecorderFailure_DoesNotBlockAuditCompletion()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartOriginAsync(strainPaths: 0);
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            // A second engine instance whose GEO aggregate recorder always
            // throws: finalize must still complete the audit (isolation ruling).
            var engine = ActivatorUtilities.CreateInstance<AuditExecutionEngine>(
                backend.App.Services,
                new GeoAggregateRecorder(new ThrowingDbProvider()));
            var auditId = await engine.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
            var audit = await WaitForCompletionAsync(backend, auditId);
            Assert.Equal(AuditStatus.Completed, audit.Status);
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task StructuredLogs_AreIdentityFree_AndExistingProvidersPreserved()
    {
        await using var backend = await StartBackendAsync();
        var providers = backend.App.Services.GetServices<ILoggerProvider>().ToList();
        Assert.Contains(providers, p => p is LocalStructuredLogProvider);
        // The default console/debug providers remain registered (no regression).
        Assert.Contains(providers, p => p is not LocalStructuredLogProvider);

        // Push identity-bearing PROPERTIES through the framework pipeline with
        // a clean fixed message: the sink's filter must drop them (NR-17 /
        // exclusion row 6) while the technical event itself is preserved.
        var logger = backend.App.Services.GetRequiredService<ILogger<ReopenBatchWiringTests>>();
        logger.Log(
            LogLevel.Information,
            0,
            new List<KeyValuePair<string, object?>>
            {
                new("UserId", "probe-user-secret"),
                new("MachineName", "probe-machine-secret"),
                new("technicalDetail", "kept-value")
            },
            null,
            (_, _) => "Identity property probe");

        var registry = backend.App.Services.GetRequiredService<DiagnosticArtifactRegistry>();
        var files = Directory.GetFiles(registry.LogDirectory, "structured-*.jsonl");
        Assert.NotEmpty(files);
        var lines = files.SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("message", out _));
            Assert.True(document.RootElement.TryGetProperty("level", out _));
        }

        var text = string.Join("\n", lines);
        Assert.Contains("Identity property probe", text);
        Assert.Contains("kept-value", text);
        Assert.DoesNotContain("probe-user-secret", text);
        Assert.DoesNotContain("probe-machine-secret", text);
        foreach (var forbidden in new[] { "userId", "machineName", "machineId", "osVersion", "accountId", "userName", "installationId" })
        {
            Assert.DoesNotContain(forbidden, text);
        }
    }

    [Fact]
    public async Task OfflineReAudit_CompletesAfterOriginIsGone()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartOriginAsync(strainPaths: 0);
        long sourceAuditId;
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
            sourceAuditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
            var source = await WaitForCompletionAsync(backend, sourceAuditId);
            Assert.Equal(AuditStatus.Completed, source.Status);
        }
        finally
        {
            // The origin is GONE: any re-fetch would fail. The offline path
            // must complete purely from stored artifacts (criterion #15).
            StopOrigin();
        }

        var reauditPort = backend.App.Services.GetRequiredService<IReAuditExecutionPort>();
        var newAuditId = await reauditPort.ReAuditAsync(sourceAuditId, refresh: false);
        var reaudit = await WaitForCompletionAsync(backend, newAuditId);
        Assert.Equal(AuditStatus.Completed, reaudit.Status);

        var urls = backend.App.Services.GetRequiredService<IReadRepository<UrlRecordDto>>();
        var sourceUrls = await urls.ListAsync(new ListQuery(0, 1000, new Dictionary<string, string> { ["auditId"] = sourceAuditId.ToString() }));
        var reauditUrls = await urls.ListAsync(new ListQuery(0, 1000, new Dictionary<string, string> { ["auditId"] = newAuditId.ToString() }));
        Assert.Equal(sourceUrls.TotalCount, reauditUrls.TotalCount);
        Assert.True(reauditUrls.TotalCount > 0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<CrawlForgeBackend> StartBackendAsync(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configureBuilder = null)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        return await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"],
            configureBuilder);
    }

    private static async Task<(ProjectDto Project, CrawlConfigurationDto Configuration)> SeedAsync(
        CrawlForgeBackend backend, string baseUrl)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Reopen Batch", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { baseUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return (project, configuration);
    }

    private static async Task<AuditDto> SeedAuditRowAsync(CrawlForgeBackend backend, long projectId, long configurationId)
    {
        var audits = backend.App.Services.GetRequiredService<IWriteRepository<AuditDto>>();
        return await audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Completed, AuditPhase.Finalizing,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "{}", "test", false));
    }

    private static async Task<AuditDto> WaitForCompletionAsync(CrawlForgeBackend backend, long auditId)
    {
        var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await audits.GetByIdAsync(auditId);
            if (audit?.Status is AuditStatus.Completed or AuditStatus.Failed or AuditStatus.Cancelled)
            {
                return audit;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach a terminal state in time.");
    }

    private static async Task<HttpResponseMessage> GetAsync(CrawlForgeBackend backend, string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        client.DefaultRequestHeaders.Add(SessionHeader, backend.SessionToken);
        return await client.GetAsync(path);
    }

    private static HttpListener? _listener;

    private static async Task<string> StartOriginAsync(int strainPaths, int delayMilliseconds = 0, int pageCount = 4)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var baseUrl = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        _listener = listener;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds);
                }

                if (path == "/robots.txt")
                {
                    var robots = Encoding.UTF8.GetBytes("User-agent: *\nAllow: /\n");
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = robots.Length;
                    await context.Response.OutputStream.WriteAsync(robots);
                    context.Response.Close();
                    continue;
                }

                if (path.StartsWith("/strain-", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 429;
                    context.Response.ContentLength64 = 0;
                    context.Response.Close();
                    continue;
                }

                var links = new StringBuilder();
                for (var i = 0; i < pageCount; i++)
                {
                    links.Append($"<a href=\"/page-{i}\">page {i}</a> ");
                }

                for (var i = 0; i < strainPaths; i++)
                {
                    links.Append($"<a href=\"/strain-{i}\">strain {i}</a> ");
                }

                var body = Encoding.UTF8.GetBytes(
                    $"<html><head><title>Origin</title></head><body>{links}</body></html>");
                context.Response.ContentType = "text/html";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return baseUrl;
    }

    private static void StopOrigin()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception)
        {
            // Best-effort origin teardown.
        }

        _listener = null;
    }

    private sealed class RecordingHubChannel : IHubClientChannel
    {
        public ConcurrentQueue<(string Name, string PayloadJson)> Events { get; } = new();

        public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingDbProvider : IDbProvider
    {
        public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;

        public string Dialect => "throwing-test-provider";

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated aggregate-recorder storage failure.");

        public Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated aggregate-recorder storage failure.");
    }
}
