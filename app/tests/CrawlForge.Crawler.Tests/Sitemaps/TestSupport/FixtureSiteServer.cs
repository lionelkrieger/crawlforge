using System.Diagnostics;
using System.Text.Json;

namespace CrawlForge.Crawler.Tests.Sitemaps.TestSupport;

/// <summary>
/// WP-09-01 OWN fixture-site copy for live component tests: starts the
/// read-only fixture server (app/tests/fixture-site/server.mjs, never edited)
/// on the builder-assigned port 47313 and stops exactly that process on
/// dispose. Never uses the coordinator/sibling ports 47311/47312 and never
/// touches %APPDATA%\CrawlForge.
/// </summary>
public sealed class FixtureSiteServer : IAsyncDisposable
{
    public const int Port = 47313;

    private readonly Process _process;

    private FixtureSiteServer(Process process)
    {
        _process = process;
    }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static async Task<FixtureSiteServer> StartAsync()
    {
        var serverPath = LocateServerScript();
        var startInfo = new ProcessStartInfo("node", $"\"{serverPath}\" --port {Port}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the fixture-site node process.");
        var server = new FixtureSiteServer(process);

        // Wait for readiness (bounded); surface stderr on failure.
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Fixture server exited early (code {process.ExitCode}): {stderr}");
            }

            try
            {
                using var response = await httpClient.GetAsync(server.BaseUrl + "/__fixture/reset");
                if (response.IsSuccessStatusCode)
                {
                    return server;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(150);
        }

        await server.DisposeAsync();
        throw new InvalidOperationException($"Fixture server on port {Port} did not become ready within 20s. Is another process holding the port?");
    }

    public async Task ResetAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await httpClient.GetAsync(BaseUrl + "/__fixture/reset");
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonDocument> RequestsAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await httpClient.GetStringAsync(BaseUrl + "/__fixture/requests");
        return JsonDocument.Parse(body);
    }

    private static string LocateServerScript()
    {
        // Walk up from the test assembly location to the repository root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(directory.FullName, "app", "tests", "fixture-site", "server.mjs"),
                Path.Combine(directory.FullName, "tests", "fixture-site", "server.mjs"),
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate app/tests/fixture-site/server.mjs above the test assembly.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                // Kill only the process this class started (tracked PID).
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
