using CrawlForge.Api.Hubs;
using CrawlForge.Api.Metrics;
using CrawlForge.Audit.Accessibility;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.ListMode;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Rendering;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Crawler.Sitemaps;
using CrawlForge.Crawler.Traps;
using CrawlForge.Crawler.Waf;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using CrawlForge.Export;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Delivery;
using CrawlForge.Host.Configuration;
using CrawlForge.Host.Execution;
using CrawlForge.Host.Hosting;
using CrawlForge.Intelligence.Embeddings;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.MarTech;
using CrawlForge.Intelligence.NGrams;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Engine services module (WP-06-01): binds every accepted engine surface —
/// crawler, audit pipeline + analyzer family, scoring, intelligence, exports,
/// scheduler, and the real-time hub — into the composition root. All services
/// are local-only; outbound network surfaces are the consented crawl-target,
/// user-webhook, and user-database boundaries exclusively.
/// </summary>
public sealed class EngineServicesModule : IHostModule
{
    /// <inheritdoc />
    public string Name => "engine-services";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        RegisterCrawler(services);
        RegisterAudit(services);
        RegisterIntelligence(services);
        RegisterExports(services);
        RegisterScheduler(services);
        RegisterHub(services);
    }

    private static void RegisterCrawler(IServiceCollection services)
    {
        services.AddSingleton<UrlRewriteEngine>();
        services.AddSingleton<IUrlRewriteEngine>(p => p.GetRequiredService<UrlRewriteEngine>());
        services.AddSingleton<IUrlCanonicalizer>(_ => new UrlCanonicalizer());
        services.AddSingleton<IRedirectChainRecorder>(_ => new RedirectChainRecorder());
        services.AddSingleton<ISignedRequestHandler>(_ => new SignedRequestHandler());
        services.AddSingleton(_ => new HttpFetcherOptions());
        services.AddSingleton(_ =>
            CrawlTargetHttpClientFactory.Create(TimeSpan.FromSeconds(120)));
        services.AddSingleton<IHttpFetcher>(p => new HttpFetcher(
            p.GetRequiredService<HttpFetcherOptions>(),
            p.GetRequiredService<HttpClient>(),
            p.GetRequiredService<IRedirectChainRecorder>()));
        services.AddSingleton<IWafSignatureCatalogue>(p => new WafSignatureCatalogue(
            p.GetRequiredService<IDbProvider>(),
            AssetLocator.ResolveFile("waf-signatures", "waf-catalogue.json")));
        services.AddSingleton(p => new WafChallengeEvaluator(
            p.GetRequiredService<IWafSignatureCatalogue>()));
        services.AddSingleton<IWafChallengeEvaluator>(p => p.GetRequiredService<WafChallengeEvaluator>());
        // WAF recording surfaces (WP-06-01 reopen): the engine persists
        // challenge detections + suspected notices and reports the site-wide
        // block-rate diagnostic through these accepted Crawler components.
        services.AddSingleton(p => new WafDetectionRecorder(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton(p => new WafBlockRateDiagnostic(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IBackendLifecycle, WafCatalogueInitializationParticipant>();
        services.AddSingleton<IRenderingPool>(p => new HeadlessRenderingPool(
            contentStore: p.GetRequiredService<IContentStore>(),
            wafEvaluator: p.GetRequiredService<IWafChallengeEvaluator>()));
        // WP-10-02 capture-mode wiring: capture-time snippet executor over the
        // locked Jint runner + the per-audit coordinator factory (adaptive
        // group state lives for exactly one run).
        services.AddSingleton<Crawler.Capture.ICaptureSnippetExecutor>(p =>
            new Crawler.Capture.CaptureSnippetExecutor(p.GetRequiredService<IJsSnippetRunner>()));
        services.AddSingleton<Crawler.Capture.ICaptureModeCoordinatorFactory>(p =>
            new Crawler.Capture.CaptureModeCoordinatorFactory(
                p.GetRequiredService<IRenderingPool>(),
                p.GetRequiredService<ITolerantDomParser>(),
                p.GetRequiredService<IContentStore>(),
                p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
                p.GetRequiredService<IWriteRepository<UrlRecordDto>>(),
                p.GetRequiredService<Crawler.Capture.ICaptureSnippetExecutor>(),
                // WP-10-04: rendering-dependent audits build a per-audit pool from
                // RenderingPoolOptions.FromCaptureSettings(snapshot.CaptureSettings)
                // (WP-10-01 handoff §4.1) with the same WAF hook as the shared
                // pool; the shared singleton stays the tools/single-page path.
                wafEvaluator: p.GetRequiredService<IWafChallengeEvaluator>()));
        services.AddSingleton<IPreCrawlValidator>(p => new PreCrawlValidator(
            p.GetRequiredService<IHttpFetcher>(),
            p.GetRequiredService<IWafChallengeEvaluator>()));
        services.AddSingleton<ICrawlCheckpointService>(p =>
            new CrawlCheckpointService(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IRobotsTxtParser>(_ => new RobotsTxtParser());
        services.AddSingleton<IListModeIngestor>(p => new ListModeIngestor(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IUrlCanonicalizer>(),
            p.GetRequiredService<IUrlRewriteEngine>(),
            new PriorityFrontier(new AimdPolitenessController())));

        // AIMD observability (WP-06-01 reopen, criterion #36): the bounded
        // history store and completion summarizer are shared surfaces; the
        // engine builds the per-audit recorder + coalesced hub composite.
        services.AddSingleton<IHostThrottleEventStore>(p => new RepositoryHostThrottleEventStore(
            p.GetRequiredService<IReadRepository<HostThrottleEventDto>>(),
            p.GetRequiredService<IWriteRepository<HostThrottleEventDto>>()));
        services.AddSingleton<AimdMetricSummarizer>();
        // The locked controller surface addresses the live audit's controller
        // (idle fallback when no crawl is running).
        services.AddSingleton<IAimdPolitenessController>(p => new LiveAimdControllerRouter(p));
        services.AddSingleton<IRedirectMatrixValidator>(p => new RedirectMatrixValidator(
            p.GetRequiredService<IHttpFetcher>(),
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IUrlCanonicalizer>(),
            p.GetRequiredService<IRealtimeHub>()));
    }

    private static void RegisterAudit(IServiceCollection services)
    {
        services.AddSingleton<ITolerantDomParser>(_ => new AngleSharpDomParser());
        services.AddSingleton<IElementInventoryExtractor>(_ => new ElementInventoryExtractor());
        services.AddSingleton(_ => new ContentScopeResolver());
        services.AddSingleton<IContentScopeResolver>(p => p.GetRequiredService<ContentScopeResolver>());
        // D9 (WP-06-01 envelope batch): ToolsController activates only when the
        // concrete scope-preview service is resolvable in the real Host.
        services.AddSingleton(p => new ScopePreviewService(p.GetRequiredService<ContentScopeResolver>()));
        services.AddSingleton(p => new ContentScopeProfileStore(
            p.GetRequiredService<IReadRepository<ContentScopeProfileDto>>(),
            p.GetRequiredService<IWriteRepository<ContentScopeProfileDto>>()));
        services.AddSingleton<ISoft404Detector>(_ => new Soft404Detector());
        services.AddSingleton<IIndexabilityAnalyzer>(p =>
            new IndexabilityAnalyzer(p.GetRequiredService<ISoft404Detector>()));
        services.AddSingleton<IHeaderHtmlEquivalencyAnalyzer>(_ => new HeaderHtmlEquivalencyAnalyzer());
        services.AddSingleton<IResponseRenderedComparer>(_ => new ResponseRenderedComparer());

        // Analyzer family → generic rule host (composition-only adapters).
        // D13: the content-quality analyzer is a resolvable singleton so the
        // live user-dictionary sync can feed persisted entries into the SAME
        // instance the rule host (and single-page analysis) uses.
        services.AddSingleton(p => new ContentQualityAnalyzer(
            scopeResolver: p.GetRequiredService<IContentScopeResolver>()));
        // D14: the n-gram rule host is a resolvable singleton so the engine can
        // run its site pass (RunSitePassAsync) after the per-URL passes.
        services.AddSingleton(p => new NGramRuleAnalyzer(
            p.GetRequiredService<NGramAnalyzer>(),
            p.GetRequiredService<IStopWordFilter>(),
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IContentScopeResolver>()));
        // WP-09-11 (G03/G11 reopen): the structured-data, image, URL-structure
        // and hreflang-reciprocity analyzers join the per-URL rule family. The
        // hreflang reciprocity analyzer is a resolvable singleton shared with
        // the engine: its per-URL pass COLLECTS hreflang sets and the engine
        // runs RunSitePassAsync once every page completed (D14 pattern).
        services.AddSingleton(p => new HreflangReciprocityRuleAnalyzer(
            new HreflangReciprocityValidator(),
            p.GetRequiredService<IDbProvider>()));
        services.AddSingleton(p => new UrlStructureAnalyzer(
            p.GetRequiredService<IReadRepository<UrlRecordDto>>()));
        services.AddSingleton<IAuditRuleAnalyzer>(p =>
        {
            var scopeResolver = p.GetRequiredService<IContentScopeResolver>();
            // D-021 alt takeover (WP-10-05): the accessibility image-alt rule is
            // disabled here so ImageRuleAnalyzer emits it EXACTLY ONCE under
            // Images. This single construction covers the full-audit AND
            // single-page paths (shared IAuditPipeline -> AuditRuleHost).
            var accessibility = new AccessibilityAnalyzer(new AccessibilityAnalyzerOptions
            {
                RuleOverrides = new Dictionary<string, AccessibilityRuleOverride>(StringComparer.Ordinal)
                {
                    [AccessibilityRuleKeys.ImageAlt] = new(Enabled: false),
                },
            });
            var contentQuality = p.GetRequiredService<ContentQualityAnalyzer>();
            var hreflang = new HreflangAnalyzer();
            var mobile = new MobileFriendlinessAnalyzer();
            var ngrams = p.GetRequiredService<NGramRuleAnalyzer>();
            var structuredData = new StructuredDataRuleAnalyzer(
                p.GetRequiredService<StructuredDataValidator>());
            // CR-031 (WP-10-05): artifact services let the analyzer measure
            // network image bytes from stored NetworkWaterfall artifacts;
            // without them network images stay honestly "not measured".
            var images = new ImageRuleAnalyzer(
                artifacts: p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
                contentStore: p.GetRequiredService<IContentStore>());
            var urlStructure = p.GetRequiredService<UrlStructureAnalyzer>();
            var hreflangReciprocity = p.GetRequiredService<HreflangReciprocityRuleAnalyzer>();
            return new AuditRuleHost(
            [
                new AnalyzerRuleAdapter([], accessibility.AnalyzeAsync),
                new AnalyzerRuleAdapter([], contentQuality.AnalyzeAsync),
                new AnalyzerRuleAdapter([], hreflang.AnalyzeAsync),
                new AnalyzerRuleAdapter([], mobile.AnalyzeAsync),
                ngrams,
                structuredData,
                images,
                urlStructure,
                hreflangReciprocity,
            ]);
        });

        // D13 live wiring (criterion #13 suppression facet): persisted
        // user_dictionary entries feed the singleton analyzer at startup and
        // after every REST mutation (last-wins decorator over the Core write
        // registration; the locked controller is unchanged).
        services.AddSingleton(p => new UserDictionaryLiveSync(
            p.GetRequiredService<ContentQualityAnalyzer>(),
            p.GetRequiredService<IReadRepository<UserDictionaryEntryDto>>()));
        services.AddSingleton<IWriteRepository<UserDictionaryEntryDto>>(p =>
            new UserDictionaryWriteRepository(
                new SqlRepository<UserDictionaryEntryDto>(p.GetRequiredService<IDbProvider>()),
                p.GetRequiredService<UserDictionaryLiveSync>()));
        services.AddSingleton<IBackendLifecycle>(p => new UserDictionaryLifecycleParticipant(
            p.GetRequiredService<UserDictionaryLiveSync>()));

        services.AddSingleton<IAuditPipeline>(p => new AuditPipeline(
            p.GetRequiredService<ITolerantDomParser>(),
            p.GetRequiredService<IElementInventoryExtractor>(),
            p.GetRequiredService<IContentScopeResolver>(),
            p.GetRequiredService<IAuditRuleAnalyzer>(),
            p.GetRequiredService<IReadRepository<ProjectDto>>(),
            p.GetRequiredService<ContentScopeProfileStore>(),
            p.GetRequiredService<IIndexabilityAnalyzer>(),
            p.GetRequiredService<IHeaderHtmlEquivalencyAnalyzer>(),
            p.GetRequiredService<IResponseRenderedComparer>(),
            p.GetRequiredService<ISoft404Detector>()));
        services.AddSingleton<ISinglePageAnalyzer>(p => new SinglePageAnalyzer(
            p.GetRequiredService<IHttpFetcher>(),
            p.GetRequiredService<ITolerantDomParser>(),
            p.GetRequiredService<IAuditPipeline>(),
            p.GetRequiredService<IWafChallengeEvaluator>(),
            p.GetRequiredService<IRenderingPool>(),
            p.GetRequiredService<IContentScopeResolver>()));

        services.AddSingleton(p => new AuditScoringService(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IAuditScoringService>(p => p.GetRequiredService<AuditScoringService>());
        services.AddSingleton(p => new AuditScoreRecorder(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton(p => new ScoringRecomputeCoordinator(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<AuditScoringService>(),
            p.GetRequiredService<AuditScoreRecorder>()));
        services.AddSingleton<IFindingSuppressionService>(p => new FindingSuppressionService(
            p.GetRequiredService<IReadRepository<FindingSuppressionDto>>(),
            p.GetRequiredService<IWriteRepository<FindingSuppressionDto>>(),
            p.GetRequiredService<ScoringRecomputeCoordinator>()));
        // WP-09-11 (gap #21): the emitter carries the rule catalog so emitted
        // findings resolve their declarative rule definitions.
        services.AddSingleton(p => new FindingEmitter(
            p.GetRequiredService<IReadRepository<FindingSuppressionDto>>(),
            p.GetRequiredService<IWriteRepository<FindingDto>>(),
            p.GetRequiredService<AuditRuleCatalog>()));
        services.AddSingleton(_ =>
        {
            var catalog = new AuditRuleCatalog();
            // WP-09-11: crawl-family rule keys registered additively (open
            // taxonomy; categories/severities match the accepted producers).
            catalog.Register(new RuleDefinition(CrawlFindingRuleKeys.RobotsBlocked,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(CrawlFindingRuleKeys.RobotsBlocked), 70, "low"));
            catalog.Register(new RuleDefinition(CrawlFindingRuleKeys.SessionSegmentRemoved,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Notice,
                RecommendationKeys.ForRule(CrawlFindingRuleKeys.SessionSegmentRemoved), 20));
            catalog.Register(new RuleDefinition(CrawlFindingRuleKeys.CookielessRedirectLoop,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(CrawlFindingRuleKeys.CookielessRedirectLoop), 75, "low"));
            catalog.Register(new RuleDefinition(CrawlFindingRuleKeys.UaComparisonMaterialDifference,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(CrawlFindingRuleKeys.UaComparisonMaterialDifference), 80, "medium"));
            catalog.Register(new RuleDefinition(RedirectRuleKeys.Loop,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Error,
                RecommendationKeys.ForRule(RedirectRuleKeys.Loop), 90, "medium"));
            // CR-029 (WP-10-05): the legacy aggregate redirects.inappropriate
            // seed is superseded by the four per-case keys (all toggleable,
            // default all-on). The legacy constant stays in RedirectRuleKeys
            // for compile compat; nothing emits it.
            catalog.Register(new RuleDefinition(RedirectRuleKeys.InappropriateCrossHost,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(RedirectRuleKeys.InappropriateCrossHost), 60, "low"));
            catalog.Register(new RuleDefinition(RedirectRuleKeys.InappropriateTemporaryStatus,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(RedirectRuleKeys.InappropriateTemporaryStatus), 60, "low"));
            catalog.Register(new RuleDefinition(RedirectRuleKeys.InappropriateNonIndexableTarget,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(RedirectRuleKeys.InappropriateNonIndexableTarget), 60, "low"));
            catalog.Register(new RuleDefinition(RedirectRuleKeys.InappropriateHttpsDowngrade,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(RedirectRuleKeys.InappropriateHttpsDowngrade), 60, "low"));
            // D-021 / CR-030 / CR-031 (WP-10-05) phase-10 keys.
            catalog.Register(new RuleDefinition(Contracts.Constants.FindingRuleKeys.StructuredDataDeprecatedTerm,
                Contracts.Enums.AuditCategory.StructuredData, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(Contracts.Constants.FindingRuleKeys.StructuredDataDeprecatedTerm), 55, "low"));
            catalog.Register(new RuleDefinition(Contracts.Constants.FindingRuleKeys.ImageOversized,
                Contracts.Enums.AuditCategory.Images, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(Contracts.Constants.FindingRuleKeys.ImageOversized), 60, "low"));
            catalog.Register(new RuleDefinition(Contracts.Constants.FindingRuleKeys.ImageFormat,
                Contracts.Enums.AuditCategory.Images, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(Contracts.Constants.FindingRuleKeys.ImageFormat), 50, "low"));
            // Exact producer string (NOT in locked FindingRuleKeys — D-021 alt
            // takeover emits it under Images; harness t_missing_alt matches it).
            catalog.Register(new RuleDefinition(AccessibilityRuleKeys.ImageAlt,
                Contracts.Enums.AuditCategory.Images, Contracts.Enums.FindingSeverity.Error,
                RecommendationKeys.ForRule(AccessibilityRuleKeys.ImageAlt), 85, "low"));
            catalog.Register(new RuleDefinition(RedirectRuleKeys.Chain,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(RedirectRuleKeys.Chain), 50, "low"));
            catalog.Register(new RuleDefinition(SitemapRuleKeys.MissingUrl,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(SitemapRuleKeys.MissingUrl), 55, "low"));
            catalog.Register(new RuleDefinition(SitemapRuleKeys.NonIndexable,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(SitemapRuleKeys.NonIndexable), 65, "low"));
            catalog.Register(new RuleDefinition(SitemapRuleKeys.Redirecting,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(SitemapRuleKeys.Redirecting), 60, "low"));
            catalog.Register(new RuleDefinition(LinkRuleKeys.Orphan,
                Contracts.Enums.AuditCategory.Indexability, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(LinkRuleKeys.Orphan), 55, "medium"));
            catalog.Register(new RuleDefinition(LinkRuleKeys.BrokenInternal,
                Contracts.Enums.AuditCategory.Linking, Contracts.Enums.FindingSeverity.Error,
                RecommendationKeys.ForRule(LinkRuleKeys.BrokenInternal), 85, "low"));
            catalog.Register(new RuleDefinition(LinkRuleKeys.BrokenExternal,
                Contracts.Enums.AuditCategory.Linking, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(LinkRuleKeys.BrokenExternal), 70, "low"));
            catalog.Register(new RuleDefinition(CrawlerTrapRuleKeys.TrapDetected,
                Contracts.Enums.AuditCategory.Crawl, Contracts.Enums.FindingSeverity.Warning,
                RecommendationKeys.ForRule(CrawlerTrapRuleKeys.TrapDetected), 80, "medium"));
            return catalog;
        });

        // WP-09-11 serial wiring: the crawl-component aggregate consumed by the
        // execution engine (per-audit components are built inside the crawl
        // phase; these are the shared, stateless surfaces).
        services.AddSingleton(p => new AuditCrawlComponents(
            new UrlElementRedirectChainStore(
                p.GetRequiredService<IReadRepository<UrlElementDto>>(),
                p.GetRequiredService<IWriteRepository<UrlElementDto>>()),
            new AuditRunEventPublisher(p.GetRequiredService<IRealtimeHub>()),
            p.GetRequiredService<IUrlRewriteEngine>(),
            p.GetRequiredService<IPreCrawlValidator>(),
            new CrawlFindingRecorder(p.GetRequiredService<IDbProvider>()),
            new OrphanFindingAnalyzer(p.GetRequiredService<IDbProvider>()),
            new DuplicateMetadataAnalyzer(
                p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
                p.GetRequiredService<IReadRepository<UrlElementDto>>()),
            new DuplicateContentAnalyzer(
                new ShingleNearDuplicateDetector(
                    p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
                    p.GetRequiredService<IContentStore>(),
                    p.GetRequiredService<ITolerantDomParser>(),
                    p.GetRequiredService<IContentScopeResolver>()),
                p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
                p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
                p.GetRequiredService<IContentStore>(),
                p.GetRequiredService<ITolerantDomParser>(),
                p.GetRequiredService<IContentScopeResolver>()),
            p.GetRequiredService<UrlStructureAnalyzer>(),
            p.GetRequiredService<HreflangReciprocityRuleAnalyzer>(),
            p.GetRequiredService<IReadRepository<FindingSuppressionDto>>()));
        services.AddSingleton(p => new OfflinePageContextBuilder(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<ILinkGraphAnalyzer>(p =>
            new LinkGraphAnalyzer(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<ILinkGraphReadModel>(p => new LinkGraphReadModelProvider(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<ILinkGraphAnalyzer>()));
        // D15 (criterion #48 zone/equity facet): the zone classifier, zone-aware
        // link inventory, and weighted equity calculator are live surfaces —
        // discovery classifies every anchor, and the enrichment pass resolves
        // edge targets and stamps weighted equity after the crawl phase.
        services.AddSingleton<ILinkZoneClassifier>(_ => new LinkZoneClassifier());
        services.AddSingleton(p => new LinkZoneInventoryService(
            p.GetRequiredService<ILinkZoneClassifier>(),
            p.GetRequiredService<IElementInventoryExtractor>()));
        services.AddSingleton(p => new WeightedLinkEquityCalculator(
            p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IWeightedLinkEquityCalculator>(p =>
            p.GetRequiredService<WeightedLinkEquityCalculator>());
        services.AddSingleton(p => new LinkGraphEnrichmentPass(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<WeightedLinkEquityCalculator>()));
        services.AddSingleton(_ => TechnologyFingerprintCatalogue.Load(
            AssetLocator.ResolveFile("tech-fingerprints", "tech-catalogue.json")));
        services.AddSingleton<ITechnologyDetector>(p => new TechnologyDetector(
            p.GetRequiredService<TechnologyFingerprintCatalogue>(),
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton(p => new StructuredDataValidator(
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<IStructuredDataValidator>(p => p.GetRequiredService<StructuredDataValidator>());

        // Custom content family (WP-06-01 reopen, defect D5): the accepted
        // extraction/search/snippet surfaces run per page in the engine and
        // persist extracted_content / content_search_results / snippet_results.
        services.AddSingleton<ICustomContentExtractor>(_ => new CustomContentExtractor());
        services.AddSingleton<ICustomContentSearch>(_ => new CustomContentSearch());
        services.AddSingleton<IJsSnippetRunner>(_ => new JintSnippetRunner());
    }

    private static void RegisterIntelligence(IServiceCollection services)
    {
        services.AddSingleton(p => new NGramReadModel(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton(p => new AuditComparisonReader(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IStopWordFilter>(p => new StopWordFilter(
            p.GetRequiredService<IDbProvider>(),
            AssetLocator.ResolveDirectory("stopwords")));
        services.AddSingleton(p => new NGramAnalyzer(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IStopWordFilter>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<ILocalEmbeddingProvider>(_ => new BundledOnnxEmbeddingProvider(
            AssetLocator.ResolveDirectory("onnx-model")));
        services.AddSingleton(p => new SemanticPersistence(
            p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
            p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
            p.GetRequiredService<IReadRepository<UrlEmbeddingDto>>(),
            p.GetRequiredService<IWriteRepository<UrlEmbeddingDto>>(),
            p.GetRequiredService<IReadRepository<LinkEdgeDto>>(),
            p.GetRequiredService<IReadRepository<NgramFrequencyDto>>(),
            p.GetRequiredService<IReadRepository<SemanticLinkOpportunityDto>>(),
            p.GetRequiredService<IWriteRepository<SemanticLinkOpportunityDto>>(),
            p.GetRequiredService<IReadRepository<SemanticClusterDto>>(),
            p.GetRequiredService<IWriteRepository<SemanticClusterDto>>(),
            p.GetRequiredService<IWriteRepository<FindingDto>>(),
            p.GetRequiredService<IReadRepository<FindingSuppressionDto>>(),
            p.GetRequiredService<IReadRepository<MetricSummaryDto>>(),
            p.GetRequiredService<IWriteRepository<MetricSummaryDto>>(),
            p.GetRequiredService<IReadRepository<AuditDto>>()));
        services.AddSingleton<ISemanticLinkAnalyzer>(p => new SemanticLinkingPipeline(
            p.GetRequiredService<ILocalEmbeddingProvider>(),
            p.GetRequiredService<ITolerantDomParser>(),
            p.GetRequiredService<IContentScopeResolver>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<SemanticPersistence>()));
        services.AddSingleton(p => new GeoReadinessAnalyzer(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<IGeoReadinessAnalyzer>(p => p.GetRequiredService<GeoReadinessAnalyzer>());
        services.AddSingleton(_ => AiAgentProfileCatalogue.Load(
            AssetLocator.ResolveFile("ai-agent-profiles", "ai-agent-profiles.json")));
        services.AddSingleton(p => new AiBotGovernanceMatrix(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<IRobotsTxtParser>(),
            p.GetRequiredService<AiAgentProfileCatalogue>(),
            p.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<IAiBotGovernanceMatrix>(p => p.GetRequiredService<AiBotGovernanceMatrix>());
        // GEO persistence surfaces (WP-06-01 reopen): the engine materializes
        // readiness checks, the governance matrix, and coverage aggregates at
        // finalize so the GEO dashboard reads stored rows, not live recompute.
        services.AddSingleton(p => new GovernanceMatrixRecorder(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton(p => new GeoAggregateRecorder(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<ILlmsTxtGenerator>(p => new LlmsTxtGenerator(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<ITolerantDomParser>()));

        // Log analyzer (WP-04-05, accepted).
        services.AddSingleton<IStreamingLogFileParser>(_ => new LogFileParser());
        services.AddSingleton(_ => BotCatalogue.Load(
            AssetLocator.ResolveFile("bot-cidr", "bot-catalogue.json")));
        services.AddSingleton<IBotVerifier>(p =>
            new BotVerifier(p.GetRequiredService<BotCatalogue>()));
        services.AddSingleton<ILogImportService>(p => new LogImportService(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IStreamingLogFileParser>(),
            p.GetRequiredService<IBotVerifier>(),
            p.GetRequiredService<IUrlCanonicalizer>(),
            p.GetRequiredService<IRealtimeHub>(),
            p.GetRequiredService<BotCatalogue>()));
        // Logs DI (WP-06-01 reopen): ONE concrete singleton forwarded to BOTH
        // consumed interfaces — the correlation engine and the read model are
        // the same accepted instance.
        services.AddSingleton(p => new LogCorrelationEngine(p.GetRequiredService<IDbProvider>()));
        services.AddSingleton<ILogCorrelationEngine>(p => p.GetRequiredService<LogCorrelationEngine>());
        services.AddSingleton<ILogCorrelationReadModel>(p => p.GetRequiredService<LogCorrelationEngine>());
        services.AddSingleton<ILogDnsVerificationTool>(p => new LogDnsVerificationTool(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IBotVerifier>()));
        services.AddSingleton<IMarTechAuditor>(_ => new MarTechAuditor());
    }

    private static void RegisterExports(IServiceCollection services)
    {
        // ONE shared job registry between the engine and delivery (handoff).
        services.AddSingleton<ExportJobRegistry>();
        services.AddSingleton(p => new ExportEventPublisher(p.GetRequiredService<IRealtimeHub>()));
        services.AddSingleton(p =>
        {
            var paths = p.GetRequiredService<CrawlForgePaths>();
            return new ExportOptions
            {
                OutputDirectory = Path.Combine(paths.DataDirectory, "exports")
            };
        });
        services.AddSingleton<IExportEngine>(p => new ExportEngine(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<ExportJobRegistry>(),
            p.GetRequiredService<ExportEventPublisher>(),
            p.GetRequiredService<ExportOptions>(),
            AssetLocator.ResolveDirectory("export-templates")));
        services.AddSingleton<IExportDestinationDelivery>(p => new ExportDestinationDelivery(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<ISecretProtector>(),
            p.GetRequiredService<ExportJobRegistry>(),
            p.GetRequiredService<ExportEventPublisher>(),
            p.GetRequiredService<ExportOptions>()));
        services.AddSingleton(p => new ExportDatasetReader(p.GetRequiredService<IDbProvider>()));
    }

    private static void RegisterScheduler(IServiceCollection services)
    {
        services.AddSingleton<ISchedulerClock>(_ => new SystemSchedulerClock());
        services.AddSingleton(_ => new SchedulingOptions());
        services.AddSingleton(p => new ScheduleEventPublisher(p.GetRequiredService<IRealtimeHub>()));
        services.AddSingleton<ILocalNotificationSink>(p =>
            new HubLocalNotificationSink(p.GetRequiredService<ScheduleEventPublisher>()));
        services.AddSingleton<INotificationEndpointClient>(_ =>
            new HttpNotificationEndpointClient(NotificationDestinationHttpClientFactory.Create()));
        services.AddSingleton<ICompletionActionExecutor>(p => new CompletionActionExecutor(
            p.GetRequiredService<IExportEngine>(),
            p.GetRequiredService<IExportDestinationDelivery>(),
            p.GetRequiredService<ILocalNotificationSink>(),
            p.GetRequiredService<INotificationEndpointClient>(),
            p.GetRequiredService<ISchedulerClock>(),
            p.GetRequiredService<SchedulingOptions>()));
        services.AddSingleton<IRetentionPruner>(p => new RetentionPruner(
            p.GetRequiredService<IDbProvider>(),
            p.GetRequiredService<IArtifactFileStore>()));
        services.AddSingleton<IScheduledAuditService>(p => new ScheduledAuditService(
            p.GetRequiredService<IReadRepository<ScheduleDefinitionDto>>(),
            p.GetRequiredService<IWriteRepository<ScheduleDefinitionDto>>(),
            p.GetRequiredService<IReadRepository<ScheduleRunDto>>(),
            p.GetRequiredService<IWriteRepository<ScheduleRunDto>>(),
            p.GetRequiredService<IReadRepository<AuditDto>>(),
            p.GetRequiredService<IAuditExecutionService>(),
            p.GetRequiredService<ISchedulerClock>(),
            p.GetRequiredService<ScheduleEventPublisher>(),
            p.GetRequiredService<ICompletionActionExecutor>(),
            p.GetRequiredService<IRetentionPruner>(),
            p.GetRequiredService<SchedulingOptions>()));
    }

    private static void RegisterHub(IServiceCollection services)
    {
        // The running-audit hooks resolve the execution engine lazily (at
        // heartbeat/compact time), keeping composition free of cycles.
        // WAL observability (WP-06-01 reopen): the embedded SQLite provider's
        // WAL monitor feeds health/heartbeat/metrics/compact evidence; external
        // providers (MySQL) have no WAL, so null is correct there.
        services.AddSingleton(p => new SystemHealthSampler(
            walSource: p.GetRequiredService<IDbProvider>() is SqliteDbProvider sqlite ? sqlite.WalMonitor : null,
            runningAuditId: () => p.GetRequiredService<Execution.AuditExecutionEngine>().RunningAuditId));
        services.AddSingleton<ISystemHealthSampler>(p => p.GetRequiredService<SystemHealthSampler>());
        services.AddSingleton<ISystemHealthService>(p => p.GetRequiredService<SystemHealthSampler>());
        services.AddSingleton<IWalCheckpointer>(p =>
            new SqliteWalCheckpointer(p.GetRequiredService<SqliteDbProvider>()));
        services.AddSingleton<ICompactService>(p => new CompactService(
            p.GetRequiredService<ISystemHealthSampler>(),
            p.GetRequiredService<IWalCheckpointer>(),
            isAuditRunning: () => p.GetRequiredService<Execution.AuditExecutionEngine>().RunningAuditId is not null));
        services.AddSingleton<IMetricsExposition>(p =>
            new PrometheusMetricsExposition(p.GetRequiredService<ISystemHealthSampler>()));
        services.AddSingleton<IHubClientChannel>(p =>
            new SignalRHubChannel(p.GetRequiredService<IHubContext<CrawlForgeHub>>()));
        services.AddSingleton(p => new RealtimeHubService(
            p.GetRequiredService<IHubClientChannel>(),
            p.GetRequiredService<ISystemHealthSampler>(),
            metrics: p.GetRequiredService<IMetricsExposition>()));
        services.AddSingleton<IRealtimeHub>(p => p.GetRequiredService<RealtimeHubService>());
    }
}

/// <summary>
/// Loads the effective WAF signature catalogue (bundled asset + user-local
/// entries) before any engine participant or API request can evaluate a
/// response. Local asset read only — never a runtime download (exclusion E2).
/// </summary>
public sealed class WafCatalogueInitializationParticipant : IBackendLifecycle
{
    private readonly WafChallengeEvaluator _evaluator;

    /// <summary>Creates the participant.</summary>
    /// <param name="evaluator">WAF challenge evaluator to initialize.</param>
    public WafCatalogueInitializationParticipant(WafChallengeEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    /// <inheritdoc />
    public int Order => 5;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) =>
        _evaluator.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
