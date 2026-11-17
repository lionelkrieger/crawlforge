# CrawlForge code map — where code is registered, wired and mounted

Read this before adding any feature. The first build's main failure was code that existed but was
never registered in DI, never invoked in a real audit, or never mounted in the UI. Every path below
was verified against the working tree (post-HOTFIX-01b). Paths are relative to the repository root
`E:\Crawlforge`. Abbreviations: **BE** = `app/src/backend`, **Host** = `BE/CrawlForge.Host`,
**FE** = `app/src/frontend/src`, **EL** = `app/src/electron/src`, **Api** = `BE/CrawlForge.Api`.
Gap IDs (G01–G29) refer to `audit-work/reports/UNDERBUILD-AUDIT.md`; runtime verdicts to
`audit-work/reports/runtime-probe-2/RUNTIME-PROBE.md`.

## 1. Layout

| Project / package | Purpose |
|---|---|
| `BE/CrawlForge.Host` | Executable composition root: DI modules, audit execution engine, lifecycle, handshake, session gate. |
| `BE/CrawlForge.Api` | **Library** (not executable): REST controllers, wire contracts/mappers, SignalR hub, metrics exposition. |
| `BE/CrawlForge.Contracts` | Locked service contracts, storage/REST DTO records, enums, constants (rule keys, metric keys, hub names). |
| `BE/CrawlForge.Crawler` | Fetching, frontier, AIMD politeness, canonicalization, WAF, rendering pool, list mode, scheduling, proxy. |
| `BE/CrawlForge.Audit` | Audit pipeline, analyzer families (accessibility, content quality, indexability, link graph, scoping, structured data, custom, scoring, **Performance = dead code**). |
| `BE/CrawlForge.Intelligence` | N-grams, embeddings (ONNX + loopback daemon), GEO/llms.txt, MarTech, log analyzer. |
| `BE/CrawlForge.Data.Sqlite` | Default embedded provider: CQRS connection factory, single-writer gate, WAL monitor. |
| `BE/CrawlForge.Data.MySql` | Optional user MySQL provider, conformance catalogue, provider-switch orchestrator (test-only today). |
| `BE/CrawlForge.Data.Migrations` | Logical schema catalogue + v1–v4 migrations, dialect renderers, first-run seed. |
| `BE/CrawlForge.Data.Repositories` | Generic `SqlRepository<TDto>`, DTO→table mapping, aggregation, archive, retention support. |
| `BE/CrawlForge.Data.ContentStore` | Brotli + SHA-256 dedup artifact store; retention pruner. |
| `BE/CrawlForge.Export` | Export engine, dataset readers, CSV/XLSX/ZIP writers, destination delivery, job registry. |
| `BE/CrawlForge.Mcp` | MCP JSON-RPC dispatcher, tool/resource/prompt surfaces, loopback HTTP server, token service, action log. |
| `BE/CrawlForge.Security` | Loopback port handshake, secret protection (app entropy), diagnostics/minidump, loopback guard. |
| `app/src/frontend` (`@crawlforge/frontend`) | React 19 + Mantine 8 + Zustand UI; Vite, loads over `file://` (`base: './'`). |
| `app/src/electron` (`@crawlforge/electron`) | Electron main/preload: backend supervision, handshake discovery, windows, recovery, plugin scan. |
| `app/src/mcp-stdio-adapter` (`@crawlforge/mcp-stdio-adapter`) | Stdio ↔ streamable-HTTP MCP bridge for external clients (bin `crawlforge-mcp-stdio`). |
| `app/tests/*` | 17 C# test projects + benchmarks + frontend/electron vitest + governance + fixture site (§11). |
| `app/assets/` | Bundled local assets: `ai-agent-profiles`, `bot-cidr`, `dictionaries`, `export-templates`, `font-metrics`, `onnx-model`, `plugin-schema`, `stopwords`, `tech-fingerprints`, `waf-signatures`. Resolved by `Host/Composition/AssetLocator.cs`. |
| `app/tools/` | `governance-analyzer` (exclusion rules), `network-audit` (outbound monitor). |
| `app/packaging/` | Offline packaging scripts/manifests (`verify-packaging.ps1`; `vendor/` = Electron binary, excluded from searches). |
| `app/docs/acceptance/` | Acceptance catalog evidence. |
| `app/CrawlForge.sln`, `app/global.json` | Solution (all projects incl. tests); SDK pin 10.0.404 (`rollForward: latestFeature`). |
| `app/launch-crawlforge.ps1` | Dev launcher: rebuilds stale backend/frontend/shell, sets env, starts Electron (§2). |
| `app/package.json` | npm workspaces root: `src/frontend`, `src/electron`, `src/mcp-stdio-adapter`, `tests/frontend`, `tests/electron`. |

## 2. Startup sequence

| # | Step | File → symbol |
|---|---|---|
| 1 | Dev launch: `dotnet build` Host, `npm run build` frontend, `npx tsc -p tsconfig.build.json` shell; sets `CF_UI_ROOT`, `ELECTRON_OVERRIDE_DIST_PATH`, `CF_BACKEND_COMMAND=dotnet`, `CF_BACKEND_ARGS=<Host dll>`, `CF_BACKEND_CWD` | `app/launch-crawlforge.ps1` (build steps ~263/295/326, env ~338–353) |
| 2 | Single-instance lock → `app.whenReady` → `compose()` → `wireIpc()` → `orchestrator.run()` | `EL/main.ts` (:401, :409, `compose` :273, `wireIpc` :323) |
| 3 | `compose()` builds `BackendSupervisor`, `WindowManager`, `RecoveryService`, `PluginScanner` (**null registry delegate**, :300), `StartupOrchestrator` | `EL/main.ts`; `EL/supervisor/backend-supervisor.ts`, `EL/windows/window-manager.ts`, `EL/recovery/recovery-service.ts`, `EL/plugins/plugin-scanner.ts`, `EL/startup/startup-orchestrator.ts` |
| 4 | `StartupOrchestrator.run()`: splash → `supervisor.start()` → not ready ⇒ recovery window; ready ⇒ main window, close splash → `hydration.wait(30000)` (IPC `crawlforge:hydrated`) → `plugins.scanAfterHydration` | `EL/startup/startup-orchestrator.ts` |
| 5 | Backend spawn: `CF_BACKEND_COMMAND` (dev `dotnet …/CrawlForge.Host.dll`; packaged `resources/backend/CrawlForge.Host.exe`) with `--CrawlForge:Port=0`; `waitForReady()` polls descriptor + `/api/health`; `healthLoop` 2 s, 3 failures ⇒ recovery; backoff 1 s→15 s | `EL/supervisor/backend-supervisor.ts` (`launch` :203, `waitForReady` :257, `healthLoop` :287) |
| 6 | Backend entry → `CrawlForgeBackend.StartAsync`: `LoopbackPortHandshake` probes configured port, ephemeral loopback fallback, generates per-launch session token, writes `loopback-handshake.json`; Kestrel `Listen(IPAddress.Loopback, port)` | `Host/Program.cs`; `Host/CrawlForgeBackend.cs` (:72–96); `BE/CrawlForge.Security/Handshake/LoopbackPortHandshake.cs` |
| 7 | Module discovery + registration (§3), `SessionTokenMiddleware`, minimal routes (`/hub` stub, `/api/diagnostics/version`, `POST /api/system/shutdown`), module endpoint mapping | `Host/CrawlForgeBackend.cs` (:108–140); `Host/Hosting/SessionTokenMiddleware.cs` |
| 8 | `FirstLaunchInitializer.InitializeAsync` (data dir + `settings.json` seed) → `BackendLifecycleCoordinator.StartAllAsync` (migrations first, §3) → `app.StartAsync` → `PortDescriptor.WriteAsync` writes `backend-port.json` | `Host/Initialization/FirstLaunchInitializer.cs`; `Host/Hosting/BackendLifecycleCoordinator.cs`; `Host/Handshake/PortDescriptor.cs` |
| 9 | Shell reads `{DataDirectory}/loopback-handshake.json` (`{version, port, sessionToken, createdAtUtc}`, max age 24 h) | `EL/handshake/port-discovery.ts` → `PortDiscovery.discover`; file name const in `EL/types.ts` |
| 10 | Window load: `WindowManager.open` → `loadRoute(route)` appends `?route=` (`EL/main.ts:241`); preload exposes `window.crawlForge = {backendBaseUrl, getSessionToken}` after IPC `crawlforge:get-bridge` | `EL/windows/window-manager.ts`; `EL/preload.ts`; `EL/preload/bridge.ts` |
| 11 | Renderer: `main.tsx` `createRoot().render(<App/>)` → `App` reads bridge (fallback `VITE_API_BASE_URL`) → `createShellServices` → `<CrawlForgeShell>`; lifecycle loads settings/projects/audits/plugins then `hub.start()` | `FE/main.tsx`; `FE/App.tsx`; `FE/shell/ShellContext.tsx` |
| 12 | Hub connection: `createSignalRHubClient` → `HubConnectionBuilder().withUrl(baseUrl + '/hubs/realtime')`, token as `access_token` + `X-CrawlForge-Session`; backend hub mapped by `ApiSurfaceModule.ConfigureEndpoints` | `FE/store/hubClient.ts` (`HUB_ROUTE_PATH`); `Api/Hubs/CrawlForgeHub.cs` (`HubRoutePath = "/hubs/realtime"` :20) |

