using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Intelligence.LogAnalyzer;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>Audit-start double: creates real audit rows; records calls; configurable failure.</summary>
internal sealed class FakeAuditExecutionService : IAuditExecutionService, CrawlForge.Api.Controllers.IReAuditExecutionPort, CrawlForge.Api.Controllers.IListAuditStartPort
{
    private readonly IWriteRepository<AuditDto> _audits;
    private readonly IReadRepository<AuditDto> _auditReads;

    public FakeAuditExecutionService(IWriteRepository<AuditDto> audits, IReadRepository<AuditDto> auditReads)
    {
        _audits = audits;
        _auditReads = auditReads;
    }

    public ConcurrentQueue<(long ProjectId, long ConfigurationId, ScheduleTriggerType Trigger, CrawlLimitOverrides? Overrides)> Starts { get; } = new();

    public ConcurrentQueue<(long AuditId, bool Refresh)> ReAudits { get; } = new();

    /// <summary>WP-09-06 G10: list-mode starts through the persist-before-start port.</summary>
    public ConcurrentQueue<(long ProjectId, long ConfigurationId, string? UrlListJson, bool IsListMode, CrawlLimitOverrides? Overrides)> ListStarts { get; } = new();

    /// <summary>
    /// Simulated engine first observation (ListSeedResolver.ResolveForAuditAsync
    /// reads the persisted row): captured INSIDE the start call, before the
    /// controller's post-start snapshot update — the G10 ordering proof.
    /// </summary>
    public ConcurrentQueue<(long AuditId, string? ObservedUrlListJson, bool? ObservedIsListMode)> EngineObservations { get; } = new();

    /// <summary>When true, the list start fails AFTER the row was created (audit-failed path).</summary>
    public bool FailListStartAfterCreate { get; set; }

    public Exception? StartException { get; set; }

    public async Task<long> StartAuditAsync(long projectId, long configurationId, ScheduleTriggerType trigger, CrawlLimitOverrides? limitOverrides, CancellationToken cancellationToken = default)
    {
        Starts.Enqueue((projectId, configurationId, trigger, limitOverrides));
        if (StartException is not null)
        {
            throw StartException;
        }

        var audit = await _audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Queued, AuditPhase.NotStarted, null, null, "{}", "test-engine", false), cancellationToken);
        return audit.Id;
    }

    /// <inheritdoc />
    public async Task<long> StartListAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType trigger,
        CrawlLimitOverrides? limitOverrides,
        string? urlListJson,
        bool isListMode,
        CancellationToken cancellationToken = default)
    {
        ListStarts.Enqueue((projectId, configurationId, urlListJson, isListMode, limitOverrides));

        // G10 port contract: the list payload is persisted ATOMICALLY with the
        // row creation — before any engine observation of the row.
        var audit = await _audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Queued, AuditPhase.NotStarted, null, null, "{}", "test-engine", false)
        {
            IsListMode = isListMode,
            UrlListJson = urlListJson,
        }, cancellationToken);

        var observed = await _auditReads.GetByIdAsync(audit.Id, cancellationToken);
        EngineObservations.Enqueue((audit.Id, observed!.UrlListJson, observed.IsListMode));

        if (FailListStartAfterCreate)
        {
            throw new CrawlForge.Api.Controllers.AuditStartFailedException(audit.Id, "Simulated start pipeline failure after row creation.");
        }

        return audit.Id;
    }

    public async Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken cancellationToken = default)
    {
        ReAudits.Enqueue((auditId, refresh));
        var source = await _auditReads.GetByIdAsync(auditId, cancellationToken)
            ?? throw new InvalidOperationException($"Audit {auditId} does not exist.");
        if (refresh)
        {
            return await StartAuditAsync(source.ProjectId, source.ConfigurationId, ScheduleTriggerType.Manual, null, cancellationToken);
        }

        // Offline analysis-only semantics: the new run starts in the analyzing
        // phase over stored data — no crawl is ever started.
        var audit = await _audits.CreateAsync(new AuditDto(
            0, source.ProjectId, source.ConfigurationId, source.ConfigurationSnapshotJson,
            AuditStatus.Queued, AuditPhase.Analyzing, null, null, "{}", "test-engine", false), cancellationToken);
        return audit.Id;
    }
}

