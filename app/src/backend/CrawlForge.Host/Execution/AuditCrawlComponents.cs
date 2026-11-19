using CrawlForge.Api.Hubs;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Frontier;

namespace CrawlForge.Host.Execution;

/// <summary>
/// WP-09-11 serial wiring: the DI-resolved component aggregate consumed by the
/// execution engine's crawl/post-crawl/site passes. Grouping the additive
/// surfaces keeps the engine constructor readable; every member is an accepted
/// component registered in the composition root (EngineServicesModule /
/// CoreServicesModule). Per-audit components (fetcher, canonicalizer, scope
/// evaluator, cookie jar, robots cache, virtual robots, trap detector,
/// admission gate, sitemap ingestor, list ingestor, broken-link analyzer) are
/// constructed inside the crawl phase because they bind per-run state.
/// </summary>
/// <param name="ChainStore">Durable per-URL redirect-chain storage (WP-09-01).</param>
/// <param name="RunEvents">Lossless url-batch / findings-updated publisher (WP-09-06 G05).</param>
/// <param name="RewriteEngine">Locked on-the-fly rewrite engine (list-mode §10.4).</param>
/// <param name="PreCrawlValidator">Start-URL validator (list pre-crawl probe).</param>
/// <param name="CrawlFindings">Crawl-family finding persistence (WP-09-03).</param>
/// <param name="OrphanAnalyzer">Orphan detection over the link graph (WP-09-03).</param>
/// <param name="DuplicateMetadata">Site-wide duplicate title/description pass (WP-09-04).</param>
/// <param name="DuplicateContent">Site-wide exact/near duplicate-content pass (WP-09-04).</param>
/// <param name="UrlStructure">URL-structure site pass (WP-09-05).</param>
/// <param name="HreflangReciprocity">Hreflang reciprocity collector + site pass (WP-09-04).</param>
/// <param name="SuppressionReads">Project suppressions (stamped onto crawl-family findings).</param>
public sealed record AuditCrawlComponents(
    IRedirectChainStore ChainStore,
    AuditRunEventPublisher RunEvents,
    IUrlRewriteEngine RewriteEngine,
    IPreCrawlValidator PreCrawlValidator,
    CrawlFindingRecorder CrawlFindings,
    OrphanFindingAnalyzer OrphanAnalyzer,
    DuplicateMetadataAnalyzer DuplicateMetadata,
    DuplicateContentAnalyzer DuplicateContent,
    UrlStructureAnalyzer UrlStructure,
    HreflangReciprocityRuleAnalyzer HreflangReciprocity,
    IReadRepository<FindingSuppressionDto> SuppressionReads);