**Gotcha:** the renderer **ignores `?route=`** — zero reads of `location.search` in `app/src/frontend`.
Every window loads the same shell at the persisted localStorage view (G20).

Data directory: `%APPDATA%/CrawlForge` unless `CrawlForge:DataDirectory` / `CRAWLFORGE_DATA_DIRECTORY`
override (`Host/Configuration/CrawlForgePaths.cs`; `EL/main.ts`). Files there: `crawlforge.db`,
`settings.json`, `loopback-handshake.json`, `backend-port.json`, `mcp-endpoint.json`, `mcp-token.json`,
`entropy.key`, `content-store/`, `exports/`, `diagnostics/`, `window-state.json`.

## 3. Backend composition root

**Root:** `Host/CrawlForgeBackend.cs` → `StartAsync` (:72). Modules are discovered, not listed:
`Host/Hosting/HostModuleDiscovery.cs` → `Discover()` scans loaded assemblies **and `CrawlForge.*.dll`
next to the host** for concrete `IHostModule` implementations with a parameterless ctor, ordered by
`Order` then `Name`. Interface: `Host/Hosting/IHostModule.cs` (`ConfigureServices` before build,
`ConfigureEndpoints` after build). Options binding: `CrawlForgeOptions` section `"CrawlForge"`
(`Port`, `DataDirectory`) in `Host/Configuration/CrawlForgeOptions.cs`, bound at `CrawlForgeBackend.cs:98`.

| Module (all in `Host/Composition/`) | Order | Registers |
|---|---|---|
| `CoreServicesModule.cs` | 0 | `SqliteDbProvider` as `IDbProvider` (**always SQLite**, `crawlforge.db`), `SchemaMigrator`, **39 DTO repositories** via `AddRepository<TDto>` (:90–133), `EntropyKeyStore`/`AppEntropySecretProtector`, diagnostics registry + `MinidumpCrashHandler`, `LocalStructuredLogSink`/`Provider`, `CompressedContentStore` as `IContentStore`+`IArtifactFileStore`. |
| `EngineServicesModule.cs` | 10 | `RegisterCrawler` (fetcher, WAF catalogue/evaluator/recorder, `HeadlessRenderingPool` as `IRenderingPool` :98, `PreCrawlValidator`, checkpoints, robots, list-mode ingestor, throttle store, `LiveAimdControllerRouter`, redirect-matrix validator); `RegisterAudit` (parser, inventory, scope resolver, analyzers, **`IAuditRuleAnalyzer` = `AuditRuleHost` factory** :161–177, `AuditPipeline` :193, `SinglePageAnalyzer` :204, scoring/suppression, `FindingEmitter` :223, `AuditRuleCatalog` :226, link-graph + zone/equity (D15), technology, structured-data validator, custom content); `RegisterIntelligence` (n-grams, stopwords, ONNX embeddings, semantic pipeline, GEO, governance, llms generator, log analyzer/correlation, MarTech); `RegisterExports` (job registry, engine, delivery, event publisher); `RegisterScheduler` (`ScheduledAuditService`, completion actions, retention); `RegisterHub` (`SystemHealthSampler`, WAL checkpointer, compact, `PrometheusMetricsExposition`, `SignalRHubChannel`, `RealtimeHubService` as `IRealtimeHub`). |
| `ExecutionModule.cs` | 20 | `AuditExecutionEngine` singleton bound to **four** surfaces: `IAuditExecutionService`, `IAuditLifecyclePort` (MCP), `IReAuditExecutionPort` (REST), `IBackendLifecycle`; plus `MigrationLifecycleParticipant`, `SchedulerLifecycleParticipant`. |
| `ApiSurfaceModule.cs` | 30 | `AddControllers().AddApplicationPart(typeof(ApiControllerBase).Assembly).AddEnvelopeValidation()` + `ApiJsonOptions.Create()` conventions; `AddSignalR().AddJsonProtocol` (same converters); endpoints: `MapControllers()`, `MapHub<CrawlForgeHub>`. |
| `McpModule.cs` | 40 | `FileMcpTokenStore` (`mcp-token.json`), `McpTokenService`, `AutomationActionLog`, `McpToolSurface`, `McpResourcePorts`→`McpResourceSurface`, `McpPromptSurface`, `McpJsonRpcDispatcher`, `McpLoopbackServer`, `McpLifecycleParticipant`. |

**Lifecycle participants** (`IBackendLifecycle`, started in `Order`, drained reverse;
`Host/Hosting/BackendLifecycleCoordinator.cs`):

| Order | Participant | File |
|---|---|---|
| −100 | `MigrationLifecycleParticipant` — schema migrations before anything else | `Host/Composition/ExecutionModule.cs:46` |
| −50 | `UserDictionaryLifecycleParticipant` — feeds persisted dictionary into the live analyzer (D13) | `Host/Execution/UserDictionaryLiveSync.cs:96` |
| 5 | `WafCatalogueInitializationParticipant` — loads bundled + user WAF signatures | `Host/Composition/EngineServicesModule.cs:448` |
| 20 | `AuditExecutionEngine` — restart reconciliation | `Host/Execution/AuditExecutionEngine.cs:225` |
| 30 | `SchedulerLifecycleParticipant` | `Host/Composition/ExecutionModule.cs:80` |
| 40 | `McpLifecycleParticipant` — starts MCP server, writes `mcp-endpoint.json` | `Host/Composition/McpModule.cs:99` |

**Register a new service (real example):** `IStructuredDataValidator` is registered in
`EngineServicesModule.RegisterAudit` (`EngineServicesModule.cs:256–257`):
`services.AddSingleton<IStructuredDataValidator>(p => new StructuredDataValidator(p.GetRequiredService<ITolerantDomParser>()));`
Add the registration to the module that owns the domain; a wholly new subsystem can add its own
`IHostModule` class in any `CrawlForge.*` assembly — discovery picks it up with no shared-file edits.
**Registration alone is not "live":** the service must also be consumed by the engine, a controller,
an MCP surface, or a mounted component (see §13 for what happens otherwise).

## 4. Audit execution path

Entry points (all converge on one engine):

