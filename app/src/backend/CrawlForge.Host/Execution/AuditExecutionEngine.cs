using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Waf;
using CrawlForge.Host.Hosting;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.NGrams;
using CrawlForge.Mcp;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Execution;

/// <summary>
/// End-to-end audit execution engine (WP-06-01 composition): binds the accepted
/// crawler, pipeline, scoring, and intelligence components into the locked
/// <see cref="IAuditExecutionService"/> surface and the MCP
/// <see cref="IAuditLifecyclePort"/> adapter. The engine observes persisted
/// AuditDto status transitions (pause/resume/cancel) at every work step, so the
/// API, MCP, and scheduler all drive the same lifecycle (lifecycle ruling).
/// All network I/O targets user-configured crawl destinations exclusively;
/// progress events carry local run facts only (privacy invariant).
/// </summary>
public sealed class AuditExecutionEngine : IAuditExecutionService, IAuditLifecyclePort, IReAuditExecutionPort, IBackendLifecycle
{
    /// <summary>
    /// Bounded streaming page size for dataset enumeration (analysis passes and
    /// re-audit copies). This is a memory-bounding batch size, NOT a crawl
    /// ceiling: FS-CRAWL-001 §4.2 defaults every page ceiling to Unlimited and
    /// §4.3 represents unlimited as the absence of a value — the former
    /// HardUrlSafetyCap clamp was removed (WP-06-01 reopen, criterion #4).
    /// </summary>
    private const int DatasetPageSize = 1000;

    private const int CheckpointEveryFetches = 25;
    private const string UserAgentToken = "CrawlForge";
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions PayloadJson = ApiJsonOptions.Create();

    /// <summary>
    /// Resolves the effective total-page ceiling (FS-CRAWL-001 §4.2/§4.3):
    /// null is the unlimited sentinel — the built-in default — and an explicit
    /// override is honored exactly as given. No clamp, no magic large number.
    /// </summary>
    /// <param name="overrides">Per-audit limit overrides, when present.</param>
    /// <returns>The URL ceiling, or null for unlimited.</returns>
    public static long? ResolveUrlCeiling(CrawlLimitOverrides? overrides) => overrides?.MaxUrls;

    /// <summary>
    /// Evaluates the total-page ceiling stop condition. An unlimited ceiling
    /// (null) never stops the crawl regardless of the fetched count.
    /// </summary>
    /// <param name="ceiling">Resolved URL ceiling; null = unlimited.</param>
    /// <param name="fetched">URLs fetched so far.</param>
    /// <returns>True when the ceiling is reached.</returns>
    public static bool IsUrlCeilingReached(long? ceiling, long fetched) =>
        ceiling is { } limit && fetched >= limit;

    private readonly IDbProvider _provider;
    private readonly IReadRepository<AuditDto> _auditReads;
    private readonly IWriteRepository<AuditDto> _auditWrites;
    private readonly IReadRepository<CrawlConfigurationDto> _configReads;
    private readonly IReadRepository<UrlRecordDto> _urlReads;
    private readonly IWriteRepository<UrlRecordDto> _urlWrites;
    private readonly IReadRepository<ContentArtifactDto> _artifactReads;
    private readonly IWriteRepository<ContentArtifactDto> _artifactWrites;
    private readonly IWriteRepository<UrlElementDto> _elementWrites;
    private readonly IWriteRepository<MetricSummaryDto> _metricWrites;
    private readonly IReadRepository<LinkEdgeDto> _edgeReads;
    private readonly IWriteRepository<LinkEdgeDto> _edgeWrites;
    private readonly IWriteRepository<AnalyticsDetectionDto> _analyticsWrites;
    private readonly IWriteRepository<TechnologyDetectionDto> _technologyWrites;
    private readonly IContentStore _contentStore;
    private readonly ICrawlCheckpointService _checkpoints;
    private readonly IAuditPipeline _pipeline;
    private readonly ITolerantDomParser _parser;
    private readonly IUrlCanonicalizer _canonicalizer;
    private readonly IRobotsTxtParser _robotsParser;
    private readonly IWafChallengeEvaluator _wafEvaluator;
    private readonly WafDetectionRecorder _wafRecorder;
    private readonly WafBlockRateDiagnostic _wafBlockRate;
    private readonly IReadRepository<WafDetectionDto> _detectionReads;
    private readonly IWriteRepository<WafDetectionDto> _detectionWrites;
    private readonly IAuditScoringService _scoring;
    private readonly AuditScoreRecorder _scoreRecorder;
    private readonly FindingEmitter _findings;
    private readonly IMarTechAuditor _martech;
    private readonly ITechnologyDetector _technologies;
    private readonly IHostThrottleEventStore _throttleStore;
    private readonly AimdMetricSummarizer _aimdSummarizer;
    private readonly GeoReadinessAnalyzer _geo;
    private readonly AiBotGovernanceMatrix _governance;
    private readonly GovernanceMatrixRecorder _governanceRecorder;
    private readonly GeoAggregateRecorder _geoRecorder;
    private readonly ICustomContentExtractor _customExtractor;
    private readonly ICustomContentSearch _customSearch;
    private readonly IJsSnippetRunner _snippetRunner;
    private readonly IWriteRepository<ExtractedContentDto> _extractedContentWrites;
    private readonly IWriteRepository<ContentSearchResultDto> _searchResultWrites;
    private readonly IWriteRepository<SnippetResultDto> _snippetResultWrites;
    private readonly IRealtimeHub _hub;
    private readonly NGramRuleAnalyzer _ngramSitePass;
    private readonly LinkZoneInventoryService _linkInventory;
    private readonly LinkGraphEnrichmentPass _linkGraphEnrichment;
    private readonly ILogger<AuditExecutionEngine> _logger;
    private readonly ConcurrentDictionary<long, AuditRunState> _runs = new();
    private readonly ConcurrentDictionary<long, Task> _runTasks = new();

