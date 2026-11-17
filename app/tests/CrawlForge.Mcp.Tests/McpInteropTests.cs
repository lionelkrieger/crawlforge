using System.Diagnostics;
using System.Text;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Criterion #16 interop gate: the official TypeScript MCP SDK client
/// exercises the hand-rolled C# server on BOTH transports (streamable HTTP
/// and the packaged stdio adapter), covering the handshake, all 8 tools,
/// all 15 resources, all 5 prompts, invalid-token rejection, and rotation
/// invalidation. The C# side starts the real loopback server over the real
/// persistence stack and drives the vitest interop suite as a child process.
/// </summary>
public class McpInteropTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    /// <param name="output">Test output for child-process logs.</param>
    public McpInteropTests(McpSqliteStackFixture stack, ITestOutputHelper output)
    {
        _stack = stack;
        _output = output;
    }

    private static string FindAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "mcp-stdio-adapter")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Cannot locate the app workspace root from " + AppContext.BaseDirectory);
    }

    private static async Task<(int ExitCode, string Output)> RunNpmAsync(
        string appRoot,
        string arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c npm " + arguments,
            WorkingDirectory = appRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw new TimeoutException($"npm {arguments} exceeded {timeout}.");
        }

        var output = await stdoutTask + Environment.NewLine + await stderrTask;
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task TypeScriptSdkInteropCoversBothTransportsAllSurfacesAndRotation()
    {
        var appRoot = FindAppRoot();
        var adapterEntry = Path.Combine(appRoot, "src", "mcp-stdio-adapter", "dist", "index.js");

        // Ensure workspace dependencies and the packaged adapter build exist.
        if (!Directory.Exists(Path.Combine(appRoot, "node_modules", "@modelcontextprotocol", "sdk")))
        {
            var install = await RunNpmAsync(appRoot, "install --no-audit --no-fund",
                new Dictionary<string, string>(), TimeSpan.FromMinutes(5));
            Assert.True(install.ExitCode == 0, "npm install failed:\n" + install.Output);
        }

        if (!File.Exists(adapterEntry))
        {
            var build = await RunNpmAsync(appRoot, "run build --workspace src/mcp-stdio-adapter",
                new Dictionary<string, string>(), TimeSpan.FromMinutes(3));
            Assert.True(build.ExitCode == 0, "adapter build failed:\n" + build.Output);
        }

        // Real server over the real stack, with seeded domain data.
        var host = new McpTestHost(_stack);
        await host.Tokens.InitializeAsync();
        var (auditId, projectId, configurationId) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/interop");
        await _stack.StoreBodyAsync(auditId, urlId,
            "<html><head><title>Interop</title></head><body><p>interop content</p></body></html>");
        await _stack.Findings.CreateAsync(new FindingDto(
            0, auditId, "content.missing-title", AuditCategory.Content, FindingSeverity.Error, urlId, "{}", null, false));
        var schedule = await _stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, projectId, configurationId, "weekly", true, null, null, DateTimeOffset.UtcNow.AddDays(1)));
        var import = await _stack.LogImports.CreateAsync(new LogImportDto(
            0, projectId, LogFileFormat.ApacheNginxCombined,
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, "{}",
            BotVerificationMode.CidrOnly, LogImportStatus.Completed, DateTimeOffset.UtcNow, null));

        using var server = new McpLoopbackServer(host.Dispatcher, host.Tokens);
        server.Start();

        // In-process probe: verify the exact token works before spawning npm.
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Post, server.MessageEndpoint)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
                    Encoding.UTF8, "application/json"),
            };
            probeRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + host.Tokens.CurrentToken);
            using var probeResponse = await probe.SendAsync(probeRequest);
            Assert.True(probeResponse.IsSuccessStatusCode,
                $"In-process probe failed: {probeResponse.StatusCode} (token length {host.Tokens.CurrentToken.Length})");
        }

        var baseEnvironment = new Dictionary<string, string>
        {
            ["CF_MCP_ENDPOINT"] = server.MessageEndpoint,
            ["CF_MCP_ADAPTER"] = adapterEntry,
            ["CF_MCP_AUDIT_ID"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CF_MCP_URL_ID"] = urlId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CF_MCP_PROJECT_ID"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CF_MCP_CONFIGURATION_ID"] = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CF_MCP_SCHEDULE_ID"] = schedule.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CF_MCP_IMPORT_ID"] = import.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // Phase 1: full coverage with the original token.
        var fullEnvironment = new Dictionary<string, string>(baseEnvironment)
        {
            ["CF_MCP_MODE"] = "full",
            ["CF_MCP_TOKEN"] = host.Tokens.CurrentToken,
        };
        var full = await RunNpmAsync(appRoot, "run test:interop --workspace src/mcp-stdio-adapter",
            fullEnvironment, TimeSpan.FromMinutes(4));
        _output.WriteLine("=== interop (full) ===\n" + full.Output);
        Assert.True(full.ExitCode == 0, "Full interop suite failed:\n" + full.Output);

        // Phase 2: rotate — prior clients must be invalidated on both transports.
        var newToken = await host.Tokens.RotateAsync();
        var rotationEnvironment = new Dictionary<string, string>(baseEnvironment)
        {
            ["CF_MCP_MODE"] = "rotation",
            ["CF_MCP_TOKEN"] = fullEnvironment["CF_MCP_TOKEN"],
            ["CF_MCP_NEW_TOKEN"] = newToken,
        };
        var rotation = await RunNpmAsync(appRoot, "run test:interop --workspace src/mcp-stdio-adapter",
            rotationEnvironment, TimeSpan.FromMinutes(4));
        _output.WriteLine("=== interop (rotation) ===\n" + rotation.Output);
        Assert.True(rotation.ExitCode == 0, "Rotation interop suite failed:\n" + rotation.Output);
    }
}
