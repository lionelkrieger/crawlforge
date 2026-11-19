# WP-09-01 Wiring Handoff — Sitemap Ingestion, Orphan Detection & Redirect-Hop Persistence

Builder: cf-wp-09-01. Components + component tests ONLY; nothing below is wired yet.
Consumers: WP-09-11 (engine/DI wiring), WP-09-06 (REST URL-detail mapper),
cf-wp-09-02 (cookie jar), cf-wp-09-03 (proxy/signing/rewrite), phase-gate conformance.

Verification (cwd `E:\Crawlforge\app`, SDK 10.0.404, `CF_AGENT_ID=cf-wp-09-01`):
- `dotnet build src\backend\CrawlForge.Crawler\CrawlForge.Crawler.csproj` → 0 warnings / 0 errors.
- `dotnet test tests\CrawlForge.Crawler.Tests\CrawlForge.Crawler.Tests.csproj --filter "FullyQualifiedName~CrawlForge.Crawler.Tests.Sitemaps|FullyQualifiedName~CrawlForge.Crawler.Tests.Fetching|FullyQualifiedName~CrawlForge.Crawler.Tests.Frontier"` → 277 passed.
- Full suite (same project, no filter) → 865 passed / 0 failed.
- New WP-09-01 tests: 105 (12 classes), incl. 6 live fixture tests on OWN port 47313.

## 1. Redirect-chain persistence (url_elements mechanism, zero migration)

- Group constant: `RedirectChainElementGroup.GroupName = "redirect-chain"`
  (`Crawler\Fetching\RedirectChainPersistence.cs`). One `url_elements` row per URL,
  ordinal 0. Upsert gated (SemaphoreSlim) — no duplicate rows under concurrency.
- Payload JSON (camelCase, `RedirectChainJson.SerializePayload`):
  `{"hops":[{"ordinal":0,"url":"<hop source>","toUrl":"<Location>","statusCode":301},...],
    "finalUrl":"…|null","finalStatusCode":200|null,"loopDetected":false,"chainCapExceeded":false}`
  `toUrl`, `finalUrl`, `finalStatusCode` are additive over the lead-approved minimal
  shape (lossless wire mapping). Violation payloads retain the observed hop prefix.
- Store: `IRedirectChainStore` / `UrlElementRedirectChainStore(IReadRepository<UrlElementDto>,
  IWriteRepository<UrlElementDto>)` — locked contracts only. Both repositories are
  ALREADY registered: `CoreServicesModule.cs:99 AddRepository<UrlElementDto>(services)`.
- Hop-by-hop hook: `RedirectChainPersistence.CreatePersistHook(store)` →
  `Func<RedirectChainSnapshot, CancellationToken, Task>` for the
  `RedirectChainRecorder(maxHops, persistAsync)` constructor. Recorder emits the FULL
  chain after every hop; loop/cap throw `RedirectChainException(Reason)` BEFORE the
  violating hop is retained.
- Terminal outcome: `RedirectChainPersistence.BuildOutcome(chain, finalUrl,
  finalStatusCode, violation?)` + `store.SaveOutcomeAsync(urlId, payload)`.
- Read-back: `store.GetPayloadAsync(urlId)`; tolerant `RedirectChainJson.DeserializePayload`
  (null/empty/malformed → null); `RedirectChainJson.ToHops(payload)` → contract hops.

### Engine hookup recipe (WP-09-11), `Host\Execution\AuditExecutionEngine.cs`
1. Inject `IRedirectChainStore` into the engine ctor.
2. Recorder construction (lines 603–605): `new RedirectChainRecorder()` →
   `new RedirectChainRecorder(20, RedirectChainPersistence.CreatePersistHook(store))`.
   (DI registration `EngineServicesModule.cs:77` should get the same hook.)
3. **urlId must exist before fetching** (hops are keyed by it): pre-create the
   url_records row (NotCrawled) before line 693, then UPDATE after fetch instead of
   CreateAsync at 733. Set `FetchContextScope.Current = new FetchContext(auditId, urlId)`
   around `fetcher.FetchAsync` (line 693) in try/finally → null. Without the scope the
   recorder is inert (this is why probe-2 recorded no chains — G27).
