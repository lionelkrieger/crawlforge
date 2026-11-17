# Crawlforge — UX-Surface Inventory (audit side 02)

**Scope**: every user-facing UX surface in `app/src/frontend/src/` (shell, dashboards, explorers+grid, single-page + tools, graphs, plugins UI, store/realtime) plus native windows/menus/dialogs/IPC in `app/src/electron/src/`. Excluded: `node_modules`, `dist`, `bin`, `obj`.
**Method**: every surface file read; every backend call traced through `contracts/api/routes.ts` (frozen route catalog, 108 operations) → `store/apiClient.ts` (`createLoopbackApiClient`, resolves `API_ROUTES[key]` to literal method+path) → `store/crawlForgeStore.ts` / `tools/shared/useToolCall.ts` / `grid/dataSource.ts` wrappers. Hub calls resolved to SignalR method/event names (`/hubs/realtime`). Electron calls resolved to IPC channel names.

**Status vocabulary** (per audit brief):
- `wired` — reaches a real backend endpoint/hub channel (directly or via store/hub-demux feed).
- `stub` — TODO/placeholder/fake data/disabled control with no real behavior.
- `dead` — implementation exists but the surface is unreachable in the shipped shell (not mounted / no entry point) or the handler is empty.
- `local-only` — pure client computation / local persistence.

**Key finding up front**: the shipped shell (`App.tsx` → `CrawlForgeShell` → views `hub | audits | report | tools | settings`) mounts ONLY the shell views + the tools workspace. All dashboards, all 11 explorers, the visualization/graph surfaces, and the plugin-management surface are complete, route-wired components that NOTHING mounts (they are exported from barrels only). They are inventoried below with status `dead` (zero UX reach) — do not read `dead` as "fake"; read it as "no reachable entry point".

---

## 1. Totals

- **Surface areas inventoried**: 8 (Shell, Dashboards, Explorers, Tools, Graphs, Plugins, Store/Realtime, Electron-native)
- **Distinct surfaces/entry points enumerated**: 43
- **Capability rows**: 141 (see table)
- **Wiring status**: `wired` 62 · `stub` 0 · `dead` 54 · `local-only` 25
- **Route keys in `contracts/api/routes.ts`**: 108 total — **62 called by some surface/store**, **46 with no UX reach** (Section A)
- **Hub wire names**: 20 (18 server events + subscribe/unsubscribe) — all 18 server events consumed by `store/hubDemux.ts`; `unsubscribe` invoked by no surface
- **Electron IPC channels**: 9 (`preload/bridge.ts:59-69`) + 2 inline (`crawlforge:get-bridge`, `crawlforge:hydrated`) — 0 consumed by the renderer except `get-bridge`
- **Electron window kinds**: 8 (`windows/window-catalog.ts:39-120`) — 3 reachable (splash/main/recovery), 5 never opened

> Note on capability keys: where a capability maps 1:1 onto an API route the key equals the route key (`audits.pause`) so the cross-reference lines up; tool screens use dash-slugs per the shared namespace convention (`tools.robots-tester`).

---

## 2. Capability table

### Shell & navigation

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| shell.view.hub | Nav rail "Main hub" | app/src/frontend/src/shell/NavigationRail.tsx:27 | none (view switch) | local-only | View switching persisted to localStorage; data loads happen in bootstrap |
| shell.view.audits | Nav rail "Audits" | app/src/frontend/src/shell/NavigationRail.tsx:28 | none | local-only | |
| shell.view.report | Nav rail "Report" | app/src/frontend/src/shell/NavigationRail.tsx:29 | none | local-only | Opens ReportView for active audit |
| shell.view.tools | Nav rail "Tools" | app/src/frontend/src/shell/NavigationRail.tsx:30 | none | local-only | |
| shell.view.settings | Nav rail "Settings" | app/src/frontend/src/shell/NavigationRail.tsx:31 | none | local-only | |
| shell.palette.open | Header "Search commands…" + Ctrl+K / Ctrl+P | app/src/frontend/src/shell/CommandPalette.tsx:28-42, 128-143 | none | local-only | Mantine Spotlight; fuzzy search is client-side |
| shell.palette.navigate | Palette "Navigate" group (5 actions) | app/src/frontend/src/shell/CommandPalette.tsx:51-67 | none | local-only | Mirrors the nav rail 1:1 |
| shell.palette.project-jump | Palette "Projects" group (≤20 projects) | app/src/frontend/src/shell/CommandPalette.tsx:69-79 | none | local-only | Only navigates to hub view — no project detail surface exists |
| shell.palette.audit-open | Palette "Audits" group (≤20 audits) | app/src/frontend/src/shell/CommandPalette.tsx:81-91 | hub method `Subscribe(string[])` on `/hubs/realtime` | wired | `navigation.openAudit` (ShellContext.tsx:136-142) subscribes the per-audit channel |
| shell.palette.refresh-aggregates | Palette "Refresh local aggregates" | app/src/frontend/src/shell/CommandPalette.tsx:93-106 | `GET /api/projects` (`projects.list`), `GET /api/audits` (`audits.list`), `GET /api/plugins` (`plugins.list`) | wired | Via store actions (crawlForgeStore.ts:227-241, 274-278) |
| shell.palette.resubscribe | Palette "Re-subscribe active audit channel" | app/src/frontend/src/shell/CommandPalette.tsx:107-120 | hub method `Subscribe(string[])` | wired | |
| shell.hub.connection-pill | Header connection pill | app/src/frontend/src/shell/CrawlForgeShell.tsx:34-48 | none (renders hub connection state) | local-only | State fed by SignalR connection lifecycle (hubClient.ts mapState) |
| shell.hub.lifecycle-banner | Header banner on backend shutdown | app/src/frontend/src/shell/CrawlForgeShell.tsx:51-74 | hub event `backend-lifecycle` | wired | Via liveProgress `ingestBackendLifecycle` (liveProgress.ts:325) |
| shell.activity-feed | Secondary pane "Live activity" | app/src/frontend/src/shell/ActivityFeed.tsx:36-74 | hub events (all 18 server events, via hubDemux ring buffer) | wired | Bounded buffer, newest-first, drop counter; raw payloads never shown |
| shell.statusbar.telemetry | Status bar WS/Heap/GC/WAL cells | app/src/frontend/src/shell/StatusBar.tsx:100-135 | hub event `system-health` (2 s heartbeat) | wired | Advisory coloring from settings thresholds; stale after 6 s |
| shell.statusbar.js-heap | Status bar "JS" cell | app/src/frontend/src/shell/StatusBar.tsx:136-141 | none (`store/frontendHeap.ts:17` measureFrontendJsHeap) | local-only | Documented "measured locally, never sent to the backend" |
| shell.statusbar.rate | Status bar "Rate" cell while audit runs | app/src/frontend/src/shell/StatusBar.tsx:142-149 | hub event `crawl-progress` | wired | |
| shell.system.compact | Status bar "Compact" button + result modal | app/src/frontend/src/shell/CompactAction.tsx:60-104 (call at :78) | `POST /api/system/compact` (`system.compact`) | wired | Before/after snapshot modal; guard refusals render inline |
| shell.pane.layout | Split divider drag, "Show/Hide" activity pane button | app/src/frontend/src/shell/SplitPanes.tsx:144-155; CrawlForgeShell.tsx:170-182 | none | local-only | Keyboard Enter toggles collapse |
| shell.window-state | Persisted view+layout across reloads | app/src/frontend/src/shell/windowState.ts:59-78 | none (localStorage `crawlforge.shell.windowState.v1`) | local-only | |
| shell.bootstrap | Shell mount (useShellLifecycle) | app/src/frontend/src/shell/ShellContext.tsx:171-208 | `GET /api/settings` (`settings.get`), `GET /api/projects`, `GET /api/audits`, `GET /api/plugins`; hub `start()` + `Subscribe` | wired | Offline-first: every load degrades to an error outcome |

