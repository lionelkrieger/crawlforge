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
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Rendering;
using CrawlForge.Host;
using CrawlForge.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-10-04 §5.6.3 engine pre-flight proof: a js-mode audit with NO resolvable
/// local browser fails before any crawl work with exactly the pre-crawl
/// validator's browser issue message — ONE code path with
/// <see cref="PreCrawlValidator.ResolveRenderingBrowser"/> — and the crawl
/// target receives no request at all. The shared rendering pool is the REAL
/// <see cref="HeadlessRenderingPool"/> whose resolution options make the §5.6.3
/// chain deterministically fail (empty packaged root, no local-install
/// probing); resolution probes the filesystem only and never launches anything.
/// </summary>
public sealed class PreCrawlBrowserPreFlightTests
{
    [Fact]
    public async Task JsAudit_WithoutResolvableBrowser_FailsWithValidatorMessage_AndMakesNoCrawlRequest()
    {
        BrowserResolutionOptions unresolvable = UnresolvableBrowserOptions();
        var sharedPool = new HeadlessRenderingPool(new RenderingPoolOptions { BrowserResolution = unresolvable });
        await using var target = await RecordingSite.StartAsync();
        await using var backend = await StartBackendAsync(sharedPool);
        var (projectId, configurationId) = await SeedConfigurationAsync(backend, target.BaseUrl);
        long auditId = await StartAuditAsync(backend, projectId, configurationId);

        AuditDto audit = await WaitForTerminalAsync(backend, auditId);
        Assert.Equal(AuditStatus.Failed, audit.Status);

        // Exactly the validator's message for the same unresolvable chain...
        string actual = ReadErrorMessage(audit);
        string? expected = PreCrawlValidator.CheckRenderingBrowser(
            CaptureMode.Js, CaptureSettings.Default, new PoolBrowserResolver(sharedPool));
        Assert.NotNull(expected);
        Assert.Equal(expected, actual);
        Assert.Contains("Tried, in order: the pinned bundled Chrome for Testing", actual, StringComparison.Ordinal);
        Assert.Contains("Non-JS capture remains available", actual, StringComparison.Ordinal);

        // ...and the crawl target was never touched (pre-crawl validation runs
        // before any crawl work: no seed fetch, not even robots.txt).
        Assert.Empty(target.Requests);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    /// <summary>
    /// A §5.6.3 chain that deterministically resolves nothing: an empty
    /// packaged-assets root and no local-install probing.
    /// </summary>
    private static BrowserResolutionOptions UnresolvableBrowserOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "crawlforge-no-browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new BrowserResolutionOptions { PackagedAssetsRoot = root, ProbeLocalInstalls = false };
    }

    private static Task<CrawlForgeBackend> StartBackendAsync(HeadlessRenderingPool sharedPool) =>
        CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={Path.Combine(Path.GetTempPath(), "crawlforge-browser-preflight", Guid.NewGuid().ToString("N"))}", "--CrawlForge:Port=0"],
            builder =>
            {
                // The shared pool is REAL but cannot resolve a browser — the
                // engine's pre-flight probes it through PoolBrowserResolver.
                builder.Services.AddSingleton<IRenderingPool>(sharedPool);
            });

    private static async Task<(long ProjectId, long ConfigurationId)> SeedConfigurationAsync(
        CrawlForgeBackend backend, string seedUrl)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Browser Pre-Flight", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            CaptureMode = CaptureMode.Js,
            CaptureSettings = CaptureSettings.Default,
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

    private static async Task<AuditDto> WaitForTerminalAsync(CrawlForgeBackend backend, long auditId)
    {
        var reads = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && audit.Status is AuditStatus.Completed or AuditStatus.Failed or AuditStatus.Cancelled)
            {
                return audit;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach a terminal state within 60 s.");
    }

    private static string ReadErrorMessage(AuditDto audit)
    {
        using JsonDocument counters = JsonDocument.Parse(audit.CountersJson);
        Assert.True(
            counters.RootElement.TryGetProperty("errorMessage", out JsonElement message),
            $"Audit {audit.Id} carries no failure message ({audit.CountersJson}).");
        return message.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Minimal loopback-only crawl target that records every request it
    /// receives (the §5.6.3 pre-flight must produce none).
    /// </summary>
    private sealed class RecordingSite : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly ConcurrentQueue<string> _requests = new();

        private RecordingSite(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> Requests => _requests.ToArray();

        public static async Task<RecordingSite> StartAsync()
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
            return new RecordingSite(listener, baseUrl);
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
                    _requests.Enqueue(context.Request.Url?.PathAndQuery ?? "/");
                    try
                    {
                        var path = context.Request.Url?.AbsolutePath ?? "/";
                        byte[] bytes = Encoding.UTF8.GetBytes(
                            path == "/robots.txt" ? "User-agent: *\nAllow: /\n" : "<html><body>no browser</body></html>");
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
