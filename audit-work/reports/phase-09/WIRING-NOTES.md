# Phase-09 coordinator wiring notes (lead, WP-09-11 prep)

Living notes for the serial wiring workpiece. Sources: phase-09 YAML, UI walker
(`app/tests/fixture-acceptance/harness/ui_walker.py`), CODEMAP, engine/DI reads.
The walker is the acceptance oracle — the UI must satisfy it AS WRITTEN (never
weaken the walker to match the UI).

## Walker contract for the gate surfaces (UI-01,02,03,04,10,11,12,13,20)

- **UI-01** (already functional): `hub-dashboard` + `new-project-button` + ≥1 `project-row-*`.
- **UI-02**: click `project-row-{pid}` (or dblclick, or Ctrl+K palette project pick) must
  render a dump containing **"Configurations" AND "Integrations"** (text/buttons/testids)
  or testid `project-overview`. → Project overview view with those sections.
- **UI-03**: walker calls `ensure_hub()` FIRST, then checks the HUB dump for a
  "Configurations" marker (text/buttons/testids). → The hub itself must expose a
  Configurations affordance (e.g. per-project-row button or hub section), not only the
  overview view. Shell\HubDashboard.tsx is lead-owned wiring.
- **UI-04**: walker clicks `new-project-button`, waits for `create-project-modal`,
  dumps after 500ms: needs **≥1 of the 13 tab names AND >3 inputs** IN THAT DUMP.
  → The New project dialog must embed/transition to the 13-tab configuration editor
  (project creation creates a default configuration and opens the editor inline).
  Constraint for WP-09-07 brief. Tab names verbatim: Scope; Crawl as and capture; Limits;
  Rules; Directives; Resources and external scope; Authentication and cookies; Click
  discovery and site types; Consent states; Analysis categories; Thresholds; Extraction
  and search; Advanced.
- **UI-10** (already functional): nav-audits → `open-audit-{aid}`/"Open report" →
  `report-view` + `report-state` + UPPERCASE state word in page text + `report-progress`
  or `hosts-table`. Preserve through wiring.
- **UI-11**: report-view must NOT contain placeholder text "mount into this workspace
  from the report workpieces" AND must contain one of testids `findings-grid`,
  `url-explorer`, `issues-grid`, `dashboard-charts`. → Remove the placeholder Paper from
  shell\AuditViews.tsx ReportView; mount WP-09-08 Dashboard exposing `dashboard-charts`.
- **UI-12**: report-window NAVIGATION (buttons/testids only, `_has_nav`) containing
  "Issue explorer" or "Issues". → Report nav tab/button; mount issues explorer (`issues-grid`).
- **UI-13**: nav button/testid "URL explorer" or "URLs". → Report nav; mount `url-explorer`.
- **UI-20**: nav button/testid "Export". → Report nav Export button opens WP-09-10 dialog.
- **UI-16** (extra evidence, not in gate list): specialist markers via `_has_nav` —
  add at least "List mode" + "Structured data" report-nav entries (engines land in 9A).
- **UI-entry audit flow** (run_fixture_acceptance.py): create project via
  `create-project-modal` (`project-name-input`, `project-url-input`,
  `create-project-submit`), limits bounded via REST PUT /api/configurations/{id},
  Start audit pressed IN THE UI via `start-audit-{pid}` (exists in HubDashboard today —
  preserve; also expose in project overview per UI-10 spec path).

## Backend wiring map (verified reads)

