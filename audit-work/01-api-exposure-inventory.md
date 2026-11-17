# 01 — API Exposure Inventory (CrawlForge)

- **Repo root**: `E:\Crawlforge`
- **Date**: 2026-11-17 (Africa/Johannesburg)
- **Scope**: Every programmatic capability the CrawlForge backend exposes across four channels — REST endpoints (Controllers + Host-mapped minimal APIs), SignalR hub methods/events, MCP surface (tools/prompts/resources + transport/dispatcher/token), and Electron IPC/preload channels. Excluded: `bin/`, `obj/`, `node_modules/`, `dist/`, `packaging\vendor`. Read-only audit; all route templates and names are literal quotes from source.
- **Totals**:
  - **REST actions: 114** (111 controller actions in 26 controller classes + 3 minimal-API endpoints mapped in `CrawlForge.Host\CrawlForgeBackend.cs`)
  - **Hub: 21** (2 client→server methods + 19 server→client pushed events; connection lifecycle handled by hub overrides)
  - **MCP: 8 tools, 5 prompts, 15 resources** (+ 5 transport/protocol exposure rows: loopback HTTP, SSE, session teardown, bearer-token service, stdio adapter)
  - **Electron IPC channels: 11** (9 typed channels in `IPC_CHANNELS` + `crawlforge:get-bridge` + `crawlforge:hydrated`)
- **Auth model (all channels)**: single-user desktop; no `[Authorize]`/roles/identity anywhere. REST + hub + `/metrics` are gated by a per-launch session token (`X-CrawlForge-Session` header, or SignalR `access_token` query) via `SessionTokenMiddleware` (`app\src\backend\CrawlForge.Host\Hosting\SessionTokenMiddleware.cs:46`); only `/api/health` is exempt. MCP uses its own local bearer token. Electron IPC trusts the renderer via contextIsolation-typed bridge.

Capability keys are dot-slug, stable, and aligned to the namespace style the UX inventory uses (e.g. `audits.list`, `mcp.tool.crawl-start` → here `mcp.tool.start-audit`).

---

## 1. REST surface

Route = controller `[Route]` prefix + action template. `{id:long}`/`{key}`/`{host}`/`{metric}` are route values. All controller actions return the uniform `{data, pagination?, error?}` envelope (`ApiControllerBase.cs`) except where noted. "Auth" in Notes = `X-CrawlForge-Session` session token unless stated.

### 1.1 Projects

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `projects.list` | GET `api/projects` | ListProjectsAsync | `app\src\backend\CrawlForge.Api\Controllers\ProjectsController.cs:45` | List projects with search + pagination | query: `search`, `offset`, `limit`, `sort` (id/name/createdAt/updatedAt), `order` |
| `projects.create` | POST `api/projects` | CreateProjectAsync | `ProjectsController.cs:82` | Create a project | body: name (required), description, tags, scopeSummary |
| `projects.get` | GET `api/projects/{id:long}` | GetProjectAsync | `ProjectsController.cs:108` | Read one project incl. extension settings | soft-deleted = 404 |
| `projects.update` | PUT `api/projects/{id:long}` | UpdateProjectAsync | `ProjectsController.cs:125` | Subset update of project fields + extensions | body merges contentScope/rewrite/consent/zoneWeights |
| `projects.delete` | DELETE `api/projects/{id:long}` | DeleteProjectAsync | `ProjectsController.cs:170` | Soft delete; history retained | returns deletion confirmation |

### 1.2 Configurations

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `configurations.list` | GET `api/projects/{id:long}/configurations` | ListConfigurationsAsync | `ConfigurationsController.cs:54` | List crawl configurations of a project | project-scoped |
| `configurations.create` | POST `api/projects/{id:long}/configurations` | CreateConfigurationAsync | `ConfigurationsController.cs:85` | Create configuration under project | body: configuration fields |
| `configurations.get` | GET `api/configurations/{id:long}` | GetConfigurationAsync | `ConfigurationsController.cs:137` | Read one configuration | |
| `configurations.update` | PUT `api/configurations/{id:long}` | UpdateConfigurationAsync | `ConfigurationsController.cs:154` | Subset update of configuration fields | |
| `configurations.delete` | DELETE `api/configurations/{id:long}` | DeleteConfigurationAsync | `ConfigurationsController.cs:270` | Delete a configuration | |

### 1.3 Audits (lifecycle & comparison)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `audits.start` | POST `api/audits` | StartAuditAsync | `AuditsController.cs:94` | Start audit from a configuration; returns immediately | body: configurationId (required), listMode, urlList, limitOverrides; freezes config snapshot |
| `audits.list` | GET `api/audits` | ListAuditsAsync | `AuditsController.cs:174` | List audits with project/state filters | query: `projectId`, `state`, `offset`, `limit`, `sort` (id/startedAt/completedAt/state), `order` |
| `audits.get` | GET `api/audits/{id:long}` | GetAuditAsync | `AuditsController.cs:225` | Audit detail with derived progress | |
| `audits.pause` | POST `api/audits/{id:long}/pause` | PauseAuditAsync | `AuditsController.cs:241` | Pause a running audit (validated transition) | Running→Paused only |
| `audits.resume` | POST `api/audits/{id:long}/resume` | ResumeAuditAsync | `AuditsController.cs:249` | Resume a paused audit | Paused→Running only |
| `audits.cancel` | POST `api/audits/{id:long}/cancel` | CancelAuditAsync | `AuditsController.cs:257` | Cancel running/paused audit; terminal states immutable | |
| `audits.reaudit` | POST `api/audits/{id:long}/reaudit` | ReauditAsync | `AuditsController.cs:281` | Start reaudit linked to source | body: `refresh` (false = offline re-analysis of stored artifacts, true = full re-crawl) |
| `audits.compare` | GET `api/audits/compare` | CompareAuditsAsync | `AuditsController.cs:318` | Compare two audits: new/resolved findings + metric deltas | query: `auditIdA`, `auditIdB`, `hostMapping` (staging↔production path alignment) |

