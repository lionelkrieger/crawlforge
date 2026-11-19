# WP-08-03 — Cross-Layer Payload Gap Inventory

Contract-conformance harness findings. Every gap below was observed against a
**live loopback backend** started by the harness (scratch data directory, own
fixture on port 47312) or against the frozen contract sources, with exact
citations. Per the workpiece packet and the lead's confirmation (2026-11-18),
**only the REST counter-key mismatch (G-COUNTERS / WP-09-06) gets a failing
test** (`[Trait("KnownGap","WP-09-06")]`); every other gap is recorded here and
covered by a contract-side assertion and/or a skipped-with-reason live test.
Suggested owners are routing proposals for the lead/orchestrator — this
workpiece does not edit the plan, the register, or the architecture.

Spec paths are relative to `architecture/`; code paths relative to `app/`.

---

## G-MATRIX-VERDICT — matrix-row-validated numeric enum verdict + numeric urlId

- **Wire boundary:** execution engine → hub → shell (matrix-row-validated).
- **Producer:** `MatrixRowValidatedItem(long UrlId, string SourceUrl,
  RedirectMatrixVerdict Verdict)`
  (`src/backend/CrawlForge.Crawler/ListMode/ListModeModels.cs:213`), serialized
  by the hub's `PayloadOptions` (default System.Text.Json → enums as NUMBERS,
  ids as numbers). Live capture: `{"auditId":1,"rows":[{"urlId":3,"sourceUrl":
  "http://127.0.0.1:47312/redirect/chain-1","verdict":0}]}`
  (`captured/hub-matrix-row-validated.json`).
- **Consumer:** frozen `MatrixRowValidatedEntry`
  (`src/frontend/src/contracts/hub/events.ts:180-184`) requires
  `urlId: EntityId` (string) and `verdict: string`; the payload auditId is
  likewise numeric (G-G family).
- **Tests:** `HubEventWireConformanceTests.MatrixRowValidated_LivePayload_SpecShape`
  (passes; producer dimensions + batch shape, full payload captured) and
  `MatrixRowValidated_FrontendContractShape` (skipped, this gap). No defect-lock.
- **Suggested owner (lead routing):** phase-09 wiring family WP-09-11 alongside
  the other hub drifts (hub enum/id serialization).

## G-NOINDEX-FLAG-QUESTION — noindex trap reported indexable (semantics question, NOT asserted)

- **Observation:** the fixture's `/pages/noindex.html` serves
  `<meta name="robots" content="noindex, follow">`
  (`tests/fixture-site/pages.mjs:46`), yet the engine stores
  `is_indexable=1`, `indexability_flags=0` (scratch DB `url_records`, every
  audit of run 6) and the REST wire reports `indexable: true`
  (`captured/trap-noindex-url-row.json`, `captured/storage-noindex-row.json`).
  Indexability FINDINGS are produced (summary `countsByCategory.indexability`
  = 266 over the fixture audit), so the rule engine sees the page.
- **Why not asserted:** trap SEMANTICS belong to cf-wp-08-04, whose noindex
  verdict keys on the `indexability.noindex` finding (consistent with probe-2's
  pass, per lead). This may be a rule-engine flag defect OR a field-semantics
  ambiguity (`is_indexable` = HTTP-fetchable vs search-indexable). The lead
  records it in the phase report for owner routing.
- **Tests:** `FixtureTrapCaptureTests.NoindexTrap_PayloadsCapturedAndAsserted`
  and `StorageEngineConformanceTests.Noindex_UrlRecord_Shape` assert SHAPE only
  (boolean/INTEGER dimensions present, status 200) and capture the observed
  values as evidence — neither value is locked.

## G-CRON-NAMING — schedule wire field "cron" carries interval:/calendar: rules (notation)