### Hub dashboard (projects / audits)

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| projects.list | Hub "Projects" table | app/src/frontend/src/shell/HubDashboard.tsx:271-329 | `GET /api/projects` (`projects.list`) via store `loadProjects` | wired | Tags/description/created columns |
| projects.create | Hub "New project" button → modal → "Create project" | app/src/frontend/src/shell/HubDashboard.tsx:259, 61-120 (call at :81) | `POST /api/projects` (`projects.create`) | wired | Field-level error feedback (`error.fieldErrors`) |
| configurations.create | Same modal (target URL field) | app/src/frontend/src/shell/HubDashboard.tsx:95-102 | `POST /api/projects/{id}/configurations` (`configurations.create`) | wired | Locked two-call flow: start URL stored on configuration |
| hub.stat-cards | Hub stat cards (Projects/Running/URLs/WAF) | app/src/frontend/src/shell/HubDashboard.tsx:264-269 | none (client aggregation of store + live waf counts) | local-only | |
| audits.list | Hub "Audits" table + AuditsView table | app/src/frontend/src/shell/HubDashboard.tsx:331-369; AuditViews.tsx:92-147 | `GET /api/audits` (`audits.list`) via store `loadAudits` | wired | Live counters replaced by hub demux |
| audits.start | Per-project "Start audit" button | app/src/frontend/src/shell/HubDashboard.tsx:308-318 (flow at :208-241) | `GET /api/projects/{id}/configurations` (`configurations.list`) → `POST /api/audits` (`audits.start`) | wired | Uses first configuration; then reload + openAudit |
| audits.open | "Open"/"Open report" buttons | app/src/frontend/src/shell/HubDashboard.tsx:187; AuditViews.tsx:134 | hub method `Subscribe(string[])` | wired | Sets active audit + switches to report view |

### Audit report frame (AuditViews)

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| audits.pause | Report "Pause" button (state=running) | app/src/frontend/src/shell/AuditViews.tsx:72 (call :54) | `POST /api/audits/{id}/pause` (`audits.pause`) | wired | Then `GET /api/audits` refresh |
| audits.resume | Report "Resume" button (state=paused) | app/src/frontend/src/shell/AuditViews.tsx:77 (call :54) | `POST /api/audits/{id}/resume` (`audits.resume`) | wired | |
| audits.cancel | Report "Cancel" button | app/src/frontend/src/shell/AuditViews.tsx:82 (call :54) | `POST /api/audits/{id}/cancel` (`audits.cancel`) | wired | |
| audits.report.progress | "Crawl progress" panel + MiniStats | app/src/frontend/src/shell/AuditViews.tsx:205-220 | hub event `crawl-progress` | wired | Falls back to last `GET /api/audits` summary counters |
| audits.report.matrix-progress | "Redirect matrix" panel | app/src/frontend/src/shell/AuditViews.tsx:222-233 | hub event `matrix-row-validated` | wired | Rendered only while a matrix is attached |
| audits.report.embedding-progress | "Embeddings" panel | app/src/frontend/src/shell/AuditViews.tsx:235-254 | hub events `embedding-progress`, `embedding-completed` | wired | |
| audits.report.host-throttle | "Host throttle (AIMD)" table | app/src/frontend/src/shell/AuditViews.tsx:256-293 | hub event `host-throttle` (REST `GET /api/audits/{id}/hosts` loader exists but is never invoked — see B10) | wired | Table is empty until hub events arrive |
| audits.report.waf-summary | "WAF blocks by provider" badges | app/src/frontend/src/shell/AuditViews.tsx:295-309 | hub event `waf-blocks` | wired | Live aggregate only |
| audits.report.mount-promise | "Report surfaces" placeholder card | app/src/frontend/src/shell/AuditViews.tsx:311-321 | none | dead | Copy promises grids/charts/link-graph mounting that no code performs (see B1) |

### Settings

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| settings.get | Settings mount / every save | app/src/frontend/src/shell/SettingsSurface.tsx (via store :217-225) | `GET /api/settings` (`settings.get`) | wired | |
| settings.theme | Appearance → dark theme switch | app/src/frontend/src/shell/SettingsSurface.tsx:156-164 (call :68) | `PUT /api/settings` (`settings.update`) | wired | |
| settings.ui-language | Appearance → UI language select (en/de/fr/es/ja/zh) | app/src/frontend/src/shell/SettingsSurface.tsx:165-173 | `PUT /api/settings` (`settings.update`) | wired | |
| settings.status-bar-advisories | Working-set/WAL advisory inputs + "Save advisories" | app/src/frontend/src/shell/SettingsSurface.tsx:177-215 | `PUT /api/settings` (`settings.update`) | wired | |
| settings.database-test | Database → "Test connection" | app/src/frontend/src/shell/SettingsSurface.tsx:229-231 (call :81) | `POST /api/settings/database/test` (`settings.databaseTest`) | wired | reachable/authenticated/migration-compatible feedback |
| settings.mcp | MCP enable switch + transport select | app/src/frontend/src/shell/SettingsSurface.tsx:235-253 | `PUT /api/settings` (`settings.update`) | wired | |
| settings.mcp-token-display | "Automation token" read-only field | app/src/frontend/src/shell/SettingsSurface.tsx:254-261 | none (renders settings value) | local-only | No rotate action offered despite copy "rotate by updating the token field" |
| settings.embedding-test | Embeddings → "Test provider" | app/src/frontend/src/shell/SettingsSurface.tsx:271-273 (call :102) | `POST /api/settings/embedding/test` (`settings.embeddingTest`) | wired | |
| settings.proxy-display | Proxy section | app/src/frontend/src/shell/SettingsSurface.tsx:277-283 | none (read-only display) | local-only | No proxy editor in UI |
| settings.diagnostics-list | Diagnostics table + "Refresh" | app/src/frontend/src/shell/SettingsSurface.tsx:112-118, 290-315 | `GET /api/diagnostics/artifacts` (`diagnostics.listArtifacts`) | wired | |
| settings.diagnostics-purge | "Purge artifacts" button | app/src/frontend/src/shell/SettingsSurface.tsx:316-318 (call :122) | `DELETE /api/diagnostics/artifacts` (`diagnostics.purgeArtifacts`) | wired | |

### Dashboards (all unmounted — see B1)