### 1.4 Audit explorer

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `audits.urls.list` | GET `api/audits/{id:long}/urls` | ListUrlsAsync | `AuditExplorerController.cs:89` | List URL records with filters + pagination | extensive query filters (status class, indexability, depth…) |
| `audits.urls.get` | GET `api/audits/{id:long}/urls/{urlId:long}` | GetUrlDetailAsync | `AuditExplorerController.cs:190` | Full URL detail incl. additive diagnostics | |
| `audits.links.list` | GET `api/audits/{id:long}/links` | ListLinksAsync | `AuditExplorerController.cs:270` | List link edges | query: source/target/`internal`/domZone filters |
| `audits.findings.list` | GET `api/audits/{id:long}/findings` | ListFindingsAsync | `AuditExplorerController.cs:347` | List findings | query: severity/category/ruleKey/urlId |
| `audits.summary` | GET `api/audits/{id:long}/summary` | GetSummaryAsync | `AuditExplorerController.cs:409` | Dashboard summary cards | |
| `audits.charts` | GET `api/audits/{id:long}/charts/{metric}` | GetChartSeriesAsync | `AuditExplorerController.cs:478` | One pre-bucketed chart series | route value `{metric}`; query: `bucketSize`, `bucketBy` |
| `audits.graph` | GET `api/audits/{id:long}/graph` | GetGraphAsync | `AuditExplorerController.cs:508` | Graph projection with render caps + full totals | |
| `audits.scores` | GET `api/audits/{id:long}/scores` | GetScoresAsync | `AuditExplorerController.cs:582` | Per-category and overall scores | |

### 1.5 Content explorer

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `content.compare` | GET `api/audits/compare/content` | CompareContent | `ContentExplorerController.cs:60` | Compare one page's content across two audits | query: audit ids + url |
| `content.extracted.list` | GET `api/audits/{id:long}/extracted-content` | ExtractedContent | `ContentExplorerController.cs:122` | Custom extraction rows (dynamic columns) | |
| `content.extracted.summary` | GET `api/audits/{id:long}/extracted-content/summary` | ExtractionSummary | `ContentExplorerController.cs:181` | Per-rule extraction summaries | |
| `content.search-results.list` | GET `api/audits/{id:long}/search-results` | SearchResults | `ContentExplorerController.cs:211` | Custom search matches with per-item overview | |
| `content.snippets.list` | GET `api/audits/{id:long}/snippet-results` | SnippetResults | `ContentExplorerController.cs:266` | JS snippet output rows (dynamic columns) | |
| `content.technologies.list` | GET `api/audits/{id:long}/technologies` | Technologies | `ContentExplorerController.cs:323` | Technology detections | |

### 1.6 Analytics

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `analytics.overview` | GET `api/audits/{id:long}/analytics` | Analytics | `AnalyticsController.cs:45` | Tag inventory, consent rollup, analytics findings | |
| `analytics.coverage` | GET `api/audits/{id:long}/analytics/coverage` | Coverage | `AnalyticsController.cs:96` | Per-container coverage across indexable pages | |

### 1.7 Crawls / hosts

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `hosts.list` | GET `api/audits/{id:long}/hosts` | ListHosts | `HostsController.cs:43` | Per-host AIMD throttle state | optional adjustment history |
| `hosts.rate-override` | POST `api/audits/{id:long}/hosts/{host}/override` | OverrideHost | `HostsController.cs:77` | Pin or clear manual host rate override | route value `{host}` |

### 1.8 Ngrams

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `ngrams.list` | GET `api/audits/{id:long}/ngrams` | ListNgrams | `NgramsController.cs:43` | N-gram rows (site summaries or per-URL when `urlId` set) | query: `urlId` |
| `ngrams.cannibalization` | GET `api/audits/{id:long}/ngrams/cannibalization` | Cannibalization | `NgramsController.cs:107` | Keyword-cannibalization clusters | |
| `ngrams.gaps` | GET `api/audits/{id:long}/ngrams/gaps` | Gaps | `NgramsController.cs:144` | Topical gaps (anchor demand without on-page supply) | |

### 1.9 Semantics (embeddings)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `semantics.run` | POST `api/audits/{id:long}/semantics/run` | Run | `SemanticsController.cs:52` | Run local embedding job for an audit | returns "completed" synchronously in wire; embeddings from bundled model or loopback daemon only |
| `semantics.similar` | GET `api/audits/{id:long}/semantics/similar` | Similar | `SemanticsController.cs:71` | Top-K similar URLs by cosine similarity | query: `urlId` (required), `k` (default 10), `minSimilarity` |
| `semantics.link-opportunities` | GET `api/audits/{id:long}/semantics/link-opportunities` | LinkOpportunities | `SemanticsController.cs:113` | Semantic internal-link recommendations | suggestions only, never auto-applied |
| `semantics.clusters` | GET `api/audits/{id:long}/semantics/clusters` | Clusters | `SemanticsController.cs:158` | Semantic cannibalization clusters | query: `minSimilarity`, paging |

### 1.10 GEO / AI-bot governance

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `geo.llms-txt` | GET `api/audits/{id:long}/geo/llms-txt` | LlmsTxt | `GeoController.cs:41` | Per-host llms.txt validation results | query: `host`, paging |
| `geo.governance` | GET `api/audits/{id:long}/geo/governance` | Governance | `GeoController.cs:80` | AI-bot governance matrix rows | query: `botName`, `host`, paging |

