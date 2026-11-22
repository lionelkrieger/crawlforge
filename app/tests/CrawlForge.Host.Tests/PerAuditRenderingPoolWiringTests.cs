using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Capture;
using CrawlForge.Crawler.Rendering;
using CrawlForge.Host;
using CrawlForge.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-10-04 engine wiring proof: a rendering-dependent audit builds a PER-AUDIT
/// rendering pool whose options come from the frozen CaptureSettings snapshot
/// (WP-10-01 handoff §4.1) and the engine's audit-finally path disposes it; a
/// non-JS audit builds no pool and never renders. The real capture coordinator
/// factory runs end to end; the pool boundary is faked, so no browser is ever
/// launched and no browser binary is required.
/// </summary>
public sealed class PerAuditRenderingPoolWiringTests
{
    private const string PageHtml =
        "<!doctype html><html><head><title>Pool Wiring</title></head><body><p>Pool wiring page.</p></body></html>";

    [Fact]
    public async Task JsAudit_PerAuditPoolOptions_ComeFromCaptureSettings_AndPoolIsDisposedAtAuditEnd()
    {
        var probe = new PerAuditPoolProbe();
        var settings = new CaptureSettings(RenderWaitQuietMs: 1234, RenderWaitHardCapMs: 9000, RenderingPoolSize: 3);

        await using var target = await SinglePageSite.StartAsync();
        await using var backend = await StartBackendAsync(probe);
        var (projectId, configurationId) = await SeedConfigurationAsync(backend, target.BaseUrl, CaptureMode.Js, settings);
        long auditId = await StartAuditAsync(backend, projectId, configurationId);
        await WaitForCompletionAsync(backend, auditId);

        // WP-10-01 §4.1: the audit's non-default knobs reached the pool
        // options of the pool built FOR THIS AUDIT...
        RenderingPoolOptions options = Assert.Single(probe.PerAuditOptions);
        Assert.Equal(3, options.MaxConcurrency);
        Assert.Equal(1234, options.RenderWaitQuietMs);
        Assert.Equal(9000, options.RenderWaitHardCapMs);

        // ...that pool served every render of the audit (shared pool idle)...
        RecordingRenderingPool pool = Assert.Single(probe.PerAuditPools);
        Assert.NotEmpty(pool.Requests);
        Assert.Empty(probe.SharedPool.Requests);

        // ...and the engine's audit-finally path disposed it (polled: the
        // status flips just before the finally runs).
        await WaitUntilAsync(() => pool.DisposeCount >= 1, TimeSpan.FromSeconds(15), "per-audit pool disposal");
        Assert.Equal(1, pool.DisposeCount);
        Assert.Equal(0, probe.SharedPool.DisposeCount);
    }

    [Fact]
    public async Task NonJsAudit_BuildsNoPerAuditPool_AndNeverRenders()
    {
        var probe = new PerAuditPoolProbe();
        var settings = new CaptureSettings(RenderWaitQuietMs: 1234, RenderingPoolSize: 3);

        await using var target = await SinglePageSite.StartAsync();
        await using var backend = await StartBackendAsync(probe);
        var (projectId, configurationId) = await SeedConfigurationAsync(backend, target.BaseUrl, CaptureMode.NonJs, settings);
        long auditId = await StartAuditAsync(backend, projectId, configurationId);
        await WaitForCompletionAsync(backend, auditId);

        // Non-JS audits never build a per-audit pool and never render — no
        // browser could be launched on this path.
        Assert.Empty(probe.PerAuditOptions);
        Assert.Empty(probe.PerAuditPools);
        Assert.Empty(probe.SharedPool.Requests);
        Assert.Equal(0, probe.SharedPool.DisposeCount);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static Task<CrawlForgeBackend> StartBackendAsync(PerAuditPoolProbe probe) =>
        CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={Path.Combine(Path.GetTempPath(), "crawlforge-pool-wiring", Guid.NewGuid().ToString("N"))}", "--CrawlForge:Port=0"],
            builder =>
            {
                // Shared pool replaced by a fake: the engine's browser pre-flight
                // only probes the concrete HeadlessRenderingPool, so this test
                // needs no browser binary at all. The capture factory is the REAL
                // one — only the per-audit pool composition is recorded.
                builder.Services.AddSingleton<IRenderingPool>(probe.SharedPool);
                builder.Services.AddSingleton<ICaptureModeCoordinatorFactory>(p =>
                    new CaptureModeCoordinatorFactory(
                        p.GetRequiredService<IRenderingPool>(),
                        p.GetRequiredService<ITolerantDomParser>(),
                        p.GetRequiredService<IContentStore>(),
                        p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
                        p.GetRequiredService<IWriteRepository<UrlRecordDto>>(),
                        p.GetRequiredService<ICaptureSnippetExecutor>(),
                        logger: null,
                        renderingPoolFactory: probe.CreatePerAuditPool));
            });