| Entry | File → symbol | Calls |
|---|---|---|
| REST `POST /api/audits` | `Api/Controllers/AuditsController.cs` → `StartAuditAsync` (:95) | `IAuditExecutionService.StartAuditAsync(projectId, configurationId, Manual, overrides)` |
| REST re-audit `POST /api/audits/{id}/reaudit` | `AuditsController.cs` → `ReauditAsync` (:282) | `IReAuditExecutionPort.ReAuditAsync(id, refresh)` |
| MCP `start-audit` / lifecycle tools | `BE/CrawlForge.Mcp/McpToolSurface.cs` (:115 ff.) | same `IAuditExecutionService` / `IAuditLifecyclePort` |
| Scheduler | `BE/CrawlForge.Crawler/Scheduling/ScheduledAuditService.cs` | `IAuditExecutionService` |

**Engine:** `Host/Execution/AuditExecutionEngine.cs` (implements all four port interfaces).

1. `StartAuditAsync` (:334) — loads configuration, freezes `ConfigurationSnapshotJson`, persists
   `AuditDto` (Running/Crawling), `Task.Run(RunAuditAsync)`, publishes `stage-changed`.
2. `RunAuditAsync` (:491) — `CrawlPhaseAsync` → `RunLinkGraphEnrichmentAsync` (:843, D15) →
   `AnalyzePhaseAsync` → `ScoreAndFinalizeAsync`. Failures finalize as Failed with `errorMessage`.
3. `CrawlPhaseAsync` (:535) — limits + `AimdSettingsParser.Parse` (HOTFIX-01, accepts nested + legacy
   flat politeness JSON); per-audit `AimdPolitenessController` with `CompositeHostThrottleSink`
   (recorder + coalesced hub sink, :586–591); `PriorityFrontier`; **own `HttpClient` via
   `Composition/CrawlTargetHttpClientFactory.Create` (:601 — plain handler, no proxy/signed requests,
   G09)**; `HttpFetcher` (:603); checkpoint restore (:608) else seeds from **`SeedsJson` only** (:637).
   Loop (:654): observe persisted status → robots check (:1390) → `aimd.WaitAsync` →
   `fetcher.FetchAsync` (:693) → **`aimd.RecordResponse(..., IsChallenge: false, ...)` (:709–710, G07)**
   → `WafChallengeEvaluator.Evaluate` (:713) → `url_records` write (:733) → WAF detection/notice rows
   (:741/:745) → body into `IContentStore` (:751) → `AngleSharpDomParser.Parse` +
   `DiscoverLinksAsync` (:757–759, :1421 — zoned link inventory into `link_edges`, same-host enqueue)
   → checkpoint every 25 fetches (:763) → `PublishProgressAsync` (:770).
4. **RENDERING GAP (G03) — where rendering should plug in and does not:**
   - The engine never injects `IRenderingPool`. It is registered (`EngineServicesModule.cs:98`,
     `HeadlessRenderingPool`) but consumed only by `SinglePageAnalyzer` (`EngineServicesModule.cs:204`)
     and tools. The crawl loop fetches HTTP only; no rendered DOM is ever captured or stored.
   - `BuildStoredContextAsync` (:1476) rebuilds the analysis context from the stored body and returns
     **`new PageAuditContext(auditId, url, configuration, fetch, document, null, null, null)` (:1510)** —
     the three nulls are `RenderedDocument`, `ScopedText`, `CapturedRequests`
     (record: `BE/CrawlForge.Contracts/Audit/PipelineContracts.cs:84–92`).
   - Plug-in points: capture rendered DOM in `CrawlPhaseAsync` after fetch (store as a content
     artifact), then load it in `BuildStoredContextAsync` instead of the nulls.
5. `AnalyzePhaseAsync` (:780) — streams `url_records` in 1000-row pages; per successful URL:
   `BuildStoredContextAsync` → `AnalyzeOnePageAsync` (:1025): `IAuditPipeline.RunAsync` →
   `FindingEmitter.EmitAsync` (:1029) → `url_elements` + `metric_summaries` writes →
   `IMarTechAuditor` (:1044) → `ITechnologyDetector` (:1057) → `PersistCustomContentAsync` (:1067,
   extraction/search/snippets). Then `RunNgramSitePassAsync` (:835/:860, D14 site pass).
6. **Pipeline:** `BE/CrawlForge.Audit/Pipeline/AuditPipeline.cs` → `RunAsync` (:80), 8 steps:
   stored-HTML reuse → tolerant parse → scope resolution + element inventory → per-URL rule passes
   (`IAuditRuleAnalyzer`) → finding emission → metric aggregation → report readiness.
7. **Analyzer/rule registration:** the rule family is composed in
   `EngineServicesModule.RegisterAudit` (:161–177): `AuditRuleHost` over
   `AnalyzerRuleAdapter`(`AccessibilityAnalyzer`, `ContentQualityAnalyzer`, `HreflangAnalyzer`,
   `MobileFriendlinessAnalyzer`) + `NGramRuleAnalyzer`. Host: `Audit/Pipeline/AuditRuleHost.cs`
   (per-analyzer failure isolation); adapter: `Host/Composition/AnalyzerRuleAdapter.cs`.
   Rule catalogue: `AuditRuleCatalog` in `Audit/Pipeline/RuleDefinition.cs:45`; rule-key constants:
   `Contracts/Constants/FindingRuleKeys.cs`. **Note:** `FindingEmitter` accepts an optional catalog
   for priority/remediation-difficulty evidence enrichment, but the DI factory
   (`EngineServicesModule.cs:223–225`) constructs it **without** the catalog — enrichment is inert.
8. `ScoreAndFinalizeAsync` (:1173) — `WafBlockRateDiagnostic.ReportAsync` (:1179) →
   `PersistIntelligenceAggregatesAsync` (:1197, GEO readiness + governance matrix + coverage) →
   `AuditScoringService.ComputeAsync` + `AuditScoreRecorder.RecordAsync` (:1186–1187) →
   `FinalizeAsync` (:1236): persists terminal status + `counters_json` (+`errorMessage` on failure),
   publishes `stage-changed` then **`audit-failed` (:1270) or `audit-completed` (:1281)**.
9. Hub publication points: `PublishStageAsync` (:1647, `stage-changed`), `PublishProgressAsync`
   (:1663, `crawl-progress` throttled 1/s; counters persisted ≤2/s), `HubHostThrottleSink`
   (`Host/Execution/HostThrottleSinks.cs`, `host-throttle`).
10. **Counter wire mismatch (probe-2):** engine writes `discovered/fetched/analyzed/failed`
    (`Host/Execution/AuditRunState.cs` → `AuditRunCounters.ToPayload()` :28–34); REST
    `Api/Controllers/AuditWireMapper.cs` → `ParseCounters` (:22) reads
    **`queued/fetched/error/blocked/wafBlocked`** — only `fetched` overlaps, so REST/UI counters
    show 0 for the rest. Fix belongs in a change record; do not silently rename either side.

Findings land in `findings` via `FindingEmitter` (`Audit/Pipeline/FindingEmitter.cs`, suppression-aware,
project-scoped); aggregates in `metric_summaries`/`audit_scores` (also recomputable via
`Data.Repositories/AuditAggregationService.cs` → `RecomputeAsync`).

## 5. API surface

- **Discovery:** `Host/Composition/ApiSurfaceModule.cs` — controllers come from the Api library
  assembly via `AddApplicationPart(typeof(ApiControllerBase).Assembly)`; `MapControllers()` in
  `ConfigureEndpoints`. JSON conventions: `Api/Controllers/ApiJsonOptions.cs` (camelCase;
  `ApiEnumConverter` lowercase enum strings with locked specials e.g. `CatchUp`↔`"catch-up"`;
  envelope converters; `PaginationMetadataConverter` `TotalCount`→`total`). Envelope shape:
  `Contracts/Common/ApiEnvelope.cs`; validation filter `Api/Controllers/ApiEnvelopeValidation.cs`.