### 1.11 Logs (server log analysis)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `logs.import` | POST `api/projects/{id:long}/logs/import` | Import | `LogsController.cs:63` | Import a local log file into project aggregates | formats: combined/common, cloudflare, custom |
| `logs.list` | GET `api/projects/{id:long}/logs` | List | `LogsController.cs:100` | List a project's log imports, newest first | paging |
| `logs.delete` | DELETE `api/logs/{id:long}` | Delete | `LogsController.cs:117` | Purge a log import and its aggregates | |
| `logs.correlation` | GET `api/audits/{id:long}/logs/correlation` | Correlation | `LogsController.cs:137` | Correlation view: log hits joined with crawl coverage | |
| `logs.verify-dns` | POST `api/logs/{id:long}/verify-dns` | VerifyDns | `LogsController.cs:179` | Consented DNS bot verification for an import | the only endpoint doing DNS lookups |

### 1.12 Redirect matrix

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `redirect-matrix.upload` | POST `api/audits/{id:long}/redirect-matrix` | Upload | `RedirectMatrixController.cs:58` | Upload migration mapping rows; optional auto-seed of list-mode audit | body: rows (absolute URLs), `autoSeedAudit` |
| `redirect-matrix.results` | GET `api/audits/{id:long}/redirect-matrix/results` | Results | `RedirectMatrixController.cs:109` | Per-row validation verdicts with hop chains | query: `verdict`, paging |

### 1.13 WAF

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `waf.blocks.list` | GET `api/audits/{id:long}/waf-blocks` | ListBlocks | `WafController.cs:61` | List WAF-blocked URLs of an audit | |
| `waf.blocks.summary` | GET `api/audits/{id:long}/waf-blocks/summary` | BlocksSummary | `WafController.cs:125` | Provider/class counts, block rate | |
| `waf.signatures.list` | GET `api/waf-signatures` | ListSignatures | `WafController.cs:156` | Effective signature catalogue (bundled + user) | |
| `waf.signatures.create` | POST `api/waf-signatures` | CreateSignature | `WafController.cs:185` | Create a local user signature | |
| `waf.signatures.update` | PUT `api/waf-signatures/{key}` | UpdateSignature | `WafController.cs:217` | Update a user signature (partial) | route value `{key}` |
| `waf.signatures.delete` | DELETE `api/waf-signatures/{key}` | DeleteSignature | `WafController.cs:257` | Delete user signature, restoring bundled default | |

### 1.14 Suppressions

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `suppressions.create` | POST `api/projects/{id:long}/suppressions` | CreateSuppressionAsync | `SuppressionsController.cs:34` | Suppress rule key and/or finding scope in project | body: ruleKey and/or scope.urlId/scope.urlPattern |
| `suppressions.delete` | DELETE `api/projects/{id:long}/suppressions/{key}` | DeleteSuppressionAsync | `SuppressionsController.cs:71` | Reverse a suppression by stable key | returns inactive state |

### 1.15 Exports

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `exports.create` | POST `api/exports` | CreateAsync | `ExportsController.cs:45` | Create an export job; returns immediately | formats: `xlsx`, `csv`, `html`, `pdf` |
| `exports.get` | GET `api/exports/{id:long}` | GetAsync | `ExportsController.cs:99` | Poll one export job | |
| `exports.download` | GET `api/exports/{id:long}/file` | DownloadAsync | `ExportsController.cs:122` | Stream the generated export file | NOT JSON-enveloped; binary stream (csv = zip) |

### 1.16 Destinations (export delivery)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `destinations.list` | GET `api/destinations` | ListAsync | `DestinationsController.cs:67` | List destinations with pagination | |
| `destinations.create` | POST `api/destinations` | CreateAsync | `DestinationsController.cs:90` | Create a destination | created enabled |
| `destinations.update` | PUT `api/destinations/{id:long}` | UpdateAsync | `DestinationsController.cs:131` | Subset merge update | credentials encrypted at rest |
| `destinations.delete` | DELETE `api/destinations/{id:long}` | DeleteAsync | `DestinationsController.cs:204` | Delete a destination | |
| `destinations.test` | POST `api/destinations/{id:long}/test` | TestAsync | `DestinationsController.cs:223` | Test reachability + credentials | outbound connectivity probe |
| `destinations.deliver` | POST `api/destinations/{id:long}/deliver` | DeliverAsync | `DestinationsController.cs:240` | Deliver a completed export to this destination | additive route |

### 1.17 Schedules

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `schedules.list` | GET `api/projects/{id:long}/schedules` | ListByProjectAsync | `SchedulesController.cs:71` | List schedules of one project | same capability as GET `api/schedules` (cross-project, additive) |
| `schedules.list` | GET `api/schedules` | ListAllAsync | `SchedulesController.cs:95` | List schedules across projects | additive route |
| `schedules.create` | POST `api/projects/{id:long}/schedules` | CreateAsync | `SchedulesController.cs:114` | Create schedule for a project | body: name, configurationId, `cron` (interval:/calendar: grammar), completionActions, enabled |
| `schedules.get` | GET `api/schedules/{id:long}` | GetAsync | `SchedulesController.cs:161` | Read one schedule | |
| `schedules.update` | PUT `api/schedules/{id:long}` | UpdateAsync | `SchedulesController.cs:179` | Subset merge update | |
| `schedules.delete` | DELETE `api/schedules/{id:long}` | DeleteAsync | `SchedulesController.cs:242` | Delete schedule and run history | |
| `schedules.enable` | POST `api/schedules/{id:long}/enable` | EnableAsync | `SchedulesController.cs:267` | Enable + recompute next-due | |
| `schedules.disable` | POST `api/schedules/{id:long}/disable` | DisableAsync | `SchedulesController.cs:274` | Disable + clear next-due | |
| `schedules.run-now` | POST `api/schedules/{id:long}/run-now` | RunNowAsync | `SchedulesController.cs:281` | Manual trigger; returns started audit id | 409 on guard refusal |
| `schedules.runs.list` | GET `api/schedules/{id:long}/runs` | ListRunsAsync | `SchedulesController.cs:306` | Run history of a schedule | |

