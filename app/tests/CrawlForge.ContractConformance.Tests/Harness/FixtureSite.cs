using System.Diagnostics;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>
/// The harness's OWN fixture-site instance on port 47312 (WP-08-03 coordination:
/// cf-wp-08-04 owns 47311). Read-only use of tests/fixture-site/server.mjs;
/// started/stopped by this harness only. The recorder endpoints
/// (/__fixture/reset, /__fixture/requests) prove the crawl stayed GET-only.
/// </summary>
public sealed class FixtureSite : IAsyncDisposable
{
    /// <summary>The port reserved for this workpiece (never 47311).</summary>
    public const int Port = 47312;

    private readonly Process _process;
    private int _disposed;

    private FixtureSite(Process process)
    {
        _process = process;
    }

    /// <summary>Fixture origin base URL.</summary>
    public static string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Starts node server.mjs --port 47312 and waits until it answers.</summary>
    /// <param name="cancellationToken">Startup timeout token.</param>
    /// <returns>The running fixture instance.</returns>
    public static async Task<FixtureSite> StartAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { RepoPaths.FixtureServerMjs, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            WorkingDirectory = Path.GetDirectoryName(RepoPaths.FixtureServerMjs)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the fixture site (node).");

        // Drain the pipes so the child never blocks on a full buffer.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var site = new FixtureSite(process);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Fixture site exited early with code {process.ExitCode}.");
            }

            try
            {
                using var response = await http.GetAsync(BaseUrl + "/__fixture/reset", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return site;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Probe timeout; retry.
            }

            await Task.Delay(200, cancellationToken);
        }

        await site.DisposeAsync();
        throw new TimeoutException("Fixture site did not become ready on port 47312 within 30 s.");
    }

    /// <summary>GET /__fixture/reset — clears the non-GET/tag-hit recorder.</summary>
    /// <param name="cancellationToken">Request token.</param>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync(BaseUrl + "/__fixture/reset", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>GET /__fixture/requests — the recorder snapshot (nonGet + tagHits).</summary>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>Parsed recorder payload.</returns>
    public async Task<System.Text.Json.JsonElement> RequestsAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var text = await http.GetStringAsync(BaseUrl + "/__fixture/requests", cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
