using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Crawler.Capture;

/// <summary>
/// Capture-mode coordinator (FS-CRAWL-001 §5.6.1; WP-10-02). One instance per
/// audit, created from the frozen configuration snapshot. Implements the mode
/// semantics end to end: non-JS fetch only; JS renders every in-scope page; dual
/// stores both captures and compares every URL; adaptive samples per
/// URL-path-pattern group and switches materially-changed groups to rendered
/// capture. Manual per-pattern overrides win over adaptive switching and apply
/// on top of every mode. Render failures are isolated (§5.6.2); the dual-storage
/// threshold warns only — never a hard cap (exclusion #8).
/// </summary>
public sealed class CaptureModeCoordinator : ICaptureModeCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRenderingPool _pool;
    private readonly ITolerantDomParser _parser;
    private readonly IContentStore _contentStore;
    private readonly IReadRepository<UrlRecordDto>? _urlReads;
    private readonly IWriteRepository<UrlRecordDto>? _urlWrites;
    private readonly ICaptureSnippetExecutor? _snippetExecutor;
    private readonly ILogger? _logger;
    private readonly CaptureSettings _settings;
    private readonly int _sampleSize;
    private readonly Dictionary<string, AdaptiveGroupState> _groups = new(StringComparer.Ordinal);
    private readonly List<FindingDraft> _auditFindings = [];
    private long _dualStoredBytes;
    private bool _dualStorageWarned;

    /// <inheritdoc />
    public CaptureMode Mode { get; }

    /// <summary>Creates a coordinator for one audit.</summary>
    /// <param name="mode">Capture mode (null configuration maps to NonJs upstream).</param>
    /// <param name="settings">Capture settings (null applies the locked defaults).</param>
    /// <param name="pool">Locked rendering pool (WP-10-01 CDP implementation in production).</param>
    /// <param name="parser">Tolerant DOM parser for the post-render HTML.</param>
    /// <param name="contentStore">Artifact store (RenderedDom / NetworkWaterfall / Screenshot).</param>
    /// <param name="urlReads">Optional URL read repository (CaptureSource stamping).</param>
    /// <param name="urlWrites">Optional URL write repository (CaptureSource stamping).</param>
    /// <param name="snippetExecutor">Optional capture-time snippet executor (§7.17).</param>
    /// <param name="logger">Optional local-only logger.</param>
    public CaptureModeCoordinator(
        CaptureMode mode,
        CaptureSettings? settings,
        IRenderingPool pool,
        ITolerantDomParser parser,
        IContentStore contentStore,
        IReadRepository<UrlRecordDto>? urlReads = null,
        IWriteRepository<UrlRecordDto>? urlWrites = null,
        ICaptureSnippetExecutor? snippetExecutor = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(contentStore);
        Mode = mode;
        _settings = settings ?? CaptureSettings.Default;
        _sampleSize = Math.Clamp(_settings.AdaptiveSampleSizePerGroup, CaptureDefaults.MinAdaptiveSampleSize, CaptureDefaults.MaxAdaptiveSampleSize);
        _pool = pool;
        _parser = parser;
        _contentStore = contentStore;
        _urlReads = urlReads;
        _urlWrites = urlWrites;
        _snippetExecutor = snippetExecutor;
        _logger = logger;
    }

    /// <inheritdoc />
    public CaptureDecision Decide(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        string groupKey = UrlPathPatternGrouping.GroupKey(path);

        // Manual overrides win over adaptive switching and apply on top of every
        // mode (§5.6.1). First matching override in configuration order wins.
        if (_settings.PatternOverrides is { } overrides)
        {
            foreach (CapturePatternOverride o in overrides)
            {
                if (UrlPathPatternGrouping.Matches(o.Pattern, path))
                {
                    return o.Capture == PatternCaptureMode.Rendered
                        ? new CaptureDecision(true, "override:rendered", groupKey, OverrideApplied: true)
                        : new CaptureDecision(false, "override:non-rendered", groupKey, OverrideApplied: true);
                }
            }
        }

        return Mode switch
        {
            CaptureMode.Js => new CaptureDecision(true, "mode:js", groupKey, OverrideApplied: false),
            CaptureMode.Dual => new CaptureDecision(true, "mode:dual", groupKey, OverrideApplied: false),
            CaptureMode.Adaptive => DecideAdaptive(groupKey),
            _ => new CaptureDecision(false, "mode:non-js", groupKey, OverrideApplied: false),
        };
    }

    /// <inheritdoc />
    public async Task<RenderedCaptureOutcome> CaptureAsync(RenderedCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RawDocument);
        cancellationToken.ThrowIfCancellationRequested();

        CaptureDecision decision = Decide(request.Url);
        string? rawServiceWorker = CaptureDocumentFacts.ServiceWorkerRegistration(request.RawDocument);

        if (!decision.ShouldRender)
        {
            // Explicit per-URL capture-source marking when a capture mode is
            // active; baseline non-JS keeps the documented null (= raw) sentinel
            // to avoid write amplification on the default mode.
            UrlRecordDto? rawRow = Mode == CaptureMode.NonJs
                ? null
                : await StampCaptureSourceAsync(request.UrlId, CaptureSource.Raw, cancellationToken).ConfigureAwait(false);
            var rawSnippets = await RunSnippetsAsync(request, renderedDocument: null, renderedPage: null, cancellationToken, rawRow).ConfigureAwait(false);
            return RawOutcome(decision, rawServiceWorker is not null, rawSnippets);
        }

        RenderedPage page;
        try
        {
            var renderRequest = new RenderRequest(
                request.Url,
                request.Device,
                request.InfiniteScroll,
                request.CaptureConsole,
                request.Stealth,
                TimeoutSeconds: Math.Max(1, _settings.RenderWaitHardCapMs / 1000));
            page = await _pool.RenderAsync(renderRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // §5.6.2 failure isolation: a render failure never fails the crawl;
            // the URL keeps its raw capture.
            _logger?.LogWarning(ex, "Audit {AuditId}: render failed for {Url}; keeping raw capture.", request.AuditId, request.Url);
            UrlRecordDto? failedRow = Mode == CaptureMode.NonJs
                ? null
                : await StampCaptureSourceAsync(request.UrlId, CaptureSource.Raw, cancellationToken).ConfigureAwait(false);
            var failedSnippets = await RunSnippetsAsync(request, renderedDocument: null, renderedPage: null, cancellationToken, failedRow).ConfigureAwait(false);
            return RawOutcome(decision, rawServiceWorker is not null, failedSnippets, renderFailureReason: ex.Message);
        }

        DomDocument renderedDocument = _parser.Parse(page.Html, request.Url);
        long storedBytes = await PersistRenderedArtifactsAsync(request, page, cancellationToken).ConfigureAwait(false);

        MaterialChangeFact material = EvaluateMaterialChange(
            request.RawDocument, renderedDocument, request.Url, decision.GroupKey, _settings.MaterialChangeWordCountThreshold);
        IReadOnlyList<RenderedLinkCandidate> renderedOnlyLinks =
            CaptureDocumentFacts.RenderedOnlyLinks(request.RawDocument, renderedDocument, request.Url);
        string? renderedServiceWorker = CaptureDocumentFacts.ServiceWorkerRegistration(renderedDocument);
        bool serviceWorker = rawServiceWorker is not null || renderedServiceWorker is not null;

        CaptureSource source = decision.Reason is "mode:dual" or "adaptive:sample"
            ? CaptureSource.Both
            : CaptureSource.Rendered;

        // Adaptive state advance + template-switch finding (§5.6.1).
        if (Mode == CaptureMode.Adaptive && !decision.OverrideApplied && decision.Reason == "adaptive:sample")
        {
            AdaptiveGroupState group = Group(decision.GroupKey);
            group.SampledCount++;
            if (material.IsMaterial && !group.Switched)
            {
                group.Switched = true;
                group.SampleUrls.Add(request.Url);
                _auditFindings.Add(TemplateSwitchFinding(request, material, group));
                _logger?.LogInformation(
                    "Audit {AuditId}: adaptive group {Group} switched to rendered capture (material change on {Url}).",
                    request.AuditId, decision.GroupKey, request.Url);
            }
            else if (!group.Switched)
            {
                group.SampleUrls.Add(request.Url);
            }
        }

        // Dual storage accounting: WARNING above the configurable threshold,
        // never a hard cap (§5.6.1, exclusion #8).
        if (Mode == CaptureMode.Dual)
        {
            _dualStoredBytes += request.RawBodyBytes + storedBytes;
            if (!_dualStorageWarned && _dualStoredBytes > _settings.DualStorageWarningThresholdBytes)
            {
                _dualStorageWarned = true;
                _auditFindings.Add(DualStorageWarningFinding(request.AuditId));
            }
        }

        UrlRecordDto? urlRow = await StampCaptureSourceAsync(request.UrlId, source, cancellationToken).ConfigureAwait(false);
        var snippets = await RunSnippetsAsync(request, renderedDocument, page, cancellationToken, urlRow).ConfigureAwait(false);

        return new RenderedCaptureOutcome(
            source,
            Rendered: true,
            renderedDocument,
            page.Html,
            page,
            renderedOnlyLinks,
            Findings: [],
            material,
            snippets,
            serviceWorker,
            RenderFailureReason: null,
            decision);
    }

    /// <inheritdoc />
    public IReadOnlyList<FindingDraft> DrainAuditFindings()
    {
        if (_auditFindings.Count == 0)
        {
            return [];
        }

        var drained = _auditFindings.ToArray();
        _auditFindings.Clear();
        return drained;
    }

    private CaptureDecision DecideAdaptive(string groupKey)
    {
        AdaptiveGroupState group = Group(groupKey);
        if (group.Switched)
        {
            return new CaptureDecision(true, "adaptive:switched", groupKey, OverrideApplied: false);
        }

        return group.SampledCount < _sampleSize
            ? new CaptureDecision(true, "adaptive:sample", groupKey, OverrideApplied: false)
            : new CaptureDecision(false, "adaptive:raw", groupKey, OverrideApplied: false);
    }

    private AdaptiveGroupState Group(string groupKey)
    {
        if (!_groups.TryGetValue(groupKey, out AdaptiveGroupState? group))
        {
            group = new AdaptiveGroupState();
            _groups[groupKey] = group;
        }

        return group;
    }

    private static RenderedCaptureOutcome RawOutcome(
        CaptureDecision decision,
        bool serviceWorker,
        IReadOnlyList<CaptureSnippetOutcome> snippets,
        string? renderFailureReason = null) =>
        new(
            CaptureSource.Raw,
            Rendered: false,
            RenderedDocument: null,
            RenderedHtml: null,
            RenderedPage: null,
            RenderedOnlyLinks: [],
            Findings: [],
            MaterialChange: null,
            snippets,
            serviceWorker,
            renderFailureReason,
            decision);

    private async Task<long> PersistRenderedArtifactsAsync(RenderedCaptureRequest request, RenderedPage page, CancellationToken ct)
    {
        long bytes = 0;

        // Rendered DOM snapshot (the analysed document in JS capture; the second
        // capture in dual/adaptive-sample). BuildStoredContextAsync reloads this
        // artifact as PageAuditContext.RenderedDocument.
        using (var domStream = new MemoryStream(Encoding.UTF8.GetBytes(page.Html)))
        {
            ContentArtifactDto dom = await _contentStore.PutAsync(request.AuditId, request.UrlId, ContentArtifactKind.RenderedDom, domStream, ct).ConfigureAwait(false);
            bytes += dom.SizeBytes;
        }

        // CR-006: rendered traffic is captured directly by the rendering control
        // layer (CDP Network domain → RenderedPage.NetworkWaterfall). The
        // waterfall artifact is persisted from that capture, never the proxy.
        if (page.NetworkWaterfall is { } waterfall)
        {
            using var waterfallStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(waterfall, JsonOptions));
            ContentArtifactDto row = await _contentStore.PutAsync(request.AuditId, request.UrlId, ContentArtifactKind.NetworkWaterfall, waterfallStream, ct).ConfigureAwait(false);
            bytes += row.SizeBytes;
        }

        if (page.Screenshot.Length > 0)
        {
            using var screenshotStream = new MemoryStream(page.Screenshot.ToArray());
            ContentArtifactDto row = await _contentStore.PutAsync(request.AuditId, request.UrlId, ContentArtifactKind.Screenshot, screenshotStream, ct).ConfigureAwait(false);
            bytes += row.SizeBytes;
        }

        return bytes;
    }

    private async Task<UrlRecordDto?> StampCaptureSourceAsync(long urlId, CaptureSource source, CancellationToken ct)
    {
        if (_urlReads is null || _urlWrites is null)
        {
            return null;
        }

        UrlRecordDto? row = await _urlReads.GetByIdAsync(urlId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        UrlRecordDto updated = row with { CaptureSource = source };
        await _urlWrites.UpdateAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    private async Task<IReadOnlyList<CaptureSnippetOutcome>> RunSnippetsAsync(
        RenderedCaptureRequest request,
        DomDocument? renderedDocument,
        RenderedPage? renderedPage,
        CancellationToken ct,
        UrlRecordDto? urlRow = null)
    {
        if (request.Snippets is not { Count: > 0 } || _snippetExecutor is null)
        {
            return [];
        }

        UrlRecordDto row = urlRow ?? new UrlRecordDto(
            request.UrlId, request.AuditId, request.Url,
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty,
            request.Path, request.Depth, DiscoverySource.Link, CrawlStatusClass.Success,
            FinalStatusCode: 200, RedirectTargetUrl: null, ContentType: "text/html", SizeBytes: request.RawBodyBytes,
            TimingJson: null, RobotsState.Allowed, IndexabilityFlags.None, IsIndexable: true);

        var context = new PageAuditContext(
            request.AuditId, row, request.Configuration, Fetch: null,
            request.RawDocument, renderedDocument, ScopedText: null, CapturedRequests: null);
        return await _snippetExecutor.RunAsync(context, request.Snippets, ct).ConfigureAwait(false);
    }

    private FindingDraft TemplateSwitchFinding(RenderedCaptureRequest request, MaterialChangeFact material, AdaptiveGroupState group)
    {
        var evidence = new
        {
            groupKey = material.GroupKey,
            sampleUrls = group.SampleUrls,
            switchedAtUrl = request.Url,
            titleChanged = material.TitleChanged,
            canonicalChanged = material.CanonicalChanged,
            metaRobotsChanged = material.MetaRobotsChanged,
            hreflangChanged = material.HreflangChanged,
            linkSetChanged = material.LinkSetChanged,
            rawWordCount = material.RawWordCount,
            renderedWordCount = material.RenderedWordCount,
            wordCountChangeFraction = material.WordCountChangeFraction,
            wordCountThreshold = material.WordCountThreshold,
        };
        return new FindingDraft(
            FindingRuleKeys.ContentJavascriptDependentTemplate,
            AuditCategory.Content,
            FindingSeverity.Warning,
            request.UrlId,
            JsonSerializer.Serialize(evidence, JsonOptions),
            RecommendationKey: null);
    }

    private FindingDraft DualStorageWarningFinding(long auditId) =>
        new(
            CaptureRuleKeys.DualStorageWarning,
            AuditCategory.Crawl,
            FindingSeverity.Warning,
            UrlId: null,
            JsonSerializer.Serialize(new
            {
                storedBytes = _dualStoredBytes,
                thresholdBytes = _settings.DualStorageWarningThresholdBytes,
                mode = EnumWireNames.ToWireValue(Mode),
                hardCap = false,
            }, JsonOptions),
            RecommendationKey: null);

    /// <summary>
    /// Adaptive material-change evaluation (§5.6.1): any title, canonical,
    /// meta-robots or hreflang difference, a different link set, or a visible
    /// word-count change above the configured threshold. A zero→nonzero word
    /// count change is always material (script-injected content).
    /// </summary>
    internal static MaterialChangeFact EvaluateMaterialChange(
        DomDocument raw, DomDocument rendered, string url, string groupKey, double threshold)
    {
        bool titleChanged = !string.Equals(
            CaptureDocumentFacts.Title(raw) ?? string.Empty,
            CaptureDocumentFacts.Title(rendered) ?? string.Empty,
            StringComparison.Ordinal);
        bool canonicalChanged = !string.Equals(
            string.Join("\n", CaptureDocumentFacts.Canonicals(raw).OrderBy(h => h, StringComparer.Ordinal)),
            string.Join("\n", CaptureDocumentFacts.Canonicals(rendered).OrderBy(h => h, StringComparer.Ordinal)),
            StringComparison.Ordinal);
        bool metaRobotsChanged = !string.Equals(
            CaptureDocumentFacts.MetaRobotsSignature(raw),
            CaptureDocumentFacts.MetaRobotsSignature(rendered),
            StringComparison.Ordinal);
        bool hreflangChanged = !string.Equals(
            CaptureDocumentFacts.HreflangSignature(raw),
            CaptureDocumentFacts.HreflangSignature(rendered),
            StringComparison.Ordinal);
        bool linkSetChanged = !CaptureDocumentFacts.NormalizedLinkHrefs(raw, url)
            .SetEquals(CaptureDocumentFacts.NormalizedLinkHrefs(rendered, url));

        int rawWords = CaptureDocumentFacts.WordCount(raw);
        int renderedWords = CaptureDocumentFacts.WordCount(rendered);
        double? fraction = rawWords == 0 ? null : Math.Abs(renderedWords - rawWords) / (double)rawWords;
        bool wordMaterial = rawWords == 0 ? renderedWords > 0 : fraction!.Value > threshold;

        bool material = titleChanged || canonicalChanged || metaRobotsChanged || hreflangChanged || linkSetChanged || wordMaterial;
        return new MaterialChangeFact(
            url, groupKey, material, titleChanged, canonicalChanged, metaRobotsChanged, hreflangChanged,
            linkSetChanged, rawWords, renderedWords, fraction, threshold);
    }

    private sealed class AdaptiveGroupState
    {
        public int SampledCount { get; set; }
        public bool Switched { get; set; }
        public List<string> SampleUrls { get; } = [];
    }
}

/// <summary>
/// Default factory composing per-audit coordinators from the locked rendering
/// pool, tolerant parser, content store and URL repositories. Registered as a
/// singleton (see the WP-10-02 wiring handoff); the engine creates one
/// coordinator per audit so adaptive group state lives for exactly one run.
/// </summary>
public sealed class CaptureModeCoordinatorFactory : ICaptureModeCoordinatorFactory
{
    private readonly IRenderingPool _pool;
    private readonly ITolerantDomParser _parser;
    private readonly IContentStore _contentStore;
    private readonly IReadRepository<UrlRecordDto>? _urlReads;
    private readonly IWriteRepository<UrlRecordDto>? _urlWrites;
    private readonly ICaptureSnippetExecutor? _snippetExecutor;
    private readonly ILogger<CaptureModeCoordinatorFactory>? _logger;

    /// <summary>Creates the factory. Dependencies arrive via constructor composition.</summary>
    /// <param name="pool">Locked rendering pool.</param>
    /// <param name="parser">Tolerant DOM parser.</param>
    /// <param name="contentStore">Artifact store.</param>
    /// <param name="urlReads">Optional URL read repository.</param>
    /// <param name="urlWrites">Optional URL write repository.</param>
    /// <param name="snippetExecutor">Optional capture-time snippet executor.</param>
    /// <param name="logger">Optional local-only logger.</param>
    public CaptureModeCoordinatorFactory(
        IRenderingPool pool,
        ITolerantDomParser parser,
        IContentStore contentStore,
        IReadRepository<UrlRecordDto>? urlReads = null,
        IWriteRepository<UrlRecordDto>? urlWrites = null,
        ICaptureSnippetExecutor? snippetExecutor = null,
        ILogger<CaptureModeCoordinatorFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(contentStore);
        _pool = pool;
        _parser = parser;
        _contentStore = contentStore;
        _urlReads = urlReads;
        _urlWrites = urlWrites;
        _snippetExecutor = snippetExecutor;
        _logger = logger;
    }

    /// <inheritdoc />
    public ICaptureModeCoordinator Create(CrawlConfigurationDto configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CaptureModeCoordinator(
            configuration.CaptureMode ?? CaptureMode.NonJs,
            configuration.CaptureSettings,
            _pool,
            _parser,
            _contentStore,
            _urlReads,
            _urlWrites,
            _snippetExecutor,
            _logger);
    }
}