### 1.18 Tools

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `tools.robots-tester` | POST `api/tools/robots-test` | RobotsTest | `ToolsController.cs:93` | Test URLs against a robots.txt body or fetched URL | |
| `tools.url-check` | POST `api/tools/url-check` | UrlCheck | `ToolsController.cs:137` | URL status, redirect chain, headers, indexability | |
| `tools.structured-data-check` | POST `api/tools/structured-data-check` | StructuredDataCheck | `ToolsController.cs:170` | Validate structured data in markup or fetched URL | |
| `tools.render-test` | POST `api/tools/render-test` | RenderTest | `ToolsController.cs:202` | Render a URL: screenshot, DOM, console capture | heaviest endpoint (headless render) |
| `tools.sitemap-generate` | POST `api/tools/sitemap-generate` | SitemapGenerate | `ToolsController.cs:239` | Generate XML sitemap from audit's eligible URLs (local file) | delegates to export job (XmlSitemap) |
| `tools.scope-preview` | POST `api/tools/scope-preview` | ScopePreview | `ToolsController.cs:267` | Preview content scope against a sample document | sync action |
| `tools.rewrite-preview` | POST `api/tools/rewrite-preview` | RewritePreview | `ToolsController.cs:301` | Preview URL rewrite rules + host mappings against a URL | sync action |
| `tools.llms-generate` | POST `api/tools/llms-generate` | LlmsGenerate | `ToolsController.cs:335` | Generate llms.txt / llms-full.txt into a local directory | returns local filesystem path (E9) |
| `tools.llms-validate` | POST `api/tools/llms-validate` | LlmsValidate | `ToolsController.cs:369` | Validate llms.txt body or fetched URL against convention | |

### 1.19 Dictionary

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `dictionary.list` | GET `api/dictionary` | ListAsync | `DictionaryController.cs:37` | List accepted spellings | query: `language`, paging, sort (word/language/addedAt) |
| `dictionary.add` | POST `api/dictionary` | AddAsync | `DictionaryController.cs:64` | Accept a word | 409 on duplicate word+language |
| `dictionary.remove` | DELETE `api/dictionary` | RemoveAsync | `DictionaryController.cs:97` | Remove a word (optionally one language) | **DELETE with JSON body** (no route id) |

### 1.20 Queries (saved query-builder definitions)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `queries.list` | GET `api/queries` | ListAsync | `QueriesController.cs:56` | List saved queries with name search | stored in saved_views with surface key `query-builder` |
| `queries.create` | POST `api/queries` | CreateAsync | `QueriesController.cs:89` | Create saved query (name + rule tree) | rule-tree depth cap 16; operator whitelist |
| `queries.update` | PUT `api/queries` | UpdateAsync | `QueriesController.cs:117` | Update saved query | **id in JSON body** (no `{id}` segment) |
| `queries.delete` | DELETE `api/queries` | DeleteAsync | `QueriesController.cs:167` | Delete saved query | **id in query string** (`?id=`) |

### 1.21 Plugins (local registry)

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `plugins.list` | GET `api/plugins` | ListAsync | `PluginsController.cs:41` | List plugin registry | grants limited to `read-data`, `ui-contributions` (`commands` rejected) |
| `plugins.update` | PUT `api/plugins/{id:long}` | UpdateAsync | `PluginsController.cs:53` | Update enablement + permission grants | grant/revoke permission lists |
| `plugins.rescan` | POST `api/plugins/rescan` | RescanAsync | `PluginsController.cs:95` | Rescan local plugins dir for plugin.json manifests | SHA-256 integrity hashes; no remote registry |
| `plugins.delete` | DELETE `api/plugins/{id:long}` | DeleteAsync | `PluginsController.cs:163` | Remove registry entry | |

### 1.22 Settings

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `settings.get` | GET `api/settings` | GetSettingsAsync | `SettingsController.cs:185` | Read full settings object | secrets masked as `********`; includes MCP automation settings (enabled + displayable token) |
| `settings.update` | PUT `api/settings` | UpdateSettingsAsync | `SettingsController.cs:196` | Subset update of settings | theme, uiLanguage, database, proxy, mcp, embeddings, statusBar thresholds |
| `settings.database-test` | POST `api/settings/database/test` | TestDatabaseAsync | `SettingsController.cs:257` | Test DB reachability/auth/migration compat | **accepts MySQL host → outbound connection attempt**; sqlite uses embedded provider |
| `settings.embedding-test` | POST `api/settings/embedding/test` | TestEmbeddingAsync | `SettingsController.cs:329` | Test candidate embedding daemon | loopback-only enforced before any connection (E7) |

### 1.23 System

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `system.health` | GET `api/system/health` | GetHealthAsync | `SystemController.cs:64` | On-demand system-health snapshot (WS, GC heap, WAL, running audit) | overlaps hub `system-health` heartbeat |
| `system.compact` | POST `api/system/compact` | CompactAsync | `SystemController.cs:78` | Run compact action; before/after snapshots | guard refusals (rate limit, running audit) → 409 |
| `system.shutdown` | POST `api/system/shutdown` | (minimal API lambda) | `app\src\backend\CrawlForge.Host\CrawlForgeBackend.cs:131` | Shell-initiated graceful backend stop | session-gated; drains lifecycle then Kestrel |
| `system.hub-stub` | GET `/hub` | (minimal API lambda) | `CrawlForgeBackend.cs:125` | Static supervisor descriptor (`name/status/protocol: hub-stub`) | **no session gate in code** (see Observations) |

### 1.24 Health & metrics

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `health.probe` | GET `api/health` | GetHealthAsync | `HealthController.cs:36` | Readiness flag + uptime | **exempt from session token** (supervisor probe) |
| `health.version` | GET `api/version` | GetVersionAsync | `HealthController.cs:46` | Product version, schema version, runtime profile | |
| `health.metrics` | GET `/metrics` | GetMetrics | `HealthController.cs:70` | Prometheus-class text exposition | **not JSON-enveloped**; pull-only, never pushed |