4. Classification (lines 716–732): replace `ClassifyStatus(fetch.StatusCode, …)` with
   `var chain = await recorder.GetChainAsync(auditId, urlId, ct);`
   `var outcome = FetchOutcomeClassifier.Classify(entry.Url, fetch, chain, waf.IsChallenge);`
   Record: `StatusClass = outcome.StatusClass`, `FinalStatusCode = outcome.SourceStatusCode`
   (the SOURCE's own 3xx — the G27 fix), `RedirectTargetUrl = outcome.RedirectTargetUrl`.
   Then `store.SaveOutcomeAsync(urlId, RedirectChainPersistence.BuildOutcome(chain,
   fetch.FinalUrl, fetch.StatusCode))` (terminal facts live in the chain payload).
5. Failure path (695–706): catch `RedirectChainException` →
   `FetchOutcomeClassifier.ClassifyViolation(entry.Url, ex.Reason, partialChain)` +
   `RedirectFindingAnalyzer.AnalyzeViolation(...)` + `BuildOutcome(partial, null, null, ex.Reason)`;
   catch `FetchFailedException` → `ClassifyFailure(entry.Url, ex, partialChain)` +
   `AnalyzeFetchFailure(...)` (null for non-redirect kinds). partialChain =
   `recorder.GetChainAsync(auditId, urlId)`.
6. Redirect findings per fetch: `RedirectFindingAnalyzer.AnalyzeChain(auditId, urlId,
   entry.Url, chain, fetch.FinalUrl, fetch.StatusCode)` → drafts to `CrawlFindingRecorder`.

### REST mapper recipe (WP-09-06), `Api\Controllers\AuditExplorerController.cs:215–217`
The elements query at line 205 already loads all url_elements of the URL. Replace the
synthesized single-hop chain with:
```csharp
var chainRow = elements.Items.FirstOrDefault(e => e.ElementGroup == RedirectChainElementGroup.GroupName);
var payload = RedirectChainJson.DeserializePayload(chainRow?.PayloadJson);
var redirectChain = payload is not null
    ? payload.Hops.OrderBy(h => h.Ordinal).Select(h => new RedirectHopWire(h.Url, h.StatusCode, h.ToUrl)).ToArray()
    : url.RedirectTargetUrl is null
        ? Array.Empty<RedirectHopWire>()
        : new[] { new RedirectHopWire(url.CanonicalUrl, url.FinalStatusCode ?? 0, url.RedirectTargetUrl) }; // legacy fallback
```
Wire: `RedirectHopWire(string Url, int StatusCode, string? Location)`
(`Api\Controllers\AuditExplorerWireContracts.cs:30`). Mapping is lossless:
hop.url→Url, hop.statusCode→StatusCode, hop.toUrl→Location.

## 2. Sitemap ingestion & correlation

- `ISitemapIngestor` / `SitemapIngestor(IHttpFetcher, IRobotsTxtParser, IUrlCanonicalizer,
  SitemapIngestionOptions?)`. `IngestAsync(SitemapIngestionRequest(AuditId, SeedUrls,
  ConfiguredSitemapSeeds?, RobotsByHost?))` → `SitemapIngestionResult` (Urls:
  `SitemapSeededUrl(CanonicalUrl, Host, DeclaredUrl, SourceSitemapUrl, LastModified,
  ChangeFrequency, Priority)`; Sitemaps; Diagnostics). Discovery = robots.txt Sitemap
  directives + /sitemap.xml probe + configured seeds; index recursion with cycle/depth
  (5)/document (100)/URL (500k total, 50k per doc) guards; tolerant diagnostics.
- Seeding: `result.CreateFrontierEntries()` → `FrontierEntry(url, host, Depth:0,
  Priority:1.0, DiscoverySource.Sitemap)`. Engine: after the seed loop
  (`AuditExecutionEngine.cs:635–651`), enqueue entries not already in `seen`.
  NOTE: engine `robotsByHost` is `Dictionary<string, RobotsDocument?>`; the request
  takes non-null values — filter nulls when passing (or pass null and let the
  ingestor fetch robots per origin).
- Correlation: `SitemapCorrelationAnalyzer.Analyze(auditId, sitemapCanonicalUrls,
  IReadOnlyList<SitemapUrlCrawlFact>)` → FindingDto drafts. Fact = (CanonicalUrl,
  UrlId?, WasCrawled, StatusClass, FinalStatusCode?, RobotsState, IndexabilityFlags,
  CanonicalTargetUrl?, Chain). Build facts from url_records + stored chain payloads
  after the crawl. Keys: `sitemap.missing-url` (absent/not crawled),
  `sitemap.redirecting` (chain present; precedence over non-indexable),
  `sitemap.non-indexable` (CR-024: non-200 / robots-disallowed / noindex /
  canonical-mismatch; reasons array in evidence). All Warning / Indexability.
  Evidence hop shape = canonical `{ordinal,url,toUrl,statusCode}`.

## 3. Orphan & broken-link findings (Frontier)

- `OrphanFindingAnalyzer(IDbProvider).AnalyzeAsync(auditId, sitemapCanonicalUrls?,
  OrphanDetectionOptions(ExcludeEntryPoints:true)?)` → `links.orphan` Warning /
  **Indexability** (spec §7.4; lead decision). Zero internal inlinks, self-loops
  excluded, Seed/List entry points excluded by default. Evidence:
  `{url,urlId,discoverySource,depth,inSitemap,internalInlinkCount:0}`.
- `BrokenLinkAnalyzer(IDbProvider, IHttpFetcher).AnalyzeAsync(auditId,
  BrokenLinkOptions(CheckExternalLinks:true, MaxExternalChecks:1000,
  MaxSourcesPerFinding:10, ExternalTimeoutSeconds:15)?)` →
  `links.broken-internal` Error / Linking (4xx/5xx/FetchError targets WITH ≥1 internal
  inlink; evidence targetUrl/targetUrlId/statusCode/statusClass/sourceUrls/sourceCount)
  and `links.broken-external` Warning / Linking (distinct external targets probed once
  via GET — never enqueued; DNS/connect/TLS/timeout kinds + ≥400; UrlId null; evidence
  targetUrl/statusCode?/failureKind?/error/sourceUrls/sourceCount).
- Persistence: `CrawlFindingRecorder(IDbProvider).RecordAsync(auditId, drafts)` —
  transactional, idempotent per (audit, ruleKey, urlId) incl. NULL urlId. All analyzers
  emit Id-0 drafts; run after the crawl loop, persist via the recorder (or FindingEmitter).

## 4. Fetcher sibling hooks (cf-wp-09-02 / cf-wp-09-03)

`Crawler\Fetching\FetchHook.cs` + `HttpFetcher` ctor (additive params 9–10; null =
byte-for-byte baseline):
```csharp
public HttpFetcher(HttpFetcherOptions options, HttpClient httpClient,
    IRedirectChainRecorder? redirectRecorder = null, IAimdPolitenessController? aimdController = null,
    ISignedRequestHandler? signedRequestHandler = null, SignedRequestSecret? signingSecret = null,
    ILoopbackInterceptingProxy? interceptingProxy = null, ProxyOptions? proxyOptions = null,
    IFetchHook? fetchHook = null,
    Func<CancellationToken, Task<HttpClient>>? httpClientFactory = null)

public interface IFetchHook   // all members have no-op defaults
{
    ValueTask BeforeSendAsync(HttpRequestMessage request, FetchHookContext context, CancellationToken ct);
    ValueTask AfterReceiveAsync(HttpResponseMessage response, FetchHookContext context, CancellationToken ct);
    bool AllowRevisit(FetchHookContext context); // default false = strict loop guard
}
public sealed record FetchHookContext(long? AuditId, long? UrlId, string OriginalUrl,
    string CurrentUrl, int HopOrdinal, FetchResponseSnapshot? PreviousResponse);
public sealed record FetchResponseSnapshot(int StatusCode, IReadOnlyDictionary<string, string> Headers);
```
Call sites (`HttpFetcher.cs`): BeforeSendAsync :249–254 (after BuildRequestMessage,
every hop AND retry); AfterReceiveAsync :262–269 (immediately after SendAsync, BEFORE
redirect following + size guard → Set-Cookie on 3xx observable); AllowRevisit :230–241
(visited-URL guard; PreviousResponse = snapshot of the redirect that revisited the URL);
factory: GetClientAsync :423+ (only when no intercepting proxy; lazy, created once,
fetcher-owned/disposed; injected client never disposed).
- **cf-wp-09-02 cookie gate**: BeforeSend merges jar Cookie; AfterReceive captures
  Set-Cookie (derive host from `context.CurrentUrl` — `response.RequestMessage` may be
  null); AllowRevisit allows ONE revisit when the previous response set a cookie the
  jar now holds. Reference implementation + proof: `CookieJarFetchHook` in
  `tests\CrawlForge.Crawler.Tests\Fetching\TestSupport\RedirectChainTestDoubles.cs`;
  `HttpFetcherHookTests.CookieJarHook_CookieGate_Returns200AfterOneRedirect` (302 self +
  Set-Cookie → 200) and null-hook baseline (still RedirectLoop).
- **cf-wp-09-03**: rewrite `request.RequestUri` in BeforeSend (transport-level; FinalUrl
  and chain keep the ORIGINAL crawl URL — asserted in BeforeSendHook_RewritesRequestUriAtFetchTime).
  Signed headers: existing ISignedRequestHandler ctor dependency (applied once at
  FetchAsync top). Upstream proxy: existing ILoopbackInterceptingProxy dependency or
  httpClientFactory (custom handler). Hooks must be thread-safe (pipeline concurrency 2+).

## 5. Conformance producer/consumer shapes (phase gate)

- RedirectChainPersistenceConformanceTests: producer `RedirectChainJson.SerializePayload
  (BuildOutcome(...))` / recorder+`CreatePersistHook` → consumer `DeserializePayload` +
  `ToHops` + `UrlElementRedirectChainStore` on real SQLite (see
  `RedirectChainPersistenceTests.UrlElementStore_RealSqliteStack_PersistsHopByHopAndReadsBack`).
  Exact JSON keys: hops/finalUrl/finalStatusCode/loopDetected/chainCapExceeded;
  hop keys ordinal/url/toUrl/statusCode.
- SitemapIngestionConformanceTests: producer `SitemapIngestor.IngestAsync` →
  `CreateFrontierEntries()` → consumer `PriorityFrontier.EnqueueAsync`
  (DiscoverySource.Sitemap, depth 0, priority 1.0). Live proof:
  `SitemapLiveFixtureTests.Live_DiscoverAndIngest_FixtureSitemapSeedsOrphanUrl` (8 URLs).
- SitemapRedirectFindingShapeTests: producer `SitemapCorrelationAnalyzer.Analyze` /
  `RedirectFindingAnalyzer.AnalyzeChain|AnalyzeViolation` → FindingDto evidence →
  consumer `CrawlFindingRecorder` → `SqlRepository<FindingDto>` read-back. Evidence keys:
  chain findings `{url,urlId,hopCount,chain[],finalUrl,finalStatusCode}`; inappropriate
  adds `reasons[{hopOrdinal,check,fromUrl,toUrl,statusCode,description}]` (check names
  "cross-host"/"temporary-status"); violations `{url,urlId,violation:"loop"|
  "chain-cap-exceeded",message,hopCount,chain[]}`; orphan `{url,urlId,discoverySource,
  depth,inSitemap,internalInlinkCount}`; broken-internal `{targetUrl,targetUrlId,
  statusCode,statusClass,sourceUrls,sourceCount}`; broken-external `{targetUrl,
  statusCode,failureKind,error,sourceUrls,sourceCount}`.

## 6. Rule keys / decisions / spec gaps

- Local additive keys (NOT in Contracts): `SitemapRuleKeys` (sitemap.missing-url /
  non-indexable / redirecting), `RedirectRuleKeys` (redirects.loop / inappropriate /
  chain), `LinkRuleKeys` (links.orphan / broken-internal / broken-external).
- Categories: sitemap.*, redirects.*, links.orphan → Indexability; links.broken-* →
  Linking (spec §7.4; lead decision 1 — Contracts enum doc-comment mentioning orphan
  under Linking is a notation slip the lead records).
- Severities (lead-approved defaults, §7.5 rule-definition choices): broken-internal
  Error; redirects.loop Error; redirects.chain Error on cap violation; everything else
  Warning. redirects.chain: Warning for ≥2 hops, NO finding for a single hop
  (additive key for CAP-B-049 "reported as a redirect chain").
- **Spec gap (lead recording for owner)**: "inappropriate redirect" has no spec
  definition (§8 specifies loops only). Implemented as explicit configurable flags
  `RedirectInappropriatenessChecks` (CrossHost | TemporaryStatus=302/303/307, default
  All), documented "pending owner confirmation" — never presented as spec-derived.
- G27 root cause in the running app: engine never sets FetchContextScope and stores
  ClassifyStatus(final 200). Components fix both; wiring is WP-09-11 (recipe above).

## 7. Fixture / data safety

Live tests use OWN node copy of read-only `app\tests\fixture-site\server.mjs` on port
**47313** only (never 47311/47312); `FixtureSiteServer` kills exactly its own PID tree;
no listener remains after the run; `Live_FixtureRecorder_NoNonGetRequests` asserts the
fixture nonGet list is empty. No %APPDATA%\CrawlForge access, no owner projects, no git
commits, no new NuGet, no csproj edits, no schedule/MySQL work.
