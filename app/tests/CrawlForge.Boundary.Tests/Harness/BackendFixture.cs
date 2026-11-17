using CrawlForge.Host;
using CrawlForge.Host.Hosting;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// In-process backend fixture: starts the REAL composition root against a fresh
/// temporary data directory with an OS-assigned loopback port, and exposes an
/// HttpClient carrying the per-launch session token. The backend runs inside
/// this test process, so process-level monitors attribute all product I/O to
/// this process id.
/// </summary>
public sealed class BackendFixture : IAsyncDisposable
{
    private BackendFixture(string dataDirectory, CrawlForgeBackend backend, HttpClient http)
    {
        DataDirectory = dataDirectory;
        Backend = backend;
        Http = http;
    }

    /// <summary>Fresh per-test local data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>The running backend.</summary>
    public CrawlForgeBackend Backend { get; }

    /// <summary>Session-authenticated loopback client.</summary>
    public HttpClient Http { get; }

    /// <summary>The process hosting the backend (this test process).</summary>
    public static int ProcessId => Environment.ProcessId;

    /// <summary>Starts a fresh backend with default local configuration.</summary>
    /// <returns>The fixture; dispose to stop and clean up.</returns>
    public static async Task<BackendFixture> StartAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "cf-boundary-" + Guid.NewGuid().ToString("N"));
        var backend = await CrawlForgeBackend.StartAsync(
        [
            $"--CrawlForge:DataDirectory={dataDirectory}",
            "--CrawlForge:Port=0",
        ]);

        var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        http.DefaultRequestHeaders.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);
        return new BackendFixture(dataDirectory, backend, http);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Backend.DisposeAsync();
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