### 1.25 Diagnostics

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `diagnostics.version` | GET `api/diagnostics/version` | (minimal API lambda) | `CrawlForgeBackend.cs:126` | Version + GC profile + feature-availability report | acceptance-criterion #19 hook; duplicate-ish of `health.version` |
| `diagnostics.artifacts.list` | GET `api/diagnostics/artifacts` | ListArtifactsAsync | `DiagnosticsController.cs:37` | List local logs/minidumps (metadata only) | query: `kind` (log/minidump); artifacts not downloadable via API |
| `diagnostics.artifacts.purge` | DELETE `api/diagnostics/artifacts` | PurgeArtifactsAsync | `DiagnosticsController.cs:61` | Purge local diagnostic artifacts | query: `kind` |

### 1.26 Analysis

| Capability key | Method + Route | Action | file:line | Purpose | Notes |
|---|---|---|---|---|---|
| `analysis.single-page` | POST `api/analysis/single-page` | SinglePage | `AnalysisController.cs:28` | On-demand single-page analysis | ad-hoc; no audit row required |

---

## 2. Hub surface (SignalR)

Hub route: **`/hubs/realtime`** (`CrawlForgeHub.HubRoutePath`, `Hubs\CrawlForgeHub.cs:20`; mapped in `CrawlForge.Host\Composition\ApiSurfaceModule.cs:65`). Session token accepted via `access_token` query for WebSocket transport. Server events are delivered as client methods named after the event.

| Capability key | Name | Direction | file:line | Purpose |
|---|---|---|---|---|
| `realtime.subscribe` | `Subscribe(string[] auditIds)` | client→server | `Hubs\CrawlForgeHub.cs:47` | Join per-audit channel groups (`audit-{id}`) |
| `realtime.unsubscribe` | `Unsubscribe(string[] auditIds)` | client→server | `Hubs\CrawlForgeHub.cs:55` | Leave per-audit channel groups |
| `realtime.event.crawl-progress` | `crawl-progress` | server→client | `Hubs\HubEventCatalog.cs:16` | Per-audit crawl counters/throughput; ≤1/s |
| `realtime.event.url-batch` | `url-batch` | server→client | `Hubs\HubEventCatalog.cs:19` | Throttled (urlId, canonicalUrl, statusClass) batches |
| `realtime.event.stage-changed` | `stage-changed` | server→client | `Hubs\HubEventCatalog.cs:22` | Audit lifecycle transition (prev/new state) |
| `realtime.event.findings-updated` | `findings-updated` | server→client | `Hubs\HubEventCatalog.cs:25` | Severity/category finding-count deltas; throttled |
| `realtime.event.audit-completed` | `audit-completed` | server→client | `Hubs\HubEventCatalog.cs:28` | Once on completion: final counters, report readiness |
| `realtime.event.audit-failed` | `audit-failed` | server→client | `Hubs\HubEventCatalog.cs:31` | Once on failure: error code + message |
| `realtime.event.backend-lifecycle` | `backend-lifecycle` | server→client | `Hubs\HubEventCatalog.cs:34` | Backend readiness/shutdown notice (global) |
| `realtime.event.schedule-fired` | `schedule-fired` | server→client | `Hubs\HubEventCatalog.cs:37` | Schedule firing (schedule id, audit id, trigger) |
| `realtime.event.schedule-run-completed` | `schedule-run-completed` | server→client | `Hubs\HubEventCatalog.cs:40` | Scheduled run finalization + completion-action results |
| `realtime.event.export-delivery` | `export-delivery` | server→client | `Hubs\HubEventCatalog.cs:43` | Export destination delivery state change |
| `realtime.event.waf-blocks` | `waf-blocks` | server→client | `Hubs\HubEventCatalog.cs:48` | WAF/challenge block batches; ≤1/s |
| `realtime.event.host-throttle` | `host-throttle` | server→client | `Hubs\HubEventCatalog.cs:51` | AIMD adjustment; coalesced ≤1/s per host (locked name) |
| `realtime.event.system-health` | `system-health` | server→client | `Hubs\HubEventCatalog.cs:54` | 2-second health heartbeat while shell connected (locked name) |
| `realtime.event.matrix-row-validated` | `matrix-row-validated` | server→client | `Hubs\HubEventCatalog.cs:57` | Redirect-matrix row batches; ≤1/s |
| `realtime.event.log-import-progress` | `log-import-progress` | server→client | `Hubs\HubEventCatalog.cs:60` | Log import counters; ≤1/s |
| `realtime.event.log-import-completed` | `log-import-completed` | server→client | `Hubs\HubEventCatalog.cs:63` | Once per log import finalization |
| `realtime.event.embedding-progress` | `embedding-progress` | server→client | `Hubs\HubEventCatalog.cs:66` | Semantic embedding progress; ≤1/s |
| `realtime.event.embedding-completed` | `embedding-completed` | server→client | `Hubs\HubEventCatalog.cs:69` | Once per semantic job finalization |
| `realtime.event.local-notification` | `local-notification` | server→client | `Hubs\HubEventCatalog.cs:75` | Local OS notification request (rendered by shell) |

Note: `OnConnectedAsync`/`OnDisconnectedAsync` (`CrawlForgeHub.cs:33,37`) register/deregister connections in `RealtimeHubService`; they are lifecycle hooks, not user capabilities. Event producers live outside the hub: `AuditExecutionEngine`, `ScheduleHubEvents`, `LogImportService`, `RedirectMatrixValidator`, `HostThrottleSinks`, `ExportModels`. Open-string event policy: unknown events pass through unchanged (additive evolution never breaks the hub) — `HubEventCatalog.cs:3-10`.

---

## 3. MCP surface

Protocol: MCP over JSON-RPC 2.0 (`McpProtocol.cs`, server name `crawlforge`, version `1.0.0`, latest protocol `2025-06-18`). Two transports: loopback streamable-HTTP and a stdio adapter that bridges to it.

### 3.1 Tools (8) — `McpToolSurface.cs`

