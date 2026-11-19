using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Api.Controllers;
using CrawlForge.Api.Hubs;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.ListMode;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Traps;
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
public sealed class AuditExecutionEngine : IAuditExecutionService, IAuditLifecyclePort, IReAuditExecutionPort, IListAuditStartPort, IBackendLifecycle
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
    private readonly AuditCrawlComponents _crawl;
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
        AuditCrawlComponents crawlComponents,
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
        _crawl = crawlComponents;
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
    public Task<long> StartAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType triggerType,
        CrawlLimitOverrides? limitOverrides = null,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(projectId, configurationId, triggerType, limitOverrides, urlListJson: null, isListMode: false, cancellationToken);

    /// <summary>
    /// WP-09-11 (G10, WP-09-06 D2): list-mode start with persist-before-start.
    /// UrlListJson/IsListMode are written ATOMICALLY with the audit row so the
    /// engine loop's ListSeedResolver never races a post-create update.
    /// </summary>
    public Task<long> StartListAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType triggerType,
        CrawlLimitOverrides? limitOverrides,
        string? urlListJson,
        bool isListMode,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(projectId, configurationId, triggerType, limitOverrides, urlListJson, isListMode, cancellationToken);

    private async Task<long> StartCoreAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType triggerType,
        CrawlLimitOverrides? limitOverrides,
        string? urlListJson,
        bool isListMode,
        CancellationToken cancellationToken)
    {
        var configuration = await _configReads.GetByIdAsync(configurationId, cancellationToken)
            ?? throw new InvalidOperationException($"Configuration {configurationId} does not exist.");
        if (configuration.ProjectId != projectId)
        {
            throw new InvalidOperationException("Configuration does not belong to the project.");
        }

        // FS-CRAWL-001 §10.4 idempotency: starting an audit that is already
        // active for the same configuration is rejected idempotently — the
        // existing audit id is returned with its current state (the REST
        // surface reads the row back). Active = Queued/Running/Paused.
        var existing = await _auditReads.ListAsync(new ListQuery(
            0, 100,
            new Dictionary<string, string> { ["configurationId"] = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            SortBy: "id", SortDescending: true), cancellationToken);
        foreach (var candidate in existing.Items)
        {
            if (candidate.Status is AuditStatus.Queued or AuditStatus.Running or AuditStatus.Paused)
            {
                _logger.LogInformation(
                    "Audit start for configuration {ConfigurationId} is idempotent: audit {AuditId} is already {Status}.",
                    configurationId, candidate.Id, candidate.Status);
                return candidate.Id;
            }
        }

        var snapshot = configuration with { LimitOverrides = limitOverrides ?? configuration.LimitOverrides };
        // Freeze the DTO-shaped snapshot the engine reads back, then merge the
        // config-level enabledCategories convention (Scoring README) as an
        // additive top-level member. The REST start surface re-resolves the
        // member (config-level wins over the app-settings fallback) WITHOUT
        // changing the snapshot shape (G-REAUDIT-SNAPSHOT).
        var snapshotNode = JsonSerializer.SerializeToNode(snapshot, PayloadJson)!.AsObject();
        var configCategories = ParseConfigEnabledCategories(configuration.ScopeRulesJson);
        if (configCategories is not null)
        {
            snapshotNode["enabledCategories"] = JsonSerializer.SerializeToNode(configCategories);
        }

        var audit = new AuditDto(
            Id: 0,
            ProjectId: projectId,
            ConfigurationId: configurationId,
            ConfigurationSnapshotJson: snapshotNode.ToJsonString(),
            Status: AuditStatus.Running,
            Phase: AuditPhase.Crawling,
            StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: null,
            CountersJson: "{}",
            EngineVersion: typeof(AuditExecutionEngine).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            IsDeleted: false)
        {
            IsListMode = isListMode,
            UrlListJson = urlListJson
        };
        var auditId = (await _auditWrites.CreateAsync(audit, cancellationToken)).Id;

        var state = new AuditRunState(auditId, new CancellationTokenSource());
        _runs[auditId] = state;
        _runTasks[auditId] = Task.Run(() => RunAuditAsync(auditId, state), CancellationToken.None);
        await PublishStageAsync(auditId, AuditStatus.Running, AuditPhase.Crawling, cancellationToken);
        return auditId;
    }

    /// <summary>
    /// Reads the config-level enabledCategories preference from the stored
    /// scope-rules document (WP-09-07 §3). Absent/empty = null (all enabled).
    /// </summary>
    private static IReadOnlyList<string>? ParseConfigEnabledCategories(string? scopeRulesJson)
    {
        if (string.IsNullOrWhiteSpace(scopeRulesJson))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(scopeRulesJson);
            if (node is JsonObject obj && obj["enabledCategories"] is JsonArray array)
            {
                var names = new List<string>();
                foreach (var entry in array)
                {
                    if (entry is JsonValue value && value.TryGetValue<string>(out var name)
                        && Enum.TryParse<AuditCategory>(name, ignoreCase: true, out var category))
                    {
                        names.Add(category.ToString());
                    }
                }

                return names.Count > 0 ? names : null;
            }
        }
        catch (JsonException)
        {
            // Unreadable scope rules impose no category restriction.
        }

        return null;
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

        await PublishStageAsync(auditId, AuditStatus.Paused, audit.Phase, cancellationToken, AuditStatus.Running);
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

        await PublishStageAsync(auditId, AuditStatus.Running, audit.Phase, cancellationToken, AuditStatus.Paused);
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

        await PublishStageAsync(auditId, AuditStatus.Cancelled, audit.Phase, cancellationToken, audit.Status);
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

            state.StartedAt = audit.StartedAt;
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

            // WP-09-11: crawl-family post-crawl passes over the full URL dataset
            // (sitemap correlation, orphan detection, broken links).
            await RunPostCrawlPassesAsync(auditId, audit.ProjectId, configuration, state, ct);

            await AnalyzePhaseAsync(auditId, audit.ProjectId, configuration, state, ct);

            // WP-09-11: site-wide passes run once every per-URL pass completed
            // (D14 pattern): duplicate metadata/content, URL structure, hreflang
            // reciprocity, optional user-agent comparison.
            await RunSitePassesAsync(auditId, audit.ProjectId, configuration, state, ct);

            await ScoreAndFinalizeAsync(auditId, state, ct);
        }
        catch (OperationCanceledException)
        {
            await TryFinalizeUnlessPausedAsync(auditId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit {AuditId} failed.", auditId);
            await TryFinalizeAsync(auditId, state, AuditStatus.Failed, ex.Message);
        }
        finally
        {
            _runs.TryRemove(auditId, out _);
            _runTasks.TryRemove(auditId, out _);
            state.Dispose();
        }
    }

    /// <summary>
    /// Per-run crawl components bound to one audit (WP-09-11 wiring): the
    /// configuration-derived canonicalizer/scope/profile/jar/robots surfaces
    /// plus the admission/trap/chain components. Constructed once per crawl
    /// phase; shared by the fetch loop and link discovery.
    /// </summary>
    private sealed record CrawlRunContext(
        UrlCanonicalizer Canonicalizer,
        CrawlScopeEvaluator Scope,
        CrawlAdmissionGate Admission,
        CrawlerTrapDetector Traps,
        AuditCookieJar Jar,
        CrawlRequestProfile Profile,
        RobotsCache Robots,
        bool RespectRobots,
        string RobotsUserAgent,
        RedirectChainRecorder Chains,
        RedirectFindingAnalyzer RedirectFindings,
        HashSet<string> SessionSegmentReported,
        DirectiveHandlingOptions Directives,
        Dictionary<string, UrlCanonicalizer.CanonicalizationOutcome> SessionSegmentPending,
        ConcurrentDictionary<string, string> HostAuthorities);

    /// <summary>
    /// Translates AIMD's plain-host crawl-delay lookups into the authority-keyed
    /// robots cache (host:port), so non-default-port origins keep their
    /// crawl-delay floor while throttle events keep plain-host keys.
    /// </summary>
    private sealed class PortAwareCrawlDelaySource(
        RobotsCache cache,
        ConcurrentDictionary<string, string> hostAuthorities) : ICrawlDelaySource
    {
        public double? GetCrawlDelaySeconds(string host)
            => cache.GetCrawlDelaySeconds(hostAuthorities.TryGetValue(host, out var authority) ? authority : host);
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
        // Politeness payloads exist in two stored shapes: the REST/storage shape
        // ({"aimd":{...}}) written by ProjectWireMapper and the legacy flat shape
        // written by first-run seeding. The parser accepts both and falls back to
        // the §3.3 defaults for invalid values, so a stored row can never fail
        // the audit at start (HOTFIX-01).
        var politeness = AimdSettingsParser.Parse(configuration.PolitenessJson);
        aimdOptions = AimdControllerOptions.FromSettings(politeness.Settings, resolvedLimits);
        foreach (var note in politeness.Fallbacks)
        {
            _logger.LogWarning(
                "Audit {AuditId}: politeness configuration fallback ({Shape}) - {Note}",
                auditId,
                politeness.Shape,
                note);
        }

        // WP-09-02/07 configuration binding: the stored scope-rules document
        // drives canonicalization, scope, directive/resource toggles, cookie
        // jar, user agent, headers and cookies. Hard errors reject the run
        // (config-time validation is the REST surface's job; a stored row that
        // still fails here fails the audit with the parser's message).
        var scopeParse = CrawlScopeConfigParser.Parse(configuration.ScopeRulesJson);
        if (scopeParse.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Crawl scope configuration is invalid: " + string.Join("; ", scopeParse.Errors));
        }

        foreach (var note in scopeParse.Fallbacks)
        {
            _logger.LogWarning("Audit {AuditId}: scope configuration fallback - {Note}", auditId, note);
        }

        var behavior = ParseJsonObject(configuration.ScopeRulesJson);
        var respectRobots = behavior?["respectRobotsTxt"]?.GetValue<bool>() ?? true;
        var maxResponseBytes = behavior?["maxResponseBytes"]?.GetValue<long>() ?? 10L * 1024 * 1024;
        var virtualRobotsBody = behavior?["virtualRobotsTxt"]?.GetValue<string>();
        var scopeConfig = scopeParse.Config;
        var canonicalizer = new UrlCanonicalizer(scopeConfig.Canonicalization);
        var profile = CrawlRequestProfile.Create(scopeConfig.UserAgent, scopeConfig.Headers, scopeConfig.Cookies);
        var jar = new AuditCookieJar(scopeConfig.CookieJar);

        // AIMD observability (criterion #36): per-audit composite sink — the
        // bounded-history recorder (durable rows, flushed at checkpoints) plus
        // the coalesced hub adapter (typed host-throttle events, ≤1/s/host).
        var throttleRecorder = new HostThrottleEventRecorder(_throttleStore);
        var throttleCoalescer = new CoalescingHostThrottleSink(new HubHostThrottleSink(_hub));
        var frontier = new PriorityFrontier(new AimdPolitenessController(aimdOptions));
        state.Frontier = frontier;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedUrls = new List<string>();
        // Host → authority (host:port) map: the robots cache fetches per
        // AUTHORITY so non-default ports survive (RobotsCache strips a scheme
        // but keeps a bare authority verbatim); AIMD keeps plain-host keys, so
        // the crawl-delay adapter translates through this map.
        var hostAuthorities = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Seed hosts anchor the scope evaluator and the virtual-robots override
        // in BOTH the fresh and checkpoint-resume paths.
        foreach (var seed in ParseSeeds(configuration.SeedsJson))
        {
            if (Uri.TryCreate(seed, UriKind.Absolute, out var seedUri)
                && seedUri.Scheme is "http" or "https")
            {
                seedHosts.Add(seedUri.Host);
                seedUrls.Add(seed);
                hostAuthorities[seedUri.Host] = seedUri.Authority;
            }
        }

        var virtualRobots = new VirtualRobotsStore();
        if (!string.IsNullOrWhiteSpace(virtualRobotsBody))
        {
            foreach (var authority in hostAuthorities.Values)
            {
                virtualRobots.Set(authority, virtualRobotsBody);
            }
        }

        // Transport: one cookie-aware client per audit. The jar integrates at
        // the fetcher's IFetchHook surface (merge semantics + cookie-gate
        // AllowRevisit); CookieJarMessageHandler is the alternative
        // transport-level integration and is NOT wired simultaneously (its
        // replace semantics would drop profile cookies merged by the hook).
        // Construction lives in the approved crawl-target boundary scope
        // (NET-02): CrawlTargetTransportFactory, Crawler/Fetching.
        using var httpClient = CrawlTargetTransportFactory.Create(limits?.FetchTimeoutSeconds ?? 60);
        var fetcherOptions = new HttpFetcherOptions(
            defaultTimeoutSeconds: limits?.FetchTimeoutSeconds ?? 60,
            maxResponseBytes: maxResponseBytes,
            maxRedirectHops: 20,
            userAgent: profile.UserAgent);
        var chainRecorder = new RedirectChainRecorder(
            maxHops: 20,
            persistAsync: RedirectChainPersistence.CreatePersistHook(_crawl.ChainStore));

        // Robots cache fetches through a dedicated aimd-free fetcher (the
        // crawl-delay floor source must exist before the AIMD controller).
        // Same approved crawl-target boundary scope as the crawl transport.
        using var robotsClient = CrawlTargetTransportFactory.Create(limits?.FetchTimeoutSeconds ?? 60);
        var robotsFetcher = new HttpFetcher(fetcherOptions, robotsClient);
        var robotsScheme = seedUrls.Count > 0 && Uri.TryCreate(seedUrls[0], UriKind.Absolute, out var firstSeed)
            ? firstSeed.Scheme
            : "https";
        var robotsCache = new RobotsCache(robotsFetcher, _robotsParser, virtualRobots, new RobotsCacheOptions
        {
            Scheme = robotsScheme,
            CrawlerUserAgent = profile.UserAgent
        });

        var aimd = new AimdPolitenessController(
            aimdOptions,
            auditId: auditId,
            adjustmentSink: new CompositeHostThrottleSink(throttleRecorder, throttleCoalescer),
            crawlDelaySource: new PortAwareCrawlDelaySource(robotsCache, hostAuthorities));
        state.Aimd = aimd;
        state.AimdInitialRate = aimdOptions.InitialRate;
        state.ThrottleRecorder = throttleRecorder;
        state.ThrottleCoalescer = throttleCoalescer;
        // The frontier's politeness controller is the live per-audit AIMD.
        frontier = new PriorityFrontier(aimd);
        state.Frontier = frontier;

        var fetcher = new HttpFetcher(
            fetcherOptions, httpClient, chainRecorder, aimd,
            fetchHook: new CookieJarFetchHook(jar),
            wafChallengeEvaluator: _wafEvaluator);

        // List Mode (§10.2): the persisted list flag/URLs resolve through the
        // accepted resolver (G10 persist-before-start guarantees the row is
        // complete). The locked start wire carries no list-only/list-bounded
        // toggle; listMode=true wires LIST-ONLY (depth 0, discovery disabled —
        // the §10.2 default). List-bounded remains available through
        // IListModeIngestor.IngestAsync(allowDiscovery:true) for a future wire
        // extension (recorded interpretation).
        var listResolution = await ListSeedResolver.ResolveForAuditAsync(_provider, auditId, ct);
        ListDiscoveryGate? listGate = null;
        if (listResolution.IsListMode && listResolution.Urls.Count > 0)
        {
            foreach (var diagnostic in listResolution.Diagnostics)
            {
                _logger.LogWarning("Audit {AuditId}: list entry rejected - {Diagnostic}", auditId, diagnostic);
            }

            // Pre-crawl probe (§10.2): bounded sample through the same validator
            // pipeline as the crawl; the report is a local metric + log.
            var probe = new ListPreCrawlProbe(_crawl.PreCrawlValidator);
            var probeReport = await probe.ProbeAsync(listResolution.Urls, ct);
            _logger.LogInformation(
                "Audit {AuditId}: list pre-crawl probe sampled {Sampled} entries ({Blocked} blocked, {Invalid} invalid).",
                auditId, probeReport.ProbedCount, probeReport.BlockedCount, listResolution.InvalidCount);
            await _metricWrites.CreateAsync(new MetricSummaryDto(
                0, auditId, "list-pre-crawl-probe",
                JsonSerializer.Serialize(new { sampled = probeReport.ProbedCount, blocked = probeReport.BlockedCount, invalid = listResolution.InvalidCount }, PayloadJson),
                probeReport.ProbedCount), ct);

            var listOptions = new ListModeOptions(
                Mode: ListDiscoveryMode.ListOnly,
                BoundedDepthCeiling: maxDepth == int.MaxValue ? 1 : maxDepth,
                RewriteRules: ParseJsonList<UrlRewriteRule>(configuration.RewriteRulesJson),
                HostMappings: ParseJsonList<HostMappingRule>(configuration.HostMappingJson),
                RedirectHopLimit: 20);
            var listIngestor = new ListModeIngestor(_provider, canonicalizer, _crawl.RewriteEngine, frontier, listOptions);
            var ingestion = await listIngestor.IngestAsync(auditId, listResolution.Urls, allowDiscovery: false, ct);
            state.Counters.Discovered += ingestion.IngestedCount;
            var policy = await listIngestor.ResolvePolicyAsync(auditId, allowDiscovery: false, ct);
            listGate = new ListDiscoveryGate(policy);
            foreach (var url in policy.AllowedCanonicalUrls)
            {
                seen.Add(url);
                if (Uri.TryCreate(url, UriKind.Absolute, out var listUri))
                {
                    seedHosts.Add(listUri.Host);
                }
            }

            // User-supplied cookies apply to the list origins too (§5.1/§5.11).
            MergeUserCookies(jar, seedHosts, scopeConfig.Cookies);
        }

        var scopeEvaluator = new CrawlScopeEvaluator(scopeConfig.Scope, seedHosts, scopeConfig.Resources);
        var trapDetector = new CrawlerTrapDetector();
        var admissionGate = new CrawlAdmissionGate(listGate, trapDetector, sampleSettings: null);
        var ctx = new CrawlRunContext(
            canonicalizer, scopeEvaluator, admissionGate, trapDetector, jar, profile,
            robotsCache, respectRobots, profile.UserAgent, chainRecorder,
            new RedirectFindingAnalyzer(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            scopeConfig.Directives,
            new Dictionary<string, UrlCanonicalizer.CanonicalizationOutcome>(StringComparer.OrdinalIgnoreCase),
            hostAuthorities);

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
            state.Counters.Blocked = restored.Blocked;
            state.Counters.WafBlocked = restored.WafBlocked;
        }
        else
        {
            MergeUserCookies(jar, seedHosts, scopeConfig.Cookies);

            // Pre-seeded rows (redirect-matrix auto-seed, list ingestion above)
            // are crawl seeds: enqueue every NotCrawled row not already seen.
            // Idempotent against the seen-set (G-MATRIX-SEED-RACE crawl side).
            var preSeeded = await _urlReads.ListAsync(new ListQuery(
                0, DatasetPageSize,
                new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["statusClass"] = ((int)CrawlStatusClass.NotCrawled).ToString(System.Globalization.CultureInfo.InvariantCulture)
                }), ct);
            foreach (var row in preSeeded.Items)
            {
                if (!seen.Add(row.CanonicalUrl))
                {
                    continue;
                }

                var entry = new FrontierEntry(row.CanonicalUrl, row.Host, row.Depth, 1.0, row.DiscoverySource);
                if (!admissionGate.Admit(entry).Admit)
                {
                    continue;
                }

                await frontier.EnqueueAsync(entry, ct);
                state.Counters.Discovered++;
            }

            foreach (var seed in ParseSeeds(configuration.SeedsJson))
            {
                var outcome = canonicalizer.CanonicalizeDetailed(seed);
                var canonical = outcome.CanonicalUrl;
                if (outcome.RemovedSessionSegments.Count > 0)
                {
                    ctx.SessionSegmentPending.TryAdd(canonical, outcome);
                }

                if (!seen.Add(canonical))
                {
                    continue;
                }

                var host = new Uri(canonical).Host;
                seedHosts.Add(host);
                var entry = new FrontierEntry(canonical, host, 0, 1.0, DiscoverySource.Seed);
                if (!admissionGate.Admit(entry).Admit)
                {
                    continue;
                }

                await frontier.EnqueueAsync(entry, ct);
                state.Counters.Discovered++;
            }

            // Sitemap discovery + ingestion (§5.5): list-only audits disable
            // sitemap expansion (§10.2); full runs seed sitemap URLs at depth 0
            // through the same admission path as any other discovery.
            if (!listResolution.IsListMode && seedUrls.Count > 0)
            {
                try
                {
                    var sitemapIngestor = new SitemapIngestor(fetcher, _robotsParser, canonicalizer);
                    var sitemaps = await sitemapIngestor.IngestAsync(
                        new SitemapIngestionRequest(auditId, seedUrls), ct);
                    foreach (var diagnostic in sitemaps.Diagnostics)
                    {
                        _logger.LogDebug("Audit {AuditId}: sitemap ingestion - {Diagnostic}", auditId, diagnostic);
                    }

                    foreach (var seeded in sitemaps.Urls)
                    {
                        state.SitemapCanonicalUrls.Add(seeded.CanonicalUrl);
                        if (!seen.Add(seeded.CanonicalUrl))
                        {
                            continue;
                        }

                        var entry = new FrontierEntry(seeded.CanonicalUrl, seeded.Host, 0, 1.0, DiscoverySource.Sitemap);
                        if (!admissionGate.Admit(entry).Admit)
                        {
                            continue;
                        }

                        await frontier.EnqueueAsync(entry, ct);
                        state.Counters.Discovered++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Audit {AuditId}: sitemap ingestion failed; the crawl continues.", auditId);
                }
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

            // Robots evaluation through the per-host cache (virtual overrides,
            // TTL, crawl-delay floor via the AIMD delay source). A disallowed
            // URL is recorded NotCrawled/Disallowed with a robots-blocked
            // finding; it is NOT a WAF block (block counters stay WAF-only).
            var robotsState = RobotsState.Allowed;
            RobotsDocument? robotsDocument = null;
            RobotsVerdict? robotsVerdict = null;
            if (ctx.RespectRobots)
            {
                // Fetch per authority (port preserved). An unreachable robots
                // endpoint is permissive (RFC 9309 §2.3.1.3: unavailable →
                // allow); the failure never aborts the audit.
                var entryUri = new Uri(entry.Url);
                ctx.HostAuthorities[entryUri.Host] = entryUri.Authority;
                try
                {
                    robotsDocument = await robotsCache.GetDocumentAsync(entryUri.Authority, ct);
                    robotsVerdict = _robotsParser.Evaluate(robotsDocument, entry.Url, ctx.RobotsUserAgent);
                    if (!robotsVerdict.Allowed)
                    {
                        robotsState = RobotsState.Disallowed;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "robots.txt unavailable for {Authority}; treating as permissive.", entryUri.Authority);
                }
            }

            // Pre-create the URL row (NotCrawled) so redirect hops persist
            // against a durable urlId during the fetch (WP-09-01 recipe).
            // Pre-seeded rows (matrix/list) are reused — never duplicated.
            var urlId = await GetOrCreateUrlRowAsync(auditId, entry, ct);

            // CR-009: report removed cookieless session segments against the
            // canonical target row (deferred from discovery to first fetch so
            // the finding carries a durable urlId). Dedup per canonical URL.
            if (ctx.SessionSegmentPending.Remove(entry.Url, out var segmentOutcome)
                && ctx.SessionSegmentReported.Add(entry.Url))
            {
                var segmentFinding = CanonicalizationFindingFactory.CreateSessionSegmentFinding(urlId, segmentOutcome);
                if (segmentFinding is not null)
                {
                    // CR-009 reporting: the finding wire message projects from
                    // evidence.message (AuditWireMapper.ProjectEvidence). Name
                    // the normalization in the notice so the report says what
                    // canonicalization did, not just the rule key.
                    var segmentEvidence = System.Text.Json.Nodes.JsonNode.Parse(segmentFinding.EvidenceJson)!.AsObject();
                    segmentEvidence["message"] =
                        "Cookieless session segment removed during URL normalization; the canonical URL is stored without the session identifier.";
                    await RecordCrawlDraftsAsync(auditId, projectId,
                        [segmentFinding with { EvidenceJson = segmentEvidence.ToJsonString() }], ct);
                }
            }

            if (robotsState == RobotsState.Disallowed)
            {
                await _urlWrites.UpdateAsync(NewUrlRecord(auditId, entry, CrawlStatusClass.NotCrawled, null, RobotsState.Disallowed) with { Id = urlId }, ct);
                if (robotsVerdict is not null && robotsDocument is not null)
                {
                    var robotsFinding = RobotsBlockFindingFactory.Create(
                        urlId, entry.Url, robotsVerdict, robotsDocument, ctx.RobotsUserAgent,
                        $"{new Uri(entry.Url).GetLeftPart(UriPartial.Authority)}/robots.txt");
                    await RecordCrawlDraftsAsync(auditId, projectId, [robotsFinding], ct);
                }

                continue;
            }

            // Politeness is owned by the frontier: PriorityFrontier.DequeueAsync
            // already awaits the AIMD rate decision for the entry's host (the
            // accepted §5.7 design). A second wait here would double every
            // spacing interval and starve bounded crawls under challenge decay.
            HttpFetchResult? fetch = null;
            FetchFailedException? fetchFailure = null;
            RedirectChainException? chainViolation = null;
            FetchContextScope.Current = new FetchContext(auditId, urlId);
            try
            {
                fetch = await fetcher.FetchAsync(ctx.Profile.Apply(new HttpFetchRequest(entry.Url)), ct);
            }
            catch (RedirectChainException ex)
            {
                // The durable recorder raises its own violation exception when
                // a hop closes a loop or exceeds the chain cap (the fetcher's
                // visited-URL guard raises FetchFailedException only when no
                // recorder context is set). Both keep the Redirect class.
                chainViolation = ex;
                state.Counters.Failed++;
                _logger.LogDebug(ex, "Redirect chain violation for {Url} ({Reason}).", entry.Url, ex.Reason);
            }
            catch (FetchFailedException ex)
            {
                fetchFailure = ex;
                state.Counters.Failed++;
                _logger.LogDebug(ex, "Fetch failed for {Url} ({Kind}).", entry.Url, ex.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Counters.Failed++;
                _logger.LogDebug(ex, "Fetch failed for {Url}.", entry.Url);
            }
            finally
            {
                FetchContextScope.Current = null;
            }

            admissionGate.RecordFetched();
            var chain = await chainRecorder.GetChainAsync(auditId, urlId, ct);

            if (fetch is null)
            {
                // Transport/policy failure: redirect-loop and chain-cap failures
                // keep the Redirect class with the observed hop prefix; other
                // failures are FetchError. No url-batch entry (no observed status).
                var failureOutcome = chainViolation is not null
                    ? FetchOutcomeClassifier.ClassifyViolation(entry.Url, chainViolation.Reason, chain)
                    : fetchFailure is not null
                        ? FetchOutcomeClassifier.ClassifyFailure(entry.Url, fetchFailure, chain)
                        : FetchOutcomeClassifier.ClassifyViolation(entry.Url, null, chain);
                await _urlWrites.UpdateAsync(NewUrlRecord(auditId, entry, failureOutcome.StatusClass, null, RobotsState.Allowed) with
                {
                    Id = urlId,
                    FinalStatusCode = failureOutcome.SourceStatusCode,
                    // G27 storage lock: redirect_target_url is the FINAL target
                    // of the observed chain, never the first hop (hop-by-hop
                    // facts live in the durable chain payload).
                    RedirectTargetUrl = failureOutcome.StatusClass == CrawlStatusClass.Redirect ? failureOutcome.FinalUrl : null
                }, ct);

                if (chainViolation is not null)
                {
                    var violationFinding = ctx.RedirectFindings.AnalyzeViolation(auditId, urlId, entry.Url, chainViolation, chain);
                    await RecordCrawlFindingsAsync(auditId, projectId, [violationFinding], ct);
                    await SaveChainOutcomeAsync(urlId, chain, null, null,
                        chainViolation.Reason == RedirectChainViolation.Loop,
                        chainViolation.Reason == RedirectChainViolation.ChainCapExceeded, ct);
                }
                else if (fetchFailure is not null)
                {
                    var failureFinding = ctx.RedirectFindings.AnalyzeFetchFailure(auditId, urlId, entry.Url, fetchFailure, chain);
                    if (failureFinding is not null)
                    {
                        await RecordCrawlFindingsAsync(auditId, projectId, [failureFinding], ct);
                    }

                    if (chain.Count > 0)
                    {
                        await SaveChainOutcomeAsync(urlId, chain, null, null,
                            fetchFailure.Kind == FetchFailureKind.RedirectLoop,
                            fetchFailure.Kind == FetchFailureKind.RedirectChainExceeded, ct);
                    }
                }

                continue;
            }

            state.Counters.Fetched++;

            var bodyBytes = fetch.Body.ToArray();
            var waf = _wafEvaluator.Evaluate(new WafEvaluationInput(
                entry.Url, fetch.StatusCode, fetch.Headers,
                bodyBytes.Length > 0 ? Snippet(bodyBytes) : null));
            // G27/WP-09-01: the source record carries its OWN status (first hop
            // when redirected); terminal facts travel in the chain payload.
            var outcome = FetchOutcomeClassifier.Classify(entry.Url, fetch, chain, waf.IsChallenge);
            var statusClass = outcome.StatusClass;
            var sourceStatusCode = outcome.SourceStatusCode;

            // Cookie-gate self-redirect: when the chain resolves back to the
            // source URL, the redirect WAS followed to a terminal endpoint and
            // the source's own terminal response was observed — the row carries
            // the terminal status/class (CrawlStatusClass.Redirect is documented
            // as "not followed to a terminal endpoint"; probe-2 measured the
            // gate row at 200). The 302 hop stays in the durable chain payload.
            if (outcome.IsRedirectSource
                && string.Equals(outcome.FinalUrl, entry.Url, StringComparison.OrdinalIgnoreCase))
            {
                sourceStatusCode = outcome.FinalStatusCode ?? fetch.StatusCode;
                statusClass = FetchStatusClassifier.Classify(sourceStatusCode.Value);
            }

            // CR-024: indexable = SEARCH-indexable (final 200, robots-allowed,
            // no noindex, self/absent canonical). The evaluator parses the body
            // once when HTML; redirect sources are never indexable.
            var indexability = IndexabilityStateEvaluator.EvaluateFetch(entry.Url, fetch, RobotsState.Allowed, _parser);
            var isIndexable = statusClass == CrawlStatusClass.Success && indexability.IsIndexable;

            if (statusClass == CrawlStatusClass.Blocked)
            {
                state.Counters.Blocked++;
            }

            if (waf.IsChallenge)
            {
                state.Counters.WafBlocked++;
            }

            var urlRecord = NewUrlRecord(auditId, entry, statusClass, fetch, RobotsState.Allowed) with
            {
                Id = urlId,
                FinalStatusCode = sourceStatusCode,
                // G27 storage lock: the source row keeps its OWN status (first
                // hop) but redirect_target_url is the FINAL chain target; the
                // intermediate hops live in the durable chain payload only.
                RedirectTargetUrl = statusClass == CrawlStatusClass.Redirect ? outcome.FinalUrl : null,
                ContentType = fetch.Headers.TryGetValue("content-type", out var contentType) ? contentType : null,
                SizeBytes = bodyBytes.LongLength,
                TimingJson = JsonSerializer.Serialize(new
                {
                    ttfbMs = fetch.Ttfb.TotalMilliseconds,
                    totalMs = fetch.TotalDuration.TotalMilliseconds,
                    headers = fetch.Headers,
                    finalUrl = outcome.FinalUrl,
                    finalStatusCode = outcome.FinalStatusCode
                }, PayloadJson),
                IndexabilityFlags = indexability.Flags | (statusClass != CrawlStatusClass.Success ? IndexabilityFlags.NonIndexableStatus : IndexabilityFlags.None),
                IsIndexable = isIndexable,
                BlockReason = waf.IsChallenge ? "waf-challenge" : null,
                WafProvider = waf.IsChallenge ? waf.Provider : null,
                ChallengeClass = waf.IsChallenge ? waf.ChallengeClass : null
            };
            await _urlWrites.UpdateAsync(urlRecord, ct);

            // G05 url-batch: one entry per URL with an OBSERVED HTTP status
            // (WAF challenges carry the observed class + block annotations per
            // Appendix C). Transport-failure/robots rows are excluded — the
            // publisher validates 1xx..5xx classes.
            if (outcome.SourceStatusCode is { } observedStatus)
            {
                state.PendingUrlBatch.Add(new UrlBatchEntry(
                    urlId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entry.Url,
                    HttpStatusClassWire(observedStatus),
                    waf.IsChallenge ? "waf-challenge" : null,
                    waf.IsChallenge ? ApiEnumConverter<WafProvider>.ToWireValue(waf.Provider) : null));
            }

            // Persist the WAF verdict through the accepted recording surface:
            // corroborated challenges get a detection row + per-URL finding;
            // sub-threshold/single-facet hits get a suspected-challenge Notice
            // (never a block). Detection/reporting only (exclusion E3).
            if (waf.IsChallenge)
            {
                await _wafRecorder.RecordChallengeAsync(auditId, urlId, waf, ct);
                state.PendingWafBlocks.Add(new WafBlockBatchEntry(
                    urlId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entry.Url,
                    ApiEnumConverter<WafProvider>.ToWireValue(waf.Provider),
                    ApiEnumConverter<WafChallengeClass>.ToWireValue(waf.ChallengeClass),
                    WafConfidence(waf)));
            }
            else if (waf.MatchedSignatureKeys.Count > 0)
            {
                await _wafRecorder.RecordSuspectedNoticeAsync(auditId, urlId, waf, ct);
            }

            // Redirect findings + durable chain outcome (terminal facts, flags).
            if (chain.Count > 0)
            {
                var redirectFindings = ctx.RedirectFindings.AnalyzeChain(
                    auditId, urlId, entry.Url, chain, outcome.FinalUrl, outcome.FinalStatusCode);
                if (redirectFindings.Count > 0)
                {
                    await RecordCrawlFindingsAsync(auditId, projectId, redirectFindings, ct);
                }

                // Cookieless redirect-loop diagnostic (§5.11): when the jar is
                // off and the site gated behind Set-Cookie + self-redirect, the
                // loop becomes an actionable finding instead of a bare failure.
                var setCookieNames = ObservedSetCookieNames(fetch.Headers);
                var loopFinding = CookielessRedirectLoopDiagnostic.Create(
                    urlId, entry.Url, chain, setCookieNames, jar.Enabled);
                if (loopFinding is not null)
                {
                    await RecordCrawlDraftsAsync(auditId, projectId, [loopFinding], ct);
                }

                await SaveChainOutcomeAsync(urlId, chain, outcome.FinalUrl, outcome.FinalStatusCode,
                    CookielessRedirectLoopDiagnostic.TryFindLoop(chain, out _), chainCapExceeded: false, ct);
            }

            // Body store: success bodies AND challenge bodies (render-resolution
            // and offline re-audit consume the stored challenge page).
            if (bodyBytes.Length > 0 && (statusClass == CrawlStatusClass.Success || waf.IsChallenge))
            {
                using var bodyStream = new MemoryStream(bodyBytes);
                await _contentStore.PutAsync(auditId, urlId, ContentArtifactKind.Body, bodyStream, ct);
            }

            // Link discovery over the stored body (configured scope).
            DomDocument? document = null;
            if (statusClass == CrawlStatusClass.Success && IsHtml(urlRecord.ContentType))
            {
                document = _parser.Parse(System.Text.Encoding.UTF8.GetString(bodyBytes), entry.Url);
                await DiscoverLinksAsync(auditId, urlId, entry, document, seen, frontier, state, maxDepth, ctx, ct);

                // Crawler-trap content observation (§5.14): normalized visible
                // text hash per fetched page; detections drain at crawl end.
                ctx.Traps.ObserveContent(entry.Url, CrawlerTrapDetector.HashText(NormalizedVisibleText(document)));
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

        // Crawl-end: flush every accumulated run event (lossless), drain trap
        // detections into site-wide findings, and log throttle signals (the
        // detector already throttled admission of flagged patterns).
        await FlushRunEventsAsync(auditId, state, force: true, ct);
        var detections = ctx.Traps.DrainDetections();
        if (detections.Count > 0)
        {
            await CrawlerTrapFindings.PersistAsync(_provider, auditId, detections, ct);
        }

        foreach (var signal in ctx.Traps.DrainThrottleSignals())
        {
            _logger.LogInformation(
                "Audit {AuditId}: crawler-trap pattern {Pattern} on {Host} throttled ({UrlCount} URLs, budget {Budget}).",
                auditId, signal.PatternKey, signal.Host, signal.UrlCount, signal.Budget);
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

    /// <summary>
    /// WP-09-11: crawl-family post-crawl passes over the completed URL dataset.
    /// Each pass is failure-isolated (FS-CRAWL-002 §4.2): a pass failure never
    /// fails the audit. Findings persist through the crawl recorder with
    /// project suppression stamping. <paramref name="offlineSourceAuditId"/>
    /// switches the pass to the §7.2 stored-artifacts mode (offline re-audit):
    /// external broken-link probes are never re-fetched — the internal analysis
    /// re-runs over the copied dataset and the source audit's stored
    /// external-broken findings are copied as facts.
    /// </summary>
    private async Task RunPostCrawlPassesAsync(
        long auditId, long projectId, CrawlConfigurationDto configuration, AuditRunState state, CancellationToken ct,
        long? offlineSourceAuditId = null)
    {
        // Sitemap-vs-crawl correlation (§5.5): facts projected from url_records
        // + the durable chain store + declared canonical targets (link edges).
        if (state.SitemapCanonicalUrls.Count > 0)
        {
            try
            {
                var canonicalTargets = await LoadCanonicalTargetsAsync(auditId, ct);
                var facts = new List<SitemapUrlCrawlFact>();
                var offset = 0;
                while (true)
                {
                    var urls = await _urlReads.ListAsync(new ListQuery(
                        offset, DatasetPageSize,
                        new Dictionary<string, string> { ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct);
                    foreach (var url in urls.Items)
                    {
                        var chainPayload = await _crawl.ChainStore.GetPayloadAsync(url.Id, ct);
                        var chain = chainPayload is null ? [] : RedirectChainJson.ToHops(chainPayload);
                        facts.Add(new SitemapUrlCrawlFact(
                            url.CanonicalUrl,
                            url.Id,
                            WasCrawled: url.StatusClass is CrawlStatusClass.Success or CrawlStatusClass.Redirect
                                or CrawlStatusClass.ClientError or CrawlStatusClass.ServerError or CrawlStatusClass.Blocked,
                            url.StatusClass,
                            url.FinalStatusCode,
                            url.RobotsState,
                            url.IndexabilityFlags,
                            canonicalTargets.GetValueOrDefault(url.Id),
                            chain));
                    }

                    if (urls.Items.Count < DatasetPageSize)
                    {
                        break;
                    }

                    offset += DatasetPageSize;
                }

                var correlation = new SitemapCorrelationAnalyzer();
                var findings = correlation.Analyze(auditId, state.SitemapCanonicalUrls, facts);
                await RecordCrawlFindingsAsync(auditId, projectId, findings, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sitemap correlation failed for audit {AuditId}; the audit continues.", auditId);
            }
        }

        // Orphan detection (§15.3): no internal in-links and not an entry point.
        // The accepted analyzer streams every url row — external status-check
        // rows (CR-008) are not site orphans; keep internal-host findings only.
        try
        {
            var orphans = await _crawl.OrphanAnalyzer.AnalyzeAsync(auditId, state.SitemapCanonicalUrls, null, ct);
            var orphanScope = CrawlScopeConfigParser.Parse(configuration.ScopeRulesJson);
            var orphanSeedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seed in ParseSeeds(configuration.SeedsJson))
            {
                if (Uri.TryCreate(seed, UriKind.Absolute, out var seedUri))
                {
                    orphanSeedHosts.Add(seedUri.Host);
                }
            }
            var orphanEvaluator = new CrawlScopeEvaluator(
                orphanScope.Config.Scope, orphanSeedHosts, orphanScope.Config.Resources);
            var internalOrphans = orphans.Where(finding =>
            {
                try
                {
                    using var evidence = JsonDocument.Parse(finding.EvidenceJson);
                    var url = evidence.RootElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    return url is not null && orphanEvaluator.Evaluate(url).IsInternalHost;
                }
                catch (JsonException)
                {
                    // Unparseable evidence keeps the finding (fail visible).
                    return true;
                }
            }).ToList();
            await RecordCrawlFindingsAsync(auditId, projectId, internalOrphans, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Orphan detection failed for audit {AuditId}; the audit continues.", auditId);
        }

        // Broken links (§15.4): internal error targets + bounded external probes.
        // Offline re-audit (§7.2): internal analysis only; external-broken
        // findings are stored facts copied from the source audit, never re-probed.
        try
        {
            var scopeParse = CrawlScopeConfigParser.Parse(configuration.ScopeRulesJson);
            using var probeClient = Composition.CrawlTargetHttpClientFactory.Create(TimeSpan.FromSeconds(15));
            var probeFetcher = new HttpFetcher(
                new HttpFetcherOptions(defaultTimeoutSeconds: 15, maxRetryAttempts: 0), probeClient);
            var brokenLinks = new BrokenLinkAnalyzer(_provider, probeFetcher);
            var result = await brokenLinks.AnalyzeAsync(auditId, new BrokenLinkOptions(
                CheckExternalLinks: offlineSourceAuditId is null && scopeParse.Config.Scope.CheckExternalLinks), ct);
            await RecordCrawlFindingsAsync(auditId, projectId, result.Findings, ct);
            await StampExternalProbeRowsAsync(auditId, result.Findings, ct);
            foreach (var diagnostic in result.Diagnostics)
            {
                _logger.LogDebug("Audit {AuditId}: broken-link analysis - {Diagnostic}", auditId, diagnostic);
            }

            await _metricWrites.CreateAsync(new MetricSummaryDto(
                0, auditId, "broken-link-probe",
                JsonSerializer.Serialize(new { externalChecked = result.ExternalChecked, externalBroken = result.ExternalBroken }, PayloadJson),
                result.ExternalChecked), ct);

            if (offlineSourceAuditId is { } sourceAuditId)
            {
                await CopyExternalBrokenLinkFindingsAsync(sourceAuditId, auditId, projectId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Broken-link analysis failed for audit {AuditId}; the audit continues.", auditId);
        }

        await FlushRunEventsAsync(auditId, state, force: true, ct);
    }

    /// <summary>
    /// Declared canonical targets per source URL (rel=canonical link edges),
    /// loaded in one bounded pass for sitemap correlation facts.
    /// </summary>
    private async Task<Dictionary<long, string>> LoadCanonicalTargetsAsync(long auditId, CancellationToken ct)
    {
        var targets = new Dictionary<long, string>();
        await using var connection = await _provider.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `source_url_id`, `target_canonical_url` FROM `link_edges` " +
            "WHERE `audit_id` = @auditId AND (`relation_flags` & 64) <> 0 AND `target_canonical_url` IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            targets.TryAdd(reader.GetInt64(0), reader.GetString(1));
        }

        return targets;
    }

    /// <summary>
    /// §7.2 stored-artifacts parity: copies the source audit's external
    /// broken-link findings (site-wide, urlId null) into the offline re-audit
    /// through the same suppression-stamping recorder the live pass uses.
    /// External probes are never re-fetched offline.
    /// </summary>
    private async Task CopyExternalBrokenLinkFindingsAsync(
        long sourceAuditId, long newAuditId, long projectId, CancellationToken ct)
    {
        var copied = new List<FindingDto>();
        await using var connection = await _provider.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `category`, `severity`, `evidence_json`, `recommendation_key` FROM `findings` " +
            "WHERE `audit_id` = @source AND `rule_key` = @rule ORDER BY `id`";
        var sourceParameter = command.CreateParameter();
        sourceParameter.ParameterName = "@source";
        sourceParameter.Value = sourceAuditId;
        command.Parameters.Add(sourceParameter);
        var ruleParameter = command.CreateParameter();
        ruleParameter.ParameterName = "@rule";
        ruleParameter.Value = LinkRuleKeys.BrokenExternal;
        command.Parameters.Add(ruleParameter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            copied.Add(new FindingDto(
                0, newAuditId, LinkRuleKeys.BrokenExternal,
                (AuditCategory)reader.GetInt32(0),
                (FindingSeverity)reader.GetInt32(1),
                null,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                false));
        }

        await RecordCrawlFindingsAsync(newAuditId, projectId, copied, ct);
    }

    /// <summary>
    /// WP-09-11: site-wide passes after every per-URL pass (D14 pattern).
    /// Failure-isolated; audit-family findings persist through the suppression-
    /// aware FindingEmitter, crawl-family through the crawl recorder.
    /// <paramref name="offline"/> skips the §5.15 user-agent comparison — it
    /// fetches live samples and the §7.2 offline re-audit never re-fetches.
    /// </summary>
    private async Task RunSitePassesAsync(
        long auditId, long projectId, CrawlConfigurationDto configuration, AuditRunState state, CancellationToken ct,
        bool offline = false)
    {
        // Duplicate title/description groups (§7.6).
        try
        {
            var drafts = await _crawl.DuplicateMetadata.AnalyzeSiteAsync(auditId, ct);
            if (drafts.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, drafts, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Duplicate-metadata pass failed for audit {AuditId}; the audit continues.", auditId);
        }

        // Exact/near duplicate content (§7.7), honoring the configured scope.
        try
        {
            var drafts = await _crawl.DuplicateContent.AnalyzeSiteAsync(auditId, configuration.ContentScope, ct);
            if (drafts.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, drafts, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Duplicate-content pass failed for audit {AuditId}; the audit continues.", auditId);
        }

        // URL structure site pass (§7.10).
        try
        {
            var drafts = await _crawl.UrlStructure.AnalyzeSiteAsync(auditId, ct);
            if (drafts.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, drafts, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "URL-structure pass failed for audit {AuditId}; the audit continues.", auditId);
        }

        // Hreflang reciprocity (§7.13): the per-URL rule host collected each
        // page's hreflang set; the site pass completes the reciprocity graph.
        try
        {
            var result = await _crawl.HreflangReciprocity.RunSitePassAsync(auditId, ct);
            if (result.Findings.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, result.Findings, ct);
            }

            await _metricWrites.CreateAsync(new MetricSummaryDto(
                0, auditId, "hreflang-reciprocity",
                JsonSerializer.Serialize(new
                {
                    pageCount = result.PageCount,
                    annotatedPageCount = result.AnnotatedPageCount,
                    missingReturnCount = result.MissingReturnCount
                }, PayloadJson),
                result.MissingReturnCount), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hreflang reciprocity pass failed for audit {AuditId}; the audit continues.", auditId);
        }
        finally
        {
            _crawl.HreflangReciprocity.DiscardAudit(auditId);
        }

        // Optional user-agent comparison (§5.15): deterministic sample fetched
        // as Googlebot Smartphone and as a Chrome browser; material differences
        // are reported (detection only — never evasion, E3). Live audits only —
        // the offline re-audit never re-fetches (§7.2).
        if (!offline)
        {
            await RunUaComparisonPassAsync(auditId, projectId, configuration, ct);
        }

        await FlushRunEventsAsync(auditId, state, force: true, ct);
    }

    /// <summary>
    /// §5.15 user-agent comparison over a deterministic ordinal sample of the
    /// audit's successfully crawled URLs. Off by default; failure-isolated.
    /// </summary>
    private async Task RunUaComparisonPassAsync(
        long auditId, long projectId, CrawlConfigurationDto configuration, CancellationToken ct)
    {
        var scopeParse = CrawlScopeConfigParser.Parse(configuration.ScopeRulesJson);
        var options = scopeParse.Config.UserAgentComparison;
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            var canonicalUrls = new List<string>();
            var offset = 0;
            while (true)
            {
                var urls = await _urlReads.ListAsync(new ListQuery(
                    offset, DatasetPageSize,
                    new Dictionary<string, string>
                    {
                        ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["statusClass"] = ((int)CrawlStatusClass.Success).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }), ct);
                canonicalUrls.AddRange(urls.Items.Select(u => u.CanonicalUrl));
                if (urls.Items.Count < DatasetPageSize)
                {
                    break;
                }

                offset += DatasetPageSize;
            }

            var sample = UserAgentComparisonEvaluator.SelectSample(canonicalUrls, options);
            if (sample.Count == 0)
            {
                return;
            }

            using var client = Composition.CrawlTargetHttpClientFactory.Create(TimeSpan.FromSeconds(30));
            var fetcher = new HttpFetcher(new HttpFetcherOptions(), client);
            var findings = new List<FindingDraft>();
            foreach (var url in sample)
            {
                ct.ThrowIfCancellationRequested();
                var urlRow = await _urlReads.ListAsync(new ListQuery(
                    0, 1,
                    new Dictionary<string, string>
                    {
                        ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["canonicalUrl"] = url
                    }), ct);
                if (urlRow.Items.Count == 0)
                {
                    continue;
                }

                var googlebot = await ObserveUaFetchAsync(fetcher, url, UserAgentProfiles.GooglebotSmartphone, ct);
                var chrome = await ObserveUaFetchAsync(fetcher, url, UserAgentProfiles.ChromeBrowser, ct);
                if (googlebot is null || chrome is null)
                {
                    continue;
                }

                var result = UserAgentComparisonEvaluator.Compare(googlebot, chrome);
                var finding = UserAgentComparisonFindingFactory.Create(urlRow.Items[0].Id, result);
                if (finding is not null)
                {
                    findings.Add(finding);
                }
            }

            if (findings.Count > 0)
            {
                await _findings.EmitAsync(auditId, projectId, findings, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "User-agent comparison failed for audit {AuditId}; the audit continues.", auditId);
        }
    }

    /// <summary>
    /// One §5.15 identity observation: fetch with an explicit user-agent header
    /// and project status/redirect/meta-robots/canonical/content-hash/links.
    /// Returns null when the fetch fails (the URL is skipped, not compared).
    /// </summary>
    private async Task<UaFetchObservation?> ObserveUaFetchAsync(
        HttpFetcher fetcher, string url, string userAgent, CancellationToken ct)
    {
        HttpFetchResult fetch;
        try
        {
            var request = new HttpFetchRequest(url)
            {
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = userAgent }
            };
            fetch = await fetcher.FetchAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "UA comparison fetch failed for {Url} ({UserAgent}).", url, userAgent);
            return null;
        }

        var bytes = fetch.Body.ToArray();
        var contentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var directives = new List<string>();
        string? canonical = null;
        var links = new List<string>();

        var contentType = fetch.Headers.TryGetValue("content-type", out var type) ? type : null;
        if (IsHtml(contentType))
        {
            var document = _parser.Parse(System.Text.Encoding.UTF8.GetString(bytes), fetch.FinalUrl);
            foreach (var node in EnumerateNodes(document.Root))
            {
                if (node.TagName == "meta"
                    && node.Attributes.TryGetValue("name", out var name)
                    && string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase)
                    && node.Attributes.TryGetValue("content", out var content))
                {
                    directives.AddRange(content
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.ToLowerInvariant()));
                }
                else if (node.TagName == "link"
                    && node.Attributes.TryGetValue("rel", out var rel)
                    && rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("canonical", StringComparer.OrdinalIgnoreCase)
                    && node.Attributes.TryGetValue("href", out var href)
                    && Uri.TryCreate(new Uri(fetch.FinalUrl), href, out var canonicalUri))
                {
                    canonical ??= canonicalUri.ToString();
                }
                else if (node.TagName == "a"
                    && node.Attributes.TryGetValue("href", out var anchorHref)
                    && Uri.TryCreate(new Uri(fetch.FinalUrl), anchorHref, out var anchorUri)
                    && anchorUri.Scheme is "http" or "https")
                {
                    links.Add(_canonicalizer.Canonicalize(anchorUri.ToString()));
                }
            }
        }

        return new UaFetchObservation(
            url, fetch.StatusCode, fetch.FinalUrl, directives, canonical, contentHash, links);
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
                    // Offline sitemap-correlation parity: the stored
                    // sitemap-discovered rows project the source audit's
                    // sitemap-declared set (§7.2 stored-artifacts rule).
                    if (url.DiscoverySource == DiscoverySource.Sitemap)
                    {
                        state.SitemapCanonicalUrls.Add(url.CanonicalUrl);
                    }
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

            // WP-09-11 score parity: the offline re-audit runs the SAME
            // post-crawl + site-wide passes as the live audit over the copied
            // dataset (§7.2: no re-fetch — external probes are stored facts,
            // UA comparison is live-only). Without these the live audit's
            // crawl-family/site findings would be missing offline and the
            // overall scores would diverge.
            await RunPostCrawlPassesAsync(newAuditId, audit.ProjectId, configuration, state, ct, sourceAuditId);

            await AnalyzePhaseAsync(newAuditId, audit.ProjectId, configuration, state, ct);
            await RunSitePassesAsync(newAuditId, audit.ProjectId, configuration, state, ct, offline: true);
            await ScoreAndFinalizeAsync(newAuditId, state, ct);
        }
        catch (OperationCanceledException)
        {
            await TryFinalizeUnlessPausedAsync(newAuditId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-audit {AuditId} failed.", newAuditId);
            await TryFinalizeAsync(newAuditId, state, AuditStatus.Failed, ex.Message);
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

    private async Task FinalizeAsync(
        long auditId,
        AuditRunState state,
        AuditStatus status,
        CancellationToken ct,
        string? errorMessage = null)
    {
        var audit = await RequireAuditAsync(auditId, ct);
        var previousState = audit.Status;
        if (audit.Status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed)
        {
            status = audit.Status;
        }

        // HOTFIX-01: keep the failure reason with the audit so REST/UI can show
        // "failed" plus the message even when the hub event arrives early.
        var counters = JsonSerializer.SerializeToNode(state.Counters.ToPayload(), PayloadJson) as JsonObject ?? [];
        if (status == AuditStatus.Failed && !string.IsNullOrWhiteSpace(errorMessage))
        {
            counters["errorMessage"] = errorMessage;
        }

        await PersistPhaseAsync(auditId, AuditPhase.Finalizing, ct);
        await _auditWrites.UpdateAsync(audit with
        {
            Status = status,
            Phase = AuditPhase.Finalizing,
            FinishedAt = DateTimeOffset.UtcNow,
            CountersJson = counters.ToJsonString()
        }, ct);
        await PublishStageAsync(auditId, status, AuditPhase.Finalizing, ct, previousState);

        // G05: drain every accumulated run event BEFORE the terminal event so
        // consumers see the complete url-batch/findings stream first.
        await FlushRunEventsAsync(auditId, state, force: true, ct);

        // Cancelled runs release the hreflang collector's audit-scoped state.
        if (status == AuditStatus.Cancelled)
        {
            _crawl.HreflangReciprocity.DiscardAudit(auditId);
        }

        var auditIdWire = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (status == AuditStatus.Failed)
        {
            await _hub.PublishAsync(new HubEvent(
                HubEventCatalog.AuditFailed,
                JsonSerializer.Serialize(new
                {
                    auditId = auditIdWire,
                    errorCode = "audit-failed",
                    message = errorMessage ?? "The audit failed. See local diagnostics for details."
                }, PayloadJson)), ct);
        }
        else
        {
            await _hub.PublishAsync(new HubEvent(
                HubEventCatalog.AuditCompleted,
                JsonSerializer.Serialize(new
                {
                    auditId = auditIdWire,
                    finalCounters = state.Counters.ToPayload(),
                    reportReady = status == AuditStatus.Completed
                }, PayloadJson)), ct);
        }
    }

    private async Task TryFinalizeAsync(
        long auditId,
        AuditRunState state,
        AuditStatus status,
        string? errorMessage = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FinalizeAsync(auditId, state, status, cts.Token, errorMessage);
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

    private async Task DiscoverLinksAsync(
        long auditId,
        long sourceUrlId,
        FrontierEntry source,
        DomDocument document,
        HashSet<string> seen,
        IPriorityFrontier frontier,
        AuditRunState state,
        int maxDepth,
        CrawlRunContext ctx,
        CancellationToken ct)
    {
        var baseUri = new Uri(source.Url);
        // D15: zone-aware link inventory — every anchor carries its visible
        // text, rel flags, DOM-zone classification, and evidence into link_edges
        // at discovery time (FS-CRAWL-002 §15.1). Target references and weighted
        // equity are resolved by the post-crawl enrichment pass.
        var inventory = await _linkInventory.ExtractZonedLinksAsync(document, ct);

        // §5.12 page-level nofollow: when the page is marked nofollow and the
        // toggle is off, no link on it is followed (edges are still recorded).
        var pageNofollow = !ctx.Directives.FollowPageNofollow && PageIsNofollow(document);

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

            // §5.2 canonicalization with the configured options; reportable
            // session-segment removals are deferred to the target's first fetch.
            var outcome = ctx.Canonicalizer.CanonicalizeDetailed(resolved.ToString(), source.Url);
            var canonical = outcome.CanonicalUrl;
            if (outcome.RemovedSessionSegments.Count > 0)
            {
                ctx.SessionSegmentPending.TryAdd(canonical, outcome);
            }

            var verdict = ctx.Scope.Evaluate(canonical);
            var isInternal = verdict.IsInternalHost;
            var anchorText = string.IsNullOrWhiteSpace(link.AnchorText) ? null : link.AnchorText.Trim();

            await _edgeWrites.CreateAsync(new LinkEdgeDto(
                0, auditId, sourceUrlId, canonical, null, anchorText,
                link.RelationFlags, isInternal)
            {
                DomZone = link.Zone,
                ZoneEvidence = link.ZoneEvidence,
            }, ct);

            // CR-008 external status check: with the toggle on, the external
            // target enters the URL inventory as a status-only record (never
            // enqueued for content crawl). The post-crawl broken-link pass
            // probes it and stamps the observed status onto the row.
            if (verdict.Disposition == CrawlScopeDisposition.CheckExternalStatus && seen.Add(canonical))
            {
                var externalEntry = new FrontierEntry(
                    canonical, new Uri(canonical).Host, source.Depth + 1, 0.5, DiscoverySource.Link);
                await GetOrCreateUrlRowAsync(auditId, externalEntry, ct);
                state.Counters.Discovered++;
            }

            // Enqueue only when the scope says Enqueue; external links are
            // recorded (and status-checked post-crawl when the toggle is on).
            if (verdict.Disposition != CrawlScopeDisposition.Enqueue
                || pageNofollow
                || (!ctx.Directives.FollowLinkNofollow && (link.RelationFlags & LinkRelationFlags.Nofollow) != 0)
                || source.Depth + 1 > maxDepth
                || !seen.Add(canonical))
            {
                continue;
            }

            var targetHost = new Uri(canonical).Host;
            var entry = new FrontierEntry(canonical, targetHost, source.Depth + 1, 0.5, DiscoverySource.Link);
            var admission = ctx.Admission.Admit(entry);
            if (!admission.Admit)
            {
                _logger.LogDebug("Audit {AuditId}: discovery skipped {Url} ({Reason}).", auditId, canonical, admission.SkipReason);
                continue;
            }

            await frontier.EnqueueAsync(entry, ct);
            state.Counters.Discovered++;
        }
    }

    /// <summary>
    /// Page-level robots nofollow detection (§5.12): a meta robots directive
    /// carrying <c>nofollow</c> on the document head.
    /// </summary>
    private static bool PageIsNofollow(DomDocument document)
    {
        foreach (var node in EnumerateNodes(document.Root))
        {
            if (node.TagName != "meta"
                || !node.Attributes.TryGetValue("name", out var name)
                || !string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase)
                || !node.Attributes.TryGetValue("content", out var content))
            {
                continue;
            }

            foreach (var directive in content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(directive, "nofollow", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(directive, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<DomNode> EnumerateNodes(DomNode root)
    {
        var stack = new Stack<DomNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>
    /// Normalized visible text for crawler-trap content observation (§5.14):
    /// script/style/noscript/template subtrees excluded, whitespace collapsed.
    /// </summary>
    private static string NormalizedVisibleText(DomDocument document)
    {
        var builder = new System.Text.StringBuilder();
        var stack = new Stack<DomNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            // Prune non-visible subtrees entirely (their text is not content).
            if (node.TagName is "script" or "style" or "noscript" or "template")
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(node.TextContent))
            {
                builder.Append(node.TextContent.Trim()).Append(' ');
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
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
            // CR-024: indexable means SEARCH-indexable. Creation never stamps
            // indexable; the crawl loop applies IndexabilityStateEvaluator's
            // verdict (final 200 + robots-allowed + no noindex + self/absent
            // canonical) once the fetch facts exist.
            IsIndexable: false);
    }

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
            counters.Blocked = root.TryGetProperty("blocked", out var b) ? b.GetInt64() : 0;
            counters.WafBlocked = root.TryGetProperty("wafBlocked", out var w) ? w.GetInt64() : 0;
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

    private Task PublishStageAsync(
        long auditId,
        AuditStatus status,
        AuditPhase phase,
        CancellationToken ct,
        AuditStatus? previousState = null) =>
        _hub.PublishAsync(new HubEvent(
            HubEventCatalog.StageChanged,
            JsonSerializer.Serialize(new
            {
                auditId = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                previousState = ApiEnumConverter<AuditStatus>.ToWireValue(previousState ?? status),
                newState = ApiEnumConverter<AuditStatus>.ToWireValue(status),
                phase = ApiEnumConverter<AuditPhase>.ToWireValue(phase)
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

        // Wire derivations mirror AuditWireMapper.ToDetail exactly so the live
        // crawl-progress payload and the REST snapshot agree (G06).
        var counters = state.Counters;
        var elapsed = state.StartedAt is { } started ? Math.Max(0, (now - started).TotalSeconds) : 0;
        var throughput = elapsed > 0 ? counters.Fetched / elapsed : 0;
        double? remainder = counters.Discovered > 0 && throughput > 0 ? counters.Discovered / throughput : null;

        // Additive Appendix C telemetry: throttled-host count and mean
        // effective rate from the per-audit AIMD controller snapshot.
        int? throttledHostCount = null;
        double? meanEffectiveRate = null;
        if (state.Aimd is { } aimd)
        {
            var hosts = aimd.CreateCheckpointState().Hosts;
            if (hosts.Count > 0)
            {
                var initialRate = state.AimdInitialRate;
                throttledHostCount = hosts.Count(h => h.CurrentRate < initialRate);
                meanEffectiveRate = Math.Round(hosts.Average(h => h.CurrentRate), 3);
            }
            else
            {
                throttledHostCount = 0;
                meanEffectiveRate = 0;
            }
        }

        await _hub.PublishAsync(new HubEvent(
            HubEventCatalog.CrawlProgress,
            JsonSerializer.Serialize(new
            {
                auditId = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                counters = counters.ToPayload(),
                queued = state.Frontier?.Count ?? 0,
                phase = ApiEnumConverter<AuditPhase>.ToWireValue(state.Phase),
                elapsedSeconds = Math.Round(elapsed, 3),
                throughputPerSecond = Math.Round(throughput, 3),
                estimatedRemainderSeconds = remainder is { } value ? Math.Round(value, 3) : (double?)null,
                wafBlocked = counters.WafBlocked,
                throttledHostCount,
                meanEffectiveRate
            }, PayloadJson),
            ThrottleKey: $"crawl-progress:{auditId}",
            ThrottleInterval: TimeSpan.FromSeconds(1)), ct);

        // G05 producer cadence: flush accumulated url-batch / waf-blocks /
        // findings-updated at most once per second (lossless publisher).
        await FlushRunEventsAsync(auditId, state, force: false, ct);
    }

    /// <summary>
    /// WP-09-11 G05: flushes the run's accumulated url-batch and waf-blocks
    /// entries and derives findings-updated count deltas against the last
    /// snapshot. Cadence is the producer's responsibility (≤1/s per audit);
    /// <paramref name="force"/> bypasses the cadence gate at crawl end and
    /// pre-finalize so no accumulated entry is lost. Delivery is lossless
    /// (AuditRunEventPublisher zero throttle; waf-blocks hub-throttled 1/s).
    /// </summary>
    private async Task FlushRunEventsAsync(long auditId, AuditRunState state, bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - state.LastRunEventFlushAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        state.LastRunEventFlushAt = now;

        if (state.PendingUrlBatch.Count > 0)
        {
            var batch = state.PendingUrlBatch.ToArray();
            state.PendingUrlBatch.Clear();
            await _crawl.RunEvents.PublishUrlBatchAsync(auditId, batch, ct);
        }

        if (state.PendingWafBlocks.Count > 0)
        {
            var blocks = state.PendingWafBlocks.ToArray();
            state.PendingWafBlocks.Clear();
            await _hub.PublishAsync(new HubEvent(
                HubEventCatalog.WafBlocks,
                JsonSerializer.Serialize(new
                {
                    auditId = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    blocks
                }, PayloadJson),
                ThrottleKey: $"waf-blocks:{auditId}",
                ThrottleInterval: TimeSpan.FromSeconds(1)), ct);
        }

        // Findings-updated deltas are DERIVED from the persisted finding counts
        // (GROUP BY severity/category) against the last published snapshot, so
        // every producer — pipeline, crawl recorders, site passes — is captured
        // without invasive per-emit hooks. Deltas are incremental; the frontend
        // accumulates them.
        var (severityCounts, categoryCounts) = await CountFindingsAsync(auditId, ct);
        var severityDeltas = DiffCounts(state.FindingSeverityCounts, severityCounts);
        var categoryDeltas = DiffCounts(state.FindingCategoryCounts, categoryCounts);
        if (severityDeltas.Count > 0 || categoryDeltas.Count > 0)
        {
            await _crawl.RunEvents.PublishFindingsUpdatedAsync(auditId, severityDeltas, categoryDeltas, ct);
            ReplaceCounts(state.FindingSeverityCounts, severityCounts);
            ReplaceCounts(state.FindingCategoryCounts, categoryCounts);
        }
    }

    private async Task<(Dictionary<string, int> Severity, Dictionary<string, int> Category)> CountFindingsAsync(
        long auditId, CancellationToken ct)
    {
        var severity = new Dictionary<string, int>(StringComparer.Ordinal);
        var category = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = await _provider.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `severity`, `category`, COUNT(*) FROM `findings` WHERE `audit_id` = @auditId GROUP BY `severity`, `category`";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var count = (int)reader.GetInt64(2);
            var severityKey = ApiEnumConverter<FindingSeverity>.ToWireValue((FindingSeverity)reader.GetInt32(0));
            var categoryKey = ApiEnumConverter<AuditCategory>.ToWireValue((AuditCategory)reader.GetInt32(1));
            severity[severityKey] = severity.GetValueOrDefault(severityKey) + count;
            category[categoryKey] = category.GetValueOrDefault(categoryKey) + count;
        }

        return (severity, category);
    }

    private static Dictionary<string, int> DiffCounts(Dictionary<string, int> previous, Dictionary<string, int> current)
    {
        var deltas = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in current)
        {
            var delta = count - previous.GetValueOrDefault(key);
            if (delta != 0)
            {
                deltas[key] = delta;
            }
        }

        return deltas;
    }

    private static void ReplaceCounts(Dictionary<string, int> target, Dictionary<string, int> source)
    {
        target.Clear();
        foreach (var (key, count) in source)
        {
            target[key] = count;
        }
    }

    // ---- WP-09-11 crawl-wiring helpers -------------------------------------

    private static JsonObject? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<T>? ParseJsonList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges user-supplied cookies into the jar for every seed origin
    /// (§5.1/§5.11): host-only, sent even when the jar is cookieless.
    /// </summary>
    private static void MergeUserCookies(
        AuditCookieJar jar, IEnumerable<string> seedHosts, IReadOnlyDictionary<string, string> cookies)
    {
        if (cookies.Count == 0)
        {
            return;
        }

        foreach (var host in seedHosts)
        {
            // The scheme is resolved per origin at fetch time; the jar keys
            // cookies by host for the user-supplied path.
            if (Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var origin))
            {
                jar.MergeCookies(origin, cookies);
            }
        }
    }

    /// <summary>
    /// Returns the durable urlId for a frontier entry: pre-seeded rows
    /// (redirect-matrix auto-seed, list ingestion) are reused; otherwise a
    /// NotCrawled row is created so redirect hops persist during the fetch.
    /// Idempotent per (audit, canonical URL) — G-MATRIX-SEED-RACE crawl side.
    /// </summary>
    private async Task<long> GetOrCreateUrlRowAsync(long auditId, FrontierEntry entry, CancellationToken ct)
    {
        var existing = await _urlReads.ListAsync(new ListQuery(
            0, 1,
            new Dictionary<string, string>
            {
                ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["canonicalUrl"] = entry.Url
            }), ct);
        if (existing.Items.Count > 0)
        {
            return existing.Items[0].Id;
        }

        var created = await _urlWrites.CreateAsync(
            NewUrlRecord(auditId, entry, CrawlStatusClass.NotCrawled, null, RobotsState.Allowed), ct);
        return created.Id;
    }

    /// <summary>
    /// CR-008 external status-check records: the broken-link pass probes
    /// external targets and reports the broken ones site-wide; stamp the
    /// observed status onto the status-only inventory rows created at
    /// discovery (transport failures keep a null status with FetchError).
    /// </summary>
    private async Task StampExternalProbeRowsAsync(
        long auditId, IReadOnlyList<FindingDto> findings, CancellationToken ct)
    {
        foreach (var finding in findings)
        {
            if (finding.RuleKey != LinkRuleKeys.BrokenExternal)
            {
                continue;
            }

            string? targetUrl = null;
            int? statusCode = null;
            try
            {
                using var evidence = JsonDocument.Parse(finding.EvidenceJson);
                targetUrl = evidence.RootElement.TryGetProperty("targetUrl", out var target) ? target.GetString() : null;
                statusCode = evidence.RootElement.TryGetProperty("statusCode", out var status)
                    && status.ValueKind == JsonValueKind.Number ? status.GetInt32() : null;
            }
            catch (JsonException)
            {
                // Malformed evidence leaves the inventory row untouched.
            }

            if (string.IsNullOrEmpty(targetUrl))
            {
                continue;
            }

            var rows = await _urlReads.ListAsync(new ListQuery(
                0, 1,
                new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["canonicalUrl"] = targetUrl
                }), ct);
            if (rows.Items.Count == 0)
            {
                continue;
            }

            var statusClass = statusCode is null
                ? CrawlStatusClass.FetchError
                : statusCode >= 500 ? CrawlStatusClass.ServerError
                : statusCode >= 400 ? CrawlStatusClass.ClientError
                : CrawlStatusClass.Success;
            await _urlWrites.UpdateAsync(rows.Items[0] with
            {
                StatusClass = statusClass,
                FinalStatusCode = statusCode
            }, ct);
        }
    }

    /// <summary>
    /// Records crawl-family finding drafts with project suppression stamping
    /// (the recorder persists IsSuppressed as given; FindingEmitter owns the
    /// matcher). Idempotent per (audit, ruleKey, urlId).
    /// </summary>
    private async Task RecordCrawlDraftsAsync(
        long auditId, long projectId, IReadOnlyList<FindingDraft> drafts, CancellationToken ct)
    {
        if (drafts.Count == 0)
        {
            return;
        }

        var suppressions = await LoadSuppressionsAsync(projectId, ct);
        var findings = drafts.Select(draft => new FindingDto(
            0, auditId, draft.RuleKey, draft.Category, draft.Severity, draft.UrlId,
            draft.EvidenceJson, draft.RecommendationKey,
            FindingEmitter.IsSuppressed(draft, suppressions))).ToList();
        await _crawl.CrawlFindings.RecordAsync(auditId, findings, ct);
    }

    /// <summary>
    /// Records crawl-family findings produced as DTOs (redirect analyzer),
    /// stamping project suppressions before persistence.
    /// </summary>
    private async Task RecordCrawlFindingsAsync(
        long auditId, long projectId, IReadOnlyList<FindingDto> findings, CancellationToken ct)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var suppressions = await LoadSuppressionsAsync(projectId, ct);
        var stamped = findings.Select(finding => finding with
        {
            IsSuppressed = SuppressionMatcher.IsSuppressed(finding.RuleKey, finding.UrlId, suppressions)
        }).ToList();
        await _crawl.CrawlFindings.RecordAsync(auditId, stamped, ct);
    }

    private async Task<IReadOnlyList<FindingSuppressionDto>> LoadSuppressionsAsync(long projectId, CancellationToken ct)
    {
        var page = await _crawl.SuppressionReads.ListAsync(new ListQuery(
            0, DatasetPageSize,
            new Dictionary<string, string>
            {
                ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }), ct);
        return page.Items;
    }

    /// <summary>
    /// Persists the terminal redirect-chain facts (final URL/status, violation
    /// flags) over the incrementally stored hop prefix (WP-09-01 recipe).
    /// </summary>
    private async Task SaveChainOutcomeAsync(
        long urlId,
        IReadOnlyList<RedirectHop> chain,
        string? finalUrl,
        int? finalStatusCode,
        bool loopDetected,
        bool chainCapExceeded,
        CancellationToken ct)
    {
        var payload = new RedirectChainPayload(
            chain.OrderBy(h => h.Ordinal)
                .Select(h => new RedirectChainHopPayload(h.Ordinal, h.FromUrl, h.ToUrl, h.StatusCode))
                .ToList(),
            finalUrl,
            finalStatusCode,
            loopDetected,
            chainCapExceeded);
        await _crawl.ChainStore.SaveOutcomeAsync(urlId, payload, ct);
    }

    /// <summary>Appendix C status-class wire label for an observed HTTP status.</summary>
    private static string HttpStatusClassWire(int statusCode) => (statusCode / 100) switch
    {
        1 => "1xx",
        2 => "2xx",
        3 => "3xx",
        4 => "4xx",
        _ => "5xx"
    };

    /// <summary>
    /// Evaluator confidence rides the evidence payload (the locked verdict
    /// record carries no confidence member); parse it for the waf-blocks wire.
    /// </summary>
    private static double WafConfidence(WafEvaluationResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.EvidenceJson);
            if (document.RootElement.TryGetProperty("confidence", out var confidence)
                && confidence.TryGetDouble(out var value))
            {
                return Math.Round(value, 3);
            }
        }
        catch (JsonException)
        {
            // Malformed evidence degrades to zero confidence.
        }

        return 0;
    }

    /// <summary>Set-Cookie names observed on the terminal response (diagnostic input).</summary>
    private static IReadOnlyList<string> ObservedSetCookieNames(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "set-cookie", StringComparison.OrdinalIgnoreCase))
            {
                return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(pair => pair.Split('=', 2)[0].Trim())
                    .Where(name => name.Length > 0)
                    .ToList();
            }
        }

        return [];
    }
}
