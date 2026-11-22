# CrawlForge code map — where code is registered, wired and mounted

Read this before adding any feature. The first build's main failure was code that existed but was
never registered in DI, never invoked in a real audit, or never mounted in the UI. Every path below
was verified against the working tree (post-HOTFIX-01b; refreshed for the WP-10-04 phase-10 wiring,
uncommitted). Paths are relative to the repository root
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

Data directory: `%APPDATA%/CrawlForge` unless the **backend** configuration overrides it via
`CrawlForge:DataDirectory` (`Host/Configuration/CrawlForgePaths.cs:14` reads `IOptions<CrawlForgeOptions>`
only — command line `--CrawlForge:DataDirectory=…` or the standard `CrawlForge__DataDirectory`
environment variable). **`CRAWLFORGE_DATA_DIRECTORY` is Electron-side only** (`EL/main.ts:168–170`,
handshake discovery path); the backend does not bind it, so setting it alone desynchronizes the
shell from the backend — set both to relocate. Files there: `crawlforge.db`,
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
| `EngineServicesModule.cs` | 10 | `RegisterCrawler` (fetcher, WAF catalogue/evaluator/recorder, `HeadlessRenderingPool` as `IRenderingPool` :98, `ICaptureSnippetExecutor` + `ICaptureModeCoordinatorFactory` :106–120 — WP-10-04, per-audit pools composed at `Create` time, §4.4, `PreCrawlValidator` :121, checkpoints, robots, list-mode ingestor, throttle store, `LiveAimdControllerRouter`, redirect-matrix validator); `RegisterAudit` (parser, inventory, scope resolver, analyzers, **`IAuditRuleAnalyzer` = `AuditRuleHost` factory** :161–177, `AuditPipeline` :193, `SinglePageAnalyzer` :204, scoring/suppression, `FindingEmitter` :223, `AuditRuleCatalog` :226, link-graph + zone/equity (D15), technology, structured-data validator, custom content); `RegisterIntelligence` (n-grams, stopwords, ONNX embeddings, semantic pipeline, GEO, governance, llms generator, log analyzer/correlation, MarTech); `RegisterExports` (job registry, engine, delivery, event publisher); `RegisterScheduler` (`ScheduledAuditService`, completion actions, retention); `RegisterHub` (`SystemHealthSampler`, WAL checkpointer, compact, `PrometheusMetricsExposition`, `SignalRHubChannel`, `RealtimeHubService` as `IRealtimeHub`). |
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
| REST `POST /api/audits` | `Api/Controllers/AuditsController.cs` → `StartAuditAsync` | `IListAuditStartPort.StartAsync` when registered (G10 persist-before-start: url-list/matrix rows persisted BEFORE the engine starts), else `IAuditExecutionService.StartAuditAsync`; §10.4 idempotency: an active run of the same configuration returns the existing auditId |
| REST re-audit `POST /api/audits/{id}/reaudit` | `AuditsController.cs` → `ReauditAsync` | `IReAuditExecutionPort.ReAuditAsync(id, refresh)` — reads the DTO-shaped storage snapshot (G-REAUDIT-SNAPSHOT resolved) |
| MCP `start-audit` / lifecycle tools | `BE/CrawlForge.Mcp/McpToolSurface.cs` | same `IAuditExecutionService` / `IAuditLifecyclePort` |
| Scheduler | `BE/CrawlForge.Crawler/Scheduling/ScheduledAuditService.cs` | `IAuditExecutionService` |
| UI | hub create-project modal (full ConfigurationEditor) → project view start-audit → report window | REST entries above |

**Engine:** `Host/Execution/AuditExecutionEngine.cs` (implements all four port interfaces;
WP-09-11 serial wiring — the crawl loop is fully wired, not a stub; WP-10-04 phase-10 wiring —
rendering/capture + D-021 WAF classes — is in the working tree, uncommitted).

1. `StartAuditAsync` — loads configuration, freezes `ConfigurationSnapshotJson` (storage DTO shape;
   the API boundary merges `enabledCategories` without replacing the snapshot), persists `AuditDto`
   (Queued), idempotency check, `Task.Run(RunAuditAsync)`, publishes `stage-changed`.
2. `RunAuditAsync` — `CrawlPhaseAsync` → `RunPostCrawlPassesAsync` (sitemap correlation, orphan
   analyzer, broken-link probes) → `RunSitePassesAsync` (duplicate metadata/content, URL structure,
   hreflang reciprocity, UA comparison) → `AnalyzePhaseAsync` → `ScoreAndFinalizeAsync`. Failures
   finalize as Failed with `errorMessage` + `audit-failed`.