| Capability key | Kind | Name | file:line | Params | Purpose |
|---|---|---|---|---|---|
| `mcp.tool.start-audit` | tool | `start-audit` | `McpToolSurface.cs:89` | `projectId` (int, req), `configurationId` (int, req), `limitOverrides` (object) | Start audit exactly like the REST start operation |
| `mcp.tool.pause-audit` | tool | `pause-audit` | `McpToolSurface.cs:92` | `auditId` (int, req) | Checkpoint and pause a running audit |
| `mcp.tool.resume-audit` | tool | `resume-audit` | `McpToolSurface.cs:95` | `auditId` (int, req) | Rehydrate and resume a paused audit |
| `mcp.tool.cancel-audit` | tool | `cancel-audit` | `McpToolSurface.cs:98` | `auditId` (int, req) | Drain workers, finalize partial data |
| `mcp.tool.re-audit` | tool | `re-audit` | `McpToolSurface.cs:101` | `auditId` (int, req), `refresh` (bool, default false) | Follow-up analysis pass over same dataset; returns new audit id |
| `mcp.tool.analyze-single-page` | tool | `analyze-single-page` | `McpToolSurface.cs:104` | `url` (str, req), `render` (bool), `ruleKeys` (str[]) | Ad-hoc single-page audit: findings + element inventory |
| `mcp.tool.run-schedule-now` | tool | `run-schedule-now` | `McpToolSurface.cs:107` | `scheduleId` (int, req) | Manual schedule trigger; returns started audit id |
| `mcp.tool.get-audit-progress` | tool | `get-audit-progress` | `McpToolSurface.cs:110` | `auditId` (int, req) | Live progress (status, phase, counters) |

By design (§11.1, `McpToolSurface.cs:11-18`): **no** settings, destination, credential, proxy, or database-provider management is exposed over MCP.

### 3.2 Prompts (5) — `McpPromptSurface.cs`

| Capability key | Kind | Name | file:line | Params | Purpose |
|---|---|---|---|---|---|
| `mcp.prompt.audit-health-briefing` | prompt | `audit-health-briefing` | `McpPromptSurface.cs:107` | `auditId` (req) | Health briefing from audit summary; side-effect-free |
| `mcp.prompt.high-severity-triage` | prompt | `high-severity-triage` | `McpPromptSurface.cs:109` | `auditId` (req) | Triage briefing from high-severity findings |
| `mcp.prompt.audit-delta-briefing` | prompt | `audit-delta-briefing` | `McpPromptSurface.cs:111` | `baseAuditId` (req), `targetAuditId` (req) | Cross-audit delta briefing (uses comparison reader) |
| `mcp.prompt.ngram-topical-briefing` | prompt | `ngram-topical-briefing` | `McpPromptSurface.cs:114` | `auditId` (req) | Topical briefing from n-gram analysis + summary |
| `mcp.prompt.semantic-topical-briefing` | prompt | `semantic-topical-briefing` | `McpPromptSurface.cs:116` | `auditId` (req) | Semantic topical briefing (suggestions only, never auto-applied) |

### 3.3 Resources (15) — `McpResourceSurface.cs` (URI templates, `crawlforge://` scheme)

| Capability key | Kind | Name (URI) | file:line | Params | Purpose |
|---|---|---|---|---|---|
| `mcp.resource.audit-summary` | resource | `crawlforge://audits/{auditId}/summary` | `McpResourceSurface.cs:167` | path `{auditId}` | Status, scores, metric summaries, finding/WAF counts |
| `mcp.resource.high-severity-findings` | resource | `crawlforge://audits/{auditId}/high-severity-findings` | `McpResourceSurface.cs:168` | path `{auditId}` | Error+Warning findings with evidence/recommendations |
| `mcp.resource.url-detail` | resource | `crawlforge://audits/{auditId}/urls/{urlId}` | `McpResourceSurface.cs:169` | path `{auditId}`, `{urlId}` | URL record + findings, WAF, scope diagnostics, top n-grams, SERP, semantic relations |
| `mcp.resource.audit-list` | resource | `crawlforge://audits` | `McpResourceSurface.cs:170` | none | All audits with status and phase |
| `mcp.resource.schedule-status` | resource | `crawlforge://schedules` | `McpResourceSurface.cs:171` | none | Schedule definitions with next-due + recent runs |
| `mcp.resource.crawl-progress` | resource | `crawlforge://audits/{auditId}/progress` | `McpResourceSurface.cs:172` | path `{auditId}` | Live progress + throttle summary + matrix counters |
| `mcp.resource.extracted-content` | resource | `crawlforge://audits/{auditId}/urls/{urlId}/content` | `McpResourceSurface.cs:173` | path `{auditId}`, `{urlId}` | Extraction values + stored body text (≤20k chars) |
| `mcp.resource.ngram-analysis` | resource | `crawlforge://audits/{auditId}/ngrams` | `McpResourceSurface.cs:174` | query `zone`, `order`, `scope` | N-gram site summaries + per-URL frequencies |
| `mcp.resource.waf-blocks` | resource | `crawlforge://audits/{auditId}/waf-blocks` | `McpResourceSurface.cs:175` | path `{auditId}` | WAF detections with provider/class/evidence |
| `mcp.resource.host-throttle` | resource | `crawlforge://audits/{auditId}/host-throttle` | `McpResourceSurface.cs:176` | path `{auditId}` | Per-host AIMD throttle history |
| `mcp.resource.redirect-matrix-results` | resource | `crawlforge://audits/{auditId}/redirect-matrix` | `McpResourceSurface.cs:177` | path `{auditId}` | Redirect matrix rows + counters |
| `mcp.resource.log-correlation` | resource | `crawlforge://audits/{auditId}/log-correlation?import={logImportId}` | `McpResourceSurface.cs:178` | query `import` (required) | True orphans, budget waste, uncrawled indexable URLs |
| `mcp.resource.semantic-link-opportunities` | resource | `crawlforge://audits/{auditId}/semantic-links` | `McpResourceSurface.cs:179` | path `{auditId}` | Link opportunities + cannibalization clusters |
| `mcp.resource.geo-readiness` | resource | `crawlforge://audits/{auditId}/geo-readiness` | `McpResourceSurface.cs:180` | query `agent` | llms.txt validation + AI-bot governance summary |
| `mcp.resource.analytics-coverage` | resource | `crawlforge://audits/{auditId}/analytics-coverage` | `McpResourceSurface.cs:181` | query `vendor` | Per-container analytics coverage across indexable pages |

