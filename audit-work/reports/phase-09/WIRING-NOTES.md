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

## Coordination log

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