- **Session gate:** `Host/Hosting/SessionTokenMiddleware.cs` — `X-CrawlForge-Session` on `/api/*`
  (except `/api/health`), `/hubs/*`, `/metrics`; SignalR may use `access_token` query.
- **Wire records live in the Api project**, not Contracts: `Api/Controllers/*WireContracts.cs`
  (Audit, AuditExplorer, Content, Intelligence, Operations, Project, Tools) with mappers
  `AuditWireMapper.cs` / `ProjectWireMapper.cs`. Storage DTOs: `Contracts/Entities/*.cs`.

| Controller (`Api/Controllers/`) | Route prefix |
|---|---|
| `AuditsController`, `AuditExplorerController`, `AnalyticsController`, `HostsController`, `SemanticsController`, `GeoController`, `NgramsController`, `RedirectMatrixController` | `api/audits` |
| `ProjectsController` | `api/projects` |
| `SuppressionsController` | `api/projects/{id}/suppressions` |
| `ConfigurationsController` (`projects/{id}/configurations`, `configurations/{id}`), `SchedulesController`, `WafController`, `LogsController`, `ContentExplorerController` | `api` |
| `HealthController` (`health`, `version`) + `MetricsController` (same file, absolute `/metrics`) | `api` / `/metrics` |
| `AnalysisController` | `api/analysis` |
| `SettingsController` | `api/settings` |
| `SystemController` | `api/system` |
| `ToolsController` | `api/tools` |
| `QueriesController` | `api/queries` |
| `PluginsController` | `api/plugins` |
| `DestinationsController` | `api/destinations` |
| `DictionaryController` | `api/dictionary` |
| `ExportsController` | `api/exports` |
| `DiagnosticsController` | `api/diagnostics/artifacts` |

- **Frontend route catalogue:** `FE/contracts/api/routes.ts` — frozen `API_ROUTES`
  (88 groups / 108 method+path operations, `{method, path, group, additive, enveloped}`);
  `FE/contracts/api/contracts-map.ts` — `ApiRouteContracts` binds every route key to
  params/request/response DTO types (compile-time assertions fail `tsc` on drift). Per-domain DTO
  types: `FE/contracts/api/*.ts` (audits, projects, …). Client: `FE/store/apiClient.ts` →
  `createLoopbackApiClient` resolves `{param}` templates, sends the session header, parses envelopes
  (interface: `FE/contracts/api/client.ts`).

## 6. Real-time hub

- **Definitions:** `Api/Hubs/HubEventCatalog.cs` — 19 server→client events + `subscribe`/`unsubscribe`;
  contract `Contracts/Api/HubContracts.cs` (`IRealtimeHub.PublishAsync(HubEvent)`,
  `HubEvent(Name, PayloadJson, ThrottleKey, ThrottleInterval)`).
- **Core:** `Api/Hubs/RealtimeHubService.cs` (throttle/coalesce, per-audit groups `audit-{id}`,
  2 s heartbeat via `SystemHealthSampler`); transport `Api/Hubs/SignalRHubChannel.cs`;
  hub class `Api/Hubs/CrawlForgeHub.cs` at `/hubs/realtime`.
- **Frontend consumers:** `FE/contracts/hub/events.ts` (`HUB_SERVER_EVENT_NAMES`, :339 — **18 names**,
  payload types, guards in `hub/guards.ts`) → `FE/store/hubClient.ts` (SignalR setup) →
  `FE/store/hubDemux.ts` (`createHubDemux`, typed handlers per event) → `FE/store/crawlForgeStore.ts`
  + `FE/store/liveProgress.ts`.

| Event | Production producer |
|---|---|
| `crawl-progress` | `Host/Execution/AuditExecutionEngine.cs` → `PublishProgressAsync` (:1663) |
| `url-batch` | **no producer** |
| `stage-changed` | `AuditExecutionEngine.cs` → `PublishStageAsync` (:1647) |
| `findings-updated` | **no producer** |
| `audit-completed` | `AuditExecutionEngine.cs` → `FinalizeAsync` (:1281) |
| `audit-failed` | `AuditExecutionEngine.cs` → `FinalizeAsync` (:1270) — added by HOTFIX-01 (G05 list is stale here) |
| `backend-lifecycle` | **no producer** |
| `schedule-fired` | `Crawler/Scheduling/ScheduleHubEvents.cs` → `ScheduleEventPublisher.PublishFiredAsync` (from `ScheduledAuditService`) |
| `schedule-run-completed` | `ScheduleHubEvents.cs` → `PublishRunCompletedAsync` |
| `export-delivery` | `Export/ExportModels.cs` → `ExportEventPublisher.PublishDeliveryAsync` (from `Export/Delivery/ExportDestinationDelivery.cs`) |
| `waf-blocks` | **no producer** (name also used as metric key / MCP resource — not a hub publish) |
| `host-throttle` | `Host/Execution/HostThrottleSinks.cs` → `HubHostThrottleSink.Emit` (wire fields locked by HOTFIX-01) |
| `system-health` | `Api/Hubs/RealtimeHubService.cs` → heartbeat (`EmitHeartbeat`) |
| `matrix-row-validated` | `Crawler/ListMode/RedirectMatrixValidator.cs` → `EmitBatchesAsync` |
| `log-import-progress` | `Intelligence/LogAnalyzer/LogImportService.cs` → `PublishProgressAsync` |
| `log-import-completed` | `LogImportService.cs` → `PublishCompletedAsync` |
| `embedding-progress` | **no producer** |
| `embedding-completed` | **no producer** |
| `local-notification` | `ScheduleHubEvents.cs` → `PublishLocalNotificationAsync` via `HubLocalNotificationSink` — **absent from the frontend 18-event contract** |
| `export-progress` (extra) | `Export/ExportModels.cs` → `ExportEventPublisher.PublishProgressAsync` (from `ExportEngine.cs`) — **not in `HubEventCatalog`, not in frontend contract: emitted but unconsumed** |

**Metrics:** `Api/Metrics/MetricFamilyCatalog.cs` (15 families); exposition
`Api/Metrics/PrometheusMetricsExposition.cs`, served by `MetricsController` (`/metrics`).
Only `hub_connections` has `Record()` call sites (`RealtimeHubService` connect/disconnect);
4 runtime families (`process_working_set_bytes`, `process_managed_heap_bytes`, `gc_collections_total`,
`sqlite_wal_bytes`) are refreshed at render time; **10 families are declared-only** (G06):
`crawl_urls_total`, `crawl_throughput_per_second`, `frontier_depth`, `audit_phase_duration_seconds`,
`findings_emitted_total`, `db_operation_duration_seconds`, `content_store_bytes`,
`host_effective_rate`, `host_concurrency`, `host_throttle_adjustments_total`.

## 7. MCP

All in `BE/CrawlForge.Mcp/` (flat layout), composed by `Host/Composition/McpModule.cs`:

| Piece | File → symbol |
|---|---|
| Tools (8) | `McpToolSurface.cs` → `Definitions`: `start-audit`, `pause-audit`, `resume-audit`, `cancel-audit`, `re-audit`, `analyze-single-page`, `run-schedule-now`, `get-audit-progress`. No project/configuration tools. |
| Resources (15) | `McpResourceSurface.cs` (+ `McpResourcePorts` read-model bundle): `audit-summary`, `high-severity-findings`, `url-detail`, `audit-list`, `schedule-status`, `crawl-progress`, `extracted-content`, `ngram-analysis`, `waf-blocks`, `host-throttle`, `redirect-matrix-results`, `log-correlation`, `semantic-link-opportunities`, `geo-readiness`, `analytics-coverage` (`crawlforge://` URIs). |
| Prompts (5) | `McpPromptSurface.cs`: `audit-health-briefing`, `high-severity-triage`, `audit-delta-briefing`, `ngram-topical-briefing`, `semantic-topical-briefing`. |
| Dispatcher | `McpJsonRpcDispatcher.cs` — hand-rolled JSON-RPC 2.0; logs every request via `IAutomationActionLog`. |
| Transport | `McpLoopbackServer.cs` — **own `HttpListener`** (not ASP.NET routing): streamable HTTP `POST http://127.0.0.1:{ephemeral}/mcp` + SSE GET; bearer token on every request. Started by `McpLifecycleParticipant` (Order 40) **unconditionally — ignores `mcp.enabled` (G17)**. |
| Token | `McpTokenService.cs` — `McpTokenService` (rotate/validate) + `FileMcpTokenStore` → encrypted `{DataDirectory}/mcp-token.json`. Settings-surface token is separate (G17). |
| Endpoint descriptor | `McpModule.cs` → `McpLifecycleParticipant.StartAsync` writes `{DataDirectory}/mcp-endpoint.json` (`{endpoint, token}`, atomic tmp+move), deletes on stop. |
| Action log | `AutomationActionLog.cs` — **DB table `automation_action_log`, not a disk file** (schema `SchemaCatalog.Baseline.cs`; mapping `Data.Repositories/SchemaNames.cs`). |
| Service parity | Tools call the same services as REST: `McpToolSurface.StartAuditAsync` → `IAuditExecutionService.StartAuditAsync` (identical to `AuditsController`); lifecycle via `IAuditLifecyclePort` (`AuditLifecyclePorts.cs`), bound to the same engine. |

**Stdio adapter** `app/src/mcp-stdio-adapter/`: `src/index.ts` reads `--endpoint`/`CF_MCP_ENDPOINT`
and `--token`/`CF_MCP_TOKEN` (**does not read `mcp-endpoint.json` itself** — the spawning parent must
pass the values); `src/bridge.ts` (MCP SDK `Client` + `StreamableHTTPClientTransport` ↔ stdio
`Server`); `src/loopback.ts` (loopback-only guard). Known defect: `re-audit` fails deserializing
`$.id` into `CrawlConfigurationDto` (probe-2 §6).

## 8. Persistence

- **Provider:** `CoreServicesModule` always constructs `SqliteDbProvider`
  (`{DataDirectory}/crawlforge.db`). **MySQL is never constructed at runtime** — only
  `Api/Controllers/SettingsController.cs` → `TestDatabaseAsync` builds a `MySqlDbProvider` for a
  connection test; `Data.MySql/ProviderSwitchOrchestrator.cs` (`SwitchAsync`/`RevertAsync`) has
  **no production caller** (tests only) (G16).
- **Connections:** `Data.Sqlite/SqliteConnectionFactory.cs` — CQRS: many read connections
  (`query_only`) vs single writer (`SemaphoreSlim` gate, `SingleWriterConnection` lease);
  `SqliteConnectionConfiguration.cs` — WAL + `busy_timeout` + `foreign_keys`;
  `SqliteWalMonitor.cs` — WAL sidecar stats for heartbeat/metrics/compact.
- **Migrations:** `Data.Migrations/SchemaMigrator.cs` — ledger table `schema_migrations` keyed
  `(version, provider_dialect)`, transactional apply, idempotent; runs at startup via
  `MigrationLifecycleParticipant` (Order −100). Versions in `Schema/SchemaCatalog.Migrations.cs`:
  v1 baseline (25 tables), v2 additive (14 tables), v3 additive columns, v4
  `ngram_site_summaries` read model. First-run seed: `Seeding/FirstRunSeed.cs`. Dialect renderers:
  `Dialects/{SqlDialect,SqliteDialect,MySqlDialect}.cs` + `SqlRenderer.cs`.
- **Schema catalogue:** `Data.Migrations/Schema/SchemaCatalog.Baseline.cs` / `.Additive.cs` /
  `.Migrations.cs` (logical `Table`/`Col`/`Fk`/`Idx` declarations; model in `Schema/LogicalSchema.cs`).
  **Divergent conformance catalogue:** `Data.MySql/MySqlLogicalSchema.cs` `RequiredIndexes` still
  references non-existent columns — `schedule_runs.schedule_id` (:120),
  `content_artifacts.artifact_kind` (:121), `log_url_aggregates.import_id` (:137–139),
  `governance_matrix.agent_key` (:144) — shipped names are `schedule_definition_id`, `kind`,
  `log_import_id`, `agent_name` (G16).
- **Repositories:** `Data.Repositories/SqlRepository.cs` — one generic CQRS repo for every DTO;
  `SchemaNames.cs` (DTO→table, e.g. `FindingDto`→`findings` :23) + `EntityMapping.cs`
  (property→snake_case column). All 39 registered in `CoreServicesModule.RegisterRepositories`.
  Aggregates: `AuditAggregationService.cs` → `RecomputeAsync` (rewrites `metric_summaries` +
  `audit_scores` transactionally). Archive: `ProjectArchiveService.cs` (per-table plans).
- **Content store:** `Data.ContentStore/CompressedContentStore.cs` — Brotli + SHA-256 dedup, files
  `{DataDirectory}/content-store/sha256/<2-hex>/<64-hex>`, index rows in `content_artifacts`.
  Retention: `Data.ContentStore/RetentionPruner.cs`.

**Add a table/column end to end** (traced via `findings`): see §12 checklist (b) for the config-field
variant; for a new table: (1) `SchemaCatalog.Baseline.cs`/`.Additive.cs` declaration → (2) migration
version in `SchemaCatalog.Migrations.cs` → (3) DTO record in `Contracts/Entities/` → (4) table name
in `Data.Repositories/SchemaNames.cs` (columns auto-derive) → (5) `AddRepository<TDto>` in
`CoreServicesModule` → (6) wire record + mapper in `Api/Controllers/*WireContracts.cs` if exposed →
(7) archive plan in `ProjectArchiveService.cs` if project-scoped → (8) keep
`Data.MySql/MySqlLogicalSchema.cs` in sync.

## 9. Frontend

- **Entry:** `FE/main.tsx` → `FE/App.tsx` → `FE/shell/CrawlForgeShell.tsx` (Mantine AppShell).
  **Error boundary (HOTFIX-01):** `FE/shell/ErrorBoundary.tsx` → `SurfaceErrorBoundary`, wrapped
  around `ViewContent` at `CrawlForgeShell.tsx:186` (keyed per view, inline error + Retry).
- **Navigation:** rail registry = `const ENTRIES` in `FE/shell/NavigationRail.tsx:26` — **5 views**:
  `hub`, `audits`, `report`, `tools`, `settings` (`ShellView` union in `FE/shell/windowState.ts`,
  persisted at localStorage `crawlforge.shell.windowState.v1`). `ViewContent`
  (`CrawlForgeShell.tsx:78`) switches: hub→`HubDashboard`, audits→`AuditsView`, report→`ReportView`,
  tools→`ToolsView`, settings→`SettingsSurface`. Command palette: `FE/shell/CommandPalette.tsx`
  (Ctrl+K; nav + first 20 projects/audits; no saved queries/tools/plugin commands).
- **Shell services / store:** `FE/shell/ShellContext.tsx` (`createShellServices`, `ShellProvider`,
  `useShell`; `ShellNavigation.openAudit` sets activeAudit + view='report' + hub subscribe).
  Stores: `FE/store/crawlForgeStore.ts` (normalized entities, windowed pages, host-throttle,
  system-health, plugins, activity ring), `FE/store/liveProgress.ts` (latest-wins live counters),
  `FE/store/windowedCache.ts` (**`MAX_WINDOWED_PAGES = 24`** vs spec ≤20), `FE/store/hubDemux.ts`,
  `FE/store/hubClient.ts`, `FE/store/apiClient.ts`, `FE/store/pluginBus.ts`,
  `FE/store/selectors.ts`, `FE/store/frontendHeap.ts`.
