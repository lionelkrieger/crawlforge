using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>
/// The shared live-backend fixture (WP-08-03): builds the backend with the
/// launch-crawlforge.ps1 step-2 command under CF_AGENT_ID, starts the resulting
/// DLL headlessly on an OS-assigned loopback port with an ISOLATED scratch data
/// directory outside the repository, waits for the loopback handshake + health
/// + MCP descriptor, then runs the bounded fixture scenario:
///
///   fixture site (own instance, port 47312) → project "Conformance harness
///   fixture" + configurations → http-mode audit (rendering disabled,
///   limitOverrides maxUrls 220 / maxDepth 6 / maxDurationSeconds 180) with a
///   live SignalR capture → trap payload capture (noindex, redirect-chain) →
///   schedule / destination / export / log-import / saved-query entities →
///   OFFLINE re-audit with the fixture STOPPED (storage-to-engine proof) →
///   redirect-matrix upload + results-endpoint validation (matrix-row-validated
///   hub batches; the autoSeedAudit race is captured as G-MATRIX-SEED-RACE
///   evidence, not used as the conformance path).
///
/// Data safety: never touches %APPDATA%\CrawlForge or the owner's projects;
/// the scratch directory is kept after the run for lead review. Shutdown kills
/// the harness's own process (no /api/system/shutdown route exists in the
/// product — SystemController exposes only health + compact).
/// </summary>
public sealed class LiveBackendFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly StringBuilder _backendConsole = new();
    private readonly object _consoleGate = new();
    private Process? _backendProcess;
    private FixtureSite? _fixture;
    private SignalRJsonClient? _hub;
    private McpClient? _mcp;
    private HttpClient? _http;

    /// <summary>Session-authenticated REST client (X-CrawlForge-Session on every request).</summary>
    public HttpClient Http => _http ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>Live SignalR capture client (connected for the whole suite).</summary>
    public SignalRJsonClient Hub => _hub ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>MCP streamable-HTTP client (initialized handshake done).</summary>
    public McpClient Mcp => _mcp ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>The harness's own fixture site (47312).</summary>
    public FixtureSite Fixture => _fixture ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>Read-only inspector of the scratch database.</summary>
    public ScratchDb Db { get; private set; } = null!;

    /// <summary>Backend loopback origin.</summary>
    public string BaseAddress { get; private set; } = "";

    /// <summary>Per-launch session token from the loopback handshake.</summary>
    public string SessionToken { get; private set; } = "";

    /// <summary>Isolated scratch data directory (outside the repository; kept for review).</summary>
    public string DataDirectory { get; private set; } = "";

    /// <summary>Scratch SQLite database path.</summary>
    public string DbPath => Path.Combine(DataDirectory, "crawlforge.db");

    /// <summary>Main fixture audit (http mode, bounded; REST-started).</summary>
    public long AuditId { get; private set; }

    /// <summary>
    /// MCP-started bounded audit used as the offline re-audit SOURCE.
    /// G-REAUDIT-SNAPSHOT: REST start overwrites the engine's DTO configuration
    /// snapshot with the wire shape (AuditsController.cs:146-160), so re-audit
    /// of REST-started audits deterministically 500s. MCP start keeps the DTO
    /// snapshot, so this audit re-audits cleanly (new evidence refining
    /// probe-2, whose re-audit failed on a REST-started target).
    /// </summary>
    public long OfflineSourceAuditId { get; private set; }

    /// <summary>Offline re-audit of the MCP-started source (refresh:false, fixture stopped).</summary>
    public long ReauditId { get; private set; }

    /// <summary>Audit whose redirect-matrix validation emitted matrix-row-validated
    /// (the main audit; validation runs via the results endpoint).</summary>
    public long MatrixAuditId { get; private set; }

    /// <summary>Scratch project id.</summary>
    public long ProjectId { get; private set; }

    /// <summary>Main fixture configuration id (limits stored on the configuration).</summary>
    public long ConfigurationId { get; private set; }

    /// <summary>Bounded configuration id (maxUrls 3; schedules + sweep audits).</summary>
    public long BoundedConfigurationId { get; private set; }

    /// <summary>Scratch schedule id (manual, bounded configuration).</summary>
    public long ScheduleId { get; private set; }

    /// <summary>Scratch local-folder destination id.</summary>
    public long DestinationId { get; private set; }

    /// <summary>Scratch export job id (delivered to the local folder).</summary>
    public long ExportId { get; private set; }

    /// <summary>Scratch log import id.</summary>
    public long LogImportId { get; private set; }

    /// <summary>Scratch saved query id.</summary>
    public long QueryId { get; private set; }

    /// <summary>URL record id of /pages/noindex.html in the main audit (string wire id).</summary>
    public string NoindexUrlId { get; private set; } = "";

    /// <summary>URL record id of the redirect-chain trap row in the main audit.</summary>
    public string RedirectChainUrlId { get; private set; } = "";

    /// <summary>Recorder snapshot taken right after the main audit (nonGet must be empty).</summary>
    public JsonElement FixtureRequestsAfterAudit { get; private set; }

    /// <summary>Captured trap payloads keyed by file name (committed evidence).</summary>
    public IReadOnlyDictionary<string, JsonElement> TrapCaptures => _trapCaptures;

    private readonly Dictionary<string, JsonElement> _trapCaptures = new(StringComparer.Ordinal);

    /// <summary>Builds + starts the backend and runs the fixture scenario.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RepoPaths.CapturedDir);
        DataDirectory = Path.Combine(
            RepoPaths.ScratchRoot,
            "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDirectory);

        _fixture = await FixtureSite.StartAsync();
        await _fixture.ResetAsync();

        await BuildBackendAsync();
        await StartBackendAsync();

        Db = new ScratchDb(DbPath);
        await ConnectHubAsync();
        await SeedScenarioAsync();
        await RunMainAuditAsync();
        await CaptureTrapsAsync();
        await SeedOperationsAsync();
        await StartOfflineSourceAuditAsync();
        await RunOfflineReauditAsync();
        await RunMatrixAuditAsync();

        await SaveCaptureAsync("fixture-setup.json", JsonSerializer.SerializeToElement(new
        {
            dataDirectory = DataDirectory,
            baseAddress = BaseAddress,
            projectId = ProjectId,
            configurationId = ConfigurationId,
            boundedConfigurationId = BoundedConfigurationId,
            auditId = AuditId,
            offlineSourceAuditId = OfflineSourceAuditId,
            reauditId = ReauditId,
            matrixAuditId = MatrixAuditId,
            scheduleId = ScheduleId,
            destinationId = DestinationId,
            exportId = ExportId,
            logImportId = LogImportId,
            queryId = QueryId,
            noindexUrlId = NoindexUrlId,
            redirectChainUrlId = RedirectChainUrlId,
            fixturePort = FixtureSite.Port
        }));
    }

    /// <summary>Shuts down the harness's own processes; keeps the scratch data for review.</summary>
    public async Task DisposeAsync()
    {
        // WP-09-11 (G05 backend-lifecycle): stop the backend GRACEFULLY through
        // the shell-initiated shutdown route while the hub is still connected,
        // so the backend-lifecycle {kind:"shutdown"} notice is captured. The
        // process kill remains the fallback when the drain does not finish.
        if (_backendProcess is not null && !_backendProcess.HasExited && _http is not null)
        {
            try
            {
                await Rest.SendAsync(_http, "POST", "/api/system/shutdown", null);
                _backendProcess.WaitForExit(15000);
                // Let the hub client process the shutdown notice frame before
                // the capture is saved (loopback delivery is near-instant).
                await Task.Delay(500);
            }
            catch (Exception)
            {
                // Fall through to the kill fallback below.
            }
        }

        if (_hub is not null)
        {
            await SaveCaptureAsync("hub-events.json", JsonSerializer.SerializeToElement(
                _hub.Events.Select(e => new { name = e.Name, receivedAt = e.ReceivedAt, payload = e.Payload })));
            await _hub.DisposeAsync();
        }

        _mcp?.Dispose();
        _http?.Dispose();

        if (_backendProcess is not null)
        {
            try
            {
                if (!_backendProcess.HasExited)
                {
                    _backendProcess.Kill(entireProcessTree: true);
                    _backendProcess.WaitForExit(10000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            lock (_consoleGate)
            {
                File.WriteAllText(Path.Combine(DataDirectory, "backend-console.log"), _backendConsole.ToString());
            }

            _backendProcess.Dispose();
        }

        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    /// <summary>Starts a bounded audit through REST and returns its id.</summary>
    /// <param name="configurationId">Configuration to run.</param>
    /// <param name="maxUrls">URL cap for the run.</param>
    /// <returns>The new audit id.</returns>
    public async Task<long> StartBoundedAuditAsync(long configurationId, int maxUrls = 3)
    {
        var data = await Rest.PostDataAsync(Http, "/api/audits", new
        {
            configurationId = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limitOverrides = new { maxUrls, maxDepth = 1, maxDurationSeconds = 60 }
        });
        return long.Parse(data.GetProperty("auditId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Waits until the audit reaches a terminal state; throws on Failed/timeout.</summary>
    /// <param name="auditId">Audit to observe.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <returns>The terminal audit detail data element.</returns>
    public async Task<JsonElement> WaitForTerminalAsync(long auditId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(300));
        var path = $"/api/audits/{auditId}";
        while (DateTimeOffset.UtcNow < deadline)
        {
            var data = await Rest.GetDataAsync(Http, path);
            var state = data.GetProperty("state").GetString();
            if (state is "completed" or "cancelled")
            {
                return data;
            }

            if (state == "failed")
            {
                var message = data.TryGetProperty("errorMessage", out var error) ? error.GetString() : null;
                throw new InvalidOperationException($"Audit {auditId} failed: {message}");
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Audit {auditId} did not reach a terminal state in time.");
    }

    /// <summary>Saves a captured payload under the project's captured/ directory.</summary>
    /// <param name="fileName">File name (relative to captured/).</param>
    /// <param name="payload">JSON payload.</param>
    public async Task SaveCaptureAsync(string fileName, JsonElement payload)
    {
        var path = Path.Combine(RepoPaths.CapturedDir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, Indented));
    }

    private async Task BuildBackendAsync()
    {
        // launch-crawlforge.ps1 step 2: dotnet build <HostDir> -c Debug.
        // CF_AGENT_ID is inherited from the test process (agent output isolation).
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "build", RepoPaths.HostProjectDir, "-c", "Debug" },
            WorkingDirectory = RepoPaths.AppRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet build.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        var buildLog = output.ToString();
        await File.WriteAllTextAsync(Path.Combine(DataDirectory, "backend-build.log"), buildLog);
        if (process.ExitCode != 0)
        {
            var tail = buildLog.Length <= 3000 ? buildLog : buildLog[^3000..];
            throw new InvalidOperationException($"Backend build failed (exit {process.ExitCode}). Log tail:\n{tail}");
        }
    }

    private async Task StartBackendAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                RepoPaths.HostDll,
                "--CrawlForge:Port=0",
                $"--CrawlForge:DataDirectory={DataDirectory}"
            },
            WorkingDirectory = Path.GetDirectoryName(RepoPaths.HostDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Isolation belt-and-braces: the config argument is authoritative
        // (CrawlForgePaths binds CrawlForge:DataDirectory); the env vars keep
        // any env-based resolution on the scratch directory as well.
        startInfo.Environment["CRAWLFORGE_DATA_DIRECTORY"] = DataDirectory;
        startInfo.Environment["CrawlForge__DataDirectory"] = DataDirectory;

        _backendProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the backend process.");
        _backendProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_consoleGate) _backendConsole.AppendLine(e.Data); };
        _backendProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_consoleGate) _backendConsole.AppendLine(e.Data); };
        _backendProcess.BeginOutputReadLine();
        _backendProcess.BeginErrorReadLine();

        // Wait for the loopback handshake descriptor ({version,port,sessionToken,createdAtUtc}).
        var handshakePath = Path.Combine(DataDirectory, "loopback-handshake.json");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        JsonElement handshake = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_backendProcess.HasExited)
            {
                throw new InvalidOperationException($"Backend exited early (code {_backendProcess.ExitCode}). See backend-console.log in {DataDirectory}.");
            }

            if (File.Exists(handshakePath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(handshakePath));
                    handshake = document.RootElement.Clone();
                    break;
                }
                catch (JsonException)
                {
                    // Partial write; retry.
                }
                catch (IOException)
                {
                    // Atomic move in progress; retry.
                }
            }

            await Task.Delay(200);
        }

        if (handshake.ValueKind != JsonValueKind.Object)
        {
            throw new TimeoutException($"Loopback handshake not published within 120 s ({handshakePath}).");
        }

        var port = handshake.GetProperty("port").GetInt32();
        SessionToken = handshake.GetProperty("sessionToken").GetString()!;
        BaseAddress = $"http://127.0.0.1:{port}";
        await SaveCaptureAsync("loopback-handshake.json", handshake);

        _http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("X-CrawlForge-Session", SessionToken);

        // Health probe (token-exempt) until ready.
        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync("/api/health");
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (document.RootElement.GetProperty("data").GetProperty("ready").GetBoolean())
                    {
                        break;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Listener not up yet.
            }

            await Task.Delay(200);
        }

        // MCP endpoint descriptor ({endpoint,token}).
        var mcpPath = Path.Combine(DataDirectory, "mcp-endpoint.json");
        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        JsonElement descriptor = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(mcpPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(mcpPath));
                    descriptor = document.RootElement.Clone();
                    break;
                }
                catch (JsonException)
                {
                    // Partial write; retry.
                }
                catch (IOException)
                {
                    // Atomic move in progress; retry.
                }
            }

            await Task.Delay(200);
        }

        if (descriptor.ValueKind != JsonValueKind.Object)
        {
            throw new TimeoutException($"MCP endpoint descriptor not published within 60 s ({mcpPath}).");
        }

        await SaveCaptureAsync("mcp-endpoint.json", descriptor);
        _mcp = new McpClient(descriptor.GetProperty("endpoint").GetString()!, descriptor.GetProperty("token").GetString()!);
        var initialize = await _mcp.InitializeAsync();
        if (initialize.IsError)
        {
            throw new InvalidOperationException("MCP initialize failed: " + initialize.ResponseJson);
        }

        await SaveCaptureAsync("mcp-initialize.json", initialize.Root);
    }

    private async Task ConnectHubAsync()
    {
        _hub = new SignalRJsonClient();
        await _hub.ConnectAsync(BaseAddress, SessionToken);
    }

    private async Task SeedScenarioAsync()
    {
        var project = await Rest.PostDataAsync(Http, "/api/projects", new
        {
            name = "Conformance harness fixture",
            description = "WP-08-03 contract-conformance harness scratch project (isolated data directory).",
            tags = new[] { "conformance", "wp-08-03" }
        });
        ProjectId = long.Parse(project.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var seeds = new[]
        {
            FixtureSite.BaseUrl + "/",
            FixtureSite.BaseUrl + "/pages/noindex.html",
            FixtureSite.BaseUrl + "/redirect/chain-1"
        };
        var configuration = await Rest.PostDataAsync(Http, $"/api/projects/{ProjectId}/configurations", new
        {
            name = "fixture-http-mode",
            startUrls = seeds,
            limits = new { maxUrls = 220, maxDepth = 6, maxDurationSeconds = 180 },
            rendering = new { enabled = false, infiniteScroll = false, captureConsole = false }
        });
        ConfigurationId = long.Parse(configuration.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var bounded = await Rest.PostDataAsync(Http, $"/api/projects/{ProjectId}/configurations", new
        {
            name = "bounded",
            startUrls = new[] { FixtureSite.BaseUrl + "/" },
            limits = new { maxUrls = 3, maxDepth = 1, maxDurationSeconds = 60 },
            rendering = new { enabled = false, infiniteScroll = false, captureConsole = false }
        });
        BoundedConfigurationId = long.Parse(bounded.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RunMainAuditAsync()
    {
        var start = await Rest.PostDataAsync(Http, "/api/audits", new
        {
            configurationId = ConfigurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limitOverrides = new { maxUrls = 220, maxDepth = 6, maxDurationSeconds = 180 }
        });
        AuditId = long.Parse(start.GetProperty("auditId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        await _hub!.InvokeChannelAsync("subscribe", new[] { AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var terminal = await WaitForTerminalAsync(AuditId, TimeSpan.FromSeconds(300));
        await SaveCaptureAsync("main-audit-detail.json", terminal);

        FixtureRequestsAfterAudit = await _fixture!.RequestsAsync();
        await SaveCaptureAsync("fixture-requests-after-audit.json", FixtureRequestsAfterAudit);
    }

    private async Task CaptureTrapsAsync()
    {
        var urls = await Rest.GetDataAsync(Http, $"/api/audits/{AuditId}/urls?limit=500");
        await SaveCaptureAsync("main-audit-urls.json", urls);

        JsonElement noindexRow = default;
        JsonElement chainRow = default;
        foreach (var row in urls.EnumerateArray())
        {
            var canonical = row.GetProperty("canonicalUrl").GetString() ?? "";
            if (canonical.EndsWith("/pages/noindex.html", StringComparison.Ordinal))
            {
                noindexRow = row;
            }

            if (canonical.EndsWith("/redirect/chain-1", StringComparison.Ordinal))
            {
                chainRow = row;
            }
        }

        if (noindexRow.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The noindex trap URL was not crawled; cannot capture its payloads.");
        }

        if (chainRow.ValueKind != JsonValueKind.Object)
        {
            // The crawler may canonicalize the chain entry to its final target;
            // fall back to the row whose redirect target is the chain landing page.
            foreach (var row in urls.EnumerateArray())
            {
                var canonical = row.GetProperty("canonicalUrl").GetString() ?? "";
                if (canonical.EndsWith("/pages/redirect-target.html", StringComparison.Ordinal))
                {
                    chainRow = row;
                    break;
                }
            }
        }

        if (chainRow.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The redirect-chain trap URL was not crawled; cannot capture its payloads.");
        }

        NoindexUrlId = noindexRow.GetProperty("id").GetString()!;
        RedirectChainUrlId = chainRow.GetProperty("id").GetString()!;

        await CaptureTrapAsync("noindex", NoindexUrlId, noindexRow);
        await CaptureTrapAsync("redirect-chain", RedirectChainUrlId, chainRow);
    }

    private async Task CaptureTrapAsync(string trap, string urlId, JsonElement urlRow)
    {
        var detail = await Rest.GetDataAsync(Http, $"/api/audits/{AuditId}/urls/{urlId}");
        var findings = await Rest.GetDataAsync(Http, $"/api/audits/{AuditId}/findings?urlId={urlId}&limit=500");
        var summary = await Rest.GetDataAsync(Http, $"/api/audits/{AuditId}/summary");

        _trapCaptures[$"trap-{trap}-url-row.json"] = urlRow;
        _trapCaptures[$"trap-{trap}-url-detail.json"] = detail;
        _trapCaptures[$"trap-{trap}-findings.json"] = findings;
        _trapCaptures[$"trap-{trap}-summary.json"] = summary;

        await SaveCaptureAsync($"trap-{trap}-url-row.json", urlRow);
        await SaveCaptureAsync($"trap-{trap}-url-detail.json", detail);
        await SaveCaptureAsync($"trap-{trap}-findings.json", findings);
        await SaveCaptureAsync($"trap-{trap}-summary.json", summary);
    }

    private async Task SeedOperationsAsync()
    {
        // Schedule (bounded configuration so run-now stays small). The wire
        // "cron" field carries a RecurrenceRule expression (interval:/calendar:,
        // FS-CRAWL-001 §6.1); a daily interval keeps the next due far future so
        // only manual run-now fires during the suite.
        var schedule = await Rest.PostDataAsync(Http, $"/api/projects/{ProjectId}/schedules", new
        {
            configurationId = BoundedConfigurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            name = "Conformance harness schedule",
            enabled = true,
            cron = "interval:1.00:00:00",
            completionActions = Array.Empty<object>()
        });
        ScheduleId = long.Parse(schedule.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        // Local-folder destination inside the scratch directory.
        var deliveryDir = Path.Combine(DataDirectory, "export-delivery");
        Directory.CreateDirectory(deliveryDir);
        var destination = await Rest.PostDataAsync(Http, "/api/destinations", new
        {
            name = "Conformance harness local folder",
            kind = "local-folder",
            target = deliveryDir
        });
        DestinationId = long.Parse(destination.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        // Export job delivered to the local folder (export-delivery hub event).
        var export = await Rest.PostDataAsync(Http, "/api/exports", new
        {
            auditId = AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            format = "csv",
            datasets = new[] { "summary" },
            destinationId = DestinationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        ExportId = long.Parse(export.GetProperty("exportId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        // The engine queues generation on a background task
        // (ExportEngine.CreateExportAsync); inline delivery only fires for
        // synchronously-completed jobs (ExportsController.cs:80). Poll until
        // completed, then deliver through the explicit destination route.
        var exportDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        var exportState = "queued";
        while (DateTimeOffset.UtcNow < exportDeadline)
        {
            var job = await Rest.GetDataAsync(Http, $"/api/exports/{ExportId}");
            exportState = job.GetProperty("state").GetString()!;
            if (exportState is "completed" or "failed")
            {
                break;
            }

            await Task.Delay(250);
        }

        if (exportState != "completed")
        {
            throw new TimeoutException($"Export {ExportId} did not complete within 90 s (state: {exportState}).");
        }

        await Rest.PostDataAsync(Http, $"/api/destinations/{DestinationId}/deliver", new
        {
            exportId = ExportId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var delivery = await _hub!.WaitForEventsAsync(
            "export-delivery",
            // Locked wire shape (events.ts ExportDeliveryPayload): exportId is a
            // string EntityId (WP-09-10 G-F producer fix). Tolerate the legacy
            // exportJobId name and numeric tokens during the transition — the
            // comparison is id-text based, never GetInt64 on a string token.
            payload => (payload.TryGetProperty("exportId", out var id) || payload.TryGetProperty("exportJobId", out id))
                && id.ToString() == ExportId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && payload.TryGetProperty("state", out var state) && state.GetString() == "delivered",
            TimeSpan.FromSeconds(90));
        if (delivery.Count == 0)
        {
            throw new TimeoutException($"No delivered export-delivery hub event for export {ExportId} within 90 s.");
        }

        // Log import (combined-format access log referencing fixture URLs).
        var logPath = Path.Combine(DataDirectory, "fixture-access.log");
        var logLines = new StringBuilder();
        foreach (var path in new[] { "/", "/pages/noindex.html", "/pages/redirect-target.html", "/redirect/chain-1", "/pages/orphan.html" })
        {
            logLines.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"127.0.0.1 - - [18/Nov/2026:00:00:00 +0200] \"GET {path} HTTP/1.1\" 200 512 \"-\" \"Mozilla/5.0 (compatible; conformance-harness)\"");
        }

        await File.WriteAllTextAsync(logPath, logLines.ToString());
        var import = await Rest.PostDataAsync(Http, $"/api/projects/{ProjectId}/logs/import", new
        {
            filePath = logPath,
            format = "combined"
        });
        LogImportId = long.Parse(import.GetProperty("importId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var importCompleted = await _hub.WaitForEventsAsync(
            "log-import-completed",
            // G-G: the hub boundary normalizes ids to string EntityId (events.ts);
            // compare id text, tolerating numeric tokens from older producers.
            payload => payload.TryGetProperty("importId", out var id)
                && id.ToString() == LogImportId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(90));
        if (importCompleted.Count == 0)
        {
            throw new TimeoutException($"No log-import-completed hub event for import {LogImportId} within 90 s.");
        }

        // Saved query.
        var query = await Rest.PostDataAsync(Http, "/api/queries", new
        {
            name = "Conformance harness query",
            ruleTree = new
            {
                kind = "group",
                @operator = "and",
                children = new[] { new { kind = "condition", field = "severity", @operator = "eq", value = "error" } }
            }
        });
        QueryId = long.Parse(query.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task StartOfflineSourceAuditAsync()
    {
        // MCP start keeps the engine DTO snapshot (no controller overwrite),
        // so this audit is a valid re-audit source despite G-REAUDIT-SNAPSHOT.
        var exchange = await Mcp.CallToolAsync("start-audit", new
        {
            projectId = ProjectId,
            configurationId = BoundedConfigurationId,
            limitOverrides = new { maxUrls = 6, maxDepth = 2, maxDurationSeconds = 60 }
        });
        Assert.False(exchange.IsError, "MCP start-audit for the offline source failed.");
        var text = exchange.Result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);
        OfflineSourceAuditId = document.RootElement.GetProperty("auditId").GetInt64();
        await WaitForTerminalAsync(OfflineSourceAuditId, TimeSpan.FromSeconds(120));
    }

    private async Task RunOfflineReauditAsync()
    {
        // G-REAUDIT-SNAPSHOT evidence: re-audit of the REST-started main audit
        // deterministically fails (wire-shaped snapshot vs DTO read). Capture
        // the raw response as evidence; NO test asserts the broken behavior
        // (lead steer: defect-locks would fail once the product is fixed).
        var broken = await Rest.SendAsync(Http, "POST", $"/api/audits/{AuditId}/reaudit", new { refresh = false });
        await SaveCaptureAsync("gap-reaudit-rest-start-response.json", JsonSerializer.SerializeToElement(new
        {
            gap = "G-REAUDIT-SNAPSHOT",
            request = $"POST /api/audits/{AuditId}/reaudit {{refresh:false}}",
            status = broken.Status,
            contentType = broken.ContentType,
            body = broken.Body,
            expectedSpecBehavior = "201 envelope with {auditId, sourceAuditId, state} (ReauditResponse)",
            owners = "WP-09-06 (Api/Controllers overwrite side) + WP-09-11 (Host/Execution PayloadJson read side)"
        }));

        // Storage-to-engine proof: STOP the fixture so any network fetch would
        // fail, then re-audit the MCP-started source offline (refresh:false)
        // over stored artifacts (FS-CRAWL-001 §7.2 stored-artifacts rule).
        await _fixture!.DisposeAsync();
        _fixture = null;

        var reaudit = await Rest.PostDataAsync(Http, $"/api/audits/{OfflineSourceAuditId}/reaudit", new { refresh = false });
        ReauditId = long.Parse(reaudit.GetProperty("auditId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var terminal = await WaitForTerminalAsync(ReauditId, TimeSpan.FromSeconds(240));
        await SaveCaptureAsync("offline-reaudit-detail.json", terminal);

        _fixture = await FixtureSite.StartAsync();
    }

    private async Task RunMatrixAuditAsync()
    {
        var rows = new[]
        {
            new
            {
                sourceUrl = FixtureSite.BaseUrl + "/redirect/chain-1",
                targetUrl = FixtureSite.BaseUrl + "/pages/redirect-target.html"
            }
        };

        // G-MATRIX-SEED-RACE evidence: autoSeedAudit:true starts a full crawl
        // (source configuration) CONCURRENTLY with URL ingestion
        // (RedirectMatrixController.cs:94-95). The matrix source URL is also a
        // crawl seed/discovered URL, so both inserters hit
        // UNIQUE(audit_id, canonical_url): either the seeded audit fails in
        // CrawlPhaseAsync or the upload itself 500s in IngestAsync, depending
        // on which inserter wins. Capture whichever outcome occurs; NO test
        // locks the failure (lead ownership steer).
        var seededExchange = await Rest.SendAsync(Http, "POST", $"/api/audits/{AuditId}/redirect-matrix", new { rows, autoSeedAudit = true });
        long? seededAuditId = null;
        if (seededExchange.Status == 200
            && seededExchange.Root.TryGetProperty("data", out var seededData)
            && seededData.TryGetProperty("auditId", out var seededIdToken)
            && seededIdToken.ValueKind == JsonValueKind.String)
        {
            seededAuditId = long.Parse(seededIdToken.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        object? seededDetail = null;
        var seededState = seededAuditId is null ? "upload-failed" : "queued";
        if (seededAuditId is long sid)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                var detail = await Rest.GetDataAsync(Http, $"/api/audits/{sid}");
                seededState = detail.GetProperty("state").GetString()!;
                seededDetail = detail;
                if (seededState is "completed" or "failed" or "cancelled")
                {
                    break;
                }

                await Task.Delay(500);
            }

            // Hygiene: stop the seeded audit if it is somehow still active.
            if (seededState is "queued" or "running" or "paused")
            {
                await Rest.SendAsync(Http, "POST", $"/api/audits/{sid}/cancel", new { });
            }
        }

        await SaveCaptureAsync("gap-matrix-seed-race.json", JsonSerializer.SerializeToElement(new
        {
            gap = "G-MATRIX-SEED-RACE",
            request = $"POST /api/audits/{AuditId}/redirect-matrix {{rows:[chain-1→redirect-target], autoSeedAudit:true}}",
            uploadStatus = seededExchange.Status,
            uploadBody = seededExchange.Body,
            seededAuditId,
            terminalState = seededState,
            detail = seededDetail,
            mechanism = "StartAuditAsync crawl + IngestAsync insert the same canonical URL concurrently; UNIQUE(audit_id, canonical_url) violation (CrawlPhaseAsync or IngestAsync depending on race order)",
            producer = "RedirectMatrixController.cs:94-95 → AuditExecutionEngine.CrawlPhaseAsync / UrlListIngestor → SqlRepository.CreateAsync"
        }));

        // Hygiene: cancel any still-active audit (the seeded audit may survive
        // a 500ing upload with its id unknown to the harness).
        var auditList = await Rest.GetDataAsync(Http, "/api/audits");
        foreach (var item in auditList.EnumerateArray())
        {
            var state = item.GetProperty("state").GetString();
            if (state is "queued" or "running" or "paused")
            {
                var id = item.GetProperty("id").GetString();
                await Rest.SendAsync(Http, "POST", $"/api/audits/{id}/cancel", new { });
            }
        }

        // Conformance path: re-upload WITHOUT auto-seed (the controller replaces
        // the mappings) and drive validation through the results endpoint — the
        // real consumer of RedirectMatrixValidator, which emits the throttled
        // matrix-row-validated batches (§10.8) over the stored redirect chains.
        await Rest.PostDataAsync(Http, $"/api/audits/{AuditId}/redirect-matrix", new { rows, autoSeedAudit = false });
        MatrixAuditId = AuditId;
        var results = await Rest.GetDataAsync(Http, $"/api/audits/{AuditId}/redirect-matrix/results");
        await SaveCaptureAsync("matrix-results.json", results);

        var validated = await _hub!.WaitForEventsAsync(
            "matrix-row-validated",
            payload => payload.TryGetProperty("auditId", out var id)
                && id.ToString() == AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(15));
        if (validated.Count == 0)
        {
            throw new InvalidOperationException("No matrix-row-validated hub events after the results-endpoint validation.");
        }
    }
}

/// <summary>Collection definition: one live backend shared by all live suites.</summary>
[CollectionDefinition(Name)]
public sealed class LiveBackendCollection : ICollectionFixture<LiveBackendFixture>
{
    /// <summary>Collection name used by [Collection] on the live test classes.</summary>
    public const string Name = "live-backend";
}