- The frozen wire `ScheduleCreateRequest.Cron`
  (`src/backend/CrawlForge.Api/Controllers/OperationsWireContracts.cs:109`)
  is parsed by the engine as a `RecurrenceRule` expression —
  `interval:<timespan>` or `calendar:HH:mm` (FS-CRAWL-001 §6.1;
  `src/backend/CrawlForge.Crawler/Scheduling/RecurrenceRule.cs:90-121`), NOT a
  cron expression. A null/empty value fails firing with "Invalid recurrence
  rule" (run-now → 409 conflict). The harness drives
  `cron: "interval:1.00:00:00"`. Field-name vs content-format divergence —
  spec-notation item for lead/owner routing (no functional defect when the
  documented rule format is used).

## G-REAUDIT-SNAPSHOT — configuration snapshot wire-vs-entity mismatch — RESOLVED (WP-09-11)

- RESOLVED by the WP-09-11 serial wiring: `AuditsController.StartAuditAsync`
  now merges `enabledCategories` into the engine's DTO-shaped storage snapshot
  (`ParseSnapshotObject`; absent/empty/malformed → fresh DTO serialization)
  instead of overwriting it with the wire projection. Re-audit of REST-started
  audits deserializes cleanly; the probabilistic REST-start crawl failure is
  gone (both snapshot writers now agree on the DTO shape).
- **Tests:** `Reaudit_RestStartedAudit_SpecShape` UNSKIPPED (live created
  envelope + new audit id); `ReauditWireContract_Shape` unchanged. The
  MCP-started offline zero-network proof stays live.
- **Owner:** WP-09-11 (done). Original evidence below retained for the record.

- **Wire boundary:** REST API layer → execution engine (stored configuration
  snapshot).
- **Overwrite side:** `AuditsController.StartAsync`
  (`src/backend/CrawlForge.Api/Controllers/AuditsController.cs:146-160`)
  replaces the engine's DTO-shaped `ConfigurationSnapshotJson` with the WIRE
  configuration (`ProjectWireMapper.ToConfiguration` → `AuditConfigurationWire`:
  string ids, `startUrls`/`limits`/`rendering`/`description`).