- **Report-surface mounting point:** `FE/shell/AuditViews.tsx` → `ReportView`, the "Report surfaces"
  Paper at **:312–322** is a literal placeholder ("URL / finding / link grids, charts, and the link
  graph mount into this workspace from the report workpieces…"). Only live telemetry renders above it.
  **A new surface becomes reachable by:** mounting its component inside `ReportView` (or a new
  `ShellView` in `ENTRIES` + `ViewContent`), wiring data through `useShell().api` / store selectors.
  The integrator recipe exists in `FE/surfaces/dashboards/README.md` §"Shell wiring (for the
  integrator)" (:58–68): `useDashboardAggregates(api, auditId)` props, `applyChartCrossFilter`,
  `VisualizationSurface` takes `api`+`auditId`. **None of that wiring exists in `shell/` today.**
- **Built but unmounted surfaces** (only `index.ts` re-exports + tests/bench import them):
  `FE/surfaces/dashboards/` (`AuditDashboard`, `CrawlHealthPanel`, `ScoreTrendSurface`,
  `IntelligenceDashboards` panels, `VisualizationSurface`), `FE/surfaces/explorers/`
  (`ExplorerHost`, `coreExplorers.tsx` 5 explorers, `analysisExplorers.tsx` 6 explorers; registry
  `EXPLORERS`/`EXPLORER_NAV` in `explorers/index.ts` never imported by the shell),
  `FE/graphs/{LinkGraphSurface,SemanticGraphSurface}.tsx`, `FE/charts/EChart.tsx`,
  `FE/grid/DataGrid.tsx`, `FE/plugins/PluginManagementSurface.tsx`.
- **Mounted surfaces:** `FE/shell/HubDashboard.tsx` (projects + New project modal),
  `FE/shell/AuditViews.tsx` (run view), `FE/surfaces/single-page/SinglePageAnalyzer.tsx` and 11 more
  tools via `FE/tools/ToolsWorkspace.tsx` `CATALOG` (12 entries) → `FE/shell/ToolsView.tsx`,
  `FE/shell/SettingsSurface.tsx`, `FE/shell/StatusBar.tsx` (footer, `CrawlForgeShell.tsx:197`).

**UI surface map** (spec: `architecture/06-frontend-shell-and-ui/04-ui-surface-map.md`;
M = mounted, U = component exists but unmounted, N = no component; runtime verdicts: probe-2 §3):

| ID | Surface | Status | Component / evidence |
|---|---|---|---|
| UI-01 | Main hub › Projects | M | `shell/HubDashboard.tsx` |
| UI-02 | Project overview | N | no overview screen; row click does nothing |
| UI-03 | Project › Configurations | N | DTOs only (`contracts/api/projects.ts`) |
| UI-04 | Configuration editor (13 tabs) | N | no editor component; 0/13 tabs |
| UI-05 | Project › Integrations | N | — |
| UI-06 | Project › Segments | N | — |
| UI-07 | Project › Audit history & retention | N | global audits list exists; no per-project surface |
| UI-08 | Main hub › Clients | N | — |
| UI-09 | Main hub › Schedules | N | routes + demux handlers exist; no UI |
| UI-10 | Audit run view | M | `shell/AuditViews.tsx` (`AuditsView`/`ReportView` telemetry) |
| UI-11 | Report › Dashboard | **placeholder** | `AuditViews.tsx:312–322`; real dashboards U |
| UI-12 | Report › Issue explorer | U | `explorers/coreExplorers.tsx` `issueExplorer` + `ExplorerHost` |
| UI-13 | Report › URL explorer & detail | U | `urlExplorer`; no URL-detail component |
| UI-14 | Visualization window | U | `dashboards/VisualizationSurface.tsx` + graphs; Electron window exists, renderer ignores `?route=` |
| UI-15 | Report › Comparison | N | `AuditComparisonReader` backend only |
| UI-16 | Report › Specialist surfaces | U/N | extraction/ngram/log/semantic/geo/analytics/redirect-matrix/WAF explorers U; accessibility/performance/technology/templates/custom-search/snippet/rendered-comparison N |
| UI-17 | Log file analyzer | U | `logExplorer` + `LogIntelligencePanel`; no import UI |
| UI-18 | Single-page analyzer | M | `surfaces/single-page/SinglePageAnalyzer.tsx` via Tools |
| UI-19 | Standalone tools | M | `tools/ToolsWorkspace.tsx` CATALOG (12 tools) |
| UI-20 | Export dialog | N | no component uses `exports.*` routes; REST exports work |
| UI-21 | Settings | M (partial) | `shell/SettingsSurface.tsx`; missing browser/dictionary/integrations/templates/action-log/release-check/Plugins sections |
| UI-22 | Plugin manager | U | `plugins/PluginManagementSurface.tsx`; Settings never imports it |
| UI-23 | Status bar | M | `shell/StatusBar.tsx` |
| UI-24 | Recovery screen | N (renderer) | Electron opens `recovery` window; renderer has no recovery component and ignores `?route=recovery` |

## 10. Electron

| Piece | File → symbol |
|---|---|
| Main | `EL/main.ts` — `compose()` (:273), `wireIpc()` (:323), single-instance (:401), `before-quit` drain; backend launch resolution (`CF_BACKEND_COMMAND` / packaged exe / dev dll); `?route=` set at :241 |
| Supervision | `EL/supervisor/backend-supervisor.ts` — `start`/`launch`/`waitForReady`/`healthLoop`/`stop`/`restart`; 3 consecutive failures ⇒ recovery |
| Handshake | `EL/handshake/port-discovery.ts` — `PortDiscovery.discover` reads `loopback-handshake.json`; consts in `EL/types.ts` |
| Startup | `EL/startup/startup-orchestrator.ts` — splash → backend → main/recovery → hydration gate → plugin scan |
| Recovery | `EL/recovery/recovery-service.ts` — `buildReport`, `listArtifacts` (scans `logs`, `diagnostics`, `crash-dumps` — non-recursive, G15), `purgeDiagnostics`, `retry` |
| Windows | `EL/windows/window-catalog.ts` — `WINDOW_CATALOG` 8 kinds: `splash`, `main`, `report`, `visualization`, `analyzer`, `tools`, `settings`, `recovery`; `EL/windows/window-manager.ts` — `open`/`loadRoute`; state in `window-state.json` |
| Preload bridge | `EL/preload.ts` + `EL/preload/bridge.ts` — `window.crawlForge` `{backendBaseUrl, getSessionToken}`; `window.crawlForgeShell` `{showFileDialog, showNotification, setTaskbarProgress, minimizeWindow, maximizeWindow, closeWindow, restartBackend, purgeDiagnostics, onBackendStatus, signalHydrated}` |
| IPC channels | `crawlforge:get-bridge`, `crawlforge:hydrated`, `crawlforge:file-dialog`, `crawlforge:notification`, `crawlforge:taskbar-progress`, `crawlforge:window-minimize`, `crawlforge:window-maximize`, `crawlforge:window-close`, `crawlforge:backend-restart`, `crawlforge:diagnostics-purge`, `crawlforge:backend-status` (`IPC_CHANNELS` in `preload/bridge.ts`) |
| Plugins | `EL/plugins/plugin-scanner.ts` — manifest scan + trust prompt; **`new PluginScanner(fileSystem, logger, trustPrompt, null)` at `main.ts:300`** — inert registry delegate (G02); `signalHydrated` has **0 frontend callers** |

## 11. Tests