/// <summary>
/// WP-09-06 D2: engine lifecycle port double over the real SQLite stack.
/// Mirrors the engine contract: pause persists Paused and checkpoints the run
/// (FrontierSnapshot + CountersJson + AimdStateJson); resume releases the gate
/// for an in-memory run, or REHYDRATES from the latest checkpoint when the run
/// is not in memory (crash recovery — SimulateProcessRestart clears the run
/// set exactly like a process death); cancel persists Cancelled + FinishedAt.
/// Invalid transitions throw InvalidOperationException (REST maps to 409).
/// </summary>
internal sealed class FakeAuditLifecyclePort : CrawlForge.Api.Controllers.IAuditLifecycleExecutionPort
{
    private readonly IReadRepository<AuditDto> _auditReads;
    private readonly IWriteRepository<AuditDto> _auditWrites;
    private readonly IReadRepository<CrawlCheckpointDto> _checkpointReads;
    private readonly IWriteRepository<CrawlCheckpointDto> _checkpointWrites;
    private readonly HashSet<long> _runsInMemory = [];

    public FakeAuditLifecyclePort(
        IReadRepository<AuditDto> auditReads,
        IWriteRepository<AuditDto> auditWrites,
        IReadRepository<CrawlCheckpointDto> checkpointReads,
        IWriteRepository<CrawlCheckpointDto> checkpointWrites)
    {
        _auditReads = auditReads;
        _auditWrites = auditWrites;
        _checkpointReads = checkpointReads;
        _checkpointWrites = checkpointWrites;
    }

    public ConcurrentQueue<(long AuditId, string Action)> Calls { get; } = new();

    /// <summary>Checkpoint rehydrations performed by resume-after-restart.</summary>
    public ConcurrentQueue<(long AuditId, long? CheckpointId, string? Frontier, string? Counters, string? Aimd)> Rehydrations { get; } = new();

    /// <summary>Marks a run as live in memory (the engine loop exists).</summary>
    public void TrackRun(long auditId) => _runsInMemory.Add(auditId);

    /// <summary>Simulates a process crash/restart: no run survives in memory.</summary>
    public void SimulateProcessRestart() => _runsInMemory.Clear();

    public async Task<CrawlForge.Api.Controllers.AuditLifecycleTransition> PauseAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAsync(auditId, cancellationToken);
        if (audit.Status != AuditStatus.Running)
        {
            throw new InvalidOperationException($"Audit {auditId} is not running (status {audit.Status}).");
        }

        Calls.Enqueue((auditId, "pause"));
        var updated = await _auditWrites.UpdateAsync(audit with { Status = AuditStatus.Paused }, cancellationToken);
        if (_runsInMemory.Contains(auditId))
        {
            // The engine checkpoints on pause observation: frontier + counters + AIMD.
            await _checkpointWrites.CreateAsync(new CrawlCheckpointDto(
                0, auditId, "{\"entries\":[]}", null, audit.CountersJson, DateTimeOffset.UtcNow)
            {
                AimdStateJson = "{\"hosts\":[]}",
            }, cancellationToken);
        }