All reads bounded to 200 rows (`DefaultReadLimit`, `McpResourceSurface.cs:95`) and side-effect-free.

### 3.4 Transport / protocol exposure

| Capability key | Kind | Name | file:line | Params | Purpose |
|---|---|---|---|---|---|
| `mcp.transport.loopback-http` | transport | `POST /mcp` (JSON-RPC) | `McpLoopbackServer.cs:152` | HTTP body = JSON-RPC; `Authorization: Bearer`; `Mcp-Session-Id`; `X-MCP-Transport` | The MCP message endpoint; loopback-bind enforced at construction and runtime |
| `mcp.transport.sse` | transport | `GET /mcp` (SSE) | `McpLoopbackServer.cs:236` | `Mcp-Session-Id` | Server→client channel; keep-alive only, no spontaneous notifications (listChanged=false) |
| `mcp.session.teardown` | transport | `DELETE /mcp` | `McpLoopbackServer.cs:265` | `Mcp-Session-Id` | Ends a session (204) |
| `mcp.auth.token` | service | `McpTokenService` | `McpTokenService.cs:75` | 256-bit hex bearer token | Constant-time validation; rotation invalidates prior clients instantly (criterion #16); encrypted at rest (`mcp-token.json`) |
| `mcp.transport.stdio-adapter` | transport | `crawlforge-mcp-stdio` process | `app\src\mcp-stdio-adapter\src\index.ts:25` | `--endpoint`/`CF_MCP_ENDPOINT`, `--token`/`CF_MCP_TOKEN` | Bridges stdio ↔ loopback endpoint; forwards exactly the 6 request types below; loopback-only enforced without DNS (`loopback.ts:28`) |

JSON-RPC methods routed by `McpJsonRpcDispatcher.cs` (all recorded in the automation action log per §11.5): `initialize` (:170, assigns `Mcp-Session-Id`, returns capabilities + instructions "Loopback-only; no settings, credential, or configuration management is exposed"), `ping` (:197), `tools/list` (:200), `tools/call` (:212), `resources/list` (:232), `resources/read` (:245), `prompts/list` (:264), `prompts/get` (:281); anything else → method-not-found. The stdio adapter (`bridge.ts:58-63`) forwards `tools/list`, `tools/call`, `resources/list`, `resources/read`, `prompts/list`, `prompts/get` — **no additional exposure** beyond the C# surfaces.

---

## 4. Electron IPC / preload surface

contextIsolation ON, nodeIntegration OFF, sandbox ON (`preload.ts:1-10`). Renderer sees only `window.crawlForge` (core) and `window.crawlForgeShell` (shell duties) — no Node/Electron APIs leak.

| Capability key | Channel | file:line | Purpose |
|---|---|---|---|
| `ipc.file-dialog` | `crawlforge:file-dialog` (invoke) | `preload\bridge.ts:60`; handler `main.ts:324` | Native open-file/open-directory/save-file dialog; returns local paths only |
| `ipc.notification` | `crawlforge:notification` (send) | `preload\bridge.ts:61`; handler `main.ts:342` | Show a local OS notification |
| `ipc.taskbar-progress` | `crawlforge:taskbar-progress` (send) | `preload\bridge.ts:62`; handler `main.ts:348` | Set taskbar progress mode/value on the calling window |
| `ipc.window.minimize` | `crawlforge:window-minimize` (send) | `preload\bridge.ts:63`; handler `main.ts:353` | Minimize the calling window |
| `ipc.window.maximize` | `crawlforge:window-maximize` (send) | `preload\bridge.ts:64`; handler `main.ts:356` | Toggle maximize/unmaximize |
| `ipc.window.close` | `crawlforge:window-close` (send) | `preload\bridge.ts:65`; handler `main.ts:364` | Close the calling window |
| `ipc.backend.restart` | `crawlforge:backend-restart` (invoke) | `preload\bridge.ts:66`; handler `main.ts:368` | Restart the backend via `orchestrator.retryFromRecovery()` |
| `ipc.diagnostics-purge` | `crawlforge:diagnostics-purge` (invoke) | `preload\bridge.ts:67`; handler `main.ts:371` | Purge local diagnostic artifacts; returns purged count |
| `ipc.backend-status` | `crawlforge:backend-status` (main→renderer push) | `preload\bridge.ts:68`; sender `main.ts:314` | Backend supervisor status change fan-out to all windows |
| `ipc.get-bridge` | `crawlforge:get-bridge` (invoke) | `preload.ts:59`; handler `main.ts:375` | Return handshake descriptor (port + session token) for `window.crawlForge` construction |
| `ipc.hydrated` | `crawlforge:hydrated` (send) | `preload.ts:55`; handler `main.ts:381` | Renderer signals first paint/hydration complete (startup gate) |

`window.crawlForge` = `{ backendBaseUrl, getSessionToken() }` (`preload.ts:65-71`); `window.crawlForgeShell` = the 9 shell-duty methods above (`preload.ts:39-56`). External links are force-opened in the OS browser, never in-shell (`main.ts:387-394`).

---

## 5. Observations (for UX-vs-API gap cross-referencing)

1. **`ApiSurfaceMetadata.cs` does NOT enumerate the API surface.** It is a display-metadata sidecar (`ApiSurfaceMetadataStore`, `ApiSurfaceMetadata.cs:20`): names/createdAt/updatedAt for **schedules, destinations, queries** stored under key `apiSurface` inside `application_settings.ui_preferences_json` (the locked schema lacks name/timestamp columns for those three tables). Cross-check: exactly the three controllers that inject it — `SchedulesController`, `DestinationsController`, `QueriesController` — and the section keys (`schedules`, `destinations`, `queries`) match. **No machine-readable route catalog exists anywhere in the repo**; this inventory is derived purely from attributes/minimal-API mappings. Wire-contract files (`*WireContracts.cs`, `*WireMapper.cs`) are pure DTO shapes with no surface facts; `ApiControllerBase.cs` supplies the uniform envelope and states "No authorization exists beyond the loopback boundary (§10.1)".

2. **Auth is a single per-launch session token, not roles.** `SessionTokenMiddleware` gates `/api/*` (except `/api/health`), `/hubs/*`, `/metrics` via `X-CrawlForge-Session` or SignalR `access_token` query (`SessionTokenMiddleware.cs:46-65`). No `[Authorize]`, no roles, no per-endpoint permissions — nothing to distinguish "admin" from "user" endpoints. ⚠️ **Doc/code discrepancy**: the middleware remarks claim the `/hub` stub "remain[s] token-gated", but the gating predicate matches only `/api/`, `/hubs/`, `/metrics` — literal `GET /hub` is **ungated** (static stub payload only; low risk, but a false gap/finding risk if the UX inventory assumes it protected). MCP has a separate bearer token (`McpTokenService`), rotation-safe; its endpoint+plaintext token are published to `mcp-endpoint.json` in the per-user data dir at startup (`McpModule.cs:132-140`).

3. **Feature toggles exist but gate nothing.** `FirstRunSeed.cs:32` seeds `localFeatureToggles` (`scheduledAudits`, `customExtraction`, `customSearch`, `javascriptSnippets`, `logAnalyzer`, `semanticLinking`, `geoAnalyzer`, `marTechAnalyzer`, all true) plus `mcpAutomationEnabled: false`. `IFeatureAvailability`/`LocalFeatureAvailability` is registered (`CrawlForgeBackend.cs:102`) but **`IsEnabled(...)` is never called by any controller, hub, or MCP surface** — every endpoint is unconditionally served. Two mismatches worth flagging to the cross-reference: (a) `LocalFeatureAvailability` reads JSON key `featureToggles` while the seed writes `localFeatureToggles` (toggles may never even load); (b) the MCP loopback server starts unconditionally (`McpModule.cs:127-141`) despite `mcpAutomationEnabled: false`. A UX that shows feature toggles would be toggling dead switches; conversely no API capability is actually hidden behind a flag.

4. **Ops/admin-ish and non-UI-friendly surfaces.** Candidates for "advanced/hidden" UX treatment: `POST /api/system/shutdown` (stops the backend), `POST /api/system/compact` (DB compact, 409 on guard refusal), `POST /api/settings/database/test` (**the only endpoint that opens outbound network connections — MySQL reachability test** in an otherwise offline-first app), `POST /api/settings/embedding/test` (loopback-only), `DELETE /api/diagnostics/artifacts`, `POST /api/plugins/rescan` (local FS scan), `POST /api/logs/{id}/verify-dns` (DNS lookups, consented), and IPC `backend-restart`/`diagnostics-purge`. Also UI-hostile response shapes: `/metrics` is Prometheus text (not JSON-enveloped), `GET /api/exports/{id}/file` is a binary stream, `POST /api/tools/render-test` returns screenshot/DOM/console captures, `tools.llms-generate` returns local filesystem paths. Diagnostic artifacts are listable/purgeable but **not downloadable** through any endpoint.

5. **Channel asymmetries & duplicate capabilities (prime gap-audit material).**
   - **MCP covers only 8 lifecycle tools** (start/pause/resume/cancel/re-audit/analyze-single-page/run-schedule-now/get-audit-progress) vs 114 REST actions — intentional per §11.1 (no settings/destination/credential/config management over MCP), so any UX "automation" story must not promise MCP parity for exports, suppressions, dictionary, WAF signatures, plugins, etc.
   - **The automation action log is a write-only surface**: every MCP invocation is recorded (`McpJsonRpcDispatcher.cs:152`, `AutomationActionLog.cs`), and `IAutomationActionLog.ListAsync` exists — but **no REST/hub/IPC endpoint exposes it**. If the UX documents an "automation history" feature, the read path is missing from the API inventory (or lives only in direct DB access).
   - **Duplicate/overlapping reads**: hub `system-health` (2s heartbeat) vs `GET /api/system/health`; `GET /api/diagnostics/version` vs `GET /api/version` (different payloads: GC profile vs schema version); `GET /hub` stub vs `GET /api/health` (both supervisor probes); MCP `get-audit-progress`/`crawl-progress` resource vs `GET /api/audits/{id}` + hub `crawl-progress`; MCP tools map 1:1 onto REST start/pause/resume/cancel/reaudit/single-page/run-now.
   - **Unusual wire conventions the UI must follow**: `DELETE /api/dictionary` and `PUT/DELETE /api/queries` carry the identifier in the body/query string (frozen route catalog has no `{id}` segment — `QueriesController.cs:10-16`); schedule recurrence uses a custom `interval:`/`calendar:` grammar under the wire field name `cron`; secrets round-trip as the literal mask `********` (`SettingsController.cs:155`).
   - **Segmented families**: all audit-scoped explorer data hangs under `api/audits/{id}/...` (urls, links, findings, summary, charts, graph, scores, hosts, ngrams, semantics, geo, logs/correlation, redirect-matrix, waf-blocks, analytics, extracted-content, search/snippet results, technologies) — 30+ endpoints share the `{id}` audit namespace; WAF signature management and destinations/schedules/queries/plugins/dictionary are global (non-audit) surfaces. If the UI bundles these differently (e.g. "Reports", "Advanced tools"), expect systematic key mismatches in cross-reference.