| Project (`app/tests/`) | Covers |
|---|---|
| `CrawlForge.Acceptance.Tests` | Acceptance catalogue/evidence integrity, packaging verification |
| `CrawlForge.Api.Tests` | Controller routes, envelopes, export labels |
| `CrawlForge.Audit.Tests` | Analyzers (accessibility, content quality/spelling, …) |
| `CrawlForge.Boundary.Tests` | Air-gap/zero-outbound, consent, diagnostics locality |
| `CrawlForge.Contracts.Tests` | Contract surface, enum vocabulary, envelope behavior |
| `CrawlForge.Crawler.Tests` | Fetching, redirects, politeness, frontier |
| `CrawlForge.Data.ContentStore.Tests` | **Empty project** (csproj only; real tests in Data.Repositories.Tests) |
| `CrawlForge.Data.Migrations.Tests` | Migration apply/idempotency, seeding, live MySQL, report parity |
| `CrawlForge.Data.MySql.Tests` | Provider/dialect, logical schema, provider switch |
| `CrawlForge.Data.Repositories.Tests` | CRUD/paging, aggregation, archive, content store |
| `CrawlForge.Data.Sqlite.Tests` | Provider conformance, WAL, options |
| `CrawlForge.E2E.Tests` | Full-stack audits, AIMD, MySQL parity, MCP delegation |
| `CrawlForge.Export.Tests` | Export engine/writers/delivery |
| `CrawlForge.Host.Tests` | Host wiring + **wire-shape contract tests** |
| `CrawlForge.Intelligence.Tests` | Embeddings/ONNX, semantic pipeline |
| `CrawlForge.Mcp.Tests` | Protocol, tools/resources/prompts, tokens |
| `CrawlForge.Security.Tests` | Secrets, handshake, crash dumps, guardrails |
| `CrawlForge.Scale.Benchmarks` | 500k-scale pipeline/export/WAL pressure |
| `electron` | Placeholder only (`placeholder.ts`) |
| `frontend` | Vitest: contracts, shell (incl. `hubDemux.test.ts`), grid, charts, tools, plugins |
| `governance` | Governance analyzer: planted-violation fixtures + live `app/src` scan |
| `fixture-site` | Trap site + expectations (below) |

**Contract-conformance tests (HOTFIX-01) — the pattern every cross-layer payload must follow:**
- `app/tests/CrawlForge.Host.Tests/Contracts/WireShapeContractTests.cs` — real producer → real
  consumer: `ProjectWireMapper.SerializeAimd` → `AimdSettingsParser.Parse` (nested/legacy/invalid);
  `HostThrottleHubPayload_UsesFrontendContractFieldNames` (:79) serializes real `HubHostThrottleSink`
  output and asserts the exact frontend field names.
- `app/tests/frontend/contracts/host-throttle-wire.test.ts` — feeds the mirrored producer payload
  into the real `isHubServerEvent` guard and `demuxGraph` demultiplexer, asserts run-view host state.

**Fixture site:** `app/tests/fixture-site/server.mjs` → `http://127.0.0.1:47311`;
`GET /__fixture/reset` before each run, `GET /__fixture/requests` after (`nonGet` must be empty);
`expected-results.json` — 31 traps + global assertion `no-non-get-requests`.
**Fixture acceptance harness:** `app/tests/fixture-acceptance/` **does not exist yet** — build plan
WP-08-04 owns it. Runners: `dotnet test app/CrawlForge.sln`; `npm test` (vitest workspaces);
UI automation via a **copy** of the launch script with `--remote-debugging-port=9222` (CDP).

## 12. "How to add" checklists

Every file listed must change, in order. Registration without consumption is not done (§13).

**(a) New audit rule**
1. Analyzer method/class in `BE/CrawlForge.Audit/<family>/` producing `FindingDraft`s.
2. Rule key constant in `BE/CrawlForge.Contracts/Constants/FindingRuleKeys.cs`; definition in
   `AuditRuleCatalog` (`BE/CrawlForge.Audit/Pipeline/RuleDefinition.cs`).
3. Register the analyzer singleton **and add it to the `AuditRuleHost` adapter list** in
   `Host/Composition/EngineServicesModule.cs` → `RegisterAudit` `IAuditRuleAnalyzer` factory (:161–177).
4. (If priority/difficulty evidence is wanted) pass the catalog into the `FindingEmitter` factory
   (:223–225) — currently omitted.
5. Unit test in `app/tests/CrawlForge.Audit.Tests`; wire test if a new payload shape crosses layers.
6. Frontend: rule label/i18n where the issue explorer renders rule keys (once mounted).
**Live when:** a full fixture audit (REST or MCP start) emits the rule's finding into `findings`
(readable via `GET /api/audits/{id}/findings`) for the matching trap — fixture harness (WP-08-04).

**(b) New configuration field (storage → REST → engine → editor)**
1. Column: `Data.Migrations/Schema/SchemaCatalog.Additive.cs` (+ migration bump in
   `SchemaCatalog.Migrations.cs`); or a field inside an existing JSON payload column.
2. DTO: `CrawlConfigurationDto` in `BE/CrawlForge.Contracts/Entities/CoreEntities.cs:136`.
3. Wire: record in `Api/Controllers/ProjectWireContracts.cs` + `ProjectWireMapper.ToConfiguration`
   / `ApplyExtensions` (`Api/Controllers/ProjectWireMapper.cs`).
4. REST: `ConfigurationsController` POST/PUT already round-trips the DTO — verify the field survives.
5. Engine: consume `configuration.<Field>` in `AuditExecutionEngine.CrawlPhaseAsync` /
   `AnalyzePhaseAsync` (snapshot is frozen at start).
6. Frontend types: `FE/contracts/api/projects.ts` (+ `contracts-map.ts` if shape changes).
7. **Configuration editor UI-04 does not exist** — the field is unreachable from the UI until the
   editor workpiece builds it; REST-only is not "live" for a configure capability.
**Live when:** PUT round-trips the field through REST **and** a fixture audit changes behavior
accordingly **and** the editor (when built) shows/sets it.

**(c) New REST endpoint**
1. Action in an `Api/Controllers/*Controller.cs` (route attribute; envelope helpers from
   `ApiControllerBase`).
2. Wire record in the matching `*WireContracts.cs` + mapper method.
3. `FE/contracts/api/routes.ts` — add the `API_ROUTES` entry; `FE/contracts/api/contracts-map.ts` —
   bind params/request/response; DTO type in the domain `contracts/api/*.ts`.
4. Store action / component call via `useShell().api`.
5. Route/envelope test in `CrawlForge.Api.Tests`; contract-conformance test if the payload feeds a
   hub/store consumer.
**Live when:** a session-token request returns the envelope **and** the bound UI control exercises
it end to end (probe-style REST call + UI path).

**(d) New hub event**
1. Constant in `Api/Hubs/HubEventCatalog.cs` (add to `AuditScopedEvents` in `RealtimeHubService.cs`
   if per-audit routed).
2. Producer: publish `HubEvent` via `IRealtimeHub.PublishAsync` with throttle key (engine, scheduler,
   export, … — never a controller).
3. `FE/contracts/hub/events.ts` — name in `HUB_SERVER_EVENT_NAMES` + payload type + guard.
4. `FE/store/hubDemux.ts` — handler; store slice in `crawlForgeStore.ts` or `liveProgress.ts`.
5. Contract-conformance test feeding the **real producer output** into the real demux
   (`host-throttle-wire.test.ts` pattern).
**Live when:** a fixture run triggers the event and the run view updates without manual refresh.

**(e) New MCP tool**
1. Definition + handler in `BE/CrawlForge.Mcp/McpToolSurface.cs` (`Definitions`), calling an existing
   application service (same one REST uses).
2. Dispatcher/action log pick it up automatically; no transport change.
3. Tests in `CrawlForge.Mcp.Tests`; stdio listing check.
**Live when:** `tools/list` through `app/src/mcp-stdio-adapter` shows it and `tools/call` drives a
real fixture audit (not a mock).

**(f) New UI surface**
1. Component in `FE/surfaces/<area>/` (data via `useShell().api` / selectors; follow
   `surfaces/dashboards/README.md` wiring recipe).
