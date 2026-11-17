using System.Net;
using System.Net.Http.Json;
using System.Runtime;
using System.Text.Json;
using CrawlForge.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// Scaffold verification for acceptance criterion #1 (first-launch initialization
/// completes with no network and opens the main hub route) and criterion #19
/// (version/diagnostics surface reports the active GC profile).
/// </summary>
public sealed class HostScaffoldTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "crawlforge-scaffold", Guid.NewGuid().ToString("N"));

    private string[] BuildArgs() =>
    [
        $"--CrawlForge:DataDirectory={_dataDirectory}",
        "--CrawlForge:Port=0"
    ];

    [Fact]
    public async Task Scaffold_FirstLaunch_CompletesOffline_AndOpensMainHubRoute()
    {
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());

        // First-launch initialization completed against a fresh local data directory.
        Assert.True(backend.FirstLaunch.FirstRun);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "settings.json")));

        // Loopback-only binding with a dynamically assigned free port.
        Assert.True(backend.Port > 0);
        Assert.StartsWith("http://127.0.0.1:", backend.BaseAddress);
        Assert.All(backend.App.Urls, url => Assert.StartsWith("http://127.0.0.1:", url));

        using var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };

        // The main hub route is open (stub descriptor retained for compatibility).
        var hubResponse = await http.GetAsync("/hub");
        Assert.Equal(HttpStatusCode.OK, hubResponse.StatusCode);
        using var hub = JsonDocument.Parse(await hubResponse.Content.ReadAsStringAsync());
        Assert.Equal("ready", hub.RootElement.GetProperty("status").GetString());

        // The real SignalR hub answers negotiate with the session token.
        using var negotiateRequest = new HttpRequestMessage(
            HttpMethod.Post, "/hubs/realtime/negotiate?negotiateVersion=1");
        negotiateRequest.Headers.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);
        var negotiateResponse = await http.SendAsync(negotiateRequest);
        Assert.Equal(HttpStatusCode.OK, negotiateResponse.StatusCode);
        using var negotiate = JsonDocument.Parse(await negotiateResponse.Content.ReadAsStringAsync());
        Assert.True(negotiate.RootElement.TryGetProperty("connectionToken", out _));

        // Without the token the hub is gated (composition-layer session gate).
        var anonymousNegotiate = await http.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousNegotiate.StatusCode);

        // The readiness probe responds without a token (supervisor probe exemption).
        var healthResponse = await http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        // The spawn-time port handshake descriptor matches the actual port.
        var descriptorPath = Path.Combine(_dataDirectory, "backend-port.json");
        Assert.True(File.Exists(descriptorPath));
        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath));
        Assert.Equal(backend.Port, descriptor.RootElement.GetProperty("port").GetInt32());

        // The authoritative Security handshake publishes port + per-launch token.
        var handshakePath = Path.Combine(_dataDirectory, "loopback-handshake.json");
        Assert.True(File.Exists(handshakePath));
        using var handshake = JsonDocument.Parse(await File.ReadAllTextAsync(handshakePath));
        Assert.Equal(1, handshake.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(backend.Port, handshake.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(backend.SessionToken, handshake.RootElement.GetProperty("sessionToken").GetString());

        // A second launch reuses the seeded local state (no re-initialization).
        await backend.StopAsync();

        // Graceful stop retires the handshake descriptor.
        Assert.False(File.Exists(handshakePath));

        await using var second = await CrawlForgeBackend.StartAsync(BuildArgs());
        Assert.False(second.FirstLaunch.FirstRun);
        Assert.NotEqual(backend.SessionToken, second.SessionToken);
    }

    [Fact]
    public async Task Scaffold_Diagnostics_ReportsActiveServerGcProfile()
    {
        await using var backend = await CrawlForgeBackend.StartAsync(BuildArgs());
        using var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        http.DefaultRequestHeaders.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);

        var report = await http.GetFromJsonAsync<JsonElement>("/api/diagnostics/version");

        Assert.Equal("CrawlForge", report.GetProperty("product").GetString());
        Assert.True(report.GetProperty("gc").GetProperty("server").GetBoolean());
        Assert.True(report.GetProperty("gc").GetProperty("concurrent").GetBoolean());

        // The profile is active in this process too: Directory.Build.props applies
        // Server GC + concurrent GC to every project, including the test host.
        Assert.True(GCSettings.IsServerGC);
    }

    [Fact]
    public async Task Scaffold_Lifecycle_DrainsParticipantsOnGracefulShutdown()
    {
        var participant = new RecordingLifecycle();
        await using var backend = await CrawlForgeBackend.StartAsync(
            BuildArgs(),
            builder => builder.Services.AddSingleton<IBackendLifecycle>(participant));

        Assert.True(participant.Started);
        Assert.False(participant.Stopped);

        await backend.StopAsync();

        Assert.True(participant.Stopped);
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

    private sealed class RecordingLifecycle : IBackendLifecycle
    {
        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }
}