- **Read side:** `AuditExecutionEngine.ReAuditAsync`
  (`src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:436-437`)
  deserializes the snapshot as `CrawlConfigurationDto` (long `Id`, `seedsJson`)
  with `PayloadJson` → `System.Text.Json.JsonException` at `$.id` ("Cannot get
  the value of a token type 'String' as a number") → **unhandled 500, empty
  body, no envelope**, for BOTH refresh modes. `RunAuditAsync:497-499` and
  `RunAnalysisOnlyAsync:888-890` read the same snapshot as the DTO: the main
  REST crawl only survives because the engine task usually reads the row
  BEFORE the controller's overwrite lands — a **probabilistic REST start
  failure** (cf-wp-08-04 independently observed a bulk audit fail at start
  with the identical `$.id` error; lead merged both confirmations into one
  defect entry).
- **Scope:** audits started via MCP / scheduler / matrix keep the engine DTO
  snapshot (no controller overwrite) and re-audit cleanly. This is NEW
  evidence refining probe-2: RUNTIME-PROBE.md §6 records "re-audit | fails |
  error deserializing $.id" — consistent with a REST-started target; probe-2
  never tried re-audit of an MCP-started audit.
- **Evidence:** `captured/gap-reaudit-rest-start-response.json` (raw 500);
  scratch `run-20261118-000314-654970b7/backend-console.log` (full
  JsonException stack) and the scratch DB snapshot
  (`{"id":"1","projectId":"1","name":"fixture-http-mode","description":null,...}`).
- **Tests:** `RestEnvelopeConformanceTests.ReauditWireContract_Shape`
  (passes; frozen wire contract intact at the API boundary) and
  `Reaudit_RestStartedAudit_SpecShape` (skipped, this gap). The harness keeps
  the storage-to-engine zero-network proof LIVE by re-auditing an MCP-started
  source audit offline (`StorageEngineConformanceTests.OfflineReaudit_ReadsStoredArtifacts_ZeroNetwork`,
  `McpWireConformanceTests.ToolCall_ReAudit_ReturnsNewAuditId`). Per the lead's
  ownership steer, NO test locks the broken 500 as expected behavior.
- **Owners (lead-routed):** WP-09-06 (owns `CrawlForge.Api\Controllers\` — the
  overwrite side) + WP-09-11 (owns `Host\Execution\` — the `PayloadJson` read
  side).

## G-MATRIX-SEED-RACE — redirect-matrix autoSeedAudit crawl/ingest duplicate insert — RESOLVED (WP-09-11)

- RESOLVED by the WP-09-11 crawl-loop wiring: `GetOrCreateUrlRowAsync` reuses
  pre-seeded `url_records` rows (matrix/list) instead of inserting duplicates,
  and the fresh-start path enqueues pre-seeded NotCrawled rows through the
  admission gate with seen-dedup. The seeded validation audit completes.
- **Tests:** `MatrixAutoSeed_StartsValidationAudit_SpecShape` UNSKIPPED (live
  autoSeedAudit:true → completed validation audit).
- **Owner:** WP-09-11 (done). Original evidence below retained for the record.

- **Wire boundary:** REST redirect-matrix upload → execution engine + URL
  ingestor (shared `url_records` table).
- **Producer:** `RedirectMatrixController.Upload`
  (`src/backend/CrawlForge.Api/Controllers/RedirectMatrixController.cs:94-95`)
  with `autoSeedAudit:true` calls `StartAuditAsync` (which immediately queues a
  full crawl of the SOURCE configuration) and then `IngestAsync` with the
  matrix source URLs — concurrently, with no dedup coordination.
- **Observed:** the matrix source URL (`/redirect/chain-1`) is also a crawl
  seed/discovered URL, so both inserters write the same canonical URL:
  `SQLite Error 19: UNIQUE constraint failed: url_records.audit_id,
  url_records.canonical_url` in `AuditExecutionEngine.CrawlPhaseAsync` →
  `SqlRepository.CreateAsync` → the seeded audit fails (run 5, audit 4). If the
  crawl inserter wins instead, `IngestAsync` throws and the upload itself 500s
  — the race outcome depends on scheduling.
- **Evidence:** `captured/gap-matrix-seed-race.json` (upload response + seeded
  audit terminal detail); scratch `run-20261118-002104-ef453ecd/backend-console.log`
  (full SqliteException stack).
- **Conformance path used instead:** upload with `autoSeedAudit:false` +
  `GET /api/audits/{id}/redirect-matrix/results` — the results endpoint is the
  real consumer of `RedirectMatrixValidator.ValidateAsync`
  (`RedirectMatrixController.cs:128`), which emits the throttled
  `matrix-row-validated` batches (§10.8) over stored redirect chains.
  `HubEventWireConformanceTests.MatrixRowValidated_LivePayload_SpecShape`
  captures them live. No test locks the race failure.
- **Tests:** race outcome captured as `captured/gap-matrix-seed-race.json`
  evidence; `RestEnvelopeConformanceTests.MatrixAutoSeed_StartsValidationAudit_SpecShape`
  (skipped, this gap) asserts the conformant behavior. The live conformance
  path uses `autoSeedAudit:false` + the results endpoint (above). No test
  locks the race failure.
- **Owners (lead-verified + routed):** WP-09-06 (owns `Api\Controllers\` —
  the autoSeed ordering) + WP-09-03 (owns `Crawler\ListMode\` — the seeder;
  sibling start-before-persist defect G10 / ListModeStartConformanceTests) +
  WP-09-11 (`Host\Execution\` crawl insert path). Recorded in
  PHASE-08-REPORT.md; routed at phase-09 dispatch.

## G-COUNTERS — REST audit counter-key mismatch (WP-09-06) — FAILING TEST

- **Wire boundary:** engine storage → REST audit detail/summary.
- **Producer:** `AuditRunCounters.ToPayload()` writes `counters_json` with keys
  `{discovered, fetched, analyzed, failed}`
  (`src/backend/CrawlForge.Host/Execution/AuditRunState.cs`).
- **Consumer:** `AuditWireMapper.ParseCounters` reads
  `{queued, fetched, error, blocked, wafBlocked?}`; missing keys default to 0
  (`src/backend/CrawlForge.Api/Controllers/AuditWireMapper.cs:22-30`).
- **Effect:** only `fetched` overlaps. Every REST audit response silently
  reports `queued=0`, `error=0`, `blocked=0` regardless of the real run.
- **Evidence:** `audit-work/reports/hotfix-01/hotfix-01-report.md` §6;
  `app/CODEMAP.md` §4.10; captured `engine-counters.json` vs `rest-counters.json`.
- **Test:** `AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters`
  `[Trait("KnownGap","WP-09-06")]` — the ONLY failing test. Gates run with
  `--filter KnownGap!=WP-09-06`.
- **Owner:** phase 09 WP-09-06 (counter-key reconciliation).

## G-PROGRESS — hub crawl-progress payload drift — RESOLVED (WP-09-06 + WP-09-11)

- RESOLVED: WP-09-06's HubPayloadNormalizer maps counters to the consumer keys
  and stringifies ids; WP-09-11's PublishProgressAsync adds
  throughputPerSecond/elapsedSeconds/estimatedRemainderSeconds/phase (+
  additive throttledHostCount/meanEffectiveRate) with string auditId.
- **Tests:** `CrawlProgress_FrontendContractShape` UNSKIPPED (live frozen
  contract shape); `CrawlProgress_LivePayload_ProducerShape` unchanged
  (≤1/s average cadence).
- **Owner:** WP-09-11 (done). Original drift description below retained.

- **Producer:** `AuditExecutionEngine.PublishProgressAsync`
  (`src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:1663-1690`)
  emits `{auditId:number, counters:{discovered,fetched,analyzed,failed}, queued}`.
- **Consumer:** `CrawlProgressPayload`
  (`src/frontend/src/contracts/hub/events.ts:44-61`) requires string `auditId`,
  `counters: AuditCounters` (`queued/fetched/error/blocked/wafBlocked?`,
  `src/frontend/src/contracts/api/audits.ts:36-43`), `throughputPerSecond`,
  `elapsedSeconds`, `estimatedRemainderSeconds`, `phase`.
- **Drift:** (a) counter keys — same root cause as G-COUNTERS (WP-09-06
  family); (b) missing `throughputPerSecond`/`elapsedSeconds`/
  `estimatedRemainderSeconds`/`phase` (WP-09-11); (c) numeric `auditId` (G-G).
- **Tests:** `HubEventWireConformanceTests.CrawlProgress_LivePayload_ProducerShape`
  (passes; shared dimensions + ≤1/s cadence verified as the AVERAGE emission
  rate over the capture window — per-gap received-at measurement is unreliable
  because TCP Nagle/delayed-ACK compresses arrival gaps; producer throttle is
  source-locked at AuditExecutionEngine.cs:1681-1690) and
  `CrawlProgress_FrontendContractShape` (skipped, this gap).
- **Owners:** counter keys → WP-09-06; throughput/phase fields → WP-09-11
  (lead routing, 2026-11-18).

## G-COMPLETED-COUNTERS — audit-completed.finalCounters keys (WP-09-06 family)

- **Producer:** `AuditExecutionEngine.FinalizeAsync`
  (`AuditExecutionEngine.cs:1236-1290`, event at :1282) emits `finalCounters`
  from `AuditRunCounters.ToPayload()` (engine keys).
- **Consumer:** `AuditCompletedPayload.finalCounters: AuditCounters`
  (`events.ts:98-102` → `audits.ts:36-43`, consumer keys).
- **Tests:** `AuditCompleted_LivePayload_SpecShape` (passes; spec-level fields)
  and `AuditCompleted_FrontendCountersKeys` (skipped, this gap).
- **Owner:** WP-09-06 family.

## G-HUB-COUNTERS — hub counters vs REST wire counters (WP-09-06 family)

- The shell's live view (hub, engine keys) and the REST snapshot (consumer
  keys) cannot agree key-for-key until G-COUNTERS is reconciled.
- **Tests:** `AuditCountersWireConformanceTests.HubCounters_EngineKeys_MatchStoredCounters`
  (passes; producer consistency) and `HubCounters_MatchRestWireKeys`
  (skipped, this gap).
- **Owner:** WP-09-06 family.

## G-G — systemic numeric ids on hub events vs string EntityId

- **Producer side (all numeric):** `AuditExecutionEngine.cs:1654-1690`
  (stage-changed/crawl-progress), `:1271-1290` (audit-failed/audit-completed);
  `ScheduleHubEvents.cs:33-59` (scheduleDefinitionId/auditId);
  `Export/ExportModels.cs:171-178` (exportJobId/destinationId);
  `Crawler/ListMode/RedirectMatrixValidator.cs:279` +
  `ListModeModels.cs:213-221` (auditId/urlId);
  `Intelligence/LogAnalyzer/LogImportService.cs:425-503` (importId).
- **Consumer side:** locked `EntityId` is `string`; every payload guard
  requires `isString(...)` on id fields
  (`src/frontend/src/contracts/hub/guards.ts`, e.g. :272, :288, :291, :300,
  :310, :320, :330).
- **Masking:** `store/hubDemux.ts` coerces object keys with `String(...)`, so
  store lookups still work; the guards and the plugin relay see the wrong
  type. Live evidence: `captured/hub-numeric-auditid-drift.json`.
- **Tests:** `HubEventWireConformanceTests.AuditScopedEvents_CarryNumericAuditId_SystemicDrift`
  (passes; contract-side guard check + live id-kind EVIDENCE capture
  `captured/hub-numeric-auditid-drift.json` — the broken type is NOT locked as
  expected behavior, per the lead's ownership steer) and
  `AuditScopedEvents_CarryStringAuditId_FrontendContract` (skipped, this gap).
  Per-event frontend-conformance skips cite this gap where relevant
  (schedule-fired/run-completed/export-delivery/log-import-progress).
- **Suggested owner:** phase 09 serial wiring WP-09-11 (lead routing).

## G-D — schedule-fired field drift

- **Producer:** `ScheduleFiredPayload`
  (`src/backend/CrawlForge.Crawler/Scheduling/ScheduleHubEvents.cs:33-37`):
  `{scheduleDefinitionId:long, auditId:long|null, triggerType, startedAt}`.
- **Consumer:** `ScheduleFiredPayload`
  (`src/frontend/src/contracts/hub/events.ts:118-122`):
  `{scheduleId:string, auditId:string, triggerType}`; guard `guards.ts:220-228`
  requires string ids. The guard would reject the live payload.
- **Tests:** `ScheduleFired_LivePayload_ProducerShape` (passes; shared
  dimensions from a real run-now, full payload captured) and
  `ScheduleFired_FrontendContractShape` (skipped, this gap).
- **Suggested owner:** phase 14 WP-14-01 (scheduler + wiring; lead routing).

## G-E — schedule-run-completed field drift

- **Producer:** `ScheduleRunCompletedPayload` (`ScheduleHubEvents.cs:51-59`):
  `scheduleDefinitionId`, `completionActions` (+ startedAt/finishedAt extras).
- **Consumer:** `events.ts:125-130`: `scheduleId`, `completionActionResults`;
  guard `guards.ts:230-239`.
- **Tests:** `ScheduleRunCompleted_LivePayload_ProducerShape` (passes; shared
  dimensions, full payload captured) and
  `ScheduleRunCompleted_FrontendContractShape` (skipped, this gap).
- **Observed variant:** skipped-overlap runs emit `schedule-fired` /
  `schedule-run-completed` WITHOUT `auditId` and with
  `failureReason: "Previous run still active (skipped-overlap.)"`
  (`captured/hub-events.json`). The harness pairs the fired/completed tests to
  its own run via the run-now response `auditId`
  (`ScheduleRunNowResponse`, OperationsWireContracts.cs:127), so the skipped
  variant never contaminates the success-run assertions.
- **Suggested owner:** phase 14 WP-14-01 (lead routing).

## G-F — export-delivery field drift

- **Producer:** `ExportDeliveryPayload`
  (`src/backend/CrawlForge.Export/ExportModels.cs:171-178`):
  `{exportJobId:long, destinationId:long, state, attempt, message?, timestamp}`.
- **Consumer:** `events.ts:133-137`: `{exportId:string, destinationId:string,
  state}`; guard `guards.ts:241-249`.
- **Tests:** `ExportDelivery_LivePayload_ProducerShape` (passes; delivered
  state transition + id dimensions, full payload captured) and
  `ExportDelivery_FrontendContractShape` (skipped, this gap).
- **Suggested owner:** phase 09 WP-09-09 (export dialog/delivery; lead routing).

## G-S — system-health GC counter naming: spec table vs consistent code (CORRECTED)

- **Spec:** `architecture/05-api-hub-and-mcp/02-realtime-websocket-hub.md:47`
  writes `gcGen0/1/2Counts` (plural).
- **Implementation (consistent, SINGULAR):** backend
  `src/backend/CrawlForge.Api/Hubs/SystemHealthSampler.cs:21-29`
  (`GcGen0Count/GcGen1Count/GcGen2Count` → camelCase `gcGen*Count`);
  frontend `src/frontend/src/contracts/api/system.ts:194-196`
  (`SystemHealthPayload` aliases `SystemHealthSnapshot`, `events.ts:177`);
  guard `guards.ts:126`; UI `src/frontend/src/shell/StatusBar.tsx:122`.
- **Effect:** NO functional defect — backend, frontend, guard, and status bar
  agree. The divergence is spec notation vs code. (An earlier draft of this
  report claimed a frontend plural/undefined-read defect; that was wrong and
  was corrected to the lead on 2026-11-18.)
- **Test:** `SystemHealth_LivePayload_Heartbeat` (passes; 2 s cadence +
  singular producer fields) and `SystemHealth_FrontendGcCounterNames`
  (skipped; asserts the spec-table plural against the live singular payload).
- **Suggested owner:** orchestrator/lead — spec clarification via CR or a code
  rename; NOT a phase-12 defect.

## G-I — export-progress emitted but uncataloged — RESOLVED (WP-09-11)

- RESOLVED by the coordinator's wiring commits fe1ac88 (frontend events.ts /
  guards.ts / hubDemux.ts registration: ExportProgressPayload {exportId,
  auditId, format, state, filePath, message}) and 5ad460f
  (HubEventCatalog.ExportProgress + 1s default interval). The event is now
  cataloged AND in the frozen frontend contract; the inventory flags track it.
- **Tests:** `HubEventWireConformanceTests.ExportProgress_DriftRecorded`
  (passes; live emission + payload dimensions, capture
  `captured/hub-export-progress-drift.json`) and
  `ExportProgress_CatalogedContractShape` (passes; catalog + events.ts +
  live string-id/state-vocabulary lock — replaced the former absence-lock
  `ExportProgress_Uncataloged`).
- **Owner:** WP-09-11 (done). Phase-08 numbering slip: export G-F/G-I belong
  to WP-09-10's producer surface, catalog/contract registration to WP-09-11.

## G-N — local-notification: backend-only additive event — RESOLVED (CR-027)

- RESOLVED by CR-027 (owner-approved additive contract change) landed in
  fe1ac88: `LocalNotificationPayload {title, message}` registered in the
  frontend events.ts union/catalog/guards/demux (guarded OS Notification
  delivery). The backend catalog entry + HubLocalNotificationSink pre-existed.
- **Tests:** `HubEventWireConformanceTests.LocalNotification_CatalogOnlyDrift`
  (passes; real producer → capturing hub shape lock on the sink's own
  {title, message} contract) and `LocalNotification_InFrontendContract`
  (passes; both-sides registration lock — replaced the former absence-lock
  `LocalNotification_NotInFrontendContract`).
- **Owner:** WP-09-11 (done).

## G05 — six spec events with no producer — FOUR RESOLVED (WP-09-11)

- RESOLVED (WP-09-11 serial wiring): `url-batch` + `findings-updated`
  (AuditExecutionEngine.FlushRunEventsAsync → AuditRunEventPublisher, ≤1/s
  producer cadence, lossless delivery; findings deltas derived from persisted
  counts), `backend-lifecycle` (CrawlForgeBackend.PublishLifecycleAsync —
  ready at startup, shutdown before the drain; the harness now stops its
  backend through POST /api/system/shutdown so the notice is captured),
  `waf-blocks` (FlushRunEventsAsync, 1/s hub throttle; the fixture serves no
  WAF challenge, so the live producer proof is CrawlForge.Host.Tests
  WafWiringTests — challenge origin → frozen batch shape).
- STILL ABSENT: `embedding-progress`, `embedding-completed` (phase-12
  semantics wiring; contract-side absence locks remain).
- **Tests:** `UrlBatch_LivePayload_SpecShape`,
  `FindingsUpdated_LivePayload_SpecShape` (live, unskipped);
  `BackendLifecycle_ProducerWired_ShapeLock` + `WafBlocks_ProducerWired_NoFixtureTrigger`
  (inventory producer locks; live proofs in CrawlForge.Host.Tests
  BackendLifecycleTests / WafWiringTests — the harness hub connects after
  startup and captures shutdown at dispose); embedding absence locks unchanged.
- **Owner:** WP-09-11 (done); embeddings → phase 12.

## G-MCP-PAGING — MCP resource paging vs CR-011

- **Spec:** `architecture/05-api-hub-and-mcp/03-local-mcp-server-and-agents.md:122`
  (CR-011): every data-returning tool/resource returns at most one page
  (default 50, maximum 500) **and a cursor**; no unbounded result sets.
- **Code:** `McpResourceSurface.DefaultReadLimit = 200`, no cursor
  (`src/backend/CrawlForge.Mcp/McpResourceSurface.cs:92,165-181`).
- **Test:** `McpWireConformanceTests.ResourceRead_PagingDrift` (skipped, this
  gap); all 15 resources are still read live and shape-asserted.
- **Owner:** phase 13 / CR-011 MCP surface rebuild (lead routing).

## G-MCP-TEMPLATES — resources/templates/list unimplemented

- `McpJsonRpcDispatcher` returns `-32601 MethodNotFound` for
  `resources/templates/list` (MCP spec method; the surface exposes static
  resources only).
- **Test:** `McpWireConformanceTests.ResourcesTemplatesList_MethodNotFound`
  (passes; locks the current error shape).
- **Owner:** phase 13 MCP surface.

## G-REST-IDEMPOTENCY — duplicate audit start not idempotent — RESOLVED (WP-09-11)

- RESOLVED: `AuditExecutionEngine.StartCoreAsync` returns the existing active
  audit (Queued/Running/Paused) for the same configurationId per §10.4.
- **Tests:** `DuplicateAuditStart_IsIdempotentWhileActive` UNSKIPPED (live
  duplicate start returns the same auditId).
- **Owner:** WP-09-11 (done). Original description below retained.

- **Spec:** `architecture/05-api-hub-and-mcp/01-rest-api-contract.md` §10.4:
  duplicate start is idempotent while an identical run is active.
- **Code:** `AuditExecutionEngine.StartAuditAsync`
  (`src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:334-368`)
  always creates a new audit.
- **Test:** `RestEnvelopeConformanceTests.DuplicateAuditStart_IsIdempotentWhileActive`
  (skipped, this gap); the envelope shape of start is asserted by the sweep.
- **Suggested owner:** phase 09 wiring (lead routing).

## G27 — redirect chains not persisted hop-by-hop (shape lock only)

- The redirect-chain trap stores a single final target
  (`url_records.redirect_target_url`); intermediate hops (chain-2/chain-3)
  have no `url_records` rows. `UrlDetailWire.redirectChain` exists but is not
  populated hop-by-hop from storage. Measured state:
  `audit-work/reports/runtime-probe-2/` (G27).
- **Tests:** `StorageEngineConformanceTests.RedirectChain_FinalTargetOnly_G27`
  and `FixtureTrapCaptureTests.RedirectChainTrap_PayloadsCapturedAndAsserted`
  (pass; SHAPE-only — hop-count semantics belong to cf-wp-08-04's fixture
  acceptance, per the packet).
- **Owner:** crawler redirect persistence (orchestrator routing).

## G03 — rendered DOM artifacts absent (renderer inert)

- With `rendering.enabled=false` (http mode) no `content_artifacts` rows of
  kind `RenderedDom` (2) exist; body artifacts (kind 0) ARE stored and feed
  the offline re-audit. The renderer is inert in the current build (G03,
  probe-2).
- **Test:** `StorageEngineConformanceTests.RenderedDomArtifacts_Absent_G03`
  (passes; records the limitation, asserts body artifacts exist).
- **Owner:** rendering phase.

## Resolved / verified — MCP re-audit argument key (separate from G-REAUDIT-SNAPSHOT)

- The packet's historical `$.id` deserialization gap no longer reproduces at
  the ARGUMENT level: `McpToolSurface` declares and reads `auditId`
  consistently (`src/backend/CrawlForge.Mcp/McpToolSurface.cs:103,158`), and
  the spec (03-local-mcp-server-and-agents.md:47) does not lock a key name.
- **Test:** `McpWireConformanceTests.ToolCall_ReAudit_ReturnsNewAuditId`
  (passes live with `{auditId, refresh:false}` against the MCP-started
  offline source). Re-audit of REST-started audits is broken by the separate
  snapshot defect G-REAUDIT-SNAPSHOT above.

## CODEMAP inaccuracies (lead records; outside this workpiece's owned paths)

- CORRECTED (phase-08 erratum, WP-09-11): `POST /api/system/shutdown` DOES
  exist — mapped in the composition root (`CrawlForgeBackend.cs:131-135`),
  not in SystemController. The harness now uses it for graceful shutdown
  (backend-lifecycle capture); the process kill remains the fallback.
- CODEMAP's `CRAWLFORGE_DATA_DIRECTORY` environment claim is wrong: the
  product binds `CrawlForge:DataDirectory` from configuration only
  (`src/backend/CrawlForge.Host/Configuration/CrawlForgePaths.cs:14`); the
  harness passes `--CrawlForge:DataDirectory=<scratch>` (lead-approved).

---

## Surface coverage summary

| Surface | Inventoried | Live-tested | Contract-side / skipped |
|---|---|---|---|
| REST operations | 108 (88 groups) | 108 (sweep; 2 binary/text exemptions) | 1 idempotency skip |
| Hub server events | 18 spec + 2 drift | 12 live captures | 6 no-producer + drift skips |
| Hub client channels | subscribe/unsubscribe | live invocations | — |
| IPC channels | 11 (9 bridge + 2 literals) | source-level cross-checks | — |
| MCP tools | 8 | 8 live calls | — |
| MCP resources | 15 | 15 live reads | 1 paging skip |
| MCP prompts | 5 | 5 live gets | — |
| Storage entities | 40 | 40 schema checks + row locks | G27/G03 shape locks |

Every inventory entry carries `coveredBy` references that resolve to real
test methods (`PayloadInventoryTests.EveryInventoriedPayload_HasConformanceCoverage`).