        return new CrawlForge.Api.Controllers.AuditLifecycleTransition(updated.Id, updated.Status, updated.Phase);
    }

    public async Task<CrawlForge.Api.Controllers.AuditLifecycleTransition> ResumeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAsync(auditId, cancellationToken);
        if (audit.Status != AuditStatus.Paused)
        {
            throw new InvalidOperationException($"Audit {auditId} is not paused (status {audit.Status}).");
        }

        Calls.Enqueue((auditId, "resume"));
        var updated = await _auditWrites.UpdateAsync(audit with { Status = AuditStatus.Running }, cancellationToken);
        if (!_runsInMemory.Contains(auditId))
        {
            // Crash recovery: rehydrate from the LATEST checkpoint (§2.4).
            var page = await _checkpointReads.ListAsync(new ListQuery(
                0, 1,
                new Dictionary<string, string> { ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                "SavedAt", true), cancellationToken);
            var checkpoint = page.Items.Count > 0 ? page.Items[0] : null;
            Rehydrations.Enqueue((auditId, checkpoint?.Id, checkpoint?.SerializedFrontier, checkpoint?.CountersJson, checkpoint?.AimdStateJson));
            _runsInMemory.Add(auditId);
        }

        return new CrawlForge.Api.Controllers.AuditLifecycleTransition(updated.Id, updated.Status, updated.Phase);
    }

    public async Task<CrawlForge.Api.Controllers.AuditLifecycleTransition> CancelAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAsync(auditId, cancellationToken);
        if (audit.Status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed)
        {
            throw new InvalidOperationException($"Audit {auditId} already reached a terminal state.");
        }

        Calls.Enqueue((auditId, "cancel"));
        var updated = await _auditWrites.UpdateAsync(
            audit with { Status = AuditStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, cancellationToken);
        _runsInMemory.Remove(auditId);
        return new CrawlForge.Api.Controllers.AuditLifecycleTransition(updated.Id, updated.Status, updated.Phase);
    }

    private async Task<AuditDto> RequireAsync(long auditId, CancellationToken cancellationToken) =>
        await _auditReads.GetByIdAsync(auditId, cancellationToken)
            ?? throw new InvalidOperationException($"Audit {auditId} does not exist.");
}

/// <summary>Scheduler double for run-now delegation tests.</summary>
internal sealed class FakeScheduledAuditService : IScheduledAuditService
{
    public ConcurrentQueue<long> RunNowCalls { get; } = new();

    public long NextAuditId { get; set; } = 1;

    public Exception? RunNowException { get; set; }

    public DateTimeOffset? NextDue { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<long> RunNowAsync(long scheduleDefinitionId, CancellationToken cancellationToken = default)
    {
        RunNowCalls.Enqueue(scheduleDefinitionId);
        if (RunNowException is not null)
        {
            throw RunNowException;
        }

        return Task.FromResult(NextAuditId);
    }

    public Task<DateTimeOffset?> GetNextDueAsync(CancellationToken cancellationToken = default) => Task.FromResult(NextDue);
}

/// <summary>Export-engine double: in-memory jobs backed by real temp files.</summary>
internal sealed class FakeExportEngine : IExportEngine, IDisposable
{
    private readonly ConcurrentDictionary<long, ExportJobDto> _jobs = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "crawlforge-api-export-tests", Guid.NewGuid().ToString("N"));
    private long _nextId;

    public FakeExportEngine() => Directory.CreateDirectory(_directory);

    public ExportJobStatus CreateStatus { get; set; } = ExportJobStatus.Completed;

    public ConcurrentQueue<ExportRequest> Requests { get; } = new();