2. Mount: inside `ReportView` (`FE/shell/AuditViews.tsx`) for report surfaces, **or** new `ShellView`
   in `FE/shell/windowState.ts` + `ENTRIES` in `NavigationRail.tsx` + `ViewContent` case in
   `CrawlForgeShell.tsx`.
3. Store wiring if it needs live data (demux handler / selector).
4. Optional dedicated window: kind in `EL/windows/window-catalog.ts` **and** renderer `?route=`
   handling (currently ignored — must be added).
5. Frontend test in `app/tests/frontend/`.
**Live when:** reachable by rail/palette/window click in a launched app and renders fixture data
(probe-2-style UI walk).

**(g) New analyzer that must run in full audits**
1. Implement in `BE/CrawlForge.Audit/` (per-URL: `AnalyzeAsync(PageAuditContext, …)` shape).
2. DI singleton in `EngineServicesModule.RegisterAudit`.
3. Per-URL: add `AnalyzerRuleAdapter` to the `AuditRuleHost` list (:161–177). Site-level: follow the
   D14 pattern — resolvable singleton + explicit call in `AnalyzePhaseAsync`
   (`RunNgramSitePassAsync`, :860). Render-dependent: requires the G03 rendering fix first.
4. Persist via `FindingEmitter` / `MetricSummaryDto` writes in `AnalyzeOnePageAsync`.
5. Tests: unit + a production-path test proving the engine invokes it (not a manually injected test).
**Live when:** a full fixture audit — not the single-page tool — produces its findings/metrics.

## 13. Known unwired or unmounted code

Verified at current HEAD. "Missing registration point" = the file that must change for the code to
run in the product.

| # | Component (exists) | Missing wiring | Gap |
|---|---|---|---|
| 1 | Report dashboards/explorers/graphs/grid (`FE/surfaces/dashboards/*`, `explorers/*`, `FE/graphs/*`, `FE/grid/DataGrid.tsx`) | Mount in `FE/shell/AuditViews.tsx` `ReportView` (:312–322 placeholder) + navigation | G01 |
| 2 | Plugin system (`EL/plugins/plugin-scanner.ts`, `FE/plugins/PluginManagementSurface.tsx`, sandbox) | Real registry delegate at `EL/main.ts:300`; mount manager in Settings; `signalHydrated()` call in frontend bootstrap | G02 |
| 3 | `IRenderingPool` / `HeadlessRenderingPool` (`Crawler/Rendering/`) | Inject into `AuditExecutionEngine`; set `RenderedDocument` at `AuditExecutionEngine.cs:1510` | G03 |
| 4 | CWV/performance: `CoreWebVitalsAnalyzer`, `PerformanceBudgetEvaluator`, `CodeCoverageAnalyzer`, `PageCompositionAnalyzer`, `LabRenderingProbe` (`Audit/Performance/`) | No DI registration anywhere (`EngineServicesModule` never references them); no engine call | G04 |
| 5 | Hub events `url-batch`, `findings-updated`, `backend-lifecycle`, `waf-blocks`, `embedding-progress`, `embedding-completed` | No producer (catalog constants only). `audit-failed` was fixed by HOTFIX-01 | G05 (partial) |
| 6 | 10 metric families (§6 list) | `Record()` call sites in engine/repositories/hub | G06 |
| 7 | AIMD WAF/crawl-delay/Retry-After inputs | `AuditExecutionEngine.cs:709` passes `IsChallenge: false`; no crawl-delay source into controller options | G07 |
| 8 | Sitemap ingestion | Absent entirely (only export `SitemapWriter`); seed expansion in `CrawlPhaseAsync` | G08 |
| 9 | `ILoopbackInterceptingProxy` / `ISignedRequestHandler` (`Crawler/Proxy/`, `SignedRequestHandler` registered) | Engine's fetcher uses plain `CrawlTargetHttpClientFactory` (`AuditExecutionEngine.cs:601–605`) | G09 |
| 10 | List Mode (`IListModeIngestor` registered) | `AuditsController.StartAuditAsync` starts the engine **before** persisting `UrlListJson`; engine reads only `SeedsJson` (:637) | G10 |
| 11 | `StructuredDataValidator` (registered, `Audit/StructuredData/`) | Called only from `ToolsController`; no per-audit invocation in `AnalyzeOnePageAsync` | G11 |
| 12 | Custom search / JS snippet advanced options (`CustomSearchOptions`, `JsSnippetOptions`) | `ParseCustomDefinitions` (`AuditExecutionEngine.cs:1133`) parses minimal records; options never populated from REST/UI | G13 |
| 13 | `llms.txt` crawl-time probe | No `llms` fetch in `CrawlForge.Crawler`; `GeoReadinessAnalyzer` reads stored rows only | G14 |
| 14 | MySQL runtime (`MySqlDbProvider`, `ProviderSwitchOrchestrator`, `MySqlDialect` builders) | Host DI always SQLite; switch orchestrator test-only; `MySqlLogicalSchema` index columns diverge (:120–144) | G16 |
| 15 | MCP settings connection | `McpLifecycleParticipant` starts unconditionally (ignores `mcp.enabled`); settings token separate from `FileMcpTokenStore` | G17 |
| 16 | Configuration CRUD routes/types | No UI screen consumes them (UI-03/UI-04 N) | G18 |
| 17 | Window routes / shell bridge | Renderer ignores `?route=`; 0 frontend calls to `window.crawlForgeShell` | G20 |
| 18 | `ShingleNearDuplicateDetector` (`Audit/ContentQuality/`) | No production caller (cross-URL pass never invoked) | G23 |
| 19 | `ContentScopeValidator` (`Audit/Scoping/`) | No controller/engine call | G24 |
| 20 | `IPreCrawlValidator` (registered `EngineServicesModule.cs:101`) | Engine never invokes it; redirect chains not persisted (`FetchContextScope.Current` unset) | G27 |
| 21 | `AuditRuleCatalog` → `FindingEmitter` enrichment | DI factory (`EngineServicesModule.cs:223–225`) omits the catalog argument → priority/remediationDifficulty never stamped | new (cf. I-194/I-196) |
| 22 | `export-progress` hub event (`Export/ExportModels.cs:206`, called by `ExportEngine`) | Not in `HubEventCatalog`, not in frontend contract/demux — emitted, unconsumed | new |
| 23 | `local-notification` hub event (catalog + `HubLocalNotificationSink` producer) | Absent from frontend `HUB_SERVER_EVENT_NAMES` (18 vs backend 19) — no consumer | new |
| 24 | REST audit counters | `AuditWireMapper.ParseCounters` (:22) keys ≠ engine `ToPayload` keys → dashboard counters 0 | probe-2 §1 |
| 25 | Recovery screen renderer | Electron `recovery` window loads the normal shell; no recovery component / `?route=` handling | UI-24 |
| 26 | Native-fault minidump + recursive dump listing | Hook only on managed `UnhandledException` (`CrawlForgeBackend.cs:150`); `recovery-service.ts` scan non-recursive | G15 |
| 27 | Stdio adapter bundling | Not in offline package manifest (`packaging-report.json`) | G28 |

Counts: **27 rows** — 22 cross-referenced to UNDERBUILD gap IDs (G01–G11, G13–G18, G20, G23–G24,
G27–G28), 1 probe-2 defect (#24), 3 newly observed here (#21–23), 1 UI-24 renderer gap (#25).
G04 covers 5 dead classes; G05 covers 6 events (down from 7 after HOTFIX-01); G06 covers 10 metric
families.
Not listed as gaps: G12 (rule taxonomy), G19 (plugin capabilities), G21 (CSP), G22 (semantics
contract), G25–G26 (single-page/WAF drift), G29 (misc) — behavior defects rather than missing
registration points; see UNDERBUILD-AUDIT §4.