- DI hub: `Host\Composition\EngineServicesModule.cs`
  - RegisterCrawler :72–128 — UrlCanonicalizer() no options (:76, G27);
    RedirectChainRecorder() no persistAsync (:77, G27); HttpFetcher(options, HttpClient,
    redirectRecorder) (:82–85) — cookie/proxy/signed hooks attach here;
    WafChallengeEvaluator (:89); RobotsTxtParser (:106); ListModeIngestor (:107);
    LiveAimdControllerRouter (:122); RedirectMatrixValidator (:123).
  - RegisterAudit :130–265 — analyzer family :161–177 (Accessibility, ContentQuality,
    Hreflang, Mobile, NGrams → AuditRuleHost via AnalyzerRuleAdapter). NEW 9A analyzers
    register here as AnalyzerRuleAdapter entries. AuditPipeline ctor :193–203 (optional
    indexability/headerHtmlEquivalency/responseRenderedComparer/soft404 slots).
    FindingEmitter :223–225 OMITS AuditRuleCatalog (registered :226) — CODEMAP gap #21,
    fix in wiring (pass catalog). StructuredDataValidator registered :256 but never
    pipeline-invoked (G11). HreflangReciprocityValidator NOT registered.
    LinkGraphEnrichmentPass :248 — model for cross-URL post-passes.
  - RegisterExports :352–378; RegisterScheduler :380–411 (HubLocalNotificationSink :385 —
    CR-027 frontend contract addition is lead's); RegisterHub :413–440.
- Engine: `Host\Execution\AuditExecutionEngine.cs` (70KB)
  - RunAuditAsync :491 → CrawlPhaseAsync :535 → AnalyzePhaseAsync :780 →
    RunLinkGraphEnrichmentAsync :843 → RunNgramSitePassAsync :860 →
    ScoreAndFinalizeAsync :1173 → PersistIntelligenceAggregatesAsync :1197 → FinalizeAsync :1236.
  - **NewUrlRecord :1543–1574 — IsIndexable = statusClass==Success && robots!=Disallowed
    (:1574): the CR-024 defect. Wire WP-09-05 IndexabilityStateEvaluator here.**
  - ClassifyStatus :1577; AnalyzeOnePageAsync :1025 (_pipeline.RunAsync);
    counters AuditRunCounters.ToPayload (engine keys — KnownGap WP-09-06 fixes mapper side).
  - ReAuditAsync :433 — G-REAUDIT-SNAPSHOT race (configuration_snapshot_json) — WP-09-06
    owns AuditsController; engine side read :436–438/497.
- Pipeline: `Audit\Pipeline\AuditPipeline.cs` — per-URL ruleHost + optional analyzers,
  per-rule isolation; cross-URL passes driven by the engine (runner), not the pipeline.
  AuditRuleHost = IEnumerable<IAuditRuleAnalyzer> family; AnalyzerRuleAdapter(ruleKeys,
  delegate) wraps any analyzer composition-only.

## Frontend wiring map

- App.tsx (34 lines) mounts CrawlForgeShell; **?route= ignored (G20)** — add window-route
  handling here (lead-owned).
- shell\CrawlForgeShell.tsx ViewContent :78–93 switches ShellView
  ('hub'|'audits'|'report'|'tools'|'settings' — shell\windowState.ts:10). Extend for
  project overview/configurations/editor (or nest under hub drill-down).
- shell\AuditViews.tsx ReportView :151–325 — live telemetry REAL; placeholder Paper
  :312–322 must be replaced by Dashboard + report navigation (Issues/URLs/Export/
  specialist entries). AuditsView :93–148 fine.
- shell\HubDashboard.tsx — create-project-modal :123 (3 inputs), project-row :299,
  start-audit-{id} :320, open-audit-{id} :192. Add Configurations affordance (UI-03),
  project-row click → overview (UI-02); embed config editor in create flow (UI-04).
- store\: crawlForgeStore.ts (main), hubDemux.ts (18-event contract — add
  local-notification CR-027 + any WP-09-06 events missing), apiClient.ts (routes catalog),
  liveProgress.ts, selectors.ts. New slices: project/config/run/report (WP-09-11 deliverable).
- surfaces\: dashboards\ (WP-09-08), explorers\issues\ (WP-09-08), explorers\urls\
  (WP-09-09), project\ (WP-09-07 NEW), export\ (WP-09-10 NEW). Shared explorer files
  (ExplorerHost.tsx, explorerTypes.ts, cells.tsx, columnUtils.ts, index.ts, explorers.css)
  are LEAD-owned.

## CRs applied this phase (do not reopen)

- CR-024: indexable = search-indexable (WP-09-05 evaluator; engine wiring lead).
- CR-025: gcGen*Count singular — spec follows code, no change.
- CR-026: schedule wire field `recurrence` — rename is WP-12-10 (phase 12). NO phase-09
  builder touches schedule code.
- CR-027: local-notification {title,message} hub event — lead adds frontend contract +
  conformance test in WP-09-11.
- CR-028: MySQL deferred; skipped tests stay skipped. Plan follow-up already committed
  (c94d855): CAP-L-X03 + criterion #18 removed from phase-12/15.
- Gate filter: waves 9A/9B sln test --filter KnownGap!=WP-09-06; wave 9C conformance
  UNFILTERED (KnownGap must be green after WP-09-06 + wiring).
- Phase-08 report numbering slip: export items G-F/G-I route to **WP-09-10** (not 09-09).

## Erratum (phase-08 report §5.6)

POST /api/system/shutdown DOES exist — CrawlForgeBackend.cs:131–135 (session-gated
minimal route, StopApplication). The phase-08 claim checked only SystemController.
CODEMAP §2 step 7 was correct; no CODEMAP change needed for it.

## Acceptances (wave 9A) — ALL FIVE ACCEPTED 2026-11-19T04:04Z

| WP | Builder | Commit | Lead re-run (canonical) |
|----|---------|--------|-------------------------|
| 09-01 | cf-wp-09-01 | a82625f | Sitemaps\|Fetching\|Frontier 277/0/0; builder full Crawler 865/0 (6 live fixture tests, port 47313, nonGet empty) |
| 09-02 | cf-wp-09-02 | 7e28946 | Limits\|Waf\|Politeness\|Canonicalization 271/0/0 |
| 09-03 | cf-wp-09-03 | 1a5b647 | Traps\|ListMode 194/0/0 deterministic |
| 09-04 | cf-wp-09-04 | 19c5006 | StructuredData 154/0/0 |
| 09-05 | cf-wp-09-05 | 7299f23 | Indexability\|ContentQuality 274/0/0; builder full Audit 760/0 |

Handoffs archived: handoffs/WP-09-01-HANDOFF.md (also in src Fetching\), handoffs/
WP-09-02-WIRING-HANDOFF.md; 03/04/05 handoffs inline in their final reports (this
session transcript) — key recipes below.

### Wiring decisions issued during 9A review
- **WAF double-recording (G07)**: RESOLVED by cf-wp-09-01 addendum (commit cd880d9,
  supersedes engine-only fallback): HttpFetcher ctor param 11
  IWafChallengeEvaluator? wafChallengeEvaluator=null — with aimd+evaluator injected,
  ONE transport sample carries IsChallenge (terminal response, 4096 snippet bound).
  WIRING: inject evaluator+aimd into engine fetcher :603-605; REMOVE engine-side
  aimd.RecordResponse :709-710; engine WAF evaluate :713 for record fields stays.
  Null evaluator = byte-identical baseline. 5 unit tests prove verdict-once.
- **Per-hop Set-Cookie**: RedirectChainSnapshot.HopSetCookies additive; cookie-aware
  RecordHopAsync overload (locked interface path unchanged); payload SetCookies
  additive JsonIgnore-when-null (canonical JSON unchanged). Recorder tolerates ONE
  cookie-carrying self-redirect per chain (cookie-aware path only) — cookie gate
  under fetch context live-proven. Transport jar STILL needs IFetchHook.AllowRevisit
  for gate self-redirects (visited guard sits above handler); keep
  AllowAutoRedirect=false on inner handler.
- **Challenge bodies**: engine :748 store when statusClass==Success || waf.IsChallenge
  (re-detection/render resolution needs them).
- **Redirect chain urlId**: PRE-CREATE url_records row before fetch to obtain urlId;
  FetchContextScope.Current = new FetchContext(auditId,urlId) around FetchAsync :693
  (try/finally null) — without scope the recorder is inert (G27 root cause per 01).
  FetchOutcomeClassifier.Classify replaces ClassifyStatus :716 (source 3xx +
  first-hop target + chain; terminal facts alongside). store.SaveOutcomeAsync after.
- **Cookie jar transport**: per-audit HttpClient with CookieJarMessageHandler (jar
  sees every hop's Set-Cookie; HttpFetcher follows redirects manually). Factory
  overload is Host\Composition (mine) OR direct new HttpClient(handler) in engine.
  IFetchHook (01) composes: BeforeSend/AfterReceive/AllowRevisit; jar live cookies
  flow to CrawlRequestAuthenticator.Decorate(request, inScope, jarCookies).
- **Robots**: replace engine robotsByHost dict (:598,:1390-1419) with RobotsCache;
  pass crawlDelaySource: robotsCache at aimd ctor :588 (G07 crawl-delay flow).
  RobotsBlockFindingFactory at :681-687 block branch (record first for urlId).
- **Canonicalizer**: per-audit new UrlCanonicalizer(CrawlScopeConfigParser.Parse(
  configuration.ScopeRulesJson).Config.Canonicalization) replaces singleton use at
  :639/discovery (G27 session segments).
- **CR-024**: NewUrlRecord :1574 → IndexabilityStateEvaluator.EvaluateFetch(entryUrl,
  fetch, robots, parser); parse HTML before record write (move :757 parse earlier) or
  re-evaluate post-parse via EvaluateContext (rendered-first). IsIndexable semantics
  propagate to NGram IndexableOnly (noindex pages drop from clustering — correct).
- **Site passes order** (AnalyzePhaseAsync after :835 ngram): hreflang reciprocity
  (04), duplicateMetadata, duplicateContent(scope), urlStructure (05) — all
  failure-isolated mirrors of RunNgramSitePassAsync; orphan/broken-link/sitemap
  correlation post-CRAWL (01, need link graph + stored payloads; broken-external
  probes distinct external targets once via IHttpFetcher).
- **Trap wiring** (03): per-audit CrawlerTrapDetector + CrawlAdmissionGate(listGate,
  detector, sampleSettings); Admit at enqueue :1465-1472 AND re-consult at dequeue
  :670; ObserveContent post-fetch with HashText(normalized visible text EXCLUDING
  title/nav); RecordFetched per fetch; DrainDetections → CrawlerTrapFindings.PersistAsync;
  DrainThrottleSignals → existing recorder/coalescer; PaginationDiscovery.Discover
  post-fetch feeds same admission path. ListSeedResolver.ResolveForAuditAsync BEFORE
  seed loop (G10 engine half); ListPreCrawlProbe at start (G27 pre-crawl, IPreCrawlValidator
  registered :101 never invoked).
- **Matrix REST drift** (WP-09-06): RedirectMatrixController:161 ToWireValue →
  MatrixVerdictNames.Of; :122 verdict filter → MatrixVerdictNames.Parse.
- **G10 Api ordering** (WP-09-06): AuditsController persist UrlListJson BEFORE engine
  start (:140 vs :154-160).
- **REST redirect chain** (WP-09-06): AuditExplorerController:215-217 elements query
  :205 already loads chain row; map payload.Hops → RedirectHopWire(Url, StatusCode,
  Location); legacy single-hop fallback when no row. Payload: hops[{ordinal,url,toUrl,
  statusCode}], finalUrl, finalStatusCode, loopDetected, chainCapExceeded.

### Notation-gap register for owner (locked Contracts/spec — NOT amended this phase)
1. AuditCategory: no Images member (spec §7.4 Images row) — image rules → Performance.
2. AuditCategory: no Security member (spec §7.4 Security & protocol, CR-017) — jar
   captures CookieSecurityObservation DATA; D2 finding emitter deferred.
3. AuditCategory: no Internationalization member — hreflang conflicts → StructuredData.
4. IndexabilityFlags: no canonical bit — canonical disqualifiers ride Reasons;
   additive CanonicalConflict=16 needs Contracts amendment.
5. WafChallengeClass: 4 locked members vs 5 spec classes (managed-challenge,
   rate-limit-soft-block missing); WafProvider.Generic→Other mapping; WafSignature
   lacks first-class facet arrays (loader accepts additive titleMarkers/domMarkers/
   scriptMarkers; bundled asset upgrade = coordinator/owner item, asset not builder-owned).
6. FindingRuleKeys (Contracts) lacks additive keys: crawler-trap.detected,
   canonicalization.session-segment-removed, crawl.cookieless-redirect-loop,
   ua-comparison.material-difference, redirects.chain, content.duplicate-*,
   url.*, image.*, structured-data.missing-recommended — all defined LOCALLY in
   builder folders; catalog registration inert anyway (CODEMAP gap #21).
7. Image transferred bytes NOT observable in http mode (ProxyCapturedRequest has no
   size field) — oversized rule fires for data-URI today; subresource capture =
   rendering phase (10+). EvaluateImages accepts explicit facts for future sources.
8. "Inappropriate redirect" (CAP-B-051): no check definition in architecture —
   configurable RedirectInappropriatenessChecks (CrossHost|TemporaryStatus 302/303/307,
   default All) documented pending owner confirmation.
9. §7.4 "deprecated types" example check not specified by §7.13/CAP-B2-069..076 —
   not implemented (coverage question, not invented).
10. Microdata itemprop terms kept raw (pre-existing) — URL/prefixed terms don't
    local-name-resolve unlike RDFa; future CR candidate.
11. Phase-08 report erratum: POST /api/system/shutdown EXISTS (CrawlForgeBackend.cs:131).

## WP-09-11 execution plan (lead serial; after wave-9B gate)

Scope note: YAML owned_paths list Host\Execution, Host\Composition, Audit\Pipeline,
App.tsx, store\, shell\, explorers\ shared files. The PROMPT additionally assigns the
lead: conformance project (tests\CrawlForge.ContractConformance.Tests), CODEMAP.md,
contracts\hub\events.ts (CR-027), fixture-harness README. No other workpiece owns
these → no overlap; proceed per prompt, record in report.

Order:
A. DI (EngineServicesModule RegisterCrawler/RegisterAudit): all 9A components per
   handoffs (trap detector/pagination/authenticator singletons; per-audit gate/
   canonicalizer/jar constructed in engine; structured-data + hreflang analyzers;
   duplicate/urlstructure/image analyzers; sitemap/orphan/brokenlink/redirect
   analyzers + recorder + IRedirectChainStore; FindingEmitter catalog fix #21).
B. Engine crawl phase: fetcher composition (CookieJarMessageHandler-wrapped client,
   IFetchHook jar/rewrite, wafChallengeEvaluator+aimd injection [addendum design],
   proxy route + signed handler per 03 recipe); REMOVE engine aimd.RecordResponse
   :709-710; pre-create url_records row for urlId + FetchContextScope around fetch;
   FetchOutcomeClassifier replaces ClassifyStatus; chain SaveOutcome; CR-024
   EvaluateFetch at NewUrlRecord (parse HTML first); RobotsCache + crawlDelaySource +
   RobotsBlockFindingFactory; per-audit UrlCanonicalizer from ScopeRulesJson;
   CrawlAdmissionGate at enqueue+dequeue; ObserveContent(HashText(visible text excl
   title/nav)); RecordFetched; DrainDetections→CrawlerTrapFindings; throttle signals;
   PaginationDiscovery; sitemap seeding after seeds; ListSeedResolver (G10 engine
   half); ListPreCrawlProbe at start; challenge bodies stored (:748).
C. Post-passes: post-crawl orphan/broken-link/sitemap-correlation (failure-isolated);
   AnalyzePhaseAsync after :835: hreflang reciprocity, duplicateMetadata,
   duplicateContent(scope), urlStructure site passes (mirror RunNgramSitePassAsync).
D. Pipeline: verify soft-404 single emission with production DI; RDFa inventory rows
   in ElementInventoryExtractor if UI-16 needs (decide at wiring).
E. Hub/engine events: G05 url-batch/findings-updated/audit-failed emission points
   (09-06 handoff); CR-027 local-notification {title,message} — events.ts additive
   entry + HubLocalNotificationSink shape check + frontend demux → OS Notification;
   string ids alignment check.
F. Frontend wiring: store slices (project/config/run/report); App.tsx ?route= (G20);
   shell mounts: ProjectOverview on project-row click; ConfigurationsList + hub
   "Configurations" affordance (UI-03); ConfigurationEditor EMBEDDED in
   create-project-modal (UI-04: ≥1 tab + >3 inputs in modal dump); ReportView:
   remove placeholder Paper :312-322, mount Dashboard (dashboard-charts) default,
   nav buttons "Issue explorer"/"URL explorer"/"Export" (_has_nav buttons/testids),
   mount IssueExplorer (issues-grid)/UrlExplorer (url-explorer)/ExportDialog;
   Settings: ExportDestinationsSection ("Export destinations"); hubDemux
   local-notification; apiClient additions per 9B handoffs.
G. Conformance (lead-owned): NEW suites per builder payloads —
   RedirectChainPersistence/SitemapIngestion/SitemapRedirectFindingShape (01);
   CrawlerTrap/ListModeStart/SignedRequestProxy (03); StructuredDataFinding/
   HreflangReciprocityFinding (04); ThinContent/DuplicateTitle/Soft404Dedup/
   HeaderHtmlConflict (05); cookie-jar/robots/canonicalization/WAF-AIMD suites (02
   handoff §9); Flow1EndToEnd (harness-driven). UNSKIP/extend: G-COMPLETED-COUNTERS
   (HubEventWireConformanceTests:151), Reaudit_RestStartedAudit_SpecShape,
   MatrixAutoSeed, DuplicateAuditStart_IsIdempotentWhileActive, G05 no-producer
   events, MatrixRowValidated_FrontendContractShape. KnownGap AuditCounters goes
   green from 09-06 mapper fix (verify unfiltered).
H. CODEMAP.md: new registrations/mounts, gaps closed (#21 catalog, G03 note stays
   out-of-scope), new surfaces; fixture-harness README updates.
I. Phase gate: run-phase-gates.ps1 -Phase phase-09 (after_each_wave 8 + wave-9C 3:
   conformance UNFILTERED, fixture 17 traps http, UI walk 9 surfaces). Scale tests
   in sln gate: PipelineScaleTests 10k live crawl ≈40 min (95-min hang guard) —
   gate 4 duration is EXPECTED, not a hang. Evidence side effects restored after.
J. Criteria #49/#55/#56 evidence (UI-started per CR-023), nonGet==[], report +
   final commit; phase completed in ledger.

## Wave-9A gate run 1 (2026-11-19 06:10–07:11 local): FAIL → fixes applied

- Gates 1-3 PASS (Host build 0/0, frontend build, electron tsc).
- Gate 4 (sln, KnownGap filter): 2 failures out of ~3300 tests:
  1. Acceptance.Tests EvidenceIntegrityTests criterion #28: proof member
     'Soft404Page_EmitsDistinctWarning_WhenDetectorInjected' missing — cf-wp-09-05
     renamed it during the approved soft-404 single-emission refactor. FIX: 05 renames
     the OPT-IN test back to the exact catalog name (AcceptanceCatalog.cs:187 pointer,
     read-only), assertions kept. Routed to builder.
  2. Api.Tests ToolsRouteTests.UrlCheck_DetectsWafChallenge_FromCatalogue: asserted
     challenge on 403 = pre-spec behavior. Spec §2.4.5 (03/02:228,241): only 2xx+HTML
     transitions to waf-challenge; non-2xx signature = annotation only. 02's evaluator
     is CORRECT; test stale. COORDINATOR FIX (commit 8530f75, Api.Tests = 09-06 scope,
     pre-dispatch, noted in brief): 200+HTML challenge scenario + new 403
     annotation-only test. Api.Tests 220/0/0.
- All other projects green incl. Crawler 878/0, Audit 760/0, E2E 82/0 (4 skipped),
  ContractConformance 92/0/23 filtered, Scale.Benchmarks 10/0 (54m42s — PipelineScale
  10k live crawl ≈40 min is EXPECTED gate duration, not a hang).
- Gate 4 duration note: sln gate includes Scale Category (10k pipeline ≈40 min +
  export/seed scale). Every full-sln gate run costs ~55 min. Plan accordingly.
- After 05 rename: full gate re-run (all 8) for clean evidence.

### Conformance unskip map (23 skips inventoried; phase-09 targets)
UNSKIP after fixes land (verify each green unfiltered at wave-9C):
- AuditCountersWireConformanceTests:137 G-HUB-COUNTERS (09-06 mapper + final counters)
- HubEventWireConformanceTests:83 G-PROGRESS (09-06 keys + 09-11 throughput/phase + string ids)
- :151 G-COMPLETED-COUNTERS (09-06)
- :456 G-MATRIX-VERDICT (03 DONE — unskip at wiring, should pass)
- :368 G-F export-delivery ids (09-10; skip text's "09-09" routing is stale — prompt routes G-F/G-I to 09-10)
- :408 G-I export-progress catalog (09-10 producer + lead events.ts additive entry)
- :534/:541 G05 url-batch/findings-updated (09-06 Api surface + 09-11 engine emission)
- :600 G-N local-notification (CR-027: lead events.ts + catalog + HubLocalNotificationSink shape)
- :661 G-G numeric auditId audit-scoped events (09-11 string ids)
- RestEnvelopeConformanceTests:352 G-REST-IDEMPOTENCY duplicate start (09-06 controller)
- :416 G-REAUDIT-SNAPSHOT (09-06 + 09-11 engine ReAuditAsync:433)
- :436 G-MATRIX-SEED-RACE (03 ingestor DONE; 09-06 RedirectMatrixController:94-95 autoSeed path)
INVESTIGATE at wiring (unskip if producer exists / phase-09 scope):
- :168 audit-failed (Host.Tests FailedAuditVisibilityTests covers producer; deterministic trigger?)
- :221 G-S gcGen singular (CR-025 notation — check if spec amended; align test)
- :548 backend-lifecycle / :555 waf-blocks / :562/:569 embedding-* (producers may exist)
LEAVE SKIPPED (other phases, record): :301/:332 G-D/G-E schedule (phase-14),
- :502 G-G importId (log-import phase), McpWire:380 G-MCP-PAGING (phase-13).
KnownGap FAILING test AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters
→ green from 09-06 mapper fix (no test edit needed).

### Frontend wiring recon (done while 9B builds)
- G20: Electron window-catalog routes = splash/hub/report/visualization/analyzer/
  tools/settings/recovery (EL/windows/window-catalog.ts), ?route= appended at
  EL/main.ts:241. App.tsx wiring: parse location.search; route WINS over persisted
  localStorage view; map hub/report/tools/settings (+audits) to ShellView;
  visualization/analyzer/recovery/splash surfaces are later-phase → fall back hub
  (record in CODEMAP). Support ?route=report&auditId=X → openAudit.
- ShellView (windowState.ts) extend: +'project' +'configurations'; ShellWindowState
  +selectedProjectId (validated in sanitizeWindowState); ShellNavigation
  +openProject/openConfigurations. ViewContent switch mounts ProjectOverview/
  ConfigurationsList (09-07 components).
- ReportView: replace placeholder Paper :312-322 with report workspace: nav
  BUTTONS "Dashboard"/"Issue explorer"/"URL explorer"/"Export" (_has_nav counts
  buttons/testids only), default Dashboard (dashboard-charts), mount 09-08/09/10
  components; report-state badge must render UPPERCASE state (walker regex
  \b(COMPLETED|RUNNING|...)\b case-sensitive).
- HubDashboard: project-row onClick → openProject (walker clicks the TR);
  hub "Configurations" button (UI-03 marker); CreateProjectModal embeds
  ConfigurationEditor (UI-04: modal dump ≥1 tab name + >3 inputs; modal already
  has 3 inputs, editor Scope tab adds >3 more); submit applies editor DTO.
- events.ts CR-027: additive LocalNotificationPayload {title,message} + union +
  catalog(additive:true) + names list + header counts; hubDemux handler → OS
  Notification (guarded) + activity + relay. export-progress entry after 09-10
  proposal. Engine crawl-progress payload must gain throughputPerSecond/
  elapsedSeconds/estimatedRemainderSeconds/phase + WIRE counter keys + STRING
  auditId (G-PROGRESS/G-G); PublishStageAsync numeric auditId → string too.
- Store: locked CrawlForgeState untouched; new UI state shell-local; 9B components
  props-driven; apiClient generic over API_ROUTES (new route entries only if the
  REST spec defines them — case-by-case, recorded).

## Wave-9B acceptances

| WP | Builder | Commit | Verification (lead re-run) | Notes |
|---|---|---|---|---|
| WP-09-07 | cf-wp-09-01 | eadfbed (+ledger c29cc11) | frontend build 487ms; project tests 4 files/49 passed | 13 tabs verbatim; cookie-jar toggle → scopeRules.cookieJar.enabled; DTO extension relayed to 06 (D4); npm root flag-parsing quirk: use --workspaces --if-present -- form |
| WP-09-08 | cf-wp-09-04 | 25c46af (+ledger 22eb635) | frontend build 499ms; report tests 3 files/62 passed | dashboard-charts/issues-grid testids; ≤50 DOM rows @10k fixture; suppression 72→80→72 reversible; builder regression 59 files/750 |
| WP-09-10 | cf-wp-09-03 | 0ab0f06 (+ledger 22eb635) | Export build 0 err; Export.Tests 88/88; export UI tests 17/17 (lead-verify isolation) | G-F/G-I producer shapes exact-frozen; additive json/jsonl/text/markdown surface; csproj untouched |
| WP-09-06 | cf-wp-09-02 | 43f30b2 (+ledger c50d5ec) | Api.Tests 275/275; Host build 0/0; AuditCounters 5/5 canonical (lead) | D1 mapper+normalizer LIVE (KnownGap green); D2 ports IEnumerable-safe; D3 publisher+config fields LIVE; D4 chain/matrix vocabulary LIVE; engine recipes archived |
| WP-09-09 | cf-wp-09-05 | d4a9cbe (+ledger 3ed3fef) | frontend build 442ms; urls tests 4 files/68 passed | url-explorer testid; ≤50 DOM @10k; CR-024 verdict; 3-provenance noindex; dual canonical; 4 chain wire forms; SERP consumes existing measurer |

Wave 9B: 5/5 accepted. Builder crashes this wave: 2 (cf-wp-09-03 early,
cf-wp-09-02 mid-D1; both resumed via send_message, no work lost). Sibling
compile churn: Export writers mid-flight blocked 06 verification once —
resolved by landing writer classes together (lead-steered).

Spec-silent observations (NOT defects, no routing — recorded for owner):
- GET suppressions list: service exists backend-side but 05/01:84 defines only
  POST/DELETE. 08's initialSuppressions session-seeding is spec-compliant.
- Findings wire has no suppressed flag/filter (05/01 silent): 08's all|suppressed
  server-scoped view with honest totals is compliant; 'active-only' needs a CR.
- Contracts ExportFormat enum lacks Json/Text/Markdown (locked): additive engine
  surface (AdditiveExportFormats wire keys) is the interim truth; amendment CR
  for owner. Api TryParseFormat must accept the additive keys (wiring item).
- Webhook destination kind kept for wire parity (CR-016 excludes notification
  webhooks from the UI classes — owner may restrict the section to 3 classes).
- GET /api/exports/{id} (job status) + POST /api/destinations/{id}/deliver exist
  backend-side but are NOT in the 05/01 route table → routes.ts stays frozen
  (no spec basis to add); ExportDialog's bounded exports.download polling is the
  spec-faithful phase-09 path (10 designed for exactly this). Owner CR if wanted.

### Lead wiring landed early (while 9B finished)
- fe1ac88: events.ts += local-notification (CR-027) + export-progress (G-I);
  guards + hubDemux handlers (guarded OS Notification; activity+relay);
  contract tests LOCK new counts (20 server/22 names/21 contracts) + rejection
  cases; ErrorBoundary override fix (shared typecheck clean). 167/167
  contracts+shell tests; tsc clean except in-flight 09 urls tests.
- HubEventCatalog.cs += ExportProgress constant + 1s default interval
  (LocalNotification already existed backend-side from the scheduler work).
- Conformance unskips/inversions (lead-owned): G-COMPLETED-COUNTERS, G-F,
  G-MATRIX-VERDICT, G-G unskipped (normalizer + 03/10 fixes); G-I + G-N
  absence-locks REPLACED with presence+shape locks (ExportProgress_Cataloged-
  ContractShape, LocalNotification_InFrontendContract). Still skipped pending
  engine wiring: G-PROGRESS (throughput/phase fields), G05 url-batch/findings-
  updated absence-locks (invert when producers land), audit-failed (no
  deterministic trigger), backend-lifecycle/waf-blocks/embedding-* absence-locks
  (investigate producers at wiring), schedule/importId/McpWire (later phases).
- G05 producer investigation (read-only, during 9B gate): url-batch/findings-
  updated publisher EXISTS (06 AuditRunEventPublisher) — engine call sites =
  wiring; backend-lifecycle NO producer — wire at CrawlForgeBackend startup/
  shutdown (spec §10.3 defines it; LifecycleBanner consumes it); waf-blocks NO
  producer — wire at engine WAF recording point (throttled batches ≤1/s);
  embedding-progress/completed NO producer — phase-12 semantics scope, keep
  absence-locks skipped; audit-failed producer EXISTS (FinalizeAsync:1270 +
  06 Api start-failed) — skip stays (no deterministic fixture trigger).
  Invert the url-batch/findings-updated/backend-lifecycle/waf-blocks absence-
  locks ONLY after the producers land (defect-locks must not fail mid-wiring).

### Wave-9B gate run 1 (2026-11-19 10:31–11:30 local): FAIL at gate 4

15/19 test projects green incl. Acceptance 72/72 + Scale 10/10 (51m). Seven
failures in 4 projects — ALL stale cross-phase assertions vs the newly locked
contract (none in 9B builder owned_paths; coordinator alignment fixes per the
9A ToolsRouteTests precedent, prompt gate rule "route to owner" inapplicable —
no 9B owner for E2E/Host/Intelligence/inventory tests):

1. PayloadInventoryTests ×2 (conformance, lead-owned): coverage refs pointed at
   the renamed absence-locks (5ad460f) + hardcoded 18/19 event counts. Fixed:
   refs → LocalNotification_InFrontendContract / ExportProgress_Cataloged-
   ContractShape; counts 20/20; inventory JSON flags/counts/gap text updated;
   GAPS.md G-I/G-N marked RESOLVED; drift-test doc comments updated.
2. HubSequencingE2ETests.PayloadAuditId:205 — TryGetInt64 THROWS on string
   tokens; G-G normalizer now emits string auditId. Fixed: ValueKind switch
   (Number→TryGetInt64, String→long.TryParse).
3. ReopenBatchWiringTests:67 + AimdHostThrottleE2ETests:85 — GetInt64 on string
   host-throttle auditId. Fixed: string-tolerant parse (same G-G basis).
4. ListModeE2ETests:392-398 — old unhyphenated verdict vocabulary. Spec §10.3
   + criterion #43 name the verdicts target-mismatch/chain-exceeded/final-non-
   200/canonical-mismatch (hyphenated); 03 MatrixVerdictNames + REST filter
   validation already conform; frontend MatrixRowVerdict is open string.
   Fixed E2E expectations to the spec vocabulary.
5. SemanticPipelineTests.ProgressReportsFinalCompletion (Intelligence) —
   passes in isolation (733ms): load-timing flake, no code change; recorded.

McpDelegationE2ETests numeric auditId GetInt64 audited: MCP responses are NOT
hub-normalized (phase-13 wire), passed in run 1 — left unchanged.

Re-run: 4 projects targeted (job pwsh-b6e8a650f8514b44), then gates 5-8.
Gate-4 PASS composition documented: run-1 15 green projects + targeted re-run.
The full UNFILTERED phase gate (wave 9C) re-runs everything regardless.

## Engine emission design (WP-09-11, decided during 9B gate)
- AuditRunCounters += Blocked/WafBlocked (live increments at classification:
  statusClass==Blocked / waf.IsChallenge); ToPayload += blocked/wafBlocked
  (mapper passthrough; normalizer keeps Numbers; robots-Disallowed rows are
  NotCrawled class, NOT blocked — consistent with REST row mapping).
- AuditRunState += pending url-batch entries + waf-block entries +
  LastRunEventFlushAt + LastFindingCounts snapshot. Flush cadence ≤1/s inside
  PublishProgressAsync (progress already 1/s-throttled) + explicit flush at
  crawl-phase end and before finalize. url-batch via 06 AuditRunEventPublisher
  (lossless); waf-blocks via _hub direct (entry {urlId,canonicalUrl,provider,
  challengeClass,confidence}, wire enum strings, ThrottleInterval 1s).
- findings-updated deltas DERIVED at flush: COUNT findings GROUP BY severity/
  category vs state snapshot (captures ALL producers: pipeline, recorders,
  analyzers — no invasive per-emit hooks); emit only non-zero deltas.
- url-batch entries only for rows with an OBSERVED HTTP status (fetched rows;
  WAF challenges carry observed class + blockReason/wafProvider per Appendix C);
  fetch-error/robots-not-crawled rows have no HTTP class → counters + REST list
  surface them (interpretation recorded; publisher validates 1xx-5xx).
- crawl-progress payload += phase (ApiEnumConverter<AuditPhase> wire),
  elapsedSeconds/throughputPerSecond/estimatedRemainderSeconds per 06 handoff
  §1 derivations (match ToDetail exactly; discovered-based remainder).
- backend-lifecycle: emit {kind:'ready'} after app.StartAsync in
  CrawlForgeBackend.CreateAsync (:164+) and {kind:'shutdown'} at StopAsync
  start (:192, hub still up). POST /api/system/shutdown exists :131 (erratum).

Lead conformance-harness fixes (WP-09-11 scope, done early to unblock 06):
LiveBackendFixture export-delivery matcher → exportId string-tolerant (G-F producer
fix exposed legacy exportJobId/GetInt64 drift); log-import-completed matcher →
string-tolerant (G-G normalizer). All other GetInt64 in the project verified safe
(numeric counters/pagination/MCP responses). NOT oracle weakening: matchers now
follow the LOCKED events.ts shapes; assertions unchanged.

## Coordination log

- 2026-11-19 ~06:58Z: wave-9A gate PASS 8/8 (run 3, 07:17-08:55 local; commit
  5b9f337 incl. gate summaries + this record). Run-1 failures fixed: criterion #28
  proof member (e3831c7) + url-check WAF spec alignment (8530f75). Gate-timing
  observations: sln gate ~78 min this run (Scale 10k pipeline ~40 min + Acceptance
  16 min + conformance-in-sln 17 min under parallel contention); gate-5 standalone
  conformance had a ~15 min pre-build queue delay (MSBuild node contention after
  the sln run) then ran 18 min — NOT a hang; backend scratch logs prove progress.
- 2026-11-19 06:58Z: wave-9B claimed (1852bdf): 06→cf-wp-09-02, 07→cf-wp-09-01,
  08→cf-wp-09-04, 09→cf-wp-09-05, 10→cf-wp-09-03; index pending 39/in_progress 5;
  check_buildplan PASS. Team tasks task-9..13 with write scopes. All five builders
  dispatched with full briefs (briefs committed in 5b9f337, env lines added after).

- 2026-11-19 ~02:42Z wave-9A claims committed 334914a; five builders dispatched.
- cf-wp-09-02 turn interrupted by runtime failure (~04:55 local) before any file writes;
  resumed via send_message, read-first complete, implementing (Canonicalization files
  already modified on disk).
- cf-wp-09-01 + cf-wp-09-05 turns also interrupted by runtime failures (no writes);
  resumed via send_message with answers/status.
- Read-tool staleness observed on phase-09 YAML (owner commits between phases): verify
  externally-changed files via pwsh before editing; temp-copy trick for fresh reads.

## Decisions issued to builders (lead rulings, spec-cited)

- **Finding categories (spec 04/01 §7.4 authoritative)**: links.orphan + sitemap.* +
  redirects.* → AuditCategory.Indexability (spec lists orphan pages/sitemap coverage/
  redirect chains+loops under "Indexability & status"); links.broken-internal/external →
  Linking; crawler-trap.detected → Crawl (=6, enum doc names crawl-health). Notation slip:
  Contracts AuditEnums.cs:35 doc-comment puts orphan under Linking — spec prevails; record.
- **Severities**: spec §7.5 leaves severity to rule definitions (configurable thresholds).
  Approved cf-wp-09-01 defaults: broken-internal Error, broken-external Warning, orphan
  Warning, redirects.loop Error, redirects.inappropriate Warning, sitemap.* Warning.
- **SPEC GAP (loop §8, for owner)**: "inappropriate redirect" (CAP-B-051) has NO check
  definition anywhere in architecture. cf-wp-09-01 implements explicit configurable check
  flags defaulting to cross-host target + temporary status (302/303/307), documented
  "pending owner confirmation". Recorded in phase report ambiguity register.
- **Redirect persistence design REVISED (lead ruling, supersedes column amendment)**:
  persist chains via EXISTING url_elements table (element_group "redirect-chain" local
  constant + payload_json hops; HeaderDirectivesItem precedent AuditPipeline.cs:293–312;
  SchemaCatalog.Baseline.cs:99–107 "new element groups arrive additively"). ZERO schema/
  DTO/Contracts change. IRedirectChainStore over IWriteRepository<UrlElementDto>; engine
  calls store at fetch completion (WP-09-11); REST mapper reads group → UrlDetailWire.
  RedirectChain (WP-09-06 handoff). Column amendment only as evidence-backed fallback.
- **NO-AMENDMENT RULING (phase-09 prompt: buildplan edits = status fields only)**: no new
  amendment workpieces this phase. Locked-contract gaps are RECORDED for the owner, not
  worked around by plan edits: (a) AuditCategory has no Images member (spec §7.4 Images
  row) — cf-wp-09-05 maps image payload/format rules → Performance, documented; (b) no
  Security member (spec §7.4 Security & protocol, CR-017) — cf-wp-09-02 jar captures
  CookieSecurityObservation data only; D2 finding emitter deferred to its later workpiece.
  Proposed future CR: additive enum members Images + Security. Both in report ambiguity
  register. (Contrast phase-08, whose prompt explicitly sanctioned §11 amendment records.)
- **Additive rule keys** stay in builder folders (IndexabilityRuleKeys.cs precedent);
  locked Contracts untouched; desired additions listed in handoffs.
- cf-wp-09-03 plan approved (Traps\ detector + pagination, ListMode\ fixes incl.
  G-MATRIX-VERDICT component side + seed-race-safe seeding + ListSeedResolver for G10,
  input-mode components; Proxy\/fetcher/engine/Api items as handoff recipes).
  CrawlRequestAuthenticator takes cf-wp-09-02 jar cookies as optional merge input;
  jar exposes GetCookies()/GetCookieMap(origin)/MergeCookies(origin,cookies,source)
  (relayed both ways; signatures in 02's handoff).
- cf-wp-09-05 root causes confirmed: thin-text ratio-only (thin.html ratio 0.117 > 0.10,
  no word-count check); soft-404 double emission = IndexabilityAnalyzer step 5 +
  AuditPipeline ISoft404Detector step → single emission via pipeline step, analyzer
  opt-in default-off (no Pipeline edit needed); duplicate-title = new cross-URL site pass
  (D14 pattern), harness matches substring "duplicate-title".