Data loader: `surfaces/dashboards/dashboardData.ts` (`loadDashboardAggregates` :72-157, `loadVisualizationAggregates` :167-189).

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| dashboards.audit-overview | AuditDashboard summary cards + status/severity/category/crawl-health charts + category breakdown | app/src/frontend/src/surfaces/dashboards/AuditDashboard.tsx:41-131 | `GET /api/audits/{id}/summary` (`audits.summary`) | dead | Fully implemented; no mount point in shell |
| dashboards.score-trend | ScoreTrendSurface (score trend + category scores) | app/src/frontend/src/surfaces/dashboards/ScoreTrendSurface.tsx:22-52 | `GET /api/audits/{id}/charts/{metric}` (`audits.chartSeries`, metric=score-trend), `GET /api/audits/{id}/scores` (`audits.scores`) | dead | |
| dashboards.crawl-health | CrawlHealthPanel (host throttle timelines/rates, WAF providers/block rate) | app/src/frontend/src/surfaces/dashboards/CrawlHealthPanel.tsx:31-80 | `GET /api/audits/{id}/hosts` (`hosts.list`, includeHistory), `GET /api/audits/{id}/waf-blocks/summary` (`waf.blocksSummary`) | dead | |
| dashboards.ngram-topical | NgramTopicalPanel (top n-grams, zones, topical gaps) | app/src/frontend/src/surfaces/dashboards/IntelligenceDashboards.tsx:42-70 | `GET /api/audits/{id}/ngrams` (`ngrams.list`), `GET /api/audits/{id}/ngrams/gaps` (`ngrams.gaps`) | dead | |
| dashboards.log-intelligence | LogIntelligencePanel (correlation, bot families, hit timeline) | app/src/frontend/src/surfaces/dashboards/IntelligenceDashboards.tsx:80-102 | `GET /api/audits/{id}/logs/correlation` (`logs.correlation`), `GET /api/audits/{id}/charts/{metric}` (metric=log-hits) | dead | |
| dashboards.geo-coverage | GeoCoveragePanel (llms.txt coverage, governance verdicts) | app/src/frontend/src/surfaces/dashboards/IntelligenceDashboards.tsx:111-130 | `GET /api/audits/{id}/geo/llms-txt` (`geo.llmsTxt`), `GET /api/audits/{id}/geo/governance` (`geo.governance`) | dead | |
| dashboards.analytics-coverage | AnalyticsCoveragePanel (container + consent coverage) | app/src/frontend/src/surfaces/dashboards/IntelligenceDashboards.tsx:139-158 | `GET /api/audits/{id}/analytics` (`analytics.list`, view=tags), `GET /api/audits/{id}/analytics/coverage` (`analytics.coverage`) | dead | |
| dashboards.cross-filter | Chart click / category link → explorer filtering | app/src/frontend/src/surfaces/dashboards/AuditDashboard.tsx:83-123; charts/crossFilter.ts:30-98 | Would call `GET /api/audits/{id}/urls|findings|links|ngrams` via store `loadUrlPage/loadFindingPage/loadLinkPage/loadNgramPage` | dead | `onCrossFilter` prop is never bound by any parent; `applyChartSelection` has no caller |
| charts.export-image | Per-chart export button (`exportName`) | app/src/frontend/src/charts/EChart.tsx:31, 96-100 | none — local PNG data-URL download | dead | Unreachable with the dashboards; mechanism is local-only by design (see C2) |

### Explorers + grid (all unmounted — see B2)

Host: `surfaces/explorers/ExplorerHost.tsx` (generic grid surface; binds ONE frozen route per definition via `grid/dataSource.ts:67-122`). Registry: `surfaces/explorers/index.ts:36-48` ("all eleven explorers").

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| explorers.host | ExplorerHost frame (view tabs, summary chips, drill-down drawer, offline states) | app/src/frontend/src/surfaces/explorers/ExplorerHost.tsx:76-323 | dispatches each definition's route | dead | No shell mount; `EXPLORER_NAV` consumed by nothing |
| explorers.urls | "URL Explorer" grid | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:22-152 (route :27) | `GET /api/audits/{id}/urls` (`audits.listUrls`) | dead | Sort whitelist [id, canonicalUrl, depth, statusCode] honored |
| explorers.urls.detail | Row drill-down drawer (findings, redirect chain, element inventory, WAF, SERP metrics) | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:89-151 (call :102) | `GET /api/audits/{id}/urls/{urlId}` (`audits.getUrl`) | dead | |
| explorers.issues | "Issue Explorer" grid | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:156-216 (route :161) | `GET /api/audits/{id}/findings` (`audits.listFindings`) | dead | Evidence snippet/selector in drill-down (local row data) |
| explorers.links | "Link Explorer" grid | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:220-288 (route :225) | `GET /api/audits/{id}/links` (`audits.listLinks`) | dead | serverField maps UI sort → backend whitelist |
| explorers.extraction | "Custom Extraction" grid (dynamic columns) | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:292-350 (route :297) | `GET /api/audits/{id}/extracted-content` (`audits.listExtractedContent`) | dead | No sort affordance (route binds none) |
| explorers.extraction.summary | Summary strip chips | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:328-336 | `GET /api/audits/{id}/extracted-content/summary` (`audits.extractionSummary`) | dead | |
| explorers.ngrams | "N-Gram Explorer" grid | app/src/frontend/src/surfaces/explorers/coreExplorers.tsx:354-414 (route :359) | `GET /api/audits/{id}/ngrams` (`ngrams.list`) | dead | |
| explorers.logs | "Log File Analyzer" grid + 3 view tabs (orphans / budget-waste / uncrawled-indexable) | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:30-89 (route :35; views :24-28) | `GET /api/audits/{id}/logs/correlation` (`logs.correlation`) | dead | emptyHint references a "project Log Imports surface" that does not exist (B15) |
| explorers.semantics | "Semantic Linking" grid | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:102-147 (route :107) | `GET /api/audits/{id}/semantics/link-opportunities` (`semantics.linkOpportunities`) | dead | emptyHint references a "Semantic analysis" run action that no surface offers (`semantics.run` unused, A45) |
| explorers.geo | "GEO & Bot Governance" grid + llms.txt summary chips | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:151-211 (route :156; summary :192-200) | `GET /api/audits/{id}/geo/governance` (`geo.governance`), `GET /api/audits/{id}/geo/llms-txt` (`geo.llmsTxt`) | dead | |
| explorers.analytics | "Analytics & Tagging" grid + 2 view tabs (tags / findings) + consent chips | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:226-319 (route :231; summary :287-295) | `GET /api/audits/{id}/analytics` (`analytics.list`), `GET /api/audits/{id}/analytics/coverage` (`analytics.coverage`) | dead | |
| explorers.redirect-matrix | "Redirect Matrix" grid | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:341-392 (route :346) | `GET /api/audits/{id}/redirect-matrix/results` (`matrix.results`) | dead | emptyHint references a matrix-upload flow that no surface offers (`matrix.upload` unused, A40) |
| explorers.waf | "WAF Blocks" grid + summary chips | app/src/frontend/src/surfaces/explorers/analysisExplorers.tsx:396-478 (route :401; summary :442-459) | `GET /api/audits/{id}/waf-blocks` (`waf.listBlocks`), `GET /api/audits/{id}/waf-blocks/summary` (`waf.blocksSummary`) | dead | |
| grid.filter-sort | DataGrid toolbar: instant filter, sort headers, column pin/resize, density, refresh, row selection | app/src/frontend/src/grid/DataGrid.tsx:165-205, 271-335 | Server-windowed pages through the bound route (`grid/dataSource.ts:93`) | dead | Unreachable with ExplorerHost |
| grid.query-builder | "Query builder" toggle → QueryBuilderPanel (AND/OR tree, depth 16, saved queries, plugin filter presets) | app/src/frontend/src/grid/QueryBuilderPanel.tsx:250-390 (calls :266, :278, :290) | `GET /api/queries` (`queries.list`), `POST /api/queries` (`queries.create`), `DELETE /api/queries` (`queries.delete`) | dead | Unreachable with ExplorerHost |
| grid.export-handoff | DataGrid "export" button → "Export handoff" dialog + "Copy payload" | app/src/frontend/src/surfaces/explorers/ExplorerHost.tsx:169-182, 305-320; grid/DataGrid.tsx:317-325 | none — clipboard JSON | dead | Stops at clipboard; never calls `exports.create`/`exports.download` (C1) |

