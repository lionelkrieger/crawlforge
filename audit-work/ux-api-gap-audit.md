# CrawlForge — UX Surface vs API Exposure Gap Audit (Final Report)

## 1. Audit metadata

- **Date**: 2026-11-17 (Africa/Johannesburg) · **Repo root**: `E:\Crawlforge`
- **Scope**: shipped product surface — backend REST/hub/MCP/Electron-IPC exposure vs reachable UX shell vs documented promise layer. Source: `app\src\` excl. `bin`, `obj`, `node_modules`, `dist`.
- **Inputs (only)**: `audit-work/01-api-exposure-inventory.md` (**01**: API column — 114 REST actions, hub catalog, MCP, 11 IPC channels) · `02-ux-surface-inventory.md` (**02**: UX column — 141 rows; 108 route keys, 62 reached) · `03-documented-feature-inventory.md` (**03**: docs column — 170 capabilities, ≈89 REST ops, 19+1 hub events, 28 MCP names) · `04-gap-analysis.md` (**R1**: G1–G7, 49 findings) · `04a-ux-api-join.md` (**R2**: A1–A3, 46 findings, exact join math) · `04b-docs-channel-join.md` (**R3**: B1–B4, 38 findings, 170-row resolution, verified parity). Nothing else read or used.
- **Method (one line)**: three-column inventory → triple independent join → source-verified merge (dedup-by-substance, max-severity, corroboration counting).
- **Merge rules**: dedup by real-world substance, source IDs preserved; severity = max across registers, justified inline; all disconfirmed/adjusted claims honored (§5); one-vs-four ruling (§6); every finding source-anchored; register claims resting only on out-of-scope material dropped (none needed demotion, §8.3).
- **Anchor convention**: `b/`=`app/src/backend/`, `f/`=`app/src/frontend/src/`, `e/`=`app/src/electron/src/`; `bc/`=`b/CrawlForge.Api/Controllers/`, `bh/`=`b/CrawlForge.Host/`, `fca/`=`f/contracts/api/`, `bcr/`=`b/CrawlForge.Crawler/`, `bm/`=`b/CrawlForge.Mcp/`, `bau/`=`b/CrawlForge.Audit/`, `fs/`=`f/surfaces/`, `fst/`=`f/store/`, `bdr/`=`b/CrawlForge.Data.Repositories/`, `bi/`=`b/CrawlForge.Intelligence/`, `bdm/`=`b/CrawlForge.Data.Migrations/`, `bx/`=`b/CrawlForge.Export/`. All anchors re-checked in source 2026-11-17; corrections in §5.1.

---

## 2. Executive summary

CrawlForge's analytical core is **built but unmounted**, its automation half **API-complete but UX-dark**. The shipped shell starts crawls and shows telemetry, but no user can read a finding, URL, or link: the report workspace — 8 dashboards, 11 explorers + host + grid + query builder, link/semantic graphs (~30 route-wired capabilities) — is code nothing mounts, while the report view shows a card promising exactly those surfaces (`f/shell/AuditViews.tsx:316-320`). Whole documented domains are wire-only: schedules (9 ops), exports (create/poll/download/deliver), log imports (4 ops), semantic jobs (2 ops), dictionary, suppressions, WAF signatures, project/config editing, re-audit/compare; 46 of 108 frozen route keys have no caller in the renderer.

The promise layer outperforms the surface layer: ≈89/89 documented REST ops implemented, MCP 28/28, metric keys 20/20 (each with a writer), rule keys 30/30, completion kinds 3/3. Of 170 capabilities: 84 reachable as promised, 51 API-only, 14 dark, 19 partial, 2 truly absent (Security & protocol rules; suppressed-findings view). The product keeps its API promises and breaks its surface promises.

Realtime consumption is healthy; delivery lies: all 18 contracted events demux client-side, yet **7 of 19 catalogued server events have no publisher at all** (`url-batch`, `findings-updated`, `audit-failed`, `backend-lifecycle`, `waf-blocks`, `embedding-progress`, `embedding-completed`) — the client-wired WAF badges, embedding panel, and lifecycle banner can never fire; and `local-notification`, the one event published for user delivery, has zero contract entries and zero consumers (`bcr/Scheduling/ScheduleHubEvents.cs:111`).

Exposure-governance defects: `GET /hub` ungated despite its own comment claiming token gating (`bh/Hosting/SessionTokenMiddleware.cs:15-17` vs `:49-53`); feature toggles gate nothing (seed key `localFeatureToggles` ≠ reader key `featureToggles`; `IsEnabled` zero callers); MCP loopback server starts unconditionally, writing its plaintext bearer token to `mcp-endpoint.json` each startup while settings enable/rotate target a different store; one per-launch token with zero roles unlocks admin-class endpoints (shutdown, DB test, compact, purge, rescan, DNS verify). Two silent engine breakages compound this: the documented List Mode entry starts the audit **before** persisting the URL list (`bc/AuditsController.cs:140` vs `:154-160`), and headless rendering never reaches full audits (`IRenderingPool` only in `SinglePageAnalyzer`, `bh/Composition/EngineServicesModule.cs:204-210`) — render-dependent rules, WAF render-resolution, and the CWV suite (zero DI registrations) are dead weight.

### Headline numbers

| Measure | Value |
|---|---|
| REST actions served | 114 (111 controller + 3 minimal-API, `bh/CrawlForgeBackend.cs:125-131`) |
| Frozen contract keys (`contracts/api/routes.ts`, verified) | 108 |
| Contract orphans (served, no row) | 6 — `GET /api/exports/{id}`, `POST /api/destinations/{id}/deliver`, `GET /api/schedules`, `POST /api/system/shutdown`, `GET /api/diagnostics/version`, `GET /hub`; 5 documented nowhere |
| Route keys reached by UX | 62/108 (46 dark) |
| Documented capabilities (170) | 84 reachable · 51 API-only · 14 dark · 19 partial · 2 absent |
| Hub server events | 19 catalogued · 12 published · 7 never published · 18 contracted · 1 published-but-unconsumed |
| MCP | 8 tools / 15 resources / 5 prompts = 28/28 names; narrower than REST by design (§11.1) |
| Electron | 11 IPC channels, 1 consumed; 8 window kinds, 3 openable |
| Registers merged | 133 rows → **49 findings** (3 critical, 16 high, 24 medium, 6 low) |

---

## 3. Coverage & promise-resolution matrix

### 3.1 22-domain coverage (Docs/API/UX)

✓ = present & reachable · ~ = partial · ✗ = absent/unreachable.

| Domain | Docs | API | UX | Notes (gap refs) |
|---|---|---|---|---|
| projects | ✓ | ~ | ~ | GAP-39/40/09 |
| crawls | ✓ | ~ | ~ | GAP-19/41/22/33 |
| audits | ✓ | ✓ | ~ | GAP-10/01 |
| explorer | ✓ | ✓ | ✗ | GAP-01 |
| content | ✓ | ✓ | ✗ | GAP-01/16 |
| analytics | ✓ | ✓ | ✗ | GAP-01 |
| exports/destinations | ✓ | ~ | ~ | GAP-29/07 |
| schedules | ✓ | ✓ | ✗ | GAP-06/05 |
| tools | ✓ | ✓ | ✓ | one-vs-four (§6) |
| dictionary | ✓ | ✓ | ✗ | GAP-12 |
| queries | ✓ | ✓ | ~ | GAP-01 |
| plugins | ✓ | ✓ | ✗ | GAP-03 |
| settings | ✓ | ~ | ~ | GAP-38/46/47 |
| system/health | ✓ | ✓ | ~ | GAP-18/29/45 |
| diagnostics | ✓ | ~ | ~ | GAP-49/27/04 |
| logs | ✓ | ✓ | ✗ | GAP-08/05 |
| semantics | ✓ | ✓ | ✗ | GAP-11/20 |
| geo | ✓ | ✓ | ✗ | GAP-01 |
| waf | ✓ | ✓ | ~ | GAP-20/14 |
| suppressions | ✓ | ~ | ✗ | GAP-13 |
| redirect-matrix | ✓ | ✓ | ✗ | GAP-15/01 |
| ngrams | ✓ | ✓ | ✗ | GAP-01/17 |

66 cells: **✓ 26 · ~ 22 · ✗ 18**.

### 3.2 Promise-resolution histogram (170 capabilities)

| Resolution | Count | Meaning |
|---|---|---|
| R — implemented & reachable | 84 | as promised |
| R(api) — API-reachable, UI dark | 51 | integrators only |
| D — implemented but dark | 14 | 12 report surfaces + archive transfer + plugin contributions |
| P — partial/divergent | 19 | limits, suppression read side, action log, thresholds, signed access… |
| A — absent | 2 | `audits.rules-security`; `reports.suppressed-view` |

### 3.3 Channel consumption

| Channel | Exposed | Consumed by UX | Dark / notes |
|---|---|---|---|
| REST contract keys | 108 | 62 | 46 dark |
| REST actions (all) | 114 | 62 + 3 hardcoded Electron calls | 6 orphans; 5 undocumented (GAP-29) |
| Hub client→server | 2 | 1 (`Subscribe`) | `Unsubscribe` never called (GAP-25) |
| Hub server→client | 19 | 18 demuxed (3 buffer-only) | 7 never published (GAP-20); `local-notification` 0/0 (GAP-21) |
| MCP tools/prompts/resources | 8/5/15 | 0 (by design) | 28/28 names (GAP-06/10 UI-less verbs) |
| Electron IPC | 11 | 1 (`crawlforge:get-bridge`) | 10 dark (GAP-04) |
| Electron window kinds | 8 | 3 | 5 unopened; `?route=` ignored (GAP-04) |

---

## 4. Consolidated gap register

49 findings after dedup. **Source IDs**: R1 = G-ids, R2 = A-ids, R3 = B-ids. **Corroboration** = registers (1–3) reporting the same gap. **Severity** = max across registers (raised ratings justified inline). Anchors per §1 convention, re-verified in this merge.

### 4.a Unreachable output & analysis surfaces (dark UX)

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-01 | **Unmounted report output layer**: 8 dashboards, 11 explorers + host + grid + query builder, viz/graphs complete and route-wired; nothing mounts them (report view shows a promise card) | G2-01, G1-09, A2-02, A2-03, A2-04; R3 12 report rows `D` | 3/3 | `f/shell/AuditViews.tsx:316-320` (copy); `fs/explorers/index.ts:36-48`; `fs/dashboards/VisualizationSurface.tsx:48` | Reading results unreachable; ~20 read routes + 30+ capabilities dark | **critical** (R1+R2) | Mount ExplorerHost + dashboards + VisualizationSurface in ReportView; bind cross-filter (GAP-02) |
| GAP-02 | **Cross-filter driver unwired**: `applyChartCrossFilter` zero callers (inventory name `applyChartSelection` does not exist) | A2-06 | 1/3 | `fs/charts/crossFilter.ts:84-99`; `fst/crawlForgeStore.ts:247-258` | Latent break when GAP-01 lands | medium | Wire `onCrossFilter` → driver → explorer with GAP-01 |
| GAP-03 | **Plugin system inert**: management surface unrendered; scanner built with `null` registry delegate; grid contributions only in unmounted grids; hydration handshake never sent | G2-02, A2-05; R3 `plugins.management` R(api), `plugins.contributions` D | 3/3 | `e/main.ts:300` (`new PluginScanner(…, null)`); `f/plugins/PluginManagementSurface.tsx:60`; `f/grid/contributions.ts:22-70` | Extension platform non-functional; registry CRUD invisible | high (max: R1>R2) | Real registry delegate; mount management in Settings; call `signalHydrated()` |
| GAP-04 | **Electron native layer dark**: `window.crawlForgeShell` bridge zero consumers; `signalHydrated` never sent; 5/8 window kinds unopenable; `?route=` ignored | G2-05, A2-10…A2-13, B3-06 | 3/3 | `e/preload.ts:39-56`; `e/main.ts:236-245` vs `f/App.tsx:22-33`; `e/windows/window-catalog.ts:39-120` | Pickers, notifications, taskbar, per-audit windows, recovery UX unreachable | high (R1+R3 high) | Consume bridge per flow; honor `?route=`; open or delete dead kinds |
| GAP-05 | **Phantom UI copy ×5** at nonexistent surfaces ("project Log Imports surface", "Run the local embedding job", "Upload a migration mapping", "Configure custom extraction rules…", nav "Projects, dashboards, schedules") | G2-04, A2-08, A2-09 | 2/3 | `fs/explorers/analysisExplorers.tsx:88,146,391`; `fs/explorers/coreExplorers.tsx:349`; `f/shell/NavigationRail.tsx:27` | Users routed to nonexistent screens | medium | Buttons wired to the named routes, or honest copy |

### 4.b Dark automation domains (API-only)

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-06 | **Schedules domain fully dark** (9 ops + guide-only `GET /api/schedules` + MCP `run-schedule-now`); nav advertises it; schedule events buffer-only | G1-01, A1-01, A2-08, A2-15; R3 `audits.schedule-controls` R(api) | 3/3 | `fca/routes.ts:71-79` (zero callers); `bc/SchedulesController.cs:71-306`; `f/shell/NavigationRail.tsx:27` | Automation API-only; completion actions have no UI trigger | high (R1+R2 high) | Schedules screen from existing keys; smallest step: list + run-now + enable |
| GAP-07 | **Export pipeline dark end-to-end**: `exports.create/download` zero callers; `exports.get`/`destinations.deliver` uncontracted; "export" UX = clipboard JSON + PNG; delivery only via schedules (no UI) | G1-02, G6-01, A1-06, A2-07, A2-15, B3-03 | 3/3 | `fca/routes.ts:69-70`; `fs/explorers/ExplorerHost.tsx:305-320`; `bc/ExportsController.cs:45,99,122` | xlsx/csv/html/pdf jobs, native download, delivery unusable | high (all three high) | create→poll→download in export-handoff with native save picker |
| GAP-08 | **Server-log import domain dark** (import/list/delete/verify-dns); copy points at a nonexistent "Log Imports surface" | G1-03, A1-05; R3 `intel.logs-import` R(api) | 3/3 | `fca/routes.ts:129-133`; `bc/LogsController.cs:63,100,117,179`; `fs/explorers/analysisExplorers.tsx:88` | Advertised log pipeline has no entry; correlation always empty | high (max: R2>R1) | Log Imports panel + native picker; hook `log-import-progress/-completed` (do publish — `LogImportService.cs:439,503`) |
| GAP-09 | **Project/config lifecycle one-way (create-only)**: `projects.get/update/delete`, `configurations.get/update/delete` dark; extraction drafts lack write path | G1-10, A1-08; R3 `projects.update-delete` R(api) | 3/3 | `fca/routes.ts:45-52`; `bc/ProjectsController.cs:108,125,170`; `bc/ConfigurationsController.cs:137,154,270` | Projects never modifiable/removable; scope/limits/extraction editing unreachable | high (R2 high) | Project detail + configuration editor; `configurations.update` behind extraction drafts |
| GAP-10 | **Audit maintenance & comparison dark**: `audits.get`, `audits.reaudit`, `audits.compare`, `audits.compareContent` — no UI trigger (MCP-only) | G1-11, A1-09; R3 `audits.reaudit`/`audits.comparison` R(api) | 3/3 | `fca/routes.ts:55,59,67,88`; `bc/AuditsController.cs:225,281,318`; `bc/ContentExplorerController.cs:60` | Offline re-analysis and before/after comparison impossible in UI | high (R2 high) | "Re-audit" + "Compare with…" actions feeding the comparison views |
| GAP-11 | **Semantics jobs unstartable from UI** while copy promises "Run the local embedding job" and panels show embedding progress | G1-07, A1-07; R3 `intel.semantic-embeddings` R(api) | 2/3 | `fca/routes.ts:134-135`; `bc/SemanticsController.cs:52,71`; `fs/explorers/analysisExplorers.tsx:146` | Observe-but-never-start; link-opportunity data unproducible | medium | Run action where copy promises it; similar-URLs in URL drill-down |
| GAP-12 | **Dictionary domain dark** (list/add/remove) | G1-05, A1-02; R3 `settings.user-dictionary` R(api) | 3/3 | `fca/routes.ts:85-87`; `bc/DictionaryController.cs:37,64,97-98` | Spelling false-positives untunable from UI | medium | Dictionary section in Settings (mind DELETE-with-body, GAP-31) |
| GAP-13 | **Suppressions dark + no read path + no view**: create/delete only (no list API), no UI; docs promise a reversible suppressed view | G1-04, G4-04, A1-03, B1-05 | 3/3 | `fca/routes.ts:95-96`; `bc/SuppressionsController.cs:34,71`; `fca/projects.ts:226` | Suppressions unenumerable even via API; "reversible" unimplementable | medium | `GET …/suppressions` + suppressed view + per-finding actions |
| GAP-14 | **WAF signature management dark** (4 CRUD ops); promised Settings manager absent | G1-06, A1-04; R3 `intel.waf-signature-manager` R(api) | 3/3 | `fca/routes.ts:110-113`; `bc/WafController.cs:156-257` | WAF tuning absent; bundled signatures only | medium | Signature editor beside WAF explorer / Settings, or document API-only |
| GAP-15 | **Redirect-matrix upload dark**; copy promises "Upload a migration mapping…"; results explorer always empty | G1-08, A1-13; R3 `crawls.migration-matrix` R(api) | 3/3 | `fca/routes.ts:126`; `bc/RedirectMatrixController.cs:58`; `fs/explorers/analysisExplorers.tsx:391` | Migration workflow (upload → verdicts → seeded list audit) unreachable | medium | Upload flow (native picker, GAP-04) feeding results explorer |
| GAP-16 | **Custom-data explorers never registered** (search, snippets, technologies): routes done, DOM adapter exists, no explorer binds them | A1-10; R3 `audits.custom-search`/`audits.js-snippets` R(api) | 2/3 | `fca/routes.ts:91-93`; `fs/explorers/ExplorerHost.tsx:50-58`; `bc/ContentExplorerController.cs:211,266,323` | Collected-but-never-displayed data | medium | Register the three explorers into `EXPLORERS` with GAP-01 |
| GAP-17 | **Single ops dark**: host rate pin (`hosts.override`) + n-gram cannibalization | G1-12, A1-11, A1-12 | 2/3 | `fca/routes.ts:115,117`; `bc/HostsController.cs:77`; `bc/NgramsController.cs:107` | Throttling never correctable (docs promise a UI control); clusters unreachable | medium (max: R2>R1) | Pin/clear on throttle rows; clusters in ngram panel |
| GAP-18 | **Probe/version/metrics reads dark in renderer** (`health.get`, `version.get`, `metrics.get`, `system.health`); supervisor uses hardcoded strings | G1-13, A1-14; R3 `diagnostics.health-snapshot` R(api) | 3/3 | `fca/routes.ts:35-37,124`; `fst/apiClient.ts:141` (raw plumbing only); `e/supervisor/backend-supervisor.ts:270,301` | Version/metrics/health not viewable in-app; `/metrics` external-scrape only (by design) | low | Version info in Settings "About"; keep `/metrics` pull-only |

### 4.c Broken pipelines & dead wiring

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-19 | **List Mode persistence race**: `POST /api/audits` starts the engine **before** persisting `UrlListJson`/`IsListMode`; engine reads an audit with no URL list | G5-03 | 1/3 | `bc/AuditsController.cs:140` (start) vs `:154-160` (persist+update) — register cites `:139`/`:153-158`, same statements (§5.1) | list-mode workflow fails via its only entry; matrix auto-seed inherits it | **critical** (merge directive; R1 high — raised: silent break of a documented core workflow) | Persist list fields before `StartAuditAsync` (one reorder) |
| GAP-20 | **7 of 19 hub events never published**: `url-batch`, `findings-updated`, `audit-failed`, `backend-lifecycle`, `waf-blocks`, `embedding-progress`, `embedding-completed`; WAF badges/embedding panel/lifecycle banner can never fire | G5-01, G2-03; R3 B2-08 "all 19 emitted" = name parity only (§5.1) | 1/3 | Full `PublishAsync`-site audit (12 published / 7 not): engine emits only 3 (`bh/Execution/AuditExecutionEngine.cs:1252,1614,1641`); schedule/export/log/matrix/throttle sinks cover the other 9 (see GAP-21 anchors); semantic bridge missing `bh/Composition/EngineServicesModule.cs:297-302`; names only in `b/CrawlForge.Api/Hubs/HubEventCatalog.cs:19-69` | Integrators receive nothing on 7 documented channels; `audit-failed` alerts never arrive | **critical** (R1 critical, survived verification) | Implement publishers (engine, lifecycle, WAF recorder, semantic bridge) or delete events from docs+contracts |
| GAP-21 | **`local-notification` dead-end**: published (`ScheduleHubEvents.cs:111`, catalog `:75`) but absent from the 18-event frontend contract and every demux; notification IPC unconsumed | G2-07, G6-04, A1-15, A3-03, B2-08, B3-04 | 3/3 | `bcr/Scheduling/ScheduleHubEvents.cs:111`; `b/CrawlForge.Api/Hubs/HubEventCatalog.cs:75`; `f/contracts/hub/events.ts:316-333`; `e/main.ts:342` | "Notify me when done" silently produces nothing | high (max: R3 delivery high > R1/R2 med) | Add to contract + demux; render via `window.crawlForgeShell.showNotification` (GAP-04) |
| GAP-22 | **Rendering inert during full audits**: `IRenderingPool` reaches only `SinglePageAnalyzer`/tools, never the audit pipeline | G5-02 | 1/3 | `bh/Composition/EngineServicesModule.cs:98` (registration), `:204-210` (SinglePageAnalyzer) vs `:195-203` (pipeline without it) | JS-heavy pages audited as raw HTML; render rules/WAF render-resolution degrade silently | high (R1 high) | Inject pool into audit fetch loop; honor per-audit flag |
| GAP-23 | **CWV/performance suite dead code**: `CoreWebVitalsAnalyzer`, `PerformanceBudgetEvaluator`, `CodeCoverageAnalyzer`, `PageCompositionAnalyzer`, `LabRenderingProbe` — zero DI registrations, zero callers | G5-04; R3 `audits.cwv` R **adjusted** to dead (§5.1) | 1/3 | `bau/Performance/CoreWebVitalsAnalyzer.cs:15`, `PerformanceBudgetEvaluator.cs:15`, `LabRenderingProbe.cs:53` (self-definitions; grep registration/callers = none) | Performance/CWV capability emits nothing | high (R1 high) | Register in the audit pipeline (needs GAP-22) or demote `audits.cwv` to future scope |
| GAP-24 | **Automation action log write-only**: every MCP invocation recorded+pruned; `ListAsync/PurgeAsync` exist; no REST/hub/IPC read or purge | G6-02, A3-09, B3-02 | 3/3 | `bm/McpJsonRpcDispatcher.cs:27,38,152`; `AutomationActionLog.cs:44`; grep over controllers/hub/IPC = 0 | "user-viewable and purgeable" history invisible | medium | `GET`/`DELETE /api/automation/action-log` + Settings panel |
| GAP-25 | **Hub `Unsubscribe` never invoked**: facade + server implement it; no surface calls it; channel groups accumulate | G2-06, A2-14, B3-07 | 3/3 | `fst/hubClient.ts:182` (definition only); `b/CrawlForge.Api/Hubs/CrawlForgeHub.cs:55` | Bandwidth/memory growth in long sessions | medium (max: R2 med > R1/R3 low) | Call on active-audit change (pair with `Subscribe`) |
| GAP-26 | **Extraction/scope authoring loop broken**: rule drafts emit JSON for manual paste; SPA "send to scope" local-only | G2-08 (part), A2-18 | 2/3 | `f/tools/picker/ExtractionDrafts.tsx:2-5,21-28`; `fs/single-page/SinglePageAnalyzer.tsx:139-143` | Manual copy/paste where one write call would do | medium | POST drafts via `configurations.update` (GAP-09); persist scope staging |
| GAP-27 | **`/metrics` mostly hollow**: only hub-connections + runtime families recorded; promised crawl/finding families emit headers without samples | G5-07 | 1/3 | `b/CrawlForge.Api/PrometheusMetricsExposition.cs:38,101-117`; `Record()` callers = `b/CrawlForge.Api/Hubs/RealtimeHubService.cs:153,172` | Monitoring sees empty series for the most valuable metrics | medium | Record at engine/aggregation sites (counters exist) or trim HELP lines |
| GAP-28 | **Host-throttle history never hydrated**: `loadHosts` no call site (dashboards loader dead too); table fed only live | A2-16 (refined: 2 dead paths), B3-08 | 2/3 | `fst/crawlForgeStore.ts:156,260`; `fs/dashboards/dashboardData.ts:103` | Cold start shows empty host table; `includeHistory` unreachable | low | Call `loadHosts` on report mount |

### 4.d Contract drift & boundary leaks

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-29 | **Contract lock leaks: 6 served endpoints with no `routes.ts` row** (114−108); 5 documented nowhere; `POST /api/system/shutdown` called via hardcoded literal | G3-01, G3-02, G5-06, A3-01, B2-02, B4-04 | 3/3 | `fca/routes.ts:33-145` (108, verified) vs `bc/ExportsController.cs:99`, `DestinationsController.cs:240`, `SchedulesController.cs:95-96`, `bh/CrawlForgeBackend.cs:125,126,131`; `e/supervisor/backend-supervisor.ts:168` | Typed clients can't discover poll-export/deliver/schedules-list/shutdown/version/stub; "complete route surface" false | high (R2 high) | Add 6 rows + document 5 ops; shared route constants for Electron |
| GAP-30 | **Electron bypasses the route catalog**: hardcoded health ×2, shutdown, diagnostics-purge literals | G3-03, A3-02 | 2/3 | `e/supervisor/backend-supervisor.ts:168,270,301`; `e/recovery/recovery-service.ts:101` | Route renames silently break supervisor/recovery | medium (max: R2>R1) | Shared route-constant module for both clients |
| GAP-31 | **Hostile wire shapes on frozen routes**: `DELETE /api/dictionary` with JSON body; `/api/queries` id in body/query string with string ids; secrets round-trip via literal `********` with undocumented keep/replace/clear | G3-04 (part), A3-05, A3-06, A3-08 | 2/3 | `bc/DictionaryController.cs:97-98`; `QueriesController.cs:13-14,117-123,167-173`; `SettingsController.cs:154-155,293,436,477`; `fca/system.ts:50,65` | DELETE-with-body fails in common stacks; naive secret round-trips wipe/corrupt credentials | high (max: R2 A3-05 high > R1 low) | Document shapes; additive `POST /api/dictionary/remove`; explicit `keepSecret` later |
| GAP-32 | **Schedule `overrides` wire-absent**: entity persists `LimitOverrides` and firing honors it; no wire field | B2-03 | 1/3 | `b/CrawlForge.Api/OperationsWireContracts.cs:90-117`; `bcr/Scheduling/SchedulingEntities.cs:24`; `ScheduledAuditService.cs:345` | Per-schedule overrides documented+persisted but unconfigurable | medium | Add `limitOverrides` to schedule wire |
| GAP-33 | **Per-audit limit-override mismatch**: guide names 6 fields; REST wire has 4 and start maps 3 (drops `maxResponseBytes`); MCP deserializes 6 | B2-04 | 1/3 | `bc/ProjectWireContracts.cs:8`; `AuditWireContracts.cs:18`; `AuditsController.cs:136-138`; `bm/McpToolSurface.cs:121-124` | REST can't set concurrency/rps/timeout overrides despite docs; REST≠MCP drift | medium | Extend `CrawlLimitsWire` to 6 names; map all fields |
| GAP-34 | **query-param values renamed/absent**: logs `view=waste\|uncrawled` rejected; analytics `view=inventory` 400s; ngrams `scope=site\|url` nonexistent | B2-05, B2-06, B2-07 | 1/3 | `bc/LogsController.cs:297-310`; `AnalyticsController.cs:57-59`; `NgramsController.cs:46-53` vs `bm/McpResourceSurface.cs:174` | Doc-following clients get 400s/ignored params | low | Accept promised aliases or fix docs+comments |

### 4.e Promise↔reality mismatches

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-35 | **Security & protocol rule family entirely unimplemented** (mixed content, non-HTTPS, insecure forms, missing security headers, no HTTPS redirect): zero keys, zero analyzers, no category | B1-02, B2-11 | 1/3 | Rule census `FindingRuleKeys.cs:11-98` + `*RuleKeys.cs` (no `security.*`); `bau/Pipeline/AuditEnums.cs:27-61`; grep = 0 | Users believe audits check security issues; nothing emitted — false assurance | high (R3 high) | Implement the five checks or drop the category from docs/UI |
| GAP-36 | **Images rules partial**: only alt-text (under Accessibility); broken sources + missing dimensions absent | B1-03, B2-11 | 1/3 | `bau/Rules/AccessibilityRuleKeys.cs:11`; `PerformanceKeys.cs:48-51` | 2 of 4 documented checks never emit | medium | Add `images.*` rules or re-document as alt-text-only |
| GAP-37 | **Sentiment scoring genuinely absent**: promised optional polarity scoring — no implementation, key, flag, or UI | G4-01, B1-01 | 2/3 | grep `sentiment\|polarity\|affect` = 0 hits (readability exists: `bau/Analysis/ReadabilityCalculator.cs:12`) | Shipped-optional capability absent from every layer — genuine promise break | medium (max: R1>R3) | Implement behind default-off toggle, or reclassify spec §7.12 as future |
| GAP-38 | **Promised threshold group unexposed** (WAF thresholds, AIMD group, throttle bound, SERP budgets, n-gram bounds, catch-up, concurrency); settings wire = theme/locale/database/proxy/mcp/embedding/statusBar | G4-03, B1-06, B2-14 | 2/3 | `bc/SettingsController.cs:74-115,401`; defaults `WafDetectionPolicy.cs:21-24`, `SchedulingOptions.cs:21` | "locally configurable" knobs untunable | medium | Extend `PUT /api/settings` (even as Advanced JSON) |
| GAP-39 | **Per-project retention & storage reporting unexposed**: pruner supports keep-last-N/age but only run-history calls it; `GetTotalStoredBytesAsync` zero callers | G4-05 (part), B1-07 | 2/3 | `bdr/RetentionPruner.cs:63-138`; caller `ScheduledAuditService.cs:506`; `CompressedContentStore.cs:270` | Retention promised as configurable is effectively fixed; storage unreported | medium | Per-project policy CRUD + storage-reporting surface |
| GAP-40 | **Project archive export/import (provider-migration vehicle) implemented but never composed**: no DI, no REST, no UI | G4-05 (part), B1-08 | 2/3 | `bdr/ProjectArchiveService.cs:34-263`; `b/CrawlForge.Data.MySql/ProviderSwitchContracts.cs:17`; grep registration = none | backup/transfer + "switching providers" unreachable | high (max: R3>R1) | Register service; `POST /api/projects/{id}/archive/export\|import`; hook provider-switch |
| GAP-41 | **Sitemap ingestion absent** (sharpened: robots.txt `Sitemap:` lines ARE parsed; fetch/parse/expand is not) | G4-06 | 1/3 | `bcr/Robots/RobotsTxtParser.cs:23,86-89`; grep sitemap fetch/expand = none | Audits under-crawl sitemap-reliant sites | medium | Bounded index-aware sitemap fetcher/expander |
| GAP-42 | **Signed-request access not configurable**: engine plumbing exists; secret store never constructed; no route | B1-09 | 1/3 | `bcr/Fetching/SignedRequestHandler.cs:33`; `SignedRequestSecretStore.cs:35` | signed access unconfigurable by users | medium | Secret config on configurations; compose the store |
| GAP-43 | **User stop-word write surface missing**: `user_stopwords` + reader exist; no write route | B1-10 | 1/3 | `bdm/SchemaCatalog.Additive.cs:91`; `bau/Analysis/StopWordFilter.cs:124` | stop-word management has no add/remove path | low | CRUD beside `/api/dictionary` |

### 4.f Channel parity

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-44 | **Duplicate reads, overloaded names, key drift**: `system-health` hub vs REST (uncalled); `diagnostics/version` vs `api/version` (different payloads, one uncontracted); `/hub` vs `/api/health`; `waf-blocks`/`host-throttle`/`redirect-matrix` on up to 5 surfaces; key drift (`hosts.rate-override` vs `hosts.override`, `matrix.upload` vs `redirect-matrix.upload`) | G3-04 (part), G6-05, A3-04 (part), A3-10, B3-05, D-09 | 3/3 | `b/CrawlForge.Api/Hubs/HubEventCatalog.cs:54` vs `bc/SystemController.cs:64`; `bh/CrawlForgeBackend.cs:126` vs `bc/HealthController.cs:46`; `fca/routes.ts:115,126` | Joins mis-key on collisions; two "version" payloads; benign over-exposure | low | Surface-suffix naming convention; one canonical read per fact; `routes.ts` as key authority |

### 4.g Exposure & governance

| ID | Finding | Source IDs | Corroboration | Evidence (file:line) | Impact | Severity | Remediation |
|---|---|---|---|---|---|---|---|
| GAP-45 | **`GET /hub` ungated despite its own doc-comment** ("remain[s] token-gated"): predicate covers only `/api/*` (minus health), `/hubs/*`, `/metrics` | G7-01, B4-01, A3-04 (part), D-07 | 3/3 | `bh/Hosting/SessionTokenMiddleware.cs:15-17` vs `:49-53`; `bh/CrawlForgeBackend.cs:125` | Ungated static descriptor (low data risk); false security claim invites wrong assumptions | medium | Gate `path.Equals("/hub")` (one line) or fix the remark |
| GAP-46 | **Feature toggles gate nothing**: seed writes `localFeatureToggles` + `mcpAutomationEnabled:false`; reader reads key `featureToggles` (seed in DB prefs, reader in file); `IsEnabled` zero production callers; `mcpAutomationEnabled` read by nobody | G7-02, B4-02 | 2/3 | `bdm/Seeding/FirstRunSeed.cs:32` vs `bh/Initialization/LocalSettings.cs:12` + `bh/Features/LocalFeatureAvailability.cs:28-33,39` | Toggles would flip dead switches; nothing disableable; seeded MCP-off ignored | high (max: R1>R3) | One key + storage; call `IsEnabled` at composition boundary — or delete machinery |
| GAP-47 | **MCP server unconditional; token story disconnected**: starts with no enabled check, writes plaintext bearer token to `mcp-endpoint.json` each startup; settings token lives in another store than `FileMcpTokenStore` so rotation can't invalidate clients; "rotate by updating the token field" beside a read-only field | G7-03, B4-03, B2-13, A2-17 | 3/3 | `bh/Composition/McpModule.cs:37,127-136`; `bc/SettingsController.cs:386-389,504-512`; `f/shell/SettingsSurface.tsx:255-260` | Automation runs while UI says disabled (seed default `false`); "rotation invalidates clients instantly" false; token always on disk | high (R1+R3 high) | Gate `StartAsync` on flag; rotate via `McpTokenService.RotateAsync`; surface endpoint facts |
| GAP-48 | **Single-token zero-role auth on admin-class endpoints**: one token unlocks shutdown, DB test (only outbound endpoint), compact, purge, rescan, DNS verify | B4-06 | 1/3 | `bh/Hosting/SessionTokenMiddleware.cs:46-65` (sole gate); `bc/SettingsController.cs:257` | No privilege separation; token leak grants full admin surface | medium | Document model; split read/admin tokens if multi-role lands |
| GAP-49 | **Diagnostic artifacts purgeable but not downloadable**: GET metadata, DELETE purge; no stream endpoint | G7-05, B4-05 | 2/3 | `bc/DiagnosticsController.cs:24,37,61` | Crash evidence destroyable but not retrievable for support | low | `GET …/artifacts/{id}/file` or document the on-disk directory |

---

## 5. False-gap corrections (disconfirmed / adjusted)

Do not re-report any item below as stated.

1. **`cron` grammar is NOT a mismatch** — docs and code agree: the field explicitly "is not cron syntax"; parser implements exactly `interval:<timespan>` / `calendar:HH:mm[;days=…]` (`bcr/Scheduling/RecurrenceRule.cs:20-23,104`; `bc/SchedulesController.cs:15,136`). Residual = stale TS doc-comment wording (`fca/schedules.ts:47-48`). (R3 D-01; R2 A3-07 downgraded.)
2. **`saved_views` is not doc-only** — table exists (`SchemaCatalog.Baseline.cs:185`) and backs `/api/queries` (`QueriesController.cs:12,28-29,102`); only generalized "filter presets" is unrealized (→ GAP-44).
3. **`GET /api/schedules` IS implemented** (`SchedulesController.cs:95-96`) — drift = missing `routes.ts` row + spec silence (GAP-29/06), never "missing API".
4. **Sentiment scoring is genuinely absent** (grep = 0) — the one surviving candidate; real gap GAP-37. Contrast `saved_views`.
5. **`local-notification` is published, not missing** (`ScheduleHubEvents.cs:111`; `HubEventCatalog.cs:75`); gap is contract/consumer-side only (GAP-21). Both "isn't emitted" and "rendered by shell" are wrong.
6. **Hub-fed panels are "wired" for consumption only** — WAF-summary/embedding/lifecycle panels are client-wired but their trigger events have no publisher (GAP-20).
7. **Sitemap: discovery parsed, ingestion absent** (`RobotsTxtParser.cs:23,86-89`) — "ingestion entirely absent" overstates (GAP-41).
8. **`hosts.list` "never invoked" overstated** — a second dead path exists in the unmounted dashboards loader (`fs/dashboards/dashboardData.ts:103`); still unreachable (GAP-28).
9. **`metrics.get` has one non-catalog reference** (`fst/apiClient.ts:141`, raw plumbing) — still never invoked (GAP-18).
10. **Cross-filter driver is `applyChartCrossFilter`** (`fs/charts/crossFilter.ts:84-99`); `applyChartSelection` does not exist (GAP-02).
11. **Hub counts 20 vs 21 reconciled** — backend 19 events + 2 methods; frontend 18 + 2; delta = `local-notification` (GAP-21).
12. **`/hub` middleware remark half-right** — `/api/diagnostics/version` IS gated; only literal `/hub` falls through (GAP-45).
13. **"Only shutdown is uncontracted" understated** — 6 endpoints are (GAP-29); 114−108 closes exactly.
14. **OS taskbar progress is implemented-and-dark**, not doc-only (`e/main.ts:348`) → GAP-04.
15. **Tables extraction exists** (`ElementInventoryExtractor.cs:92`) — spec §7.3 group not absent.
16. **"Optional embedded preview" is not a gap** — vague spec wording; preview needs met by content store + sandboxed previews + render screenshots.
17. **Dashboard metrics `score-trend`/`log-hits` have no writer** — dark-UI defect (empty series once mounted), not a docs gap (GAP-01 note).
18. **Overloaded-name collisions produced no false gaps** once surface suffixes applied; `waf-block-rate` is both rule and metric key.
19. **MCP tools map 1:1 onto REST lifecycle ops** — confirmed; supports §6 intentional-design ruling.

### 5.1 Register conflicts resolved

- **Hub delivery parity**: R3 B2-08 "all 19 names emitted" vs R1 G5-01 publisher audit → resolved: name parity 19/19 vs publication parity 12/19 (re-verified at every `PublishAsync` site). Register uses publication parity; name parity in §6.
- **`audits.cwv`**: R3 `R` (types exist) vs R1 dead code → resolved for G5-04: no DI registration/caller (re-verified) → GAP-23, resolution = dead.
- **Severity raises (max rule)**: GAP-19 → critical per merge directive (R1 rated high); GAP-08/21/31/40/46 → high on the higher register; GAP-03/17/25/30/37 held at max. Lower ratings never won.
- **Anchor correction**: List Mode race = `AuditsController.cs:140` vs `:154-160`; registers cite `:139`/`:153-158` (same statements, off-by-one). All other re-checked anchors matched exactly.

---

## 6. Non-gaps & by-design (verified parity)

Not gaps — do not file:

1. **Promised REST ops 89/89 implemented** (R3 B2-01; all matched 1:1 to controller templates in the R2 join). Guide-only `GET /api/schedules` = promise kept, spec silent.
2. **MCP names 28/28 exact** — 8 tools + 15 resources + 5 prompts (`bm/McpToolSurface.cs:89-110`, `McpResourceSurface.cs:167-181`, `McpPromptSurface.cs:107-116`).
3. **Metric keys 20/20 with verified writers** (`MetricKeys.cs:8-68`; writers across the WAF, ngram, log, GEO, semantic, MarTech, equity, content-quality, AIMD, matrix, and pipeline recorders). `/metrics` family gaps = GAP-27, separate from key parity.
4. **Rule keys 30/30 present** (`FindingRuleKeys.cs:11-98` + emitters). Caveats filed separately: taxonomy 11→8 categories (`AuditEnums.cs:27-61`); Security family 0 rules (GAP-35) — key parity ≠ category parity.
5. **Completion-action kinds 3/3** (`local-notification`, `export-delivery`, `notification-endpoint`), validated at save (`ScheduleCompletionAction.cs:107-132`); formats `xlsx/csv/html/pdf`. Two of three delivery paths broken/hidden (GAP-07/21) — delivery, not coverage, gap.
6. **Intentional MCP narrowness (spec §11.1)**: 8 tools vs 114 REST is design — none of the management surfaces (settings, destinations, credentials, proxy, DB/embedding providers, plugins, log imports, matrices, GEO, analytics config, schedule/config editing, export/delivery) are exposed over MCP (`McpToolSurface.cs:11-18`). R1 G6-03 / R2 A1-16 / R3 B3-01 agree: intentional; keep the "what agents can and cannot do" boundary in user-facing docs.
7. **One-vs-four ruling** (both join registers agree): single-page analysis (`POST /api/analysis/single-page`), URL checker (`POST /api/tools/url-check`), render tester (`POST /api/tools/render-test`), MCP `analyze-single-page` = **one capability** (`inspect.single-url`) with **four surface bindings** — counted once in the 170-row resolution and once in the matrix (Tools ✓). Shared WAF-fingerprint pass + finding model; picker in two of them. Residuals stay per-surface: the three REST ops keep distinct payloads (render-test = screenshot/DOM/console; url-check = redirect chain/headers) — never merge wire contracts; no `url-check`/`render-test` MCP siblings is intentional per §11.1. (R1 §1.4, R3 §1.)
8. **Explicitly-future items — NOT gaps** (spec §15; excluded from all registers/statistics): (a) unattended/headless CLI execution; (b) manual product-update path; (c) third-party search/analytics/backlink data enrichment incl. CWV *field-performance* data (distinct from the dead CWV *lab* suite, GAP-23); (d) AMP analysis; (e) raw-SQL administration tool.
9. **Benign/by-design**: `POST /api/settings/database/test` — only outbound endpoint, documented in the airgap table (R1 G7-06). Local-only computations deliberate (SERP preview, SPA filter, JS heap, window state — R2 A2-19). `/metrics` pull-only. External links force-open in OS browser (`e/main.ts:387-393`).

---

## 7. Remediation roadmap

One sequence reconciling R1 top-10, R2 top-8, R3 top-8 (severity × breadth ÷ effort). **S** ≤3 d · **M** 1–2 wk · **L** >2 wk. Anchors re-verified 2026-11-17 (§5.1).

### P0 — ship-blockers

| # | Title (findings) | Effort | User-visible effect | Verified anchors |
|---|---|---|---|---|
| P0-1 | **Fix List Mode persistence ordering** (GAP-19) | S | Paste-URL-list bounded audits work again (incl. matrix auto-seed) | `bc/AuditsController.cs:140` vs `:154-160` |
| P0-2 | **Mount the report workspace** + bind cross-filter (GAP-01, GAP-02; unlocks GAP-16) | M | Findings/URLs/links/charts/graphs readable; ~30 dark capabilities light up at once | `f/shell/AuditViews.tsx:316-320`; `fs/explorers/index.ts:36-48`; `fs/charts/crossFilter.ts:84-99` |

### P1 — high

| # | Title (findings) | Effort | User-visible effect | Verified anchors |
|---|---|---|---|---|
| P1-1 | **Exposure-governance bundle**: gate MCP on flag, unify token store, fix toggle key + wire `IsEnabled`, gate `/hub` (GAP-45/46/47) | M | security/toggle/rotation stories true; automation stops when "disabled" | `bh/Composition/McpModule.cs:127-136`; `bdm/Seeding/FirstRunSeed.cs:32` vs `bh/Features/LocalFeatureAvailability.cs:28-33`; `bh/Hosting/SessionTokenMiddleware.cs:49-53` vs `bh/CrawlForgeBackend.cs:125` |
| P1-2 | **Publish the 7 dead hub events** — or delete from docs+contracts (GAP-20) | M | WAF badges / embedding panel / lifecycle banner light up; `audit-failed` alerts arrive | `bh/Execution/AuditExecutionEngine.cs:1252,1614,1641`; `bh/Composition/EngineServicesModule.cs:297-302`; `b/CrawlForge.Api/Hubs/HubEventCatalog.cs:19-69` |
| P1-3 | **Wire export pipeline end-to-end** (GAP-07; part GAP-04) | M | "Export" yields real xlsx/csv/html/pdf via native save dialog | `fca/routes.ts:69-70`; `fs/explorers/ExplorerHost.tsx:305-320`; `bc/ExportsController.cs:45,99,122` |
| P1-4 | **Fix `local-notification` delivery** (GAP-21) | S | Scheduled-run "notify me" shows an OS notification | `bcr/Scheduling/ScheduleHubEvents.cs:111`; `b/CrawlForge.Api/Hubs/HubEventCatalog.cs:75`; `f/contracts/hub/events.ts:316-333` |
| P1-5 | **Contract-catalog completion** + shared route constants (GAP-29/30) | S | 108-vs-114 drift ends; poll-export/deliver/schedules-list callable | `bc/SchedulesController.cs:95-96`; `ExportsController.cs:99`; `bh/CrawlForgeBackend.cs:125,126,131`; `e/supervisor/backend-supervisor.ts:168,270,301` |
| P1-6 | **Dark-domain UI batch 1**: schedules screen + log imports panel (GAP-06, GAP-08, GAP-05) | L | Automation + log analysis usable from UI; nav copy honest | `fca/routes.ts:71-79,129-133`; `bc/SchedulesController.cs:71-306`; `bc/LogsController.cs:63,100,117,179` |

### P2 — high-value follow-ups

| # | Title (findings) | Effort | User-visible effect | Verified anchors |
|---|---|---|---|---|
| P2-1 | **Rendering into full audits + register CWV suite** (GAP-22, GAP-23) | M | JS pages audited correctly; performance/CWV findings appear | `bh/Composition/EngineServicesModule.cs:204-210` vs `:195-203`; `bau/Performance/CoreWebVitalsAnalyzer.cs:15` |
| P2-2 | **Two-way lifecycle**: project/config editors + re-audit/compare (GAP-09, GAP-10, GAP-26) | L | Projects/configs editable; re-analysis and diffs possible | `fca/routes.ts:45-52,55,59,67,88`; `bc/ProjectsController.cs:108,125,170`; `AuditsController.cs:281,318` |
| P2-3 | **Make the plugin system real** (GAP-03; part GAP-04) | M | Scan/manage/grant/remove plugins works | see GAP-03 |
| P2-4 | **Consume the Electron native layer** (GAP-04) | M | Pickers, notifications, taskbar, recovery UI work | see GAP-04 |
| P2-5 | **Security rule family + suppression read path** (GAP-35, GAP-13) | L | Advertised security checks run; suppressions reversible | see GAP-35/13 |
| P2-6 | **Observability bundle**: metrics families, action-log read/purge, diagnostics download (GAP-27, GAP-24, GAP-49) | S | Real `/metrics` series; history viewable; artifacts retrievable | see GAP-27/24/49 |

### P3 — polish & promise cleanup

| # | Title (findings) | Effort | User-visible effect | Verified anchors |
|---|---|---|---|---|
| P3-1 | **Wire-shape docs & parameter aliases** (GAP-31, GAP-32, GAP-33, GAP-34) | S | Convention-following clients stop failing; promised fields/views work | see GAP-31…34 |
| P3-2 | **Dark single-op batch**: dictionary, WAF signatures, semantics run, matrix upload, host pin, cannibalization (GAP-11/12/14/15/17) | M | Expert workflows reachable; phantom-copy targets real | see GAP-11…17 |
| P3-3 | **Data-management exposure**: retention/storage + archive export/import (GAP-39, GAP-40) | M | Retention control + provider migration usable | see GAP-39/40 |
| P3-4 | **Promise-mismatch batch**: thresholds, signed-access, stop-words, images, sentiment decision, sitemap ingestion (GAP-36/37/38/41/42/43) | M | knobs/capabilities ship or are honestly re-scoped | see GAP-36…43 |
| P3-5 | **Hygiene**: `Unsubscribe`, `loadHosts`, token-rotate affordance, naming convention, auth model docs (GAP-25, GAP-28, GAP-44, GAP-47/48 remainder) | S | Long-session stability; cold-start host table; fewer join hazards | see GAP-25/28/44 |

Just-missed, in order: GAP-16 (rides P0-2), GAP-18 (Settings "About"), GAP-37 if implemented rather than re-scoped.

---

## 8. Statistics appendix

### 8.1 Counts per theme / severity / corroboration

| Theme | Findings | Critical | High | Medium | Low | 3-reg | 2-reg | 1-reg |
|---|---|---|---|---|---|---|---|---|
| (a) Unreachable output & analysis | 5 | 1 | 2 | 2 | 0 | 3 | 1 | 1 |
| (b) Dark automation domains | 13 | 0 | 5 | 7 | 1 | 10 | 3 | 0 |
| (c) Broken pipelines & dead wiring | 10 | 2 | 3 | 4 | 1 | 3 | 2 | 5 |
| (d) Contract drift & boundary leaks | 6 | 0 | 2 | 3 | 1 | 1 | 2 | 3 |
| (e) Promise↔reality mismatches | 9 | 0 | 2 | 6 | 1 | 0 | 4 | 5 |
| (f) Channel parity | 1 | 0 | 0 | 0 | 1 | 1 | 0 | 0 |
| (g) Exposure & governance | 5 | 0 | 2 | 2 | 1 | 2 | 2 | 1 |
| **Total** | **49** | **3** | **16** | **24** | **6** | **20** | **14** | **15** |

Rows close exactly: critical 3; high 16; medium 24; low 6; corroboration 20/14/15. Register inputs R1 2C/13H/23M/11L, R2 2C/10H/27M/7L, R3 0C/9H/14M/15L — 133 rows before dedup. **20 findings corroborated by all 3 registers**; 15 rest on one register's unique scope (R3 docs-side promise gaps; R1 engine publisher/DI verifications).

### 8.2 Register merge accounting

R1 (49 rows) → 38 findings reference its IDs (G1 merged by domain, G1-09 → GAP-01; G5-05 → GAP-21/29; G6-03 → §6). R2 (46) → 30 findings (A2-10…13 → GAP-04; A2-19/20 → §6/§4.g). R3 (38) → 30 findings (informational rows B2-01/09/10/12, B3-01, and the 5 future items → §6).

### 8.3 Unverified-leads appendix

**Empty.** Every §4 claim carries its own `file:line` anchor under `app\src\` (register-verified, spot-re-verified in this merge). Claims supported only by out-of-scope material were dropped. Runtime-behavior claims rest on static source tracing — authoritative for wiring questions (call sites, registrations, publishers, mount points).

### 8.4 Artifacts index (six inputs)

| File | Description |
|---|---|
| `audit-work/01-api-exposure-inventory.md` | 01: API exposure — 114 REST actions with `file:line`, hub catalog, MCP 8/5/15, 11 IPC channels, auth model |
| `audit-work/02-ux-surface-inventory.md` | 02: UX surfaces — 141 rows (62 wired / 54 dead / 25 local-only), 46 unused route keys, dead-affordance + API-bypass sections |
| `audit-work/03-documented-feature-inventory.md` | 03: promise column — 170 capabilities / 11 domains, ≈89 REST ops, 19+1 hub events, 28 MCP names, metric & rule keys, 5 future items |
| `audit-work/04-gap-analysis.md` | R1: G1–G7 (49 findings), 22-domain matrix, 27 source verifications, disconfirmed section, top-10 |
| `audit-work/04a-ux-api-join.md` | R2: A1–A3 (46 findings), exact 114↔108 / 19↔18 / 11↔1 join math, dark-surface accounting, top-8 |
| `audit-work/04b-docs-channel-join.md` | R3: B1–B4 (38 findings), 170-row resolution, verified parity (89/89, 28/28, 20/20, 30/30, 3/3), false-gap list |

*End of report — 2026-11-17. Merge of R1/R2/R3 with source re-verification. `ux-api-gap-audit.md` is the sole output.*