3. `CrawlPhaseAsync` — scope parse (`CrawlScopeConfigParser`: scopeRules/cookies/headers/behavior
   keys), canonicalizer, request profile, per-audit cookie jar (`CookieJarFetchHook` merge semantics),
   virtual robots per authority, `AimdSettingsParser.Parse` (HOTFIX-01), per-audit
   `AimdPolitenessController` with `CompositeHostThrottleSink` + `PortAwareCrawlDelaySource`
   (robots crawl-delay keyed by host→authority map), `PriorityFrontier`; own `HttpClient` via
   `Composition/CrawlTargetHttpClientFactory.Create` (AllowAutoRedirect=false; redirect chains
   recorded + persisted via `UrlElementRedirectChainStore`); checkpoint restore else seeds +
   pre-seeded row reuse (`GetOrCreateUrlRowAsync`, G-MATRIX-SEED-RACE); sitemap ingestion
   (`ISitemapIngestor`, skipped in list mode); list mode via `ListSeedResolver`/`ListModeIngestor`/
   `ListDiscoveryGate` + `IPreCrawlValidator` probe. Loop: ceilings → `ObserveStatus` (real WAF
   verdict) → dequeue (D-021 hard-class skip: `state.StoppedHosts` hosts drain without I/O,
   `AuditExecutionEngine.cs:1144`; set at :1414) → depth → robots (per AUTHORITY, port preserved;
   unreachable → permissive
   allow, RFC 9309 §2.3.1.3) → row reuse/create → `aimd.WaitAsync` → `FetchContextScope` → fetch →
   failure classification → indexability (CR-024 search-indexable) → row UPDATE → url-batch/waf-blocks
   accumulation → redirect findings + chain outcome → body into `IContentStore` →
   `DiscoverLinksAsync` (zoned inventory into `link_edges`; enqueue gated by scope/discovery) →
   trap observation (`CrawlerTrapDetector`) → checkpoint → `PublishProgressAsync` +
   `FlushRunEventsAsync` (≤1/s producer cadence, lossless).
   **AIMD (D-021, WP-10-04):** `HttpFetcher` is the single recording site — challenge-aware
   `RecordAimdSample` after every completed fetch (`Crawler/Fetching/HttpFetcher.cs:443`, sample
   :455–473). The engine resolves the politeness response only (`WafAimdTrigger.ResolveAction`,
   `AuditExecutionEngine.cs:1413–1421`): soft blocks (managed challenge / rate-limit soft block)
   feed the AIMD multiplicative decrease; hard classes (Captcha/Interstitial/BotBlock —
   `Crawler/Waf/WafBlockPolicy.cs:58–67`) stop the host for the run (host stop takes precedence
   over throttling).
4. **Rendering + capture (G03 — wired in the working tree, pending the phase-10 gate):** one
   `CaptureModeCoordinator` per audit is created in `RunAuditAsync` from the frozen configuration
   snapshot (`AuditExecutionEngine.cs:625`; factory `Crawler/Capture/CaptureModeCoordinator.cs:546`)
   and disposed in that method's `finally` — a disposal failure is logged and never fails the run
   (:662–676). Pool lifetime: rendering-dependent modes (js/dual/adaptive) compose a **per-audit**
   `HeadlessRenderingPool` from `RenderingPoolOptions.FromCaptureSettings(snapshot.CaptureSettings)`
   (`CaptureModeCoordinatorFactory.Create`, `CaptureModeCoordinator.cs:567–570`; options
   `Crawler/Rendering/RenderingPoolOptions.cs:119`) and own its disposal (`DisposeAsync` :257–268);
   non-JS audits pass the DI singleton through unowned (`EngineServicesModule.cs:100`) — the same
   singleton serves the tools/single-page path and override-forced renders. Capture call:
   `capture.CaptureAsync` in the `CrawlPhaseAsync` success path (`AuditExecutionEngine.cs:1455–1483`)
   renders through the CDP pool (CR-006: rendered traffic is CDP-direct, never the proxy), persists
   RenderedDom/Waterfall/Screenshot artifacts, and stamps `CaptureSource` (Raw/Rendered/Both) on the
   URL row (`CaptureModeCoordinator.StampCaptureSourceAsync` :347–361). Rendered-DOM links enqueue
   with `DiscoverySource.Render` (:1473–1479; `seen` dedupes so only render-only targets carry it);
   capture findings drain per-URL and at crawl tail via `capture.DrainAuditFindings` (adaptive
   template switches + dual-storage warning; try/catch-continue, :1542–1557). Analysis reads the
   stored RenderedDom artifact back in `BuildStoredContextAsync` (item 5). §5.6.3 pre-flight: a
   rendering-dependent config (mode or a rendered per-pattern override) must resolve a LOCAL
   browser before any crawl work — bundled CfT → user path → Chrome → Edge, never a runtime
   download (D-003) — via `PreCrawlValidator.ResolveRenderingBrowser`/`CheckRenderingBrowser`
   (`Crawler/Fetching/PreCrawlValidator.cs:237/:217`, `PoolBrowserResolver` :30), one message
   shared with the validator's capture-aware overload (:112); the engine throws it verbatim and
   fails the audit (`AuditExecutionEngine.cs:804–829`); non-JS never touches resolution.