### Single-page & standalone tools (mounted in ToolsView → ToolsWorkspace — reachable)

Catalog rail: `tools/ToolsWorkspace.tsx:49-62` (12 instruments), frame `shell/ToolsView.tsx:16-28`. All tool calls go through `tools/shared/useToolCall.ts:47-75` → `api.request(<route key>)` → literal method+path below.

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| tools.workspace | Tools rail "Instrument bench" (12 entries) | app/src/frontend/src/tools/ToolsWorkspace.tsx:49-103 | none | local-only | Active-tool state local |
| tools.single-page-analyzer | Single-Page Analyzer → "Analyze" | app/src/frontend/src/surfaces/single-page/SinglePageAnalyzer.tsx:99-106 (call :100) | `POST /api/analysis/single-page` (`analysis.singlePage`) | wired | Findings, element inventory, WAF verdict, scope diagnostics |
| tools.single-page.render-inspect | SPA "Inspect rendered artifact" action | app/src/frontend/src/surfaces/single-page/SinglePageAnalyzer.tsx:108-116 (call :110) | `POST /api/tools/render-test` (`tools.renderTest`, body `{url, picker:true}`) | wired | |
| tools.single-page.severity-filter | SPA severity chips + sort | app/src/frontend/src/surfaces/single-page/SinglePageAnalyzer.tsx:118-132 | none | local-only | Client-side filter/sort of returned findings |
| tools.single-page.scope-staging | SPA "send to scope" include/exclude lists | app/src/frontend/src/surfaces/single-page/SinglePageAnalyzer.tsx:139-143 | none | local-only | Lists are local state only (no scope-save route called) |
| tools.selector-picker | Render Tester / SPA / Scope "Selector picker" over sandboxed artifact | app/src/frontend/src/tools/picker/SelectorPicker.tsx:67-300 | none (sandboxed iframe + CSS/XPath match, `picker/selectorEngine.ts`, `picker/xpath.ts`) | local-only | |
| tools.extraction-drafts | "Custom extraction rule drafts" panel + rule JSON | app/src/frontend/src/tools/picker/ExtractionDrafts.tsx:2-75 | none | local-only | "emits no rule-write route, so drafts are local until pasted" |
| tools.serp-preview | "SERP & Social Preview" (snippet + 2 social cards, device toggle, pixel budgets) | app/src/frontend/src/tools/serp/SerpPreview.tsx:358-470 | none (bundled font metrics, `tools/serp/fontMetrics.ts`) | local-only | Data: URL image only; no remote content |
| tools.render-tester | "Render Tester + Picker" → Run | app/src/frontend/src/tools/RenderTesterTool.tsx:58, 75-175 | `POST /api/tools/render-test` (`tools.renderTest`) | wired | Screenshot, console, WAF verdict |
| tools.robots-tester | "Robots.txt Tester" → Run | app/src/frontend/src/tools/RobotsTesterTool.tsx:45, 59-145 | `POST /api/tools/robots-test` (`tools.robotsTest`) | wired | |
| tools.sitemap-generator | "Sitemap Generator" → Generate | app/src/frontend/src/tools/SitemapGeneratorTool.tsx:75, 96-215 | `POST /api/tools/sitemap-generate` (`tools.sitemapGenerate`) | wired | Writes file locally on backend; disabled until an audit is picked (:187) |
| tools.scope-preview | "Content Scope Preview" → Load artifact + Run | app/src/frontend/src/tools/ScopePreviewTool.tsx:43 (`tools.renderTest`), :53 (`tools.scopePreview`), 66-185 | `POST /api/tools/render-test`, `POST /api/tools/scope-preview` | wired | |
| tools.rewrite-preview | "Rewrite & Host-Mapping Preview" → Run | app/src/frontend/src/tools/RewritePreviewTool.tsx:51, 68-215 | `POST /api/tools/rewrite-preview` (`tools.rewritePreview`) | wired | Dry-run regex rules + host mappings |
| tools.llms-generator | "llms.txt Generator" tab → Generate | app/src/frontend/src/tools/LlmsTool.tsx:66, 73-135 | `POST /api/tools/llms-generate` (`tools.llmsGenerate`) | wired | Disabled until an audit is picked (:111) |
| tools.llms-validator | "llms.txt Validator" tab → Validate | app/src/frontend/src/tools/LlmsTool.tsx:151, 158-215 | `POST /api/tools/llms-validate` (`tools.llmsValidate`) | wired | Body or URL input |
| tools.url-check | "URL Check" → Run | app/src/frontend/src/tools/UrlCheckTool.tsx:34, 42-120 | `POST /api/tools/url-check` (`tools.urlCheck`) | wired | Status/redirects/headers/scope/WAF |
| tools.structured-data | "Structured Data Checker" → Run | app/src/frontend/src/tools/StructuredDataTool.tsx:38, 48-110 | `POST /api/tools/structured-data-check` (`tools.structuredDataCheck`) | wired | JSON-LD & Microdata |
| tools.query-builder | "Query Builder" (standalone): load/save/update/delete saved queries | app/src/frontend/src/tools/QueryBuilderTool.tsx:259 (`queries.list`), :278 (`queries.update`), :281 (`queries.create`), :290 (`queries.delete`), 303-380 | `GET/POST/PUT/DELETE /api/queries` | wired | The ONLY surface that calls `queries.update` |
| tools.destinations | "Export Destinations": list/new/save/toggle/test/delete | app/src/frontend/src/tools/DestinationsManager.tsx:73 (`destinations.list`), :85 (`destinations.create`), :99/:118 (`destinations.update`), :127 (`destinations.test`), :133 (`destinations.delete`), 142-300 | `GET/POST /api/destinations`, `PUT/DELETE /api/destinations/{id}`, `POST /api/destinations/{id}/test` | wired | Credentials masked; disabled destinations skipped by delivery |
| tools.destinations.refresh | Destinations "Refresh" button | app/src/frontend/src/tools/DestinationsManager.tsx:147 | `GET /api/destinations` (`destinations.list`) | wired | |