    public Task<ExportJobDto> CreateExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        var id = Interlocked.Increment(ref _nextId);
        // Mirror the locked ExportEngine file naming (§8.4: csv is a ZIP of
        // dataset CSVs; html is a single document).
        var extension = request.Format switch
        {
            ExportFormat.Excel => ".xlsx",
            ExportFormat.Csv => ".zip",
            ExportFormat.Html => ".html",
            ExportFormat.Pdf => ".pdf",
            _ => ".xml",
        };
        var filePath = Path.Combine(_directory, $"export-{id}{extension}");
        File.WriteAllBytes(filePath, [1, 2, 3, 4]);
        var job = new ExportJobDto(id, request.AuditId, request.Format, CreateStatus, DateTimeOffset.UtcNow, filePath, request.Datasets);
        _jobs[id] = job;
        return Task.FromResult(job);
    }

    public Task<ExportJobDto?> GetExportAsync(long exportJobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryGetValue(exportJobId, out var job) ? job : null);

    public Task<Stream> OpenExportFileAsync(long exportJobId, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(exportJobId, out var job) || job.FilePath is null)
        {
            throw new FileNotFoundException("Export file absent.", exportJobId.ToString());
        }

        return Task.FromResult<Stream>(File.OpenRead(job.FilePath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}

/// <summary>Destination-delivery double.</summary>
internal sealed class FakeDestinationDelivery : IExportDestinationDelivery
{
    public DestinationTestResult TestResult { get; set; } = new(true, true, null);

    public ConcurrentQueue<(long ExportJobId, long DestinationId)> Deliveries { get; } = new();

    public Exception? DeliverException { get; set; }

    public Task<DestinationTestResult> TestAsync(long destinationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TestResult);

    public Task DeliverAsync(long exportJobId, long destinationId, CancellationToken cancellationToken = default)
    {
        if (DeliverException is not null)
        {
            throw DeliverException;
        }

        Deliveries.Enqueue((exportJobId, destinationId));
        return Task.CompletedTask;
    }
}

/// <summary>Diagnostic-artifact double.</summary>
internal sealed class FakeCrashDumpHandler : ICrashDumpHandler
{
    public List<DiagnosticArtifactDto> Artifacts { get; } = [];

    public Task<DiagnosticArtifactDto?> CaptureMinidumpAsync(CancellationToken cancellationToken = default)
    {
        var artifact = new DiagnosticArtifactDto(Artifacts.Count + 1, DiagnosticArtifactKind.Minidump, DateTimeOffset.UtcNow, 1024, @"C:\dumps\test.dmp");
        Artifacts.Add(artifact);
        return Task.FromResult<DiagnosticArtifactDto?>(artifact);
    }

    public Task<IReadOnlyList<DiagnosticArtifactDto>> ListArtifactsAsync(DiagnosticArtifactKind? kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DiagnosticArtifactDto>>(Artifacts.Where(a => kind is null || a.Kind == kind).ToList());

    public Task<int> PurgeArtifactsAsync(DiagnosticArtifactKind? kind, CancellationToken cancellationToken = default)
    {
        var removed = Artifacts.RemoveAll(a => kind is null || a.Kind == kind);
        return Task.FromResult(removed);
    }
}

/// <summary>Metrics-exposition double.</summary>
internal sealed class FakeMetricsExposition : IMetricsExposition
{
    public string Text { get; set; } = "# local technical metrics\ncrawlforge_uptime_seconds 1\n";

    public ConcurrentQueue<(string Family, double Value)> Observations { get; } = new();

    public string Render() => Text;

    public void Record(string metricFamily, double value, IReadOnlyDictionary<string, string>? labels = null) =>
        Observations.Enqueue((metricFamily, value));
}

/// <summary>Single-page analyzer double.</summary>
internal sealed class FakeSinglePageAnalyzer : ISinglePageAnalyzer
{
    public SinglePageAnalysisResult Result { get; set; } = new([], [], null, null);

    public ConcurrentQueue<SinglePageAnalysisRequest> Requests { get; } = new();

    public Task<SinglePageAnalysisResult> AnalyzeAsync(SinglePageAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Result);
    }
}

/// <summary>Rendering-pool double.</summary>
internal sealed class FakeRenderingPool : IRenderingPool
{
    public RenderedPage? Page { get; set; }

    public Exception? RenderException { get; set; }

    public ConcurrentQueue<RenderRequest> Requests { get; } = new();

    public Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        if (RenderException is not null)
        {
            throw RenderException;
        }

        return Task.FromResult(Page ?? new RenderedPage(request.Url, "<html><body>rendered</body></html>", ReadOnlyMemory<byte>.Empty, [], null));
    }
}

/// <summary>Pre-crawl URL validator double.</summary>
internal sealed class FakePreCrawlValidator : IPreCrawlValidator
{
    public PreCrawlValidationResult Result { get; set; } = new(true, [], null, 200);

    public ConcurrentQueue<string> Urls { get; } = new();

    public Task<PreCrawlValidationResult> ValidateAsync(string startUrl, CancellationToken cancellationToken = default)
    {
        Urls.Enqueue(startUrl);
        return Task.FromResult(Result);
    }
}

/// <summary>HTTP fetcher double: returns a configurable response; records requests.</summary>
internal sealed class FakeHttpFetcher : IHttpFetcher
{
    public HttpFetchResult Result { get; set; } = new(
        "https://example.com/", 200, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero);

    public Exception? FetchException { get; set; }

    public ConcurrentQueue<HttpFetchRequest> Requests { get; } = new();