    /// <summary>Creates the engine over the accepted component surfaces.</summary>
    public AuditExecutionEngine(
        IDbProvider provider,
        IReadRepository<AuditDto> auditReads,
        IWriteRepository<AuditDto> auditWrites,
        IReadRepository<CrawlConfigurationDto> configReads,
        IReadRepository<UrlRecordDto> urlReads,
        IWriteRepository<UrlRecordDto> urlWrites,
        IReadRepository<ContentArtifactDto> artifactReads,
        IWriteRepository<ContentArtifactDto> artifactWrites,
        IWriteRepository<UrlElementDto> elementWrites,
        IWriteRepository<MetricSummaryDto> metricWrites,
        IReadRepository<LinkEdgeDto> edgeReads,
        IWriteRepository<LinkEdgeDto> edgeWrites,
        IWriteRepository<AnalyticsDetectionDto> analyticsWrites,
        IWriteRepository<TechnologyDetectionDto> technologyWrites,
        IContentStore contentStore,
        ICrawlCheckpointService checkpoints,
        IAuditPipeline pipeline,
        ITolerantDomParser parser,
        IUrlCanonicalizer canonicalizer,
        IRobotsTxtParser robotsParser,
        IWafChallengeEvaluator wafEvaluator,
        WafDetectionRecorder wafRecorder,
        WafBlockRateDiagnostic wafBlockRate,
        IReadRepository<WafDetectionDto> detectionReads,
        IWriteRepository<WafDetectionDto> detectionWrites,
        IAuditScoringService scoring,
        AuditScoreRecorder scoreRecorder,
        FindingEmitter findings,
        IMarTechAuditor martech,
        ITechnologyDetector technologies,
        IHostThrottleEventStore throttleStore,
        AimdMetricSummarizer aimdSummarizer,
        GeoReadinessAnalyzer geo,
        AiBotGovernanceMatrix governance,
        GovernanceMatrixRecorder governanceRecorder,
        GeoAggregateRecorder geoRecorder,
        ICustomContentExtractor customExtractor,
        ICustomContentSearch customSearch,
        IJsSnippetRunner snippetRunner,
        IWriteRepository<ExtractedContentDto> extractedContentWrites,
        IWriteRepository<ContentSearchResultDto> searchResultWrites,
        IWriteRepository<SnippetResultDto> snippetResultWrites,
        IRealtimeHub hub,
        NGramRuleAnalyzer ngramSitePass,
        LinkZoneInventoryService linkInventory,
        LinkGraphEnrichmentPass linkGraphEnrichment,
        ILogger<AuditExecutionEngine> logger)
    {
        _provider = provider;
        _auditReads = auditReads;
        _auditWrites = auditWrites;
        _configReads = configReads;
        _urlReads = urlReads;
        _urlWrites = urlWrites;
        _artifactReads = artifactReads;
        _artifactWrites = artifactWrites;
        _elementWrites = elementWrites;
        _metricWrites = metricWrites;
        _edgeReads = edgeReads;
        _edgeWrites = edgeWrites;
        _analyticsWrites = analyticsWrites;
        _technologyWrites = technologyWrites;
        _contentStore = contentStore;
        _checkpoints = checkpoints;
        _pipeline = pipeline;
        _parser = parser;
        _canonicalizer = canonicalizer;
        _robotsParser = robotsParser;
        _wafEvaluator = wafEvaluator;
        _wafRecorder = wafRecorder;
        _wafBlockRate = wafBlockRate;
        _detectionReads = detectionReads;
        _detectionWrites = detectionWrites;
        _scoring = scoring;
        _scoreRecorder = scoreRecorder;
        _findings = findings;
        _martech = martech;
        _technologies = technologies;
        _throttleStore = throttleStore;
        _aimdSummarizer = aimdSummarizer;
        _geo = geo;
        _governance = governance;
        _governanceRecorder = governanceRecorder;
        _geoRecorder = geoRecorder;
        _customExtractor = customExtractor;
        _customSearch = customSearch;
        _snippetRunner = snippetRunner;
        _extractedContentWrites = extractedContentWrites;
        _searchResultWrites = searchResultWrites;
        _snippetResultWrites = snippetResultWrites;
        _hub = hub;
        _ngramSitePass = ngramSitePass;
        _linkInventory = linkInventory;
        _linkGraphEnrichment = linkGraphEnrichment;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => 20;

    /// <summary>Identifier of a currently running audit, when any (system health).</summary>
    public long? RunningAuditId
    {
        get
        {
            foreach (var (auditId, state) in _runs)
            {
                if (state.Status == AuditStatus.Running)
                {
                    return auditId;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The live AIMD controller of the currently crawling audit, when any.
    /// The composition's <c>IAimdPolitenessController</c> router delegates to
    /// this surface so the host explorer and manual override address live state.
    /// </summary>
    public IAimdPolitenessController? ActiveAimdController
    {
        get
        {
            foreach (var (_, state) in _runs)
            {
                if (state.Status == AuditStatus.Running && state.Aimd is { } aimd)
                {
                    return aimd;
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Crash recovery (§2.4): audits left Running/Queued by a previous launch
        // become Paused — resumable from their latest checkpoint, never silently
        // restarted and never lost.
        var orphans = await _auditReads.ListAsync(new ListQuery(0, 1000), cancellationToken);
        foreach (var audit in orphans.Items)
        {
            if (audit.Status is AuditStatus.Running or AuditStatus.Queued && !audit.IsDeleted)
            {
                await _auditWrites.UpdateAsync(audit with
                {
                    Status = AuditStatus.Paused,
                    Phase = audit.Phase == AuditPhase.NotStarted ? AuditPhase.Crawling : audit.Phase
                }, cancellationToken);
                _logger.LogWarning(
                    "Audit {AuditId} interrupted by a previous shutdown; marked paused for resume.",
                    audit.Id);
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Graceful drain: checkpoint every active run, mark it paused (resumable
        // on the next launch), then cancel the workers so no poll loop outlives
        // the database provider.
        foreach (var (auditId, state) in _runs)
        {
            try
            {
                state.SetPaused();
                await PersistStatusAsync(auditId, AuditStatus.Paused, cancellationToken);
                if (state.Frontier is { } frontier && !state.PauseCheckpointSaved)
                {
                    state.PauseCheckpointSaved = true;
                    await FlushThrottleAsync(state, cancellationToken);
                    await SaveCheckpointAsync(auditId, frontier, state.Aimd, state, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause audit {AuditId} during drain.", auditId);
            }
        }

        foreach (var state in _runs.Values)
        {
            state.CancellationTokenSource.Cancel();
        }

        var tasks = _runTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        }

        foreach (var state in _runs.Values)
        {
            state.Dispose();
        }

        _runs.Clear();
        _runTasks.Clear();
    }

    /// <inheritdoc />
    public async Task<long> StartAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType triggerType,
        CrawlLimitOverrides? limitOverrides = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configReads.GetByIdAsync(configurationId, cancellationToken)
            ?? throw new InvalidOperationException($"Configuration {configurationId} does not exist.");
        if (configuration.ProjectId != projectId)
        {
            throw new InvalidOperationException("Configuration does not belong to the project.");
        }

        var snapshot = configuration with { LimitOverrides = limitOverrides ?? configuration.LimitOverrides };
        var audit = new AuditDto(
            Id: 0,
            ProjectId: projectId,
            ConfigurationId: configurationId,
            ConfigurationSnapshotJson: JsonSerializer.Serialize(snapshot, PayloadJson),
            Status: AuditStatus.Running,
            Phase: AuditPhase.Crawling,
            StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: null,
            CountersJson: "{}",
            EngineVersion: typeof(AuditExecutionEngine).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            IsDeleted: false);
        var auditId = (await _auditWrites.CreateAsync(audit, cancellationToken)).Id;

        var state = new AuditRunState(auditId, new CancellationTokenSource());
        _runs[auditId] = state;
        _runTasks[auditId] = Task.Run(() => RunAuditAsync(auditId, state), CancellationToken.None);
        await PublishStageAsync(auditId, AuditStatus.Running, AuditPhase.Crawling, cancellationToken);
        return auditId;
    }

    /// <inheritdoc />
    public async Task<AuditLifecycleSnapshot> PauseAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAuditAsync(auditId, cancellationToken);
        if (audit.Status != AuditStatus.Running)
        {
            throw new InvalidOperationException($"Audit {auditId} is not running (status {audit.Status}).");
        }

        await PersistStatusAsync(auditId, AuditStatus.Paused, cancellationToken);
        if (_runs.TryGetValue(auditId, out var state))
        {
            state.SetPaused();
        }

        return new AuditLifecycleSnapshot(auditId, AuditStatus.Paused, audit.Phase);
    }

    /// <inheritdoc />
    public async Task<AuditLifecycleSnapshot> ResumeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAuditAsync(auditId, cancellationToken);
        if (audit.Status != AuditStatus.Paused)
        {
            throw new InvalidOperationException($"Audit {auditId} is not paused (status {audit.Status}).");
        }

        await PersistStatusAsync(auditId, AuditStatus.Running, cancellationToken);
        if (_runs.TryGetValue(auditId, out var state))
        {
            state.SetRunning();
        }
        else
        {
            // Resumed after a restart: rehydrate from the latest checkpoint.
            var state2 = new AuditRunState(auditId, new CancellationTokenSource());
            _runs[auditId] = state2;
            _runTasks[auditId] = Task.Run(() => RunAuditAsync(auditId, state2), CancellationToken.None);
        }

        return new AuditLifecycleSnapshot(auditId, AuditStatus.Running, audit.Phase);
    }

    /// <inheritdoc />
    public async Task<AuditLifecycleSnapshot> CancelAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAuditAsync(auditId, cancellationToken);
        if (audit.Status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed)
        {
            throw new InvalidOperationException($"Audit {auditId} already reached a terminal state.");
        }

        await PersistStatusAsync(auditId, AuditStatus.Cancelled, cancellationToken);
        if (_runs.TryGetValue(auditId, out var state))
        {
            state.Status = AuditStatus.Cancelled;
            state.PauseGate.TrySetResult();
        }

        return new AuditLifecycleSnapshot(auditId, AuditStatus.Cancelled, audit.Phase);
    }

    /// <inheritdoc />
    public async Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken cancellationToken = default)
    {
        var source = await RequireAuditAsync(auditId, cancellationToken);
        var configuration = JsonSerializer.Deserialize<CrawlConfigurationDto>(
            source.ConfigurationSnapshotJson, PayloadJson)
            ?? throw new InvalidOperationException("Audit configuration snapshot is unreadable.");

        if (refresh)
        {
            return await StartAuditAsync(
                source.ProjectId, source.ConfigurationId, ScheduleTriggerType.Manual,
                configuration.LimitOverrides, cancellationToken);
        }

        // Analysis-only pass over the SAME stored dataset: copy URL records and
        // artifact index rows under a new audit (content keys are shared; no
        // re-fetch ever happens — FS-CRAWL-001 §7.2 stored-artifacts rule).
        var reAudit = new AuditDto(
            Id: 0,
            ProjectId: source.ProjectId,
            ConfigurationId: source.ConfigurationId,
            ConfigurationSnapshotJson: source.ConfigurationSnapshotJson,
            Status: AuditStatus.Running,
            Phase: AuditPhase.Analyzing,
            StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: null,
            CountersJson: "{}",
            EngineVersion: source.EngineVersion,
            IsDeleted: false);
        var newAuditId = (await _auditWrites.CreateAsync(reAudit, cancellationToken)).Id;

        var state = new AuditRunState(newAuditId, new CancellationTokenSource());
        _runs[newAuditId] = state;
        _runTasks[newAuditId] = Task.Run(
            () => RunAnalysisOnlyAsync(newAuditId, auditId, state), CancellationToken.None);
        return newAuditId;
    }

    /// <inheritdoc />
    public async Task<AuditProgressSnapshot> GetProgressAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var audit = await RequireAuditAsync(auditId, cancellationToken);
        var counters = ParseCounters(audit.CountersJson);
        var queued = _runs.TryGetValue(auditId, out var state) && state.Frontier is { } frontier
            ? frontier.Count
            : 0;
        double? percent = counters.Discovered > 0
            ? Math.Min(100.0, 100.0 * counters.Analyzed / counters.Discovered)
            : null;
        return new AuditProgressSnapshot(
            auditId, audit.Status, audit.Phase,
            counters.Discovered, counters.Fetched, counters.Analyzed, queued, percent);
    }

    // ------------------------------------------------------------------
    // Run orchestration
    // ------------------------------------------------------------------

    private async Task RunAuditAsync(long auditId, AuditRunState state)
    {
        var ct = state.CancellationTokenSource.Token;
        try
        {
            var audit = await RequireAuditAsync(auditId, ct);
            var configuration = JsonSerializer.Deserialize<CrawlConfigurationDto>(
                audit.ConfigurationSnapshotJson, PayloadJson)
                ?? throw new InvalidOperationException("Audit configuration snapshot is unreadable.");

            await CrawlPhaseAsync(auditId, audit.ProjectId, configuration, state, ct);

            // Observe terminal transitions before analysis (cancel during crawl).
            var current = await RequireAuditAsync(auditId, ct);
            if (current.Status == AuditStatus.Cancelled)
            {
                await FinalizeAsync(auditId, state, AuditStatus.Cancelled, ct);
                return;
            }

            // D15: resolve edge targets + compute/stamp weighted link equity
            // once the full URL dataset exists (before analysis consumes it).
            await RunLinkGraphEnrichmentAsync(auditId, ct);

            await AnalyzePhaseAsync(auditId, audit.ProjectId, configuration, state, ct);
            await ScoreAndFinalizeAsync(auditId, state, ct);
        }
        catch (OperationCanceledException)
        {
            await TryFinalizeUnlessPausedAsync(auditId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit {AuditId} failed.", auditId);
            await TryFinalizeAsync(auditId, state, AuditStatus.Failed);
        }
        finally
        {
            _runs.TryRemove(auditId, out _);
            _runTasks.TryRemove(auditId, out _);
            state.Dispose();
        }
    }

    private async Task CrawlPhaseAsync(
        long auditId,
        long projectId,
        CrawlConfigurationDto configuration,
        AuditRunState state,
        CancellationToken ct)
    {
        var limits = configuration.LimitOverrides;
        // §4.2/§4.3: null is the unlimited sentinel — no default ceiling, and an
        // explicit override is honored exactly as given (no clamp).
        var maxUrls = ResolveUrlCeiling(limits);
        var maxDepth = limits?.MaxDepth ?? int.MaxValue;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? durationLimit = limits?.MaxDurationSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;

        var resolvedLimits = new ResolvedCrawlLimits(
            limits?.MaxUrls,
            limits?.MaxDepth,
            limits?.MaxDurationSeconds,
            limits?.MaxConcurrency ?? 8,
            limits?.RequestsPerSecond ?? double.PositiveInfinity,
            limits?.FetchTimeoutSeconds ?? 60);
        var aimdOptions = new AimdControllerOptions
        {
            InitialRate = limits?.RequestsPerSecond is > 0
                ? Math.Min(limits.RequestsPerSecond.Value, 10.0)
                : 1.0,
            ResolvedThroughputCeiling = resolvedLimits.RequestsPerSecond,
            WorkerConcurrencyCeiling = resolvedLimits.MaxConcurrency
        };
        if (!string.IsNullOrWhiteSpace(configuration.PolitenessJson))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AimdSettings>(configuration.PolitenessJson, PayloadJson);
                if (settings is not null)
                {
                    aimdOptions = AimdControllerOptions.FromSettings(settings, resolvedLimits);
                }
            }
            catch (JsonException)
            {
                // Malformed politeness payload degrades to the local defaults.
            }
        }

        // AIMD observability (criterion #36): per-audit composite sink — the
        // bounded-history recorder (durable rows, flushed at checkpoints) plus
        // the coalesced hub adapter (typed host-throttle events, ≤1/s/host).
        var throttleRecorder = new HostThrottleEventRecorder(_throttleStore);
        var throttleCoalescer = new CoalescingHostThrottleSink(new HubHostThrottleSink(_hub));
        var aimd = new AimdPolitenessController(
            aimdOptions,
            auditId: auditId,
            adjustmentSink: new CompositeHostThrottleSink(throttleRecorder, throttleCoalescer));
        state.Aimd = aimd;
        state.ThrottleRecorder = throttleRecorder;
        state.ThrottleCoalescer = throttleCoalescer;
        var frontier = new PriorityFrontier(aimd);
        state.Frontier = frontier;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var robotsByHost = new Dictionary<string, RobotsDocument?>(StringComparer.OrdinalIgnoreCase);
        var seedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var httpClient = Composition.CrawlTargetHttpClientFactory.Create(
            TimeSpan.FromSeconds(limits?.FetchTimeoutSeconds ?? 60));
        var fetcher = new HttpFetcher(
            new HttpFetcherOptions(), httpClient,
            new RedirectChainRecorder(), aimd);

        // Resume from the latest checkpoint when present.
        var checkpoint = await _checkpoints.LoadLatestAsync(auditId, ct);
        if (checkpoint is not null)
        {
            var snapshot = JsonSerializer.Deserialize<FrontierSnapshot>(checkpoint.SerializedFrontier, PayloadJson);
            if (snapshot is not null)
            {
                frontier.Restore(snapshot);
                foreach (var entry in snapshot.Entries)
                {
                    seen.Add(entry.Url);
                }
            }

            if (checkpoint.AimdStateJson is { } aimdJson)
            {
                var aimdState = JsonSerializer.Deserialize<AimdCheckpointState>(aimdJson, PayloadJson);
                if (aimdState is not null)
                {
                    aimd.RestoreCheckpointState(aimdState);
                }
            }

            var restored = ParseCounters(checkpoint.CountersJson);
            state.Counters.Discovered = restored.Discovered;
            state.Counters.Fetched = restored.Fetched;
            state.Counters.Failed = restored.Failed;
        }
        else
        {
            foreach (var seed in ParseSeeds(configuration.SeedsJson))
            {
                var canonical = _canonicalizer.Canonicalize(seed);
                if (!seen.Add(canonical))
                {
                    continue;
                }

                var host = new Uri(canonical).Host;
                seedHosts.Add(host);
                await frontier.EnqueueAsync(
                    new FrontierEntry(canonical, host, 0, 1.0, DiscoverySource.Seed), ct);
                state.Counters.Discovered++;
            }
        }

        var fetchesSinceCheckpoint = 0;
        while (frontier.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if ((durationLimit is { } limit && stopwatch.Elapsed > limit)
                || IsUrlCeilingReached(maxUrls, state.Counters.Fetched))
            {
                break;
            }

            // Observe persisted lifecycle transitions (pause/resume/cancel).
            var status = await ObserveStatusAsync(auditId, state, ct);
            if (status == AuditStatus.Cancelled)
            {
                return;
            }

            var entry = await frontier.DequeueAsync(ct);
            if (entry is null)
            {
                break;
            }

            if (entry.Depth > maxDepth)
            {
                continue;
            }

            var robotsAllowed = await IsRobotsAllowedAsync(fetcher, robotsByHost, entry.Url, entry.Host, ct);
            if (!robotsAllowed)
            {
                await _urlWrites.CreateAsync(NewUrlRecord(
                    auditId, entry, CrawlStatusClass.NotCrawled, null, RobotsState.Disallowed), ct);
                continue;
            }

            await aimd.WaitAsync(entry.Host, ct);
            HttpFetchResult? fetch = null;
            try
            {
                fetch = await fetcher.FetchAsync(new HttpFetchRequest(entry.Url), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Counters.Failed++;
                _logger.LogDebug(ex, "Fetch failed for {Url}.", entry.Url);
            }

            if (fetch is null)
            {
                await _urlWrites.CreateAsync(NewUrlRecord(
                    auditId, entry, CrawlStatusClass.FetchError, null, RobotsState.Allowed), ct);
                continue;
            }

            state.Counters.Fetched++;
            aimd.RecordResponse(entry.Host, new HostResponseSample(
                fetch.Ttfb.TotalMilliseconds, fetch.StatusCode, false, DateTimeOffset.UtcNow));

            var bodyBytes = fetch.Body.ToArray();
            var waf = _wafEvaluator.Evaluate(new WafEvaluationInput(
                entry.Url, fetch.StatusCode, fetch.Headers,
                bodyBytes.Length > 0 ? Snippet(bodyBytes) : null));
            var statusClass = ClassifyStatus(fetch.StatusCode, waf.IsChallenge);

            var urlRecord = NewUrlRecord(auditId, entry, statusClass, fetch, RobotsState.Allowed) with
            {
                ContentType = fetch.Headers.TryGetValue("content-type", out var contentType) ? contentType : null,
                SizeBytes = bodyBytes.LongLength,
                TimingJson = JsonSerializer.Serialize(new
                {
                    ttfbMs = fetch.Ttfb.TotalMilliseconds,
                    totalMs = fetch.TotalDuration.TotalMilliseconds,
                    headers = fetch.Headers
                }, PayloadJson),
                RedirectTargetUrl = fetch.FinalUrl != entry.Url ? fetch.FinalUrl : null,
                BlockReason = waf.IsChallenge ? "waf-challenge" : null,
                WafProvider = waf.IsChallenge ? waf.Provider : null,
                ChallengeClass = waf.IsChallenge ? waf.ChallengeClass : null
            };
            var urlId = (await _urlWrites.CreateAsync(urlRecord, ct)).Id;

            // Persist the WAF verdict through the accepted recording surface:
            // corroborated challenges get a detection row + per-URL finding;
            // sub-threshold/single-facet hits get a suspected-challenge Notice
            // (never a block). Detection/reporting only (exclusion E3).
            if (waf.IsChallenge)
            {
                await _wafRecorder.RecordChallengeAsync(auditId, urlId, waf, ct);
            }
            else if (waf.MatchedSignatureKeys.Count > 0)
            {
                await _wafRecorder.RecordSuspectedNoticeAsync(auditId, urlId, waf, ct);
            }

            if (bodyBytes.Length > 0 && statusClass == CrawlStatusClass.Success)
            {
                using var bodyStream = new MemoryStream(bodyBytes);
                await _contentStore.PutAsync(auditId, urlId, ContentArtifactKind.Body, bodyStream, ct);
            }

            // Link discovery over the stored body (same-host scope by default).
            if (statusClass == CrawlStatusClass.Success && IsHtml(urlRecord.ContentType))
            {
                var document = _parser.Parse(System.Text.Encoding.UTF8.GetString(bodyBytes), entry.Url);
                await DiscoverLinksAsync(
                    auditId, urlId, entry, document, seen, seedHosts, frontier, state, maxDepth, ct);
            }

            fetchesSinceCheckpoint++;
            if (fetchesSinceCheckpoint >= CheckpointEveryFetches)
            {
                await FlushThrottleAsync(state, ct);
                await SaveCheckpointAsync(auditId, frontier, aimd, state, ct);
                fetchesSinceCheckpoint = 0;
            }

            await PublishProgressAsync(auditId, state, ct);
        }

        await FlushThrottleAsync(state, ct);
        await SaveCheckpointAsync(auditId, frontier, aimd, state, ct);
        // Completion summary: total adjustments + per-host final state into
        // metric_summaries under the locked host-throttle key (§3.5/§3.6).
        await _aimdSummarizer.WriteCompletionSummaryAsync(aimd, auditId, _metricWrites, ct);
    }

    private async Task AnalyzePhaseAsync(
        long auditId,
        long projectId,
        CrawlConfigurationDto configuration,
        AuditRunState state,
        CancellationToken ct)
    {
        await PersistPhaseAsync(auditId, AuditPhase.Analyzing, ct);
        await PublishStageAsync(auditId, AuditStatus.Running, AuditPhase.Analyzing, ct);

        // Bounded streaming over the URL dataset: no ceiling on the row count.
        var offset = 0;
        while (true)
        {
            var urls = await _urlReads.ListAsync(
                new ListQuery(offset, DatasetPageSize, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }),
                ct);
            foreach (var url in urls.Items)
            {
                ct.ThrowIfCancellationRequested();
                var status = await ObserveStatusAsync(auditId, state, ct);
                if (status == AuditStatus.Cancelled)
                {
                    return;
                }

                if (url.StatusClass != CrawlStatusClass.Success)
                {
                    continue;
                }

                var context = await BuildStoredContextAsync(auditId, url, configuration, ct);
                if (context is null)
                {
                    continue;
                }

                await AnalyzeOnePageAsync(auditId, projectId, url.Id, context, ct);
                state.Counters.Analyzed++;
                await PublishProgressAsync(auditId, state, ct);
            }

            if (urls.Items.Count < DatasetPageSize)
            {
                break;
            }

            offset += DatasetPageSize;
        }

        // D14 (criterion #38 site facet): the site-level n-gram pass runs once
        // every per-URL pass has completed — it materializes ngram_site_summaries
        // and emits content.keyword-cannibalization / content.topical-gap
        // findings plus the ngram metric contributions (FS-CRAWL-002 §5.4–§5.5).
        // Both the live audit and the offline re-audit converge here.
        await RunNgramSitePassAsync(auditId, projectId, ct);
    }

    /// <summary>
    /// Runs the post-crawl link-graph enrichment (target resolution + weighted
    /// equity). Failure-isolated like the other additive surfaces: an enrichment
    /// failure never fails the audit (FS-CRAWL-002 §4.2).
    /// </summary>
    private async Task RunLinkGraphEnrichmentAsync(long auditId, CancellationToken ct)
    {
        try
        {
            await _linkGraphEnrichment.RunAsync(auditId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Link-graph enrichment failed for audit {AuditId}; the audit continues.", auditId);
        }
    }

    /// <summary>
    /// Runs the n-gram site pass and persists its findings and metric
    /// contributions. Failure-isolated like the other additive surfaces: a
    /// site-pass failure never fails the audit (FS-CRAWL-002 §4.2).
    /// </summary>
    private async Task RunNgramSitePassAsync(long auditId, long projectId, CancellationToken ct)
    {
        try
        {
            var result = await _ngramSitePass.RunSitePassAsync(auditId, ct);
            if (result.Findings.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, result.Findings, ct);
            }

            foreach (var metric in result.Metrics)
            {
                await _metricWrites.CreateAsync(
                    new MetricSummaryDto(0, auditId, metric.MetricKey, metric.DimensionsJson, metric.Value), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "N-gram site pass failed for audit {AuditId}; the audit continues.", auditId);
        }
    }

    private async Task RunAnalysisOnlyAsync(long newAuditId, long sourceAuditId, AuditRunState state)
    {
        var ct = state.CancellationTokenSource.Token;
        try
        {
            var audit = await RequireAuditAsync(newAuditId, ct);
            var configuration = JsonSerializer.Deserialize<CrawlConfigurationDto>(
                audit.ConfigurationSnapshotJson, PayloadJson)
                ?? throw new InvalidOperationException("Audit configuration snapshot is unreadable.");

            // Copy the stored dataset under the new audit (no re-fetch), in
            // bounded pages so unlimited-size datasets are never truncated.
            var urlIdMap = new Dictionary<long, long>();
            var urlOffset = 0;
            while (true)
            {
                var urls = await _urlReads.ListAsync(
                    new ListQuery(urlOffset, DatasetPageSize, new Dictionary<string, string> { ["auditId"] = sourceAuditId.ToString() }),
                    ct);
                foreach (var url in urls.Items)
                {
                    var newId = (await _urlWrites.CreateAsync(url with { Id = 0, AuditId = newAuditId }, ct)).Id;
                    urlIdMap[url.Id] = newId;
                    state.Counters.Discovered++;
                    state.Counters.Fetched++;
                }

                if (urls.Items.Count < DatasetPageSize)
                {
                    break;
                }

                urlOffset += DatasetPageSize;
            }

            var artifactOffset = 0;
            while (true)
            {
                var artifacts = await _artifactReads.ListAsync(
                    new ListQuery(artifactOffset, DatasetPageSize, new Dictionary<string, string> { ["auditId"] = sourceAuditId.ToString() }),
                    ct);
                foreach (var artifact in artifacts.Items)
                {
                    if (urlIdMap.TryGetValue(artifact.UrlId, out var mappedUrlId))
                    {
                        await _artifactWrites.CreateAsync(
                            artifact with { Id = 0, AuditId = newAuditId, UrlId = mappedUrlId }, ct);
                    }
                }

                if (artifacts.Items.Count < DatasetPageSize)
                {
                    break;
                }

                artifactOffset += DatasetPageSize;
            }

            // Copy stored WAF detections with remapped URL references so the
            // re-audit's waf-blocks surface and evidence stay complete offline.
            var detectionOffset = 0;
            while (true)
            {
                var detections = await _detectionReads.ListAsync(
                    new ListQuery(detectionOffset, DatasetPageSize, new Dictionary<string, string> { ["auditId"] = sourceAuditId.ToString() }),
                    ct);
                foreach (var detection in detections.Items)
                {
                    if (urlIdMap.TryGetValue(detection.UrlId, out var mappedUrlId))
                    {
                        await _detectionWrites.CreateAsync(
                            detection with { Id = 0, AuditId = newAuditId, UrlId = mappedUrlId }, ct);
                    }
                }

                if (detections.Items.Count < DatasetPageSize)
                {
                    break;
                }

                detectionOffset += DatasetPageSize;
            }

            // D15: copy the stored link graph with remapped URL references so
            // the re-audit's anchor-text zones, graph surfaces, and equity stay
            // complete offline (the dataset is reused, never re-fetched).
            var edgeOffset = 0;
            while (true)
            {
                var edges = await _edgeReads.ListAsync(
                    new ListQuery(edgeOffset, DatasetPageSize, new Dictionary<string, string> { ["auditId"] = sourceAuditId.ToString() }),
                    ct);
                foreach (var edge in edges.Items)
                {
                    if (!urlIdMap.TryGetValue(edge.SourceUrlId, out var mappedSource))
                    {
                        continue;
                    }

                    long? mappedTarget = edge.TargetUrlId is { } target && urlIdMap.TryGetValue(target, out var mapped)
                        ? mapped
                        : null;
                    await _edgeWrites.CreateAsync(edge with
                    {
                        Id = 0,
                        AuditId = newAuditId,
                        SourceUrlId = mappedSource,
                        TargetUrlId = mappedTarget,
                        WeightedEquity = null,
                    }, ct);
                }

                if (edges.Items.Count < DatasetPageSize)
                {
                    break;
                }

                edgeOffset += DatasetPageSize;
            }

            // Re-resolve targets and recompute equity for the new audit id.
            await RunLinkGraphEnrichmentAsync(newAuditId, ct);

            await AnalyzePhaseAsync(newAuditId, audit.ProjectId, configuration, state, ct);
            await ScoreAndFinalizeAsync(newAuditId, state, ct);
        }
        catch (OperationCanceledException)
        {
            await TryFinalizeUnlessPausedAsync(newAuditId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-audit {AuditId} failed.", newAuditId);
            await TryFinalizeAsync(newAuditId, state, AuditStatus.Failed);
        }
        finally
        {
            _runs.TryRemove(newAuditId, out _);
            _runTasks.TryRemove(newAuditId, out _);
            state.Dispose();
        }
    }

    private async Task AnalyzeOnePageAsync(
        long auditId, long projectId, long urlId, PageAuditContext context, CancellationToken ct)
    {
        var result = await _pipeline.RunAsync(context, ct);
        await _findings.EmitAsync(auditId, projectId, result.Findings, ct);

        foreach (var item in result.Inventory)
        {
            await _elementWrites.CreateAsync(
                new UrlElementDto(0, urlId, item.Group, item.PayloadJson, item.Ordinal), ct);
        }

        foreach (var metric in result.Metrics)
        {
            await _metricWrites.CreateAsync(
                new MetricSummaryDto(0, auditId, metric.MetricKey, metric.DimensionsJson, metric.Value), ct);
        }

        // MarTech + technology detections persist alongside the pipeline output.
        var martech = await _martech.AuditAsync(context, ct);
        foreach (var detection in martech.Detections)
        {
            await _analyticsWrites.CreateAsync(new AnalyticsDetectionDto(
                0, auditId, urlId, detection.Vendor, detection.ContainerId,
                detection.DetectionSource, detection.Placement), ct);
        }

        if (martech.Findings.Count > 0)
        {
            await _findings.EmitAsync(auditId, projectId, martech.Findings, ct);
        }

        foreach (var tech in await _technologies.DetectAsync(context, ct))
        {
            await _technologyWrites.CreateAsync(new TechnologyDetectionDto(
                0, auditId, urlId, tech.TechnologyKey, tech.Category, tech.Confidence), ct);
        }

        // Custom content family (FS-CRAWL-001 §7.9/§7.16/§7.17): user-defined
        // extraction rules, search items, and JS snippets run per page and
        // persist into extracted_content / content_search_results /
        // snippet_results. Failure-isolated like the other additive surfaces.
        await PersistCustomContentAsync(context, ct);
    }

    /// <summary>
    /// Runs the accepted custom content family against one analyzed page and
    /// persists its drafts. Definitions ride the configuration's
    /// <c>CustomExtractionRulesJson</c> payload: a JSON array carries extraction
    /// rules only; a JSON object carries <c>rules</c>, <c>searchItems</c>, and
    /// <c>snippets</c> together. Snippets honor the runner's rendering gate
    /// (they execute against the stored DOM when no rendered document exists).
    /// Component failures never fail the page analysis.
    /// </summary>
    private async Task PersistCustomContentAsync(PageAuditContext context, CancellationToken ct)
    {
        var payload = context.Configuration.CustomExtractionRulesJson;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            var (rules, items, snippets) = ParseCustomDefinitions(payload);
            var auditId = context.Url.AuditId;
            var urlId = context.Url.Id;

            if (rules.Count > 0)
            {
                foreach (var value in await _customExtractor.ExtractAsync(context, rules, ct))
                {
                    await _extractedContentWrites.CreateAsync(new ExtractedContentDto(
                        0, auditId, urlId, value.RuleName, value.Ordinal, value.ValueJson,
                        value.ValueType, value.IsInvalid, value.IsTruncated), ct);
                }
            }

            if (items.Count > 0)
            {
                foreach (var match in await _customSearch.SearchAsync(context, items, ct))
                {
                    await _searchResultWrites.CreateAsync(new ContentSearchResultDto(
                        0, auditId, urlId, match.ItemKey, match.MatchCount,
                        JsonSerializer.Serialize(match.Snippets)), ct);
                }
            }

            if (snippets.Count > 0 && context.Configuration.RenderingMode != RenderMode.None)
            {
                foreach (var draft in await _snippetRunner.RunAsync(context, snippets, ct))
                {
                    await _snippetResultWrites.CreateAsync(new SnippetResultDto(
                        0, auditId, urlId, draft.SnippetKey, draft.ColumnsJson, draft.RowsJson), ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Custom content persistence failed for URL {UrlId}; the page analysis continues.", context.Url.Id);
        }
    }

    /// <summary>
    /// Tolerantly parses the custom definition payload (array = rules only;
    /// object = rules + searchItems + snippets). Malformed payloads yield no
    /// definitions rather than failing the audit.
    /// </summary>
    private static (IReadOnlyList<CustomExtractionRule> Rules, IReadOnlyList<CustomSearchItem> Items, IReadOnlyList<JsSnippetDefinition> Snippets) ParseCustomDefinitions(string payload)
    {
        var rules = new List<CustomExtractionRule>();
        var items = new List<CustomSearchItem>();
        var snippets = new List<JsSnippetDefinition>();
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                rules.AddRange(document.RootElement.Deserialize<List<CustomExtractionRule>>(CustomJsonOptions) ?? []);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
                {
                    rules.AddRange(rulesElement.Deserialize<List<CustomExtractionRule>>(CustomJsonOptions) ?? []);
                }

                if (document.RootElement.TryGetProperty("searchItems", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
                {
                    items.AddRange(itemsElement.Deserialize<List<CustomSearchItem>>(CustomJsonOptions) ?? []);
                }

                if (document.RootElement.TryGetProperty("snippets", out var snippetsElement) && snippetsElement.ValueKind == JsonValueKind.Array)
                {
                    snippets.AddRange(snippetsElement.Deserialize<List<JsSnippetDefinition>>(CustomJsonOptions) ?? []);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed user payload: no custom definitions apply.
        }

        return (rules, items, snippets);
    }

    private static readonly JsonSerializerOptions CustomJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task ScoreAndFinalizeAsync(long auditId, AuditRunState state, CancellationToken ct)
    {
        // Site-wide WAF block-rate diagnostic: materializes waf-blocks /
        // waf-block-rate metrics and the WAF-Block alert when thresholds are
        // reached. Derived from stored url_records, so re-audits re-derive it
        // offline (criterion #15).
        await _wafBlockRate.ReportAsync(auditId, ct);

        // GEO/governance aggregates: failure-isolated — persistence must never
        // block or fail audit completion.
        await PersistIntelligenceAggregatesAsync(auditId, ct);

        await PersistPhaseAsync(auditId, AuditPhase.Scoring, ct);
        var drafts = await _scoring.ComputeAsync(auditId, ct);
        await _scoreRecorder.RecordAsync(auditId, drafts, ct);
        await FinalizeAsync(auditId, state, AuditStatus.Completed, ct);
    }

    /// <summary>
    /// Materializes the GEO dashboard aggregates from stored data only:
    /// readiness check scores, the advisory governance matrix, and governance
    /// coverage. Any recorder failure is logged and swallowed — the audit
    /// completes regardless (aggregate persistence is best-effort evidence).
    /// </summary>
    private async Task PersistIntelligenceAggregatesAsync(long auditId, CancellationToken ct)
    {
        try
        {
            var audit = await RequireAuditAsync(auditId, ct);
            var (geo, checks) = await _geo.AnalyzeWithChecksAsync(auditId, ct);
            await _geoRecorder.RecordReadinessAsync(auditId, checks, geo.ReadinessScore, ct);

            var (rows, findings) = await _governance.EvaluateWithFindingsAsync(auditId, ct);
            await _governanceRecorder.RecordAsync(auditId, rows, ct);
            await _geoRecorder.RecordGovernanceCoverageAsync(auditId, rows, ct);
            if (findings.Count > 0)
            {
                await _findings.EmitAsync(auditId, audit.ProjectId, findings, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Intelligence aggregate persistence failed for audit {AuditId}; audit completion is unaffected.",
                auditId);
        }
    }

    /// <summary>
    /// Drains the live crawl's throttle observability: forwards due coalesced
    /// hub adjustments and persists every buffered history row. Called before
    /// each checkpoint save so persisted evidence never lags the checkpoint.
    /// </summary>
    private static async Task FlushThrottleAsync(AuditRunState state, CancellationToken ct)
    {
        state.ThrottleCoalescer?.FlushPending();
        if (state.ThrottleRecorder is { } recorder)
        {
            await recorder.FlushAsync(ct);
        }
    }

    private async Task FinalizeAsync(long auditId, AuditRunState state, AuditStatus status, CancellationToken ct)
    {
        var audit = await RequireAuditAsync(auditId, ct);
        if (audit.Status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed)
        {
            status = audit.Status;
        }

        await PersistPhaseAsync(auditId, AuditPhase.Finalizing, ct);
        await _auditWrites.UpdateAsync(audit with
        {
            Status = status,
            Phase = AuditPhase.Finalizing,
            FinishedAt = DateTimeOffset.UtcNow,
            CountersJson = JsonSerializer.Serialize(state.Counters.ToPayload(), PayloadJson)
        }, ct);
        await PublishStageAsync(auditId, status, AuditPhase.Finalizing, ct);
        await _hub.PublishAsync(new HubEvent(
            "audit-completed",
            JsonSerializer.Serialize(new
            {
                auditId,
                status = status.ToString().ToLowerInvariant(),
                counters = state.Counters.ToPayload()
            }, PayloadJson)), ct);
    }

    private async Task TryFinalizeAsync(long auditId, AuditRunState state, AuditStatus status)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FinalizeAsync(auditId, state, status, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize audit {AuditId} as {Status}.", auditId, status);
        }
    }

    private async Task TryFinalizeUnlessPausedAsync(long auditId, AuditRunState state)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var audit = await _auditReads.GetByIdAsync(auditId, cts.Token);
            if (audit?.Status == AuditStatus.Paused)
            {
                // Graceful drain: the run stays paused and resumable (§2.4).
                return;
            }

            await FinalizeAsync(auditId, state, AuditStatus.Cancelled, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize cancelled audit {AuditId}.", auditId);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<AuditStatus> ObserveStatusAsync(long auditId, AuditRunState state, CancellationToken ct)
    {
        while (true)
        {
            var audit = await _auditReads.GetByIdAsync(auditId, ct);
            var status = audit?.Status ?? AuditStatus.Cancelled;
            if (status == AuditStatus.Running)
            {
                state.Status = AuditStatus.Running;
                return status;
            }

            if (status is AuditStatus.Cancelled or AuditStatus.Completed or AuditStatus.Failed)
            {
                state.Status = status;
                return status;
            }

            // Paused: checkpoint once, then poll until resumed or cancelled.
            if (!state.PauseCheckpointSaved)
            {
                state.PauseCheckpointSaved = true;
                if (state.Frontier is { } frontier)
                {
                    await FlushThrottleAsync(state, ct);
                    await SaveCheckpointAsync(auditId, frontier, state.Aimd, state, ct);
                }

                await PublishStageAsync(auditId, AuditStatus.Paused, audit?.Phase ?? AuditPhase.Crawling, ct);
            }

            await Task.Delay(StatusPollInterval, ct);
        }
    }

    private async Task SaveCheckpointAsync(
        long auditId,
        IPriorityFrontier frontier,
        AimdPolitenessController? aimd,
        AuditRunState state,
        CancellationToken ct)
    {
        var snapshot = frontier.CreateSnapshot();
        await _checkpoints.SaveAsync(new CrawlCheckpointDto(
            Id: 0,
            AuditId: auditId,
            SerializedFrontier: JsonSerializer.Serialize(snapshot, PayloadJson),
            SeenSetReference: null,
            CountersJson: JsonSerializer.Serialize(state.Counters.ToPayload(), PayloadJson),
            SavedAt: DateTimeOffset.UtcNow)
        {
            AimdStateJson = aimd is null
                ? null
                : JsonSerializer.Serialize(aimd.CreateCheckpointState(), PayloadJson)
        }, ct);
    }

    private async Task<bool> IsRobotsAllowedAsync(
        HttpFetcher fetcher,
        Dictionary<string, RobotsDocument?> robotsByHost,
        string url,
        string host,
        CancellationToken ct)
    {
        if (!robotsByHost.TryGetValue(host, out var document))
        {
            document = null;
            try
            {
                var robotsUrl = new Uri(new Uri(url), "/robots.txt").ToString();
                var result = await fetcher.FetchAsync(new HttpFetchRequest(robotsUrl), ct);
                if (result.StatusCode == 200)
                {
                    document = _robotsParser.Parse(System.Text.Encoding.UTF8.GetString(result.Body.ToArray()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreachable robots.txt imposes no restriction (RFC 9309).
                _logger.LogDebug(ex, "robots.txt unavailable for {Host}.", host);
            }

            robotsByHost[host] = document;
        }

        return document is null || _robotsParser.Evaluate(document, url, UserAgentToken).Allowed;
    }

    private async Task DiscoverLinksAsync(
        long auditId,
        long sourceUrlId,
        FrontierEntry source,
        DomDocument document,
        HashSet<string> seen,
        HashSet<string> seedHosts,
        IPriorityFrontier frontier,
        AuditRunState state,
        int maxDepth,
        CancellationToken ct)
    {
        var baseUri = new Uri(source.Url);
        // D15: zone-aware link inventory — every anchor carries its visible
        // text, rel flags, DOM-zone classification, and evidence into link_edges
        // at discovery time (FS-CRAWL-002 §15.1). Target references and weighted
        // equity are resolved by the post-crawl enrichment pass.
        var inventory = await _linkInventory.ExtractZonedLinksAsync(document, ct);
        foreach (var link in inventory.Links)
        {
            if (string.IsNullOrWhiteSpace(link.Href))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, link.Href, out var resolved) ||
                resolved.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var canonical = _canonicalizer.Canonicalize(resolved.ToString());
            var targetHost = new Uri(canonical).Host;
            var isInternal = seedHosts.Contains(targetHost);
            var anchorText = string.IsNullOrWhiteSpace(link.AnchorText) ? null : link.AnchorText.Trim();

            await _edgeWrites.CreateAsync(new LinkEdgeDto(
                0, auditId, sourceUrlId, canonical, null, anchorText,
                link.RelationFlags, isInternal)
            {
                DomZone = link.Zone,
                ZoneEvidence = link.ZoneEvidence,
            }, ct);

            if (!isInternal || source.Depth + 1 > maxDepth || !seen.Add(canonical))
            {
                continue;
            }

            await frontier.EnqueueAsync(new FrontierEntry(
                canonical, targetHost, source.Depth + 1, 0.5, DiscoverySource.Link), ct);
            state.Counters.Discovered++;
        }
    }

    private async Task<PageAuditContext?> BuildStoredContextAsync(
        long auditId, UrlRecordDto url, CrawlConfigurationDto configuration, CancellationToken ct)
    {
        var artifacts = await _artifactReads.ListAsync(
            new ListQuery(0, 10, new Dictionary<string, string>
            {
                ["auditId"] = auditId.ToString(),
                ["urlId"] = url.Id.ToString()
            }), ct);
        var body = artifacts.Items.FirstOrDefault(a => a.Kind == ContentArtifactKind.Body);
        if (body is null)
        {
            return null;
        }

        await using var stream = await _contentStore.OpenReadAsync(body.StorageKey, ct);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();

        var timing = ParseTiming(url.TimingJson);
        var fetch = new HttpFetchResult(
            url.CanonicalUrl,
            url.FinalStatusCode ?? 200,
            timing.Headers,
            bytes,
            timing.Ttfb,
            timing.Total);
        var document = _parser.Parse(System.Text.Encoding.UTF8.GetString(bytes), url.CanonicalUrl);
        return new PageAuditContext(auditId, url, configuration, fetch, document, null, null, null);
    }

    private static (TimeSpan Ttfb, TimeSpan Total, IReadOnlyDictionary<string, string> Headers) ParseTiming(string? timingJson)
    {
        if (string.IsNullOrWhiteSpace(timingJson))
        {
            return (TimeSpan.Zero, TimeSpan.Zero, new Dictionary<string, string>());
        }

        try
        {
            using var document = JsonDocument.Parse(timingJson);
            var root = document.RootElement;
            var ttfb = root.TryGetProperty("ttfbMs", out var t) ? TimeSpan.FromMilliseconds(t.GetDouble()) : TimeSpan.Zero;
            var total = root.TryGetProperty("totalMs", out var d) ? TimeSpan.FromMilliseconds(d.GetDouble()) : TimeSpan.Zero;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("headers", out var headerElement))
            {
                foreach (var property in headerElement.EnumerateObject())
                {
                    headers[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return (ttfb, total, headers);
        }
        catch (JsonException)
        {
            return (TimeSpan.Zero, TimeSpan.Zero, new Dictionary<string, string>());
        }
    }

    private static UrlRecordDto NewUrlRecord(
        long auditId, FrontierEntry entry, CrawlStatusClass statusClass, HttpFetchResult? fetch, RobotsState robots)
    {
        var uri = new Uri(entry.Url);
        var flags = IndexabilityFlags.None;
        if (statusClass != CrawlStatusClass.Success)
        {
            flags |= IndexabilityFlags.NonIndexableStatus;
        }

        if (robots == RobotsState.Disallowed)
        {
            flags |= IndexabilityFlags.RobotsBlocked;
        }

        return new UrlRecordDto(
            Id: 0,
            AuditId: auditId,
            CanonicalUrl: entry.Url,
            Host: entry.Host,
            Path: uri.AbsolutePath,
            Depth: entry.Depth,
            DiscoverySource: entry.DiscoverySource,
            StatusClass: statusClass,
            FinalStatusCode: fetch?.StatusCode,
            RedirectTargetUrl: null,
            ContentType: null,
            SizeBytes: null,
            TimingJson: null,
            RobotsState: robots,
            IndexabilityFlags: flags,
            IsIndexable: statusClass == CrawlStatusClass.Success && robots != RobotsState.Disallowed);
    }

    private static CrawlStatusClass ClassifyStatus(int statusCode, bool isChallenge) => statusCode switch
    {
        >= 200 and < 300 => isChallenge ? CrawlStatusClass.Blocked : CrawlStatusClass.Success,
        >= 300 and < 400 => CrawlStatusClass.Redirect,
        >= 400 and < 500 => CrawlStatusClass.ClientError,
        _ => CrawlStatusClass.ServerError
    };

    private static bool IsHtml(string? contentType) =>
        contentType is null || contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static string Snippet(byte[] body)
    {
        var length = Math.Min(body.Length, 4096);
        return System.Text.Encoding.UTF8.GetString(body, 0, length);
    }

    private static IReadOnlyList<string> ParseSeeds(string seedsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(seedsJson, PayloadJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AuditRunCounters ParseCounters(string countersJson)
    {
        var counters = new AuditRunCounters();
        if (string.IsNullOrWhiteSpace(countersJson) || countersJson == "{}")
        {
            return counters;
        }

        try
        {
            using var document = JsonDocument.Parse(countersJson);
            var root = document.RootElement;
            counters.Discovered = root.TryGetProperty("discovered", out var d) ? d.GetInt64() : 0;
            counters.Fetched = root.TryGetProperty("fetched", out var f) ? f.GetInt64() : 0;
            counters.Analyzed = root.TryGetProperty("analyzed", out var a) ? a.GetInt64() : 0;
            counters.Failed = root.TryGetProperty("failed", out var x) ? x.GetInt64() : 0;
        }
        catch (JsonException)
        {
            // Malformed counters degrade to zero; the run continues.
        }

        return counters;
    }

    private async Task<AuditDto> RequireAuditAsync(long auditId, CancellationToken ct) =>
        await _auditReads.GetByIdAsync(auditId, ct)
        ?? throw new InvalidOperationException($"Audit {auditId} does not exist.");

    private async Task PersistStatusAsync(long auditId, AuditStatus status, CancellationToken ct)
    {
        var audit = await RequireAuditAsync(auditId, ct);
        await _auditWrites.UpdateAsync(audit with { Status = status }, ct);
    }

    private async Task PersistPhaseAsync(long auditId, AuditPhase phase, CancellationToken ct)
    {
        var audit = await RequireAuditAsync(auditId, ct);
        await _auditWrites.UpdateAsync(audit with { Phase = phase }, ct);
    }

    private Task PublishStageAsync(long auditId, AuditStatus status, AuditPhase phase, CancellationToken ct) =>
        _hub.PublishAsync(new HubEvent(
            "stage-changed",
            JsonSerializer.Serialize(new
            {
                auditId,
                status = status.ToString().ToLowerInvariant(),
                phase = phase.ToString().ToLowerInvariant()
            }, PayloadJson)), ct);

    private async Task PublishProgressAsync(long auditId, AuditRunState state, CancellationToken ct)
    {
        // Persist counters at most twice per second so audit detail stays live
        // without a write per fetch; the hub event itself is throttled to 1/s.
        var now = DateTimeOffset.UtcNow;
        if (now - state.LastCounterPersistAt >= TimeSpan.FromMilliseconds(500))
        {
            state.LastCounterPersistAt = now;
            var audit = await _auditReads.GetByIdAsync(auditId, ct);
            if (audit is not null)
            {
                await _auditWrites.UpdateAsync(audit with
                {
                    CountersJson = JsonSerializer.Serialize(state.Counters.ToPayload(), PayloadJson)
                }, ct);
            }
        }

        await _hub.PublishAsync(new HubEvent(
            "crawl-progress",
            JsonSerializer.Serialize(new
            {
                auditId,
                counters = state.Counters.ToPayload(),
                queued = state.Frontier?.Count ?? 0
            }, PayloadJson),
            ThrottleKey: $"crawl-progress:{auditId}",
            ThrottleInterval: TimeSpan.FromSeconds(1)), ct);
    }
}