### Graph surfaces (unmounted — see B3)

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| graphs.link-network | VisualizationSurface "Internal link network" → LinkGraphSurface | app/src/frontend/src/graphs/LinkGraphSurface.tsx:45-187 (fed by surfaces/dashboards/VisualizationSurface.tsx:186-197) | `GET /api/audits/{id}/graph` (`audits.graph`, type=link-network, LOD-bounded) via dashboardData.ts:175-180 | dead | Weighted equity toggle, status/depth color, LOD banner |
| graphs.semantic-network | VisualizationSurface "Semantic similarity network" → SemanticGraphSurface | app/src/frontend/src/graphs/SemanticGraphSurface.tsx:34-134 (fed by VisualizationSurface.tsx:202-211) | `GET /api/audits/{id}/semantics/clusters` (`semantics.clusters`), `GET /api/audits/{id}/semantics/link-opportunities` (`semantics.linkOpportunities`) via dashboardData.ts:182-186 | dead | Opportunity overlay toggle |
| graphs.viz-controls | Weighted/color-mode/labels/iterations/node-budget/drill-radius/"Reload aggregates" | app/src/frontend/src/surfaces/dashboards/VisualizationSurface.tsx:70-182 (reload :171-178) | Reload re-triggers the 3 aggregate calls above | dead | Whole surface unmounted |
| graphs.drill-down | LinkGraph "Drill into neighborhood"/"Reset" | app/src/frontend/src/graphs/LinkGraphSurface.tsx:130-141 | none (client-side neighborhood subgraph, `graphs/graphSampling.ts:85`) | dead | |
| graphs.layout | Force layout (worker + sync fallback) | app/src/frontend/src/graphs/layoutClient.ts:37-107 | none | local-only | Pure client computation (Web Worker) |

### Plugins UI (surface unmounted — see B4)

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| plugins.list | Shell bootstrap (store `loadPlugins`) | app/src/frontend/src/shell/ShellContext.tsx:183; store/crawlForgeStore.ts:274-278 | `GET /api/plugins` (`plugins.list`) | wired | Only reachable plugins call |
| plugins.manage.registry | PluginManagementSurface table (state/integrity badges) | app/src/frontend/src/plugins/PluginManagementSurface.tsx:60-221 | `GET /api/plugins` (via pluginRegistry.ts:467) | dead | Surface not mounted anywhere |
| plugins.manage.rescan | "Rescan local directories" button | app/src/frontend/src/plugins/PluginManagementSurface.tsx:86-93, 116 (registry pluginRegistry.ts:437-449) | `POST /api/plugins/rescan` (`plugins.rescan`) | dead | |
| plugins.manage.enable-disable | Per-row enable Switch | app/src/frontend/src/plugins/PluginManagementSurface.tsx:75-84, 189-195 (registry :358-370) | `PUT /api/plugins/{id}` (`plugins.update`, body `{enabled}`) | dead | |
| plugins.manage.grants | "Grants" panel → Grant/Revoke `read-data`/`ui-contributions` | app/src/frontend/src/plugins/PluginManagementSurface.tsx:229-251 (registry :372-409) | `PUT /api/plugins/{id}` (`plugins.update`, `grantPermissions`/`revokePermissions`) | dead | `commands` permission shown "locked (400)" — never offered |
| plugins.manage.remove | Per-row "Remove" button | app/src/frontend/src/plugins/PluginManagementSurface.tsx:95-104, 212 (registry :451-465) | `DELETE /api/plugins/{id}` (`plugins.delete`) | dead | |
| plugins.manage.trust | "Trust workspace plugin" button | app/src/frontend/src/plugins/PluginManagementSurface.tsx:199-208 (registry :337) | none — local consent state (native trust dialog at scan: electron/src/main.ts:286-300) | dead | |
| plugins.manage.network-grant | "Grant network to selected destinations" / "Revoke network" | app/src/frontend/src/plugins/PluginManagementSurface.tsx:259-308 (registry :411-435) | none — local consent state (by design "network destination grants as local consent state") | dead | See C6 |
| plugins.ui-bridge | Plugin sandbox API `window.cf.request(...)` | app/src/frontend/src/plugins/sandboxFrame.ts:86-95 | Internal bridge channels: `data.getState`, `data.getSlice`, `data.subscribe`, `ui.registerTab`, `ui.registerColumnFormatter`, `ui.registerFilterPreset`, `network.fetch` | local-only | `network.fetch` is restricted to granted destination rules (sandboxBridge.ts:212-229) |
| plugins.hook-bus | Plugin lifecycle hooks + 1 s coalesced slice relay | app/src/frontend/src/store/pluginBus.ts:40-43, 103-230 | none (local store relay) | local-only | |
| plugins.grid-contributions | Column formatters / filter presets contributed into grids | app/src/frontend/src/grid/contributions.ts:22-70 | none | dead | Consumed only by the unmounted ExplorerHost |

### Store / realtime

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| hub.realtime.connection | Connection pill + all live surfaces | app/src/frontend/src/store/hubClient.ts:43 (`HUB_ROUTE_PATH = '/hubs/realtime'`), 233+ | SignalR WebSocket `/hubs/realtime` (session token rides access-token + `X-CrawlForge-Session`) | wired | Auto-reconnect |
| hub.realtime.subscribe | openAudit / palette resubscribe | app/src/frontend/src/store/hubClient.ts:105-110, 139+ | hub method `Subscribe(string[] auditIds)` | wired | |
| hub.realtime.unsubscribe | (none) | app/src/frontend/src/store/hubClient.ts:182; contract contracts/hub/client.ts:86 | hub method `Unsubscribe(string[] auditIds)` | dead | Never invoked by any surface (B9) |
| live.crawl-telemetry | ReportView/StatusBar live counters | app/src/frontend/src/store/liveProgress.ts:137-325 (ingests) | hub events `crawl-progress`, `url-batch`, `findings-updated`, `waf-blocks`, `matrix-row-validated`, `embedding-progress/-completed`, `log-import-progress/-completed`, `host-throttle`, `backend-lifecycle` | wired | Bounded (32 live audits, 16 imports) |
| live.activity-buffer | Activity feed ring buffer + plugin relay | app/src/frontend/src/store/hubDemux.ts:86-303 | All 18 hub server events demuxed; `schedule-fired`, `schedule-run-completed`, `export-delivery` reach ONLY this buffer (no dedicated UI) | wired | ≤1 000 entries, drop counter |
| live.system-health | Status bar heartbeat + store slice | app/src/frontend/src/store/hubDemux.ts:254-263 | hub event `system-health` (payload = `SystemHealthSnapshot`) | wired | REST `GET /api/system/health` (`system.health`) is never called (A39) |
| store.windowed-pages | Windowed LRU caches for url/finding/link/ngram pages | app/src/frontend/src/store/crawlForgeStore.ts:152-165, 247-258 | `GET /api/audits/{id}/urls|findings|links`, `GET /api/audits/{id}/ngrams` — invoked only from charts/crossFilter.ts:95-98 | dead | Loader chain unreachable (B9) |

### Electron-native

Window catalog: `app/src/electron/src/windows/window-catalog.ts:39-120`. IPC channels: `app/src/electron/src/preload/bridge.ts:59-69` + inline `crawlforge:get-bridge`/`crawlforge:hydrated`. Preload exposure: `app/src/electron/src/preload.ts:39-56` (`window.crawlForgeShell`) and :59-72 (`window.crawlForge`).