    public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        if (FetchException is not null)
        {
            throw FetchException;
        }

        return Task.FromResult(Result);
    }
}

/// <summary>Redirect-matrix validator double.</summary>
internal sealed class FakeRedirectMatrixValidator : IRedirectMatrixValidator
{
    public Func<RedirectMappingDto, RedirectMatrixRowResult>? RowResult { get; set; }

    public Task<IReadOnlyList<RedirectMatrixRowResult>> ValidateAsync(long auditId, IReadOnlyList<RedirectMappingDto> mappings, CancellationToken cancellationToken = default)
    {
        var results = mappings
            .Select(m => RowResult?.Invoke(m) ?? new RedirectMatrixRowResult(m, RedirectMatrixVerdict.Pass, m.ExpectedTargetUrl, 1, [], m.ExpectedTargetUrl))
            .ToList();
        return Task.FromResult<IReadOnlyList<RedirectMatrixRowResult>>(results);
    }
}

/// <summary>Semantic analyzer double.</summary>
internal sealed class FakeSemanticLinkAnalyzer : ISemanticLinkAnalyzer
{
    public SemanticAnalysisResult Result { get; set; } = new([], []);

    public ConcurrentQueue<(long AuditId, SemanticAnalysisOptions Options)> Runs { get; } = new();

    public Task<SemanticAnalysisResult> RunAsync(long auditId, SemanticAnalysisOptions options, CancellationToken cancellationToken = default)
    {
        Runs.Enqueue((auditId, options));
        return Task.FromResult(Result);
    }
}

/// <summary>Log-import service double backed by the real import repositories.</summary>
internal sealed class FakeLogImportService : ILogImportService
{
    private readonly IReadRepository<LogImportDto> _reads;
    private readonly IWriteRepository<LogImportDto> _writes;

    public FakeLogImportService(IReadRepository<LogImportDto> reads, IWriteRepository<LogImportDto> writes)
    {
        _reads = reads;
        _writes = writes;
    }

    public LogImportOutcome? NextOutcome { get; set; }

    public ConcurrentQueue<LogImportRequest> Requests { get; } = new();

    public ConcurrentQueue<long> Purged { get; } = new();

    public async Task<LogImportOutcome> ImportAsync(LogImportRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        if (NextOutcome is not null)
        {
            return NextOutcome;
        }

        var counters = new LogImportCountersPayload(10, 9, 1, 0, [], null, null, 1000, 3, 5, [], "test", "1");
        var import = await _writes.CreateAsync(new LogImportDto(
            0, request.ProjectId, request.Format ?? LogFileFormat.ApacheNginxCombined,
            request.WindowStart ?? DateTimeOffset.UtcNow.AddDays(-1), request.WindowEnd ?? DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(counters), request.VerificationMode, LogImportStatus.Completed, DateTimeOffset.UtcNow, request.SourceFilePath), cancellationToken);
        return new LogImportOutcome(import.Id, LogImportStatus.Completed, counters, null);
    }

    public async Task<IReadOnlyList<LogImportRecord>> ListImportsAsync(long projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        var page = await _reads.ListAsync(new ListQuery(
            Math.Max(0, offset), Math.Clamp(limit, 1, 10_000),
            new Dictionary<string, string> { ["projectId"] = projectId.ToString() }, "ImportedAt", true), cancellationToken);
        return page.Items.Select(i => new LogImportRecord(i, DeserializeCounters(i.CountersJson))).ToList();
    }

    public async Task PurgeImportAsync(long importId, CancellationToken cancellationToken = default)
    {
        Purged.Enqueue(importId);
        await _writes.DeleteAsync(importId, cancellationToken);
    }

    private static LogImportCountersPayload DeserializeCounters(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LogImportCountersPayload>(json)
                ?? new LogImportCountersPayload(0, 0, 0, 0, [], null, null, 0, 0, 0, [], null, null);
        }
        catch (JsonException)
        {
            return new LogImportCountersPayload(0, 0, 0, 0, [], null, null, 0, 0, 0, [], null, null);
        }
    }
}

