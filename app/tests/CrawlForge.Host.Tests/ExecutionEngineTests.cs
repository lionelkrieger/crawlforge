using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host.Hosting;
using CrawlForge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// End-to-end execution verification (WP-06-01): the composed backend runs a
/// real audit against a local loopback target through the accepted crawler,
/// pipeline, and scoring components; observes pause/resume/cancel transitions;
/// recovers interrupted audits; and gates every data surface with the
/// per-launch session token. All network I/O stays on 127.0.0.1.
/// </summary>
public sealed class ExecutionEngineTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "crawlforge-execution", Guid.NewGuid().ToString("N"));

    private string[] BuildArgs() =>
    [
        $"--CrawlForge:DataDirectory={_dataDirectory}",
        "--CrawlForge:Port=0"
    ];

    [Fact]
    public async Task FullAudit_Completes_WithUrlsScoresAndCounters()
    {
        await using var target = await LocalTargetSite.StartAsync(threePageSite: true);
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        var (projectId, configurationId) = await SeedProjectAsync(backend, target.BaseUrl);
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 50));

        var audit = await WaitForStatusAsync(backend, auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        Assert.Equal(AuditPhase.Finalizing, audit.Phase);
        Assert.NotNull(audit.FinishedAt);

        var urls = backend.App.Services.GetRequiredService<IReadRepository<UrlRecordDto>>();
        var page = await urls.ListAsync(new ListQuery(0, 100,
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        Assert.True(page.TotalCount >= 3,
            $"Expected at least 3 crawled URLs, got {page.TotalCount}; counters={audit.CountersJson}; snapshot={audit.ConfigurationSnapshotJson}");

        var scores = backend.App.Services.GetRequiredService<IReadRepository<AuditScoreDto>>();
        var scorePage = await scores.ListAsync(new ListQuery(0, 100,
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        Assert.True(scorePage.TotalCount >= 1, "Expected at least the overall score row.");

        var counters = JsonDocument.Parse(audit.CountersJson).RootElement;
        Assert.Equal(3, counters.GetProperty("analyzed").GetInt64());
        Assert.True(counters.GetProperty("fetched").GetInt64() >= 3);

        // Checkpoints were persisted during the run.
        var checkpoints = backend.App.Services.GetRequiredService<ICrawlCheckpointService>();
        Assert.NotNull(await checkpoints.LoadLatestAsync(auditId));
    }

    [Fact]
    public async Task PauseResume_CheckpointsAndCompletes()
    {
        await using var target = await LocalTargetSite.StartAsync(threePageSite: false);
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        var (projectId, configurationId) = await SeedProjectAsync(backend, target.BaseUrl);
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var lifecycle = backend.App.Services.GetRequiredService<IAuditLifecyclePort>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 5));

        // Wait until the crawl is underway, then pause.
        await WaitForConditionAsync(backend, auditId,
            a => a.Status == AuditStatus.Running && CountersOf(a).fetched >= 1,
            TimeSpan.FromSeconds(30));
        var paused = await lifecycle.PauseAsync(auditId);
        Assert.Equal(AuditStatus.Paused, paused.Status);

        await WaitForStatusAsync(backend, auditId, AuditStatus.Paused, TimeSpan.FromSeconds(15));
        var checkpoints = backend.App.Services.GetRequiredService<ICrawlCheckpointService>();
        var checkpointDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        CrawlCheckpointDto? checkpoint = null;
        while (DateTimeOffset.UtcNow < checkpointDeadline && checkpoint is null)
        {
            checkpoint = await checkpoints.LoadLatestAsync(auditId);
            if (checkpoint is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(checkpoint);

        var resumed = await lifecycle.ResumeAsync(auditId);
        Assert.Equal(AuditStatus.Running, resumed.Status);
        await WaitForStatusAsync(backend, auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Cancel_RetainsPartialData()
    {
        await using var target = await LocalTargetSite.StartAsync(threePageSite: false);
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        var (projectId, configurationId) = await SeedProjectAsync(backend, target.BaseUrl);
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var lifecycle = backend.App.Services.GetRequiredService<IAuditLifecyclePort>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 5));

        await WaitForConditionAsync(backend, auditId,
            a => a.Status == AuditStatus.Running && CountersOf(a).fetched >= 1,
            TimeSpan.FromSeconds(30));
        var cancelled = await lifecycle.CancelAsync(auditId);
        Assert.Equal(AuditStatus.Cancelled, cancelled.Status);

        var audit = await WaitForConditionAsync(backend, auditId,
            a => a.Status == AuditStatus.Cancelled && a.FinishedAt is not null,
            TimeSpan.FromSeconds(30));
        Assert.NotNull(audit.FinishedAt);

        var urls = backend.App.Services.GetRequiredService<IReadRepository<UrlRecordDto>>();
        var page = await urls.ListAsync(new ListQuery(0, 100,
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        Assert.True(page.TotalCount >= 1, "Partial URL data must be retained on cancel.");
    }

    [Fact]
    public async Task CrashRecovery_MarksInterruptedAuditsPaused()
    {
        // First launch: seed an audit row left Running by a simulated crash.
        await using (var first = await CrawlForgeBackend.StartAsync(BuildArgs()))
        {
            var (projectId, configurationId) = await SeedProjectAsync(first, "http://127.0.0.1:1/");
            var audits = first.App.Services.GetRequiredService<IWriteRepository<AuditDto>>();
            await audits.CreateAsync(new AuditDto(
                0, projectId, configurationId, "{}", AuditStatus.Running, AuditPhase.Crawling,
                DateTimeOffset.UtcNow, null, "{}", "1.0.0", false));
        }

        // Second launch: the orphan must be recoverable, never silently restarted.
        await using var second = await CrawlForgeBackend.StartAsync(BuildArgs());
        var reads = second.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var page = await reads.ListAsync(new ListQuery(0, 10));
        var orphan = Assert.Single(page.Items);
        Assert.Equal(AuditStatus.Paused, orphan.Status);
    }

    [Fact]
    public async Task ApiSurface_ServesEnvelopesAndGatesAnonymousRequests()
    {
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());
        await SeedProjectAsync(backend, "http://127.0.0.1:1/");

        using var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        var anonymous = await http.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        http.DefaultRequestHeaders.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);
        var response = await http.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(envelope.RootElement.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.True(envelope.RootElement.GetProperty("pagination").GetProperty("total").GetInt64() >= 1);

        // Health stays exempt for the supervisor probe.
        using var probe = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        var health = await probe.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_IsPublishedAndTokenGated()
    {
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        var descriptorPath = Path.Combine(_dataDirectory, "mcp-endpoint.json");
        Assert.True(File.Exists(descriptorPath));
        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath));
        var endpoint = descriptor.RootElement.GetProperty("endpoint").GetString()!;
        var token = descriptor.RootElement.GetProperty("token").GetString()!;
        Assert.StartsWith("http://127.0.0.1:", endpoint);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var anonymous = await http.PostAsync(endpoint, new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task ReAudit_UsesStoredDatasetWithoutRefetching()
    {
        await using var target = await LocalTargetSite.StartAsync(threePageSite: true);
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        var (projectId, configurationId) = await SeedProjectAsync(backend, target.BaseUrl);
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var lifecycle = backend.App.Services.GetRequiredService<IAuditLifecyclePort>();
        var auditId = await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 50));
        await WaitForStatusAsync(backend, auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var hitsAfterFirst = target.RequestCount;
        var reAuditId = await lifecycle.ReAuditAsync(auditId, refresh: false);
        Assert.NotEqual(auditId, reAuditId);
        await WaitForStatusAsync(backend, reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        Assert.Equal(hitsAfterFirst, target.RequestCount);

        var scores = backend.App.Services.GetRequiredService<IReadRepository<AuditScoreDto>>();
        var scorePage = await scores.ListAsync(new ListQuery(0, 100,
            new Dictionary<string, string> { ["auditId"] = reAuditId.ToString() }));
        Assert.True(scorePage.TotalCount >= 1, "Re-audit must produce scores over the stored dataset.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<(long ProjectId, long ConfigurationId)> SeedProjectAsync(
        CrawlForgeBackend backend, string seedUrl)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Execution Test", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return (project.Id, configuration.Id);
    }

    private static async Task<AuditDto> WaitForStatusAsync(
        CrawlForgeBackend backend, long auditId, AuditStatus status, TimeSpan timeout) =>
        await WaitForConditionAsync(backend, auditId, a => a.Status == status, timeout);

    private static async Task<AuditDto> WaitForConditionAsync(
        CrawlForgeBackend backend, long auditId, Func<AuditDto, bool> condition, TimeSpan timeout)
    {
        var reads = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && condition(audit))
            {
                return audit;
            }

            if (audit?.Status == AuditStatus.Failed)
            {
                throw new InvalidOperationException($"Audit {auditId} failed while waiting for {condition.Method.Name}.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach the expected state within {timeout}.");
    }

    private static (long discovered, long fetched, long analyzed) CountersOf(AuditDto audit)
    {
        try
        {
            using var document = JsonDocument.Parse(audit.CountersJson);
            var root = document.RootElement;
            return (
                root.TryGetProperty("discovered", out var d) ? d.GetInt64() : 0,
                root.TryGetProperty("fetched", out var f) ? f.GetInt64() : 0,
                root.TryGetProperty("analyzed", out var a) ? a.GetInt64() : 0);
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the per-test temp directory.
        }
    }

    /// <summary>
    /// Minimal loopback-only crawl target: a three-page site or a slower
    /// many-page site for lifecycle timing. Counts requests so re-audit tests
    /// can prove no re-fetch happened.
    /// </summary>
    private sealed class LocalTargetSite : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _threePageSite;
        private int _requestCount;

        private LocalTargetSite(HttpListener listener, string baseUrl, bool threePageSite)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _threePageSite = threePageSite;
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static async Task<LocalTargetSite> StartAsync(bool threePageSite)
        {
            // Acquire a free loopback port, then bind the listener to it.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            var baseUrl = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            await Task.Yield();
            return new LocalTargetSite(listener, baseUrl, threePageSite);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (!_threePageSite && path.StartsWith("/p/", StringComparison.Ordinal))
                {
                    await Task.Delay(150, _cts.Token);
                }

                string body;
                if (path == "/robots.txt")
                {
                    body = "User-agent: *\nAllow: /\n";
                }
                else if (_threePageSite)
                {
                    body = path switch
                    {
                        "/" => Page("Home", "<a href='/a'>A</a> <a href='/b'>B</a>"),
                        "/a" => Page("Page A", "<a href='/'>Home</a>"),
                        "/b" => Page("Page B", "<p>Leaf page.</p>"),
                        _ => Page("Not Found", "<p>Missing.</p>")
                    };
                }
                else
                {
                    var links = string.Join(" ", Enumerable.Range(0, 12).Select(i => $"<a href='/p/{i}'>P{i}</a>"));
                    body = path == "/" ? Page("Index", links) : Page("Slow Page", "<a href='/'>Home</a>");
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = path is not ("/") and not ("/a") and not ("/b") && !path.StartsWith("/p/") && path != "/robots.txt"
                    ? 404
                    : 200;
                context.Response.ContentType = path == "/robots.txt" ? "text/plain" : "text/html";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _cts.Token);
            }
            catch (Exception)
            {
                // Connection aborted during shutdown — nothing to report.
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }
        }

        private static string Page(string title, string content) =>
            $"<!doctype html><html><head><title>{title}</title></head><body>{content}</body></html>";

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // Listener already closed.
            }

            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Shutdown race — the listener is closed regardless.
            }

            _cts.Dispose();
        }
    }
}