| Capability key | Surface entry point | file:line | Backend calls (exact routes/methods/channels) | Status | Notes |
|---|---|---|---|---|---|
| electron.window.splash | Startup splash (frameless) | app/src/electron/src/startup/startup-orchestrator.ts:59; window-catalog.ts:40-49 | none | wired | Closed once main hydrates |
| electron.window.main | Main hub window (route `hub`) | app/src/electron/src/startup/startup-orchestrator.ts:69-70; main.ts:197-246 | none (loads local UI assets; renderer then calls REST/hub) | wired | Singleton; second-instance focuses |
| electron.window.recovery | Recovery window on backend failure | app/src/electron/src/startup/startup-orchestrator.ts:63-66 | Recovery service probes `GET /api/health`; purge `DELETE /api/diagnostics/artifacts` | wired | Renderer has no recovery UI (B14) |
| electron.window.report | Catalog kind `report` (per-audit) | app/src/electron/src/windows/window-catalog.ts:60-69 | — | dead | No opener: no IPC/menu/button creates it |
| electron.window.visualization | Catalog kind `visualization` | app/src/electron/src/windows/window-catalog.ts:70-79 | — | dead | |
| electron.window.analyzer | Catalog kind `analyzer` | app/src/electron/src/windows/window-catalog.ts:80-89 | — | dead | |
| electron.window.tools | Catalog kind `tools` (per-key) | app/src/electron/src/windows/window-catalog.ts:90-99 | — | dead | |
| electron.window.settings | Catalog kind `settings` | app/src/electron/src/windows/window-catalog.ts:100-109 | — | dead | |
| electron.ipc.get-bridge | Preload handshake `crawlforge:get-bridge` | app/src/electron/src/preload.ts:59-72; main.ts:375-380 | IPC `crawlforge:get-bridge` → `window.crawlForge { backendBaseUrl, getSessionToken }` | wired | Consumed by App.tsx:24-29 |
| electron.ipc.hydrated | `signalHydrated()` → `crawlforge:hydrated` | app/src/electron/src/preload.ts:55; main.ts:381-385 | IPC `crawlforge:hydrated` | dead | No renderer code calls `signalHydrated` (B13) |
| electron.ipc.file-dialog | `showFileDialog` (open-file/open-directory/save-file) | app/src/electron/src/preload.ts:40; main.ts:324-339 | IPC `crawlforge:file-dialog` → `dialog.showSaveDialog`/`showOpenDialog` | dead | Zero renderer consumers (B12) |
| electron.ipc.notification | `showNotification` | app/src/electron/src/preload.ts:41-42; main.ts:342-346 | IPC `crawlforge:notification` → native `Notification` | dead | Zero renderer consumers |
| electron.ipc.taskbar-progress | `setTaskbarProgress` | app/src/electron/src/preload.ts:43-44; main.ts:348-351 | IPC `crawlforge:taskbar-progress` → `window.setProgressBar` | dead | Zero renderer consumers |
| electron.ipc.window-controls | `minimizeWindow`/`maximizeWindow`/`closeWindow` | app/src/electron/src/preload.ts:45-47; main.ts:353-366 | IPC `crawlforge:window-minimize|-maximize|-close` | dead | Zero renderer consumers (frame:true windows use native chrome) |
| electron.ipc.backend-restart | `restartBackend` | app/src/electron/src/preload.ts:48; main.ts:368-370 | IPC `crawlforge:backend-restart` | dead | Supervisor restart used internally; no renderer button |
| electron.ipc.diagnostics-purge | `purgeDiagnostics` | app/src/frontend: none; preload.ts:49; main.ts:371 | IPC `crawlforge:diagnostics-purge` → recovery `purgeDiagnostics` | dead | Settings purge button uses REST `DELETE /api/diagnostics/artifacts` instead |
| electron.ipc.backend-status | `onBackendStatus` listener | app/src/electron/src/preload.ts:50-54; main.ts:313-315 | IPC `crawlforge:backend-status` (push) | dead | Zero renderer consumers |
| electron.dialog.plugin-trust | Native "Plugin trust" message box during plugin scan | app/src/electron/src/main.ts:286-300; plugins/plugin-scanner.ts:49 | none (native dialog) | wired | Reachable at startup scan of untrusted workspace plugins |
| electron.shell.open-external | Any http(s) link opening from the app | app/src/electron/src/main.ts:387-393 | none (`shell.openExternal`) | wired | Enforces external-browser boundary |
| electron.supervisor.health-probe | Backend readiness + 2 s health loop | app/src/electron/src/supervisor/backend-supervisor.ts:270, 301 | `GET /api/health` (hardcoded string, matches `health.get`) | wired | Not resolved through routes.ts (C8) |
| electron.supervisor.shutdown | Graceful backend stop on quit | app/src/electron/src/supervisor/backend-supervisor.ts:162-170 | `POST /api/system/shutdown` (hardcoded string) | wired | **No row in routes.ts** — server capability with zero client contract (C9) |
| electron.recovery.purge | Recovery "purge diagnostics" path | app/src/electron/src/recovery/recovery-service.ts:99-105 | `DELETE /api/diagnostics/artifacts` (hardcoded string, matches `diagnostics.purgeArtifacts`) | wired | |
| electron.window-state | Persisted window placement (`window-state.json`) | app/src/electron/src/windows/window-manager.ts:141+; window-catalog.ts:159-174 | none (local file) | local-only | |
| electron.app-lifecycle | second-instance focus, window-all-closed, activate reopen, before-quit | app/src/electron/src/main.ts:405-435 | none | wired | macOS close-hides convention |

---

## 3. Section A — Unused API client wrappers

`contracts/api/*` exports are **types-only** except the frozen route catalog (`routes.ts`) and the typed-call contract (`client.ts`); the callable wrapper surface is `LoopbackApiClient.request(<route key>)` which resolves `API_ROUTES[key]` to the literal method+path. "Unused wrapper" = a route key that no surface, store action, tool, or electron code ever invokes (grep of each key across `app/src/frontend/src` shows references only in `contracts-map.ts`/`routes.ts`). **46 of 108 route keys have no UX reach.**