/// <summary>Log-correlation read-model double.</summary>
internal sealed class FakeLogCorrelationReadModel : ILogCorrelationReadModel
{
    public LogCorrelationPage Page { get; set; } = new(LogCorrelationView.Summary, [], 0, []);

    public ConcurrentQueue<(long AuditId, long ImportId, LogCorrelationView View)> Calls { get; } = new();

    public Task<LogCorrelationPage> GetCorrelationPageAsync(long auditId, long logImportId, LogCorrelationView view, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((auditId, logImportId, view));
        return Task.FromResult(Page);
    }
}

/// <summary>DNS-verification tool double: refuses without consent.</summary>
internal sealed class FakeLogDnsVerificationTool : ILogDnsVerificationTool
{
    public IReadOnlyList<DnsVerificationResult> Results { get; set; } = [];

    public ConcurrentQueue<(long ImportId, bool Consent, int SampleSize)> Calls { get; } = new();

    public Task<IReadOnlyList<DnsVerificationResult>> VerifySampleAsync(long importId, bool consent, int sampleSize = 20, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((importId, consent, sampleSize));
        if (!consent)
        {
            throw new InvalidOperationException("DNS verification requires explicit per-import consent.");
        }

        return Task.FromResult(Results);
    }
}

/// <summary>GEO readiness analyzer double.</summary>
internal sealed class FakeGeoReadinessAnalyzer : IGeoReadinessAnalyzer
{
    public GeoReadinessResult Result { get; set; } = new(0, [], []);

    public LlmsTxtValidation Validation { get; set; } = new("example.com", true, true, [], []);

    public Task<GeoReadinessResult> AnalyzeAsync(long auditId, CancellationToken cancellationToken = default) => Task.FromResult(Result);

    public LlmsTxtValidation ValidateLlmsTxt(string body, string? baseUrl) => Validation;
}

/// <summary>Governance-matrix double.</summary>
internal sealed class FakeAiBotGovernanceMatrix : IAiBotGovernanceMatrix
{
    public IReadOnlyList<GovernanceMatrixRowDto> Rows { get; set; } = [];

    public Task<IReadOnlyList<GovernanceMatrixRowDto>> EvaluateAsync(long auditId, CancellationToken cancellationToken = default) => Task.FromResult(Rows);
}

/// <summary>llms.txt generator double.</summary>
internal sealed class FakeLlmsTxtGenerator : ILlmsTxtGenerator
{
    public LlmsGenerationResult Result { get; set; } = new([]);

    public ConcurrentQueue<LlmsGenerationRequest> Requests { get; } = new();

    public Task<LlmsGenerationResult> GenerateAsync(LlmsGenerationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Result);
    }
}

/// <summary>WAF signature catalogue double backed by the real user-signature repositories.</summary>
internal sealed class FakeWafSignatureCatalogue : IWafSignatureCatalogue
{
    private readonly IReadRepository<WafUserSignatureDto> _reads;
    private readonly IWriteRepository<WafUserSignatureDto> _writes;

    public FakeWafSignatureCatalogue(IReadRepository<WafUserSignatureDto> reads, IWriteRepository<WafUserSignatureDto> writes)
    {
        _reads = reads;
        _writes = writes;
    }

    public List<WafSignature> Bundled { get; } = [];

    public async Task<WafCatalogueSnapshot> GetEffectiveCatalogueAsync(CancellationToken cancellationToken = default)
    {
        var users = await _reads.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        var signatures = Bundled.ToList();
        signatures.AddRange(users.Items.Select(u => new WafSignature(
            u.Key, u.Provider, u.ChallengeClass, ParseMarkers(u.HeaderMarkersJson), ParseMarkers(u.BodyMarkersJson), IsBundled: false)));
        return new WafCatalogueSnapshot("bundled-test", "rev-1", signatures);
    }

    public Task<WafUserSignatureDto> UpsertUserSignatureAsync(WafUserSignatureDto signature, CancellationToken cancellationToken = default) =>
        signature.Id == 0 ? _writes.CreateAsync(signature, cancellationToken) : _writes.UpdateAsync(signature, cancellationToken);