5. `AnalyzePhaseAsync` — streams `url_records` in pages; per successful URL:
   `BuildStoredContextAsync` (reads Body **and** the stored RenderedDom artifact into
   `PageAuditContext.RenderedDocument`, `AuditExecutionEngine.cs:2871–2889`; artifact page size 20
   so Body/RenderedDom/Waterfall/Screenshot are all visible, :2837) → `AnalyzeOnePageAsync`: `IAuditPipeline.RunAsync` →
   `FindingEmitter.EmitAsync` (catalog-enriched: priority/remediationDifficulty stamped) →
   `url_elements` + `metric_summaries` writes → `IMarTechAuditor` → `ITechnologyDetector` →
   custom content persistence. Then n-gram site pass.
6. **Pipeline:** `BE/CrawlForge.Audit/Pipeline/AuditPipeline.cs` → `RunAsync`, 8 steps:
   stored-HTML reuse → tolerant parse → scope resolution + element inventory → per-URL rule passes
   (`IAuditRuleAnalyzer`) → finding emission → metric aggregation → report readiness.
7. **Analyzer/rule registration:** `EngineServicesModule.RegisterAudit` composes `AuditRuleHost`
   over `AnalyzerRuleAdapter`(Accessibility/ContentQuality/Hreflang/MobileFriendliness) +
   `NGramRuleAnalyzer` + `StructuredDataRuleAnalyzer` (G11) + `ImageRuleAnalyzer` +
   `UrlStructureAnalyzer` + `HreflangReciprocityRuleAnalyzer`. `AuditRuleCatalog` (49 seeded
   `RuleDefinition`s after the phase-10 family) is registered and injected into `FindingEmitter`
   (gap #21 resolved). Phase-10 catalog (WP-10-04 working tree): the `render.*` delta family +
   `content.javascript-dependent-template` + builder-local `render.dual-storage-warning` seed in the
   catalog constructor (`Audit/Pipeline/RuleDefinition.cs:91–106`); hreflang keys live under
   **Internationalization** (:85 `HreflangHeaderHtmlConflict` move + :111–116); the Host
   `Register` block adds `redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,
   https-downgrade}` (legacy aggregate seed dropped, constant retained), `structured-data.deprecated-term`,
   `image.oversized`, `image.format`, and `accessibility.image-alt` under **Images**
   (`EngineServicesModule.cs:307–333`). D-021 alt takeover: the production `AccessibilityAnalyzer`
   is constructed with `accessibility.image-alt` disabled (:198–203) so `ImageRuleAnalyzer` is the
   single producer under Images; the image analyzer reads stored NetworkWaterfall artifacts for
   CR-031 byte measurement (:214–218).
   `AuditCrawlComponents` aggregate registers the wave-9A/9B crawl components (chain store, run-event
   publisher, rewrite engine, pre-crawl validator, finding recorder, orphan/duplicate/structure/
   hreflang analyzers, suppression reads).
8. `ScoreAndFinalizeAsync` — WAF block-rate diagnostic → intelligence aggregates (GEO readiness +
   governance matrix + coverage) → scoring + recorder → `FinalizeAsync`: force-flush run events,
   persist terminal status + `counters_json` (+`errorMessage` on failure), publish `stage-changed`
   then `audit-failed` or `audit-completed` (string ids, catalog constants).
9. Hub publication points: `PublishStageAsync` (stage-changed), `PublishProgressAsync`
   (crawl-progress throttled 1/s; throughput/elapsed/remainder/phase + consumer counter keys via
   `HubPayloadNormalizer`), `FlushRunEventsAsync` (url-batch, waf-blocks, findings-updated deltas),
   `HubHostThrottleSink` (host-throttle, HOTFIX-01 locked names), `CrawlForgeBackend.PublishLifecycleAsync`
   (backend-lifecycle ready/shutdown).
10. **Counters:** engine writes `discovered/fetched/analyzed/failed/blocked/wafBlocked`
    (`AuditRunState.ToPayload`); `AuditWireMapper.ParseCounters` maps them to the consumer keys
    `queued/fetched/error/blocked/wafBlocked` at the REST + hub boundaries (WP-09-06; storage keeps
    engine keys — no silent rename).

Findings land in `findings` via `FindingEmitter` (`Audit/Pipeline/FindingEmitter.cs`, suppression-aware,
project-scoped, catalog-enriched); aggregates in `metric_summaries`/`audit_scores` (also recomputable
via `Data.Repositories/AuditAggregationService.cs` → `RecomputeAsync`).

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
- **WP-10-04 REST additions:** `GET api/settings/browser` — UI-21 rendering-browser identity
  (`SettingsController.cs:382`, `BrowserInfoWire` :158; filesystem probes only, D-003) bound to FE
  route `settings.browser` (`FE/contracts/api/routes.ts:139`, `BrowserInfo`
  `FE/contracts/api/system.ts:140`); `RenderTestResponse.NetworkWaterfall` — additive 7th positional
  (`Api/Controllers/ToolsWireContracts.cs:90–97`) filled by `ToolsController.cs:237` from the CDP
  render result (the TS mirror already carried `networkWaterfall`).

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
  (89 contract rows / 109 method+path operations after `settings.browser`, `{method, path, group, additive, enveloped}`);
  `FE/contracts/api/contracts-map.ts` — `ApiRouteContracts` binds every route key to
  params/request/response DTO types (compile-time assertions fail `tsc` on drift). Per-domain DTO
  types: `FE/contracts/api/*.ts` (audits, projects, …). Client: `FE/store/apiClient.ts` →
  `createLoopbackApiClient` resolves `{param}` templates, sends the session header, parses envelopes
  (interface: `FE/contracts/api/client.ts`).

## 6. Real-time hub

- **Definitions:** `Api/Hubs/HubEventCatalog.cs` — 20 server→client events + `subscribe`/`unsubscribe`;
  contract `Contracts/Api/HubContracts.cs` (`IRealtimeHub.PublishAsync(HubEvent)`,
  `HubEvent(Name, PayloadJson, ThrottleKey, ThrottleInterval)`).
- **Core:** `Api/Hubs/RealtimeHubService.cs` (throttle/coalesce, per-audit groups `audit-{id}`,
  2 s heartbeat via `SystemHealthSampler`, boundary normalization via `HubPayloadNormalizer`
  — consumer counter keys + recursive id stringification on every event, WP-09-06);
  transport `Api/Hubs/SignalRHubChannel.cs`; hub class `Api/Hubs/CrawlForgeHub.cs` at `/hubs/realtime`.
- **Frontend consumers:** `FE/contracts/hub/events.ts` (`HUB_SERVER_EVENT_NAMES`, :370 — **20 names**,
  payload types, guards in `hub/guards.ts`) → `FE/store/hubClient.ts` (SignalR setup) →
  `FE/store/hubDemux.ts` (`createHubDemux`, typed handlers per event) → `FE/store/crawlForgeStore.ts`
  + `FE/store/liveProgress.ts`.

| Event | Production producer |
|---|---|
| `crawl-progress` | `Host/Execution/AuditExecutionEngine.cs` → `PublishProgressAsync` (WP-09-11: string auditId, consumer counter keys via `HubPayloadNormalizer`, throughput/elapsed/remainder/phase + additive throttledHostCount/meanEffectiveRate; ≤1/s throttle) |
| `url-batch` | `AuditExecutionEngine.cs` → `FlushRunEventsAsync` → `AuditRunEventPublisher.PublishUrlBatchAsync` (WP-09-11; ≤1/s producer cadence, lossless) |
| `stage-changed` | `AuditExecutionEngine.cs` → `PublishStageAsync` (WP-09-11: catalog constant, string ids, wire states) |
| `findings-updated` | `AuditExecutionEngine.cs` → `FlushRunEventsAsync` → `AuditRunEventPublisher.PublishFindingsUpdatedAsync` (WP-09-11; deltas derived from persisted finding counts vs last snapshot) |
| `audit-completed` | `AuditExecutionEngine.cs` → `FinalizeAsync` (WP-09-11: string auditId, finalCounters incl blocked/wafBlocked, reportReady) |
| `audit-failed` | `AuditExecutionEngine.cs` → `FinalizeAsync` — added by HOTFIX-01; `Api/Controllers/AuditsController.cs` also publishes `start-failed` on post-create start failure (WP-09-06) |
| `backend-lifecycle` | `Host/CrawlForgeBackend.cs` → `PublishLifecycleAsync` (WP-09-11: `{kind:"ready"}` after startup, `{kind:"shutdown"}` before the drain) |
| `schedule-fired` | `Crawler/Scheduling/ScheduleHubEvents.cs` → `ScheduleEventPublisher.PublishFiredAsync` (from `ScheduledAuditService`) |
| `schedule-run-completed` | `ScheduleHubEvents.cs` → `PublishRunCompletedAsync` |
| `export-delivery` | `Export/ExportModels.cs` → `ExportEventPublisher.PublishDeliveryAsync` (from `Export/Delivery/ExportDestinationDelivery.cs`) |
| `waf-blocks` | `AuditExecutionEngine.cs` → `FlushRunEventsAsync` (WP-09-11; 1/s hub throttle; name also used as metric key / MCP resource) |
| `host-throttle` | `Host/Execution/HostThrottleSinks.cs` → `HubHostThrottleSink.Emit` (wire fields locked by HOTFIX-01) |
| `system-health` | `Api/Hubs/RealtimeHubService.cs` → heartbeat (`EmitHeartbeat`) |
| `matrix-row-validated` | `Crawler/ListMode/RedirectMatrixValidator.cs` → `EmitBatchesAsync` |
| `log-import-progress` | `Intelligence/LogAnalyzer/LogImportService.cs` → `PublishProgressAsync` |
| `log-import-completed` | `LogImportService.cs` → `PublishCompletedAsync` |
| `embedding-progress` | **no producer** (phase-12 semantics wiring) |
| `embedding-completed` | **no producer** (phase-12 semantics wiring) |
| `local-notification` | `ScheduleHubEvents.cs` → `PublishLocalNotificationAsync` via `HubLocalNotificationSink` — registered in the frontend contract `{title,message}` (CR-027, WP-09-11 fe1ac88) |
| `export-progress` (extra) | `Export/ExportModels.cs` → `ExportEventPublisher.PublishProgressAsync` (from `ExportEngine.cs`) — cataloged + frontend-consumed (WP-09-10/11) |

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
- **Navigation:** rail registry = `const ENTRIES` in `FE/shell/NavigationRail.tsx` — **5 rail views**:
  `hub`, `audits`, `report`, `tools`, `settings`; the `ShellView` union (`FE/shell/windowState.ts`,
  persisted at localStorage `crawlforge.shell.windowState.v1`) also carries `project` +
  `configurations` (WP-09-11, reached via hub row click / Configurations button, with
  `selectedProjectId`). `ViewContent` (`CrawlForgeShell.tsx`) switches: hub→`HubDashboard`,
  audits→`AuditsView`, report→`ReportView`, tools→`ToolsView`, settings→`SettingsSurface`,
  project→`ProjectView`, configurations→`ConfigurationsView` (`FE/shell/ProjectViews.tsx`).
  Command palette: `FE/shell/CommandPalette.tsx` (Ctrl+K; nav + first 20 projects/audits).
  **Window routing (G20):** `FE/App.tsx` `parseWindowRoute` honors Electron's `?route=`
  (splash/hub/recovery→hub, report→report + `auditId` deep-link, visualization/analyzer/tools→tools,
  settings→settings); the route wins over the persisted view.
- **Shell services / store:** `FE/shell/ShellContext.tsx` (`createShellServices`, `ShellProvider`,
  `useShell`; `ShellNavigation.openAudit` sets activeAudit + view='report' + hub subscribe).
  Stores: `FE/store/crawlForgeStore.ts` (normalized entities, windowed pages, host-throttle,
  system-health, plugins, activity ring), `FE/store/liveProgress.ts` (latest-wins live counters),
  `FE/store/windowedCache.ts` (**`MAX_WINDOWED_PAGES = 24`** vs spec ≤20), `FE/store/hubDemux.ts`,
  `FE/store/hubClient.ts`, `FE/store/apiClient.ts`, `FE/store/pluginBus.ts`,
  `FE/store/selectors.ts`, `FE/store/frontendHeap.ts`.
- **Report-surface mounting (WP-09-11, G01 resolved):** `FE/shell/AuditViews.tsx` → `ReportView`
  mounts the report workspace: nav buttons Dashboard / Issue explorer / URL explorer / Export
  (`report-nav-*` testids) switching `ReportDashboard` (live counters + chart cross-filter via
  `applyChartCrossFilter`), `IssueExplorer` (suppression changes bump the dashboard refreshKey),
  `UrlExplorer`, and the `ExportDialog` modal. The UPPERCASE state badge (`report-state`) matches
  the walker's state regex. **A new surface becomes reachable by:** mounting its component inside
  `ReportView` (or a new `ShellView` in `ENTRIES` + `ViewContent`), wiring data through
  `useShell().api` / store selectors. Integrator recipe: `FE/surfaces/dashboards/README.md`.
- **Rendered-surface mounting (WP-10-03/04, working tree):** URL detail "Rendered vs raw" section
  mounts `RenderedTab` (`FE/surfaces/explorers/rendered/` — WP-10-03 components;
  `UrlDetailPanel.tsx:438–444`, testid `url-detail-rendered`; its `useRenderedTabMount` :72–83
  Mantine/shell probe is test-accommodation scaffolding, gap #32); report nav "Rendered comparison"
  pane mounts `RenderedComparisonSurface` (`AuditViews.tsx:458–459`; button `report-nav-rendered`
  :422, `ReportPane` union :50) whose row drill-down hands the URL to the explorer via
  `openUrlId`/`openUrlKey` (`UrlExplorer.tsx:50–52`, token-guarded effect :151–158 — same
  `openDetail` path as row activation); the run view shows `CaptureModeIndicator` from the locked
  `configurations.get` read (`AuditViews.tsx:200–231`) — the CURRENT configuration mode, not a
  frozen run snapshot, and nothing renders until the read lands (:273);
  `RenderTesterTool` mounts `RenderedOutputPanel` over the live `tools.renderTest` response
  (`RenderTesterTool.tsx:197`).
- **Mounted surfaces (WP-09-11 additions):** `FE/shell/ProjectViews.tsx` (`ProjectView` overview +
  `ConfigurationsView` with `ConfigurationsList`/`ConfigurationEditor` 13 tabs),
  `FE/surfaces/dashboards/ReportDashboard.tsx`, `FE/surfaces/explorers/` Issue/URL explorers via
  `ExplorerHost`, `FE/surfaces/exports/ExportDialog.tsx`, `ExportDestinationsSection` in Settings.
  Still unmounted: `VisualizationSurface` + graphs (UI-14), specialist explorers beyond Issue/URL
  (UI-16), `PluginManagementSurface` (UI-22), log import UI (UI-17).
- **Mounted surfaces:** `FE/shell/HubDashboard.tsx` (projects + New project modal with the full
  ConfigurationEditor; row click → project overview; per-row Configurations button),
  `FE/shell/AuditViews.tsx` (run view + report workspace), `FE/surfaces/single-page/SinglePageAnalyzer.tsx`
  and 11 more tools via `FE/tools/ToolsWorkspace.tsx` `CATALOG` (12 entries) → `FE/shell/ToolsView.tsx`,
  `FE/shell/SettingsSurface.tsx` (+ export destinations), `FE/shell/StatusBar.tsx` (footer).

**UI surface map** (spec: `architecture/06-frontend-shell-and-ui/04-ui-surface-map.md`;
M = mounted, U = component exists but unmounted, N = no component; runtime verdicts: probe-2 §3):

| ID | Surface | Status | Component / evidence |
|---|---|---|---|
| UI-01 | Main hub › Projects | M | `shell/HubDashboard.tsx` |
| UI-02 | Project overview | M (WP-09-11) | `shell/ProjectViews.tsx` `ProjectView` (hub row click; `project-overview` testid) |
| UI-03 | Project › Configurations | M (WP-09-11) | `ConfigurationsView` + `ConfigurationsList` (`configurations-view` testid) |
| UI-04 | Configuration editor (13 tabs) | M (WP-09-11) | `ConfigurationEditor` in ConfigurationsView + create-project modal (13/13 tabs) |
| UI-05 | Project › Integrations | N | — |
| UI-06 | Project › Segments | N | — |
| UI-07 | Project › Audit history & retention | N | global audits list exists; no per-project surface |
| UI-08 | Main hub › Clients | N | — |
| UI-09 | Main hub › Schedules | N | routes + demux handlers exist; no UI |
| UI-10 | Audit run view | M | `shell/AuditViews.tsx` (`AuditsView`/`ReportView` telemetry + UPPERCASE state badge + `CaptureModeIndicator` from `configurations.get`, WP-10-04) |
| UI-11 | Report › Dashboard | M (WP-09-11) | `ReportDashboard` mounted in `ReportView` (`dashboard-charts`; placeholder removed) |
| UI-12 | Report › Issue explorer | M (WP-09-11) | `IssueExplorer` via report-nav (`issues-grid`) |
| UI-13 | Report › URL explorer & detail | M (WP-09-11) | `UrlExplorer` via report-nav (`url-explorer`); URL-detail drill-down via onOpenUrl; "Rendered vs raw" = `RenderedTab` (`url-detail-rendered`, WP-10-04) |
| UI-14 | Visualization window | U | `dashboards/VisualizationSurface.tsx` + graphs; `?route=visualization` maps to tools view (dedicated surface unmounted) |
| UI-15 | Report › Comparison | N | `AuditComparisonReader` backend only |
| UI-16 | Report › Specialist surfaces | M/U/N | rendered comparison M via report-nav (`RenderedComparisonSurface`, `report-nav-rendered`, WP-10-04); extraction/ngram/log/semantic/geo/analytics/redirect-matrix/WAF explorers U; accessibility/performance/technology/templates/custom-search/snippet N |
| UI-17 | Log file analyzer | U | `logExplorer` + `LogIntelligencePanel`; no import UI |
| UI-18 | Single-page analyzer | M | `surfaces/single-page/SinglePageAnalyzer.tsx` via Tools |
| UI-19 | Standalone tools | M | `tools/ToolsWorkspace.tsx` CATALOG (12 tools); render tester shows real rendered output via `RenderedOutputPanel` (WP-10-04) |
| UI-20 | Export dialog | M (WP-09-11) | `ExportDialog` modal via report-nav Export; `ExportDestinationsSection` in Settings |
| UI-21 | Settings | M (partial) | `shell/SettingsSurface.tsx` (+ export destinations + "Rendering browser" section `settings-browser-section`, WP-10-04); missing dictionary/integrations/templates/action-log/release-check/Plugins sections |
| UI-22 | Plugin manager | U | `plugins/PluginManagementSurface.tsx`; Settings never imports it |
| UI-23 | Status bar | M | `shell/StatusBar.tsx` |
| UI-24 | Recovery screen | N (renderer) | Electron opens `recovery` window; `?route=recovery` maps to hub (no dedicated recovery component) |

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
**Fixture acceptance harness (WP-08-04, delivered):** `app/tests/fixture-acceptance/` —
`run_fixture_acceptance.py` (`--mode http|js|dual|adaptive`, `--traps`, `--surfaces`, `--mcp`,
`--scheduled`, `--rest-start`, `--reuse-app`; see its `README.md` + `DESIGN.md`). Starts the
fixture site, launches the app through the normal entry points, compares against
`expected-results.json`, checks `/__fixture/requests`, walks UI surfaces over CDP.
Runners: `dotnet test app/CrawlForge.sln`; `npm test` (vitest workspaces);
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
   (`RunNgramSitePassAsync`, :860). Render-dependent: `PageAuditContext.RenderedDocument` is now
   populated from stored RenderedDom artifacts (G03 wired in the working tree, §4.4 — full rendered
   audits still pending the phase-10 gate).
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

### WP-09-11 resolution pass (2026-11-19, serial wiring)

The table above is the WP-08-01 snapshot. The phase-09 serial wiring resolves:

| Row | Status after WP-09-11 |
|---|---|
| #1 G01 | RESOLVED — `ReportView` mounts ReportDashboard/IssueExplorer/UrlExplorer + ExportDialog with report-nav navigation (`FE/shell/AuditViews.tsx`); placeholder removed |
| #3 G03 | PARTIAL — rendering profile parsed/persisted, `IRenderingPool` registered (consumed by SinglePageAnalyzer + `IResponseRenderedComparer` in the pipeline family); headless crawl rendering is phase-10 (Flow 2) scope |
| #5 G05 | 4/6 RESOLVED — url-batch, findings-updated, backend-lifecycle, waf-blocks producers wired (§6); embedding pair remains (phase 12) |
| #7 G07 | RESOLVED — WAF verdicts feed `AimdPolitenessController.ObserveStatus` (IsChallenge real); crawl-delay via `PortAwareCrawlDelaySource` over the robots cache |
| #8 G08 | RESOLVED — sitemap ingestion (`ISitemapIngestor`) in the crawl phase + sitemap correlation post-pass |
| #10 G10 | RESOLVED — persist-before-start via `IListAuditStartPort` (Api delegates when registered); list-mode ingestor wired |
| #11 G11 | RESOLVED — `StructuredDataRuleAnalyzer` in the per-audit rule family (`EngineServicesModule`); hreflang reciprocity site pass wired |
| #16 G18 | RESOLVED — ConfigurationsView + ConfigurationEditor mounted (UI-03/UI-04); create-project modal carries the full editor |
| #17 G20 | PARTIAL — renderer honors `?route=` (window-catalog → shell view map, report deep-link `auditId`); `window.crawlForgeShell` bridge consumption remains |
| #18 G23 | RESOLVED — `DuplicateContentAnalyzer` site pass invokes `ShingleNearDuplicateDetector` |
| #20 G27 | RESOLVED — `IPreCrawlValidator` invoked (list-mode probe); redirect chains persisted via `UrlElementRedirectChainStore` + `FetchContextScope` |
| #21 | RESOLVED — `AuditRuleCatalog` registered (14 crawl-family definitions) and injected into `FindingEmitter` |
| #22 | RESOLVED — export-progress cataloged + frontend-consumed |
| #23 | RESOLVED — local-notification in the frontend 20-event contract (CR-027) |
| #24 | RESOLVED — `AuditWireMapper.ParseCounters` maps engine keys (WP-09-06); hub boundary normalization |
| #25 UI-24 | PARTIAL — `?route=recovery` maps to the hub view; dedicated recovery component remains |

Also resolved outside the table: G-REST-IDEMPOTENCY (§10.4 duplicate-start), G-REAUDIT-SNAPSHOT
(DTO-shaped storage snapshot), G-MATRIX-SEED-RACE (pre-seeded row reuse), G-PROGRESS
(throughput/phase fields) — see `tests/CrawlForge.ContractConformance.Tests/GAPS.md`.

### WP-10-04 resolution pass (2026-11-22, phase-10 wiring — uncommitted working tree)

| Row | Status after WP-10-04 |
|---|---|
| #3 G03 | **WIRED in the working tree, pending the phase-10 gate** — per-audit `CaptureModeCoordinator` + per-audit `HeadlessRenderingPool` (CaptureSettings-derived) in the audit execution path, RenderedDom read back into `PageAuditContext.RenderedDocument`, `DiscoverySource.Render` link discovery, CaptureSource stamping, crawl-tail finding drain (§4.4); rendered UI surfaces mounted (§9). Not yet gate-accepted. |

New known gaps observed at wiring (#28–#33):

| # | Component / area | Missing wiring or defect |
|---|---|---|
| 28 | Per-URL render-artifact read route | The frozen REST catalog exposes no content-artifact read route, so `RenderedTab`'s DOM/screenshot/console/waterfall viewers (`FE/surfaces/explorers/rendered/`) render empty in full audits — the render-tester response carries artifacts, the URL-detail path does not |
| 29 | Browser product/version persistence | Spec 5.6.3 identity is resolved at the rendering pre-flight and shown in UI-21 (`GET api/settings/browser`) but never stored on audit or URL records |
| 30 | `IPreCrawlValidator` (locked interface, config-less) | `Contracts/Crawl/FetchingContracts.cs` passes no capture mode, so the list-mode probe (`Crawler/ListMode/ListPreCrawlProbe.cs`) never runs the §5.6.3 browser check; the engine pre-flight covers full audits (one shared message, `PreCrawlValidator.cs`) |
| 31 | Proxy capture in http mode | Not wired (CR-031 ruling: http mode reports non-inline images as not measured, so the NetworkWaterfall proxy artifact path is not required today) |
| 32 | `UrlDetailPanel` Mantine/shell probe | `useRenderedTabMount` (`UrlDetailPanel.tsx`) catches the `useShell` guard and probes `MantineContext` to keep provider-less test mounts working — test-accommodation scaffolding, not product behavior |
| 33 | Sitemap correlation (pre-existing defect) | `LoadCanonicalTargetsAsync` (`AuditExecutionEngine.cs`) queries `link_edges.target_canonical_url`; the column is `canonical_target_url` (`SchemaCatalog.Baseline.cs`), so the correlation query fails every audit and the failure is swallowed by the post-crawl try/catch-continue |