| Wrapper (route key) | file:line | Route it would call |
|---|---|---|
| health.get | contracts/api/routes.ts:35 | `GET /api/health` |
| version.get | contracts/api/routes.ts:36 | `GET /api/version` |
| metrics.get | contracts/api/routes.ts:37 | `GET /metrics` |
| projects.get | contracts/api/routes.ts:45 | `GET /api/projects/{id}` |
| projects.update | contracts/api/routes.ts:46 | `PUT /api/projects/{id}` |
| projects.delete | contracts/api/routes.ts:47 | `DELETE /api/projects/{id}` |
| configurations.get | contracts/api/routes.ts:50 | `GET /api/configurations/{id}` |
| configurations.update | contracts/api/routes.ts:51 | `PUT /api/configurations/{id}` |
| configurations.delete | contracts/api/routes.ts:52 | `DELETE /api/configurations/{id}` |
| audits.get | contracts/api/routes.ts:55 | `GET /api/audits/{id}` |
| audits.reaudit | contracts/api/routes.ts:59 | `POST /api/audits/{id}/reaudit` |
| audits.compare | contracts/api/routes.ts:67 | `GET /api/audits/compare` |
| exports.create | contracts/api/routes.ts:69 | `POST /api/exports` |
| exports.download | contracts/api/routes.ts:70 | `GET /api/exports/{id}/file` |
| schedules.list | contracts/api/routes.ts:71 | `GET /api/projects/{id}/schedules` |
| schedules.create | contracts/api/routes.ts:72 | `POST /api/projects/{id}/schedules` |
| schedules.get | contracts/api/routes.ts:73 | `GET /api/schedules/{id}` |
| schedules.update | contracts/api/routes.ts:74 | `PUT /api/schedules/{id}` |
| schedules.delete | contracts/api/routes.ts:75 | `DELETE /api/schedules/{id}` |
| schedules.enable | contracts/api/routes.ts:76 | `POST /api/schedules/{id}/enable` |
| schedules.disable | contracts/api/routes.ts:77 | `POST /api/schedules/{id}/disable` |
| schedules.runNow | contracts/api/routes.ts:78 | `POST /api/schedules/{id}/run-now` |
| schedules.listRuns | contracts/api/routes.ts:79 | `GET /api/schedules/{id}/runs` |
| dictionary.list | contracts/api/routes.ts:85 | `GET /api/dictionary` |
| dictionary.add | contracts/api/routes.ts:86 | `POST /api/dictionary` |
| dictionary.remove | contracts/api/routes.ts:87 | `DELETE /api/dictionary` |
| audits.compareContent | contracts/api/routes.ts:88 | `GET /api/audits/compare/content` |
| audits.listSearchResults | contracts/api/routes.ts:91 | `GET /api/audits/{id}/search-results` |
| audits.listSnippetResults | contracts/api/routes.ts:92 | `GET /api/audits/{id}/snippet-results` |
| audits.listTechnologies | contracts/api/routes.ts:93 | `GET /api/audits/{id}/technologies` |
| suppressions.create | contracts/api/routes.ts:95 | `POST /api/projects/{id}/suppressions` |
| suppressions.delete | contracts/api/routes.ts:96 | `DELETE /api/projects/{id}/suppressions/{key}` |
| waf.listSignatures | contracts/api/routes.ts:110 | `GET /api/waf-signatures` |
| waf.createSignature | contracts/api/routes.ts:111 | `POST /api/waf-signatures` |
| waf.updateSignature | contracts/api/routes.ts:112 | `PUT /api/waf-signatures/{key}` |
| waf.deleteSignature | contracts/api/routes.ts:113 | `DELETE /api/waf-signatures/{key}` |
| hosts.override | contracts/api/routes.ts:115 | `POST /api/audits/{id}/hosts/{host}/override` |
| ngrams.cannibalization | contracts/api/routes.ts:117 | `GET /api/audits/{id}/ngrams/cannibalization` |
| system.health | contracts/api/routes.ts:124 | `GET /api/system/health` |
| matrix.upload | contracts/api/routes.ts:126 | `POST /api/audits/{id}/redirect-matrix` |
| logs.import | contracts/api/routes.ts:129 | `POST /api/projects/{id}/logs/import` |
| logs.list | contracts/api/routes.ts:130 | `GET /api/projects/{id}/logs` |
| logs.delete | contracts/api/routes.ts:131 | `DELETE /api/logs/{id}` |
| logs.verifyDns | contracts/api/routes.ts:133 | `POST /api/logs/{id}/verify-dns` |
| semantics.run | contracts/api/routes.ts:134 | `POST /api/audits/{id}/semantics/run` |
| semantics.similar | contracts/api/routes.ts:135 | `GET /api/audits/{id}/semantics/similar` |

Whole domains with zero UX reach: **schedules (9 operations), dictionary (3), suppressions (2), WAF signatures (4), log imports (4), exports (2), semantics jobs (2)**, plus project/config CRUD update+delete, audit detail/reaudit/compare, n-gram cannibalization, host overrides, matrix upload, health/version/metrics probes. Note the DOM adapter `adapterKind: 'search'` exists in `ExplorerHost.tsx:54-58` for `SearchResultsResponse`, but no explorer definition binds it — `audits.listSearchResults`/`listSnippetResults` were clearly intended for explorers that were never registered.

---

## 4. Section B — Stubs & dead affordances