    public async Task<bool> DeleteUserSignatureAsync(string key, CancellationToken cancellationToken = default)
    {
        var users = await _reads.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        var match = users.Items.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));
        return match is not null && await _writes.DeleteAsync(match.Id, cancellationToken);
    }

    private static IReadOnlyList<string> ParseMarkers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>System-health double (WP-05-A1 surface).</summary>
internal sealed class FakeSystemHealthService : ISystemHealthService
{
    public SystemHealthSnapshot Snapshot { get; set; } = new(100_000_000, 50_000_000, 1_000_000, DateTimeOffset.UtcNow);

    public SystemHealthSnapshot GetSnapshot() => Snapshot;
}

/// <summary>Compact double (WP-05-A1 surface): configurable guard refusal.</summary>
internal sealed class FakeCompactService : ICompactService
{
    public CompactResult? Result { get; set; }

    public InvalidOperationException? GuardException { get; set; }

    public int Calls { get; private set; }

    public Task<CompactResult> CompactAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (GuardException is not null)
        {
            throw GuardException;
        }

        var before = new SystemHealthSnapshot(100_000_000, 50_000_000, 1_000_000, DateTimeOffset.UtcNow);
        var after = new SystemHealthSnapshot(60_000_000, 30_000_000, 0, DateTimeOffset.UtcNow);
        return Task.FromResult(Result ?? new CompactResult(before, after, DateTimeOffset.UtcNow,
            new[] { "managed-gc-compaction", "wal-checkpoint-truncate", "working-set-trim" }));
    }
}

/// <summary>Secret-protector double: reversible marker prefix.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedText) =>
        protectedText.StartsWith("enc:", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[4..]))
            : throw new InvalidOperationException("Not a protected secret.");
}

/// <summary>Content-store double (in-memory artifacts).</summary>
internal sealed class FakeContentStore : IContentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private long _nextId;

    public async Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var key = $"artifact-{Interlocked.Increment(ref _nextId)}";
        _files[key] = buffer.ToArray();
        return new ContentArtifactDto(_nextId, auditId, urlId, kind, key, buffer.Length, "hash");
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_files.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryRemove(storageKey, out _));
}

/// <summary>Realtime-hub double.</summary>
internal sealed class FakeRealtimeHub : IRealtimeHub
{
    public ConcurrentQueue<HubEvent> Events { get; } = new();

    public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        Events.Enqueue(hubEvent);
        return Task.CompletedTask;
    }
}

/// <summary>AIMD politeness controller double: in-memory per-host state.</summary>
internal sealed class FakeAimdPolitenessController : IAimdPolitenessController
{
    private readonly Dictionary<string, HostThrottleState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<(string Host, double? Rate)> Overrides { get; } = new();

    public void Seed(HostThrottleState state) => _hosts[state.Host] = state;

    public ValueTask WaitAsync(string host, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void RecordResponse(string host, HostResponseSample sample)
    {
    }

    public HostThrottleState GetState(string host) =>
        _hosts.TryGetValue(host, out var state)
            ? state
            : new HostThrottleState(host, 2.0, 1, 0, 0, null, null);

    public void SetManualOverride(string host, double? requestsPerSecond)
    {
        Overrides.Enqueue((host, requestsPerSecond));
        var current = GetState(host);
        _hosts[host] = current with { ManualOverrideRate = requestsPerSecond, CurrentRate = requestsPerSecond ?? current.CurrentRate };
    }

    public AimdCheckpointState CreateCheckpointState() => new(_hosts.Values.ToList());

    public void RestoreCheckpointState(AimdCheckpointState state)
    {
        _hosts.Clear();
        foreach (var host in state.Hosts)
        {
            _hosts[host.Host] = host;
        }
    }
}

/// <summary>List-mode ingestor double: records calls.</summary>
internal sealed class FakeListModeIngestor : IListModeIngestor
{
    public ConcurrentQueue<(long AuditId, IReadOnlyList<string> Urls, bool AllowDiscovery)> Calls { get; } = new();

    public Task<ListIngestionResult> IngestAsync(long auditId, IReadOnlyList<string> urls, bool allowDiscovery, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((auditId, urls, allowDiscovery));
        return Task.FromResult(new ListIngestionResult(urls.Count, 0, 0));
    }
}