    private static async Task<(long ProjectId, long ConfigurationId)> SeedConfigurationAsync(
        CrawlForgeBackend backend, string seedUrl, CaptureMode mode, CaptureSettings settings)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Pool Wiring", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            CaptureMode = mode,
            CaptureSettings = settings,
        });
        return (project.Id, configuration.Id);
    }

    private static async Task<long> StartAuditAsync(CrawlForgeBackend backend, long projectId, long configurationId)
    {
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        return await execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(RequestsPerSecond: 50));
    }

    private static async Task WaitForCompletionAsync(CrawlForgeBackend backend, long auditId)
    {
        var reads = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && audit.Status is AuditStatus.Completed or AuditStatus.Failed)
            {
                Assert.True(audit.Status == AuditStatus.Completed,
                    $"Audit {auditId} ended as {audit.Status} ({audit.CountersJson}).");
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not complete within 60 s.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// Records every per-audit pool the capture factory composes (options +
    /// instance) and exposes the shared fake pool. Every pool double is a
    /// no-browser stub: constructing or calling it can never launch anything.
    /// </summary>
    private sealed class PerAuditPoolProbe
    {
        private readonly ConcurrentQueue<RenderingPoolOptions> _options = new();
        private readonly ConcurrentQueue<RecordingRenderingPool> _pools = new();

        public RecordingRenderingPool SharedPool { get; } = new();

        public IReadOnlyList<RenderingPoolOptions> PerAuditOptions => _options.ToArray();

        public IReadOnlyList<RecordingRenderingPool> PerAuditPools => _pools.ToArray();

        public IRenderingPool CreatePerAuditPool(RenderingPoolOptions options)
        {
            _options.Enqueue(options);
            var pool = new RecordingRenderingPool();
            _pools.Enqueue(pool);
            return pool;
        }
    }

    /// <summary>Browser-free rendering pool double with disposal tracking.</summary>
    private sealed class RecordingRenderingPool : IRenderingPool, IAsyncDisposable
    {
        private readonly ConcurrentQueue<RenderRequest> _requests = new();
        private int _disposeCount;

        public IReadOnlyList<RenderRequest> Requests => _requests.ToArray();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RenderedPage(
                request.Url,
                PageHtml,
                Screenshot: ReadOnlyMemory<byte>.Empty,
                ConsoleMessages: Array.Empty<ConsoleMessage>(),
                PostRenderWafVerdict: null));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Minimal loopback-only crawl target (one page + robots.txt).</summary>
    private sealed class SinglePageSite : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private SinglePageSite(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public static async Task<SinglePageSite> StartAsync()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            var baseUrl = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            await Task.Yield();
            return new SinglePageSite(listener, baseUrl);
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

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var path = context.Request.Url?.AbsolutePath ?? "/";
                        byte[] bytes = Encoding.UTF8.GetBytes(
                            path == "/robots.txt" ? "User-agent: *\nAllow: /\n" : PageHtml);
                        context.Response.StatusCode = 200;
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
                });
            }
        }

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