| What | file:line | Evidence (quote) | Impact |
|---|---|---|---|
| B1. Dashboard surfaces never mounted | surfaces/dashboards/* (exported at surfaces/dashboards/index.ts:9-23); promise at shell/AuditViews.tsx:316-320 | "URL / finding / link grids, charts, and the link graph mount into this workspace from the report workpieces" | 8 dashboard capabilities (incl. cross-filter, chart export) have zero reachable entry |
| B2. All 11 explorers + ExplorerHost never mounted | surfaces/explorers/index.ts:36-57; no importer anywhere | `EXPLORERS`/`EXPLORER_NAV` referenced only in the barrel | 16 explorer/grid capabilities unreachable; grid/query-builder/export-handoff with them |
| B3. Visualization + graph surfaces never mounted | surfaces/dashboards/VisualizationSurface.tsx:48; graphs/LinkGraphSurface.tsx:45; graphs/SemanticGraphSurface.tsx:34 | No component imports `VisualizationSurface` outside its own module | Link/semantic network UX unreachable despite wired loaders |
| B4. Plugin management surface never mounted | plugins/PluginManagementSurface.tsx:60 | Exported at plugins/index.ts:95-97; zero renderers | All plugin management affordances unreachable (only bootstrap `plugins.list` runs) |
| B5. "Export handoff" stops at clipboard | surfaces/explorers/ExplorerHost.tsx:305-320 | "This payload carries the current selection, filters, sort chain, and locked query tree into the export flow." + "Copy payload" | The word "export" in the UI produces only a clipboard JSON; `exports.create`/`exports.download` never called |
| B6. Chart "export" is a local PNG only | charts/EChart.tsx:31, 96-100 | "When set, shows an export-to-image button (local download only)" | No xlsx/csv/html/pdf export anywhere in the UI despite `ExportFormat` contract |
| B7. Extraction rule drafts have no write path | tools/picker/ExtractionDrafts.tsx:2-5 | "the panel emits no rule-write route, so drafts are local until pasted" | Manual copy/paste required; `configurations.update` (the obvious write route) unused (A8) |
| B8. Five Electron window kinds have no opener | electron/src/windows/window-catalog.ts:60-109 | Catalog defines `report`/`visualization`/`analyzer`/`tools`/`settings` windows; `WindowManager.open` called only for splash/main/recovery (startup-orchestrator.ts:59-70) | Per-audit report windows, dedicated analyzer/tools windows, settings window all unreachable |
| B9. Cross-filter → windowed-page pipeline unwired | charts/crossFilter.ts:87-98; store/crawlForgeStore.ts:247-258 | `loadUrlPage/loadFindingPage/loadLinkPage/loadNgramPage` referenced only from crossFilter.ts; `applyChartSelection` has no caller | Even if dashboards were mounted, the promised chart→explorer wiring has no driver |
| B10. `loadHosts` never invoked | store/crawlForgeStore.ts:260-272 | `loadHosts(auditId)` defined on the actions interface; grep finds no call site | ReportView host table relies solely on `host-throttle` hub events; REST `hosts.list` unreachable from shell |
| B11. `hub.unsubscribe` never invoked | store/hubClient.ts:182; contracts/hub/client.ts:86 | Facade method exists; no surface calls it | Per-audit channels accumulate until connection stop |
| B12. Entire `window.crawlForgeShell` bridge unconsumed | electron/src/preload.ts:39-56 | grep of `crawlForgeShell|showFileDialog|restartBackend|purgeDiagnostics|taskbarProgress` across `app/src/frontend/src` = 0 matches | 7 native capabilities (dialogs, notifications, taskbar, window controls, backend restart, diagnostics purge) are dead IPC |
| B13. `signalHydrated` never called | electron/src/preload.ts:55; main.ts:381-385 | `crawlforge:hydrated` handler waits; no renderer sends it | Startup hydration handshake effectively times out/falls back |
| B14. Window `?route=` param ignored by renderer | electron/src/main.ts:236-245 vs app/src/frontend/src/App.tsx:22-33 | Main loads `index.html?route=<kind>`; `App` always mounts `CrawlForgeShell` with no router | splash/recovery/report/… windows would all render the same hub shell; recovery UI exists nowhere |
| B15. Empty-state copy references non-existent surfaces | surfaces/explorers/analysisExplorers.tsx:88, :146, :391; coreExplorers.tsx:349 | "Import server logs from the project Log Imports surface", "Run the local embedding job (Semantic analysis)", "Upload a migration mapping … to seed the matrix" | Users are pointed at screens that do not exist (see Observations #5) |
| B16. `disabled`/placeholder affordances (preconditions, not defects) | tools/SitemapGeneratorTool.tsx:187; tools/LlmsTool.tsx:111; tools/ScopePreviewTool.tsx:148; grid/QueryBuilderPanel.tsx:339,359 | `disabled={!auditId}` / `disabled={!validation.valid}` etc. | Legitimate gating; noted for completeness — no unconditional disabled controls found |
| B17. No TODO/FIXME/not-implemented markers in shipped UI | grep `TODO\|FIXME\|not implemented\|placeholder\|mock\|fake\|coming soon` over frontend+electron src | Only input `placeholder=` attrs and test-injection comments match | The gaps are structural (unmounted surfaces), not marked stubs |

---

## 5. Section C — API-bypassing UI

| UI feature | file:line | What it does instead of the API | Expected backend capability (per contracts) |
|---|---|---|---|
| C1. Explorer "export handoff" | surfaces/explorers/ExplorerHost.tsx:169-182, 305-320 | Serializes selection/filters/sort/query-tree to JSON → clipboard | `exports.create` (`POST /api/exports`) + `exports.download` (`GET /api/exports/{id}/file`), `ExportFormat` xlsx/csv/html/pdf |
| C2. Chart export-to-image | charts/EChart.tsx:96-100 (`downloadDataUrl`) | Local PNG data-URL anchor download | `exports.create` (chart datasets) — export pipeline unused |
| C3. SERP pixel truncation & social-card preview | tools/serp/SerpPreview.tsx:358-470; tools/serp/fontMetrics.ts | Pure client-side text measurement from bundled font metrics | `SerpMetrics` exists in the URL-detail DTO (audits.ts:190-198) and `analysis.singlePage` computes SERP fields server-side; the tool deliberately computes locally ("no network, no remote fonts") |
| C4. SPA severity filter + sort | surfaces/single-page/SinglePageAnalyzer.tsx:118-132 | Client-side filter/sort of response findings | Server list filtering exists for audit findings (`audits.listFindings` query) but single-page responses are unfiltered |
| C5. Extraction rule drafts | tools/picker/ExtractionDrafts.tsx:21-28 (`draftsToJson`) | Emits JSON text for manual paste into a configuration | `configurations.update` (`PUT /api/configurations/{id}`) — unused (A8) |
| C6. Plugin network destination grants | plugins/pluginRegistry.ts:411-435 | Local consent state (in-memory registry), not persisted | `plugins.update` (`PUT /api/plugins/{id}`) persists read-data/ui-contributions but network grants bypass it (documented "local consent state") |
| C7. Shell window/layout persistence | shell/windowState.ts:59-78 | localStorage `crawlforge.shell.windowState.v1` | `settings.update` persists other UI prefs; window state never round-trips through `GET /api/settings` |
| C8. Electron hardcoded health probe | electron/src/supervisor/backend-supervisor.ts:270, 301 | Literal `` `${baseUrl}/api/health` `` strings | `health.get` route key exists but the main process never uses the route catalog |
| C9. Electron hardcoded shutdown call | electron/src/supervisor/backend-supervisor.ts:162-170 | Literal `` `${baseUrl}/api/system/shutdown` `` | **Not present in `routes.ts` at all** — backend endpoint with no client contract row (cross-ref candidate for the API inventory) |
| C10. Electron hardcoded diagnostics purge | electron/src/recovery/recovery-service.ts:99-105 | Literal `` `${status.baseUrl}/api/diagnostics/artifacts` `` DELETE | `diagnostics.purgeArtifacts` route key exists; main bypasses the catalog |
| C11. Renderer JS heap | store/frontendHeap.ts:17; shell/StatusBar.tsx:136-141 | `performance.memory`-style local measurement | `system.health` snapshot is server-side; frontend heap deliberately never sent (documented) |

---

## 6. Observations (gap-audit feed)

1. **The report workspace is a promise, not a product.** `ReportView` (shell/AuditViews.tsx:311-321) tells users that "URL / finding / link grids, charts, and the link graph mount into this workspace", yet none of the 30+ dashboard/explorer/visualization capabilities are mounted by any component. All of them are complete, route-wired, tested-looking modules exported from barrels — cross-reference should treat `audits.listUrls`, `audits.listFindings`, `audits.summary`, `audits.graph`, `analytics.*`, `geo.*`, `waf.listBlocks`, `matrix.results`, etc. as **implemented-but-unreachable** (UX gap, not API gap).
2. **46/108 route keys have zero UX callers** (Section A). Entire user-visible feature areas from the docs — schedules (9 routes!), custom dictionary, finding suppressions, WAF signature management, server-log imports, export jobs — have no screen at all. The hub nav description even advertises "Projects, dashboards, schedules" (NavigationRail.tsx:27) while no schedules surface exists.
3. **The export story is disconnected end-to-end.** The only "export" affordances in the entire UI are a clipboard handoff (ExplorerHost) and local PNG chart downloads; `exports.create`/`exports.download` and the `ExportFormat` contract are untouched. Destinations can be CRUD'd and tested (`tools.destinations`) but nothing can ever be delivered to them from the UI except via schedule completion actions — which also have no UI.
4. **The Electron native layer is built but dark.** All 9 IPC channels of `window.crawlForgeShell` and 5 of 8 window kinds have no renderer consumer; `signalHydrated` is never sent; and the renderer ignores the `?route=` parameter main attaches to every window, so even the catalogued windows would all render the identical hub shell. Native file dialogs would be the natural pickers for log import / matrix upload / sitemap save — none are hooked up (contrast: sitemap/llms "written locally" by the backend, surfaced only as a filename+path text).
5. **UI copy references phantom surfaces** (parity mismatches within the UI itself): "project Log Imports surface" (analysisExplorers.tsx:88), "Run the local embedding job (Semantic analysis)" (:146), "Upload a migration mapping" (:391), "Configure custom extraction rules in the audit configuration" (coreExplorers.tsx:349) — each points at a screen or action that does not exist, and each maps to an unused route (`logs.import`, `semantics.run`, `matrix.upload`, `configurations.update`). The command palette is the only place audit-open + resubscribe + refresh live (no buttons elsewhere), and palette project entries only bounce to the hub (no project detail view exists — `projects.get` unused).
6. **Realtime is the healthiest layer**: all 18 hub events are demuxed into live slices and the activity feed; only `schedule-fired`/`schedule-run-completed`/`export-delivery` land in the activity buffer with no dedicated UI, and `hub.unsubscribe` is never called (channels accumulate per session). REST `system.health` is dead because the hub heartbeat covers it — a benign duplicate, but it will cross-reference as "API without UX".
