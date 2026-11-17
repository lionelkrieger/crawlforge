# 04b — Docs ↔ Channel Join: promise-vs-reality and channel-parity gap registers

Audit: CrawlForge UX surface vs API exposure gap audit. This is the join of the three inventories —
`03-documented-feature-inventory.md` (promise baseline, 170 capabilities / ≈89 REST ops / 20 hub events /
28 MCP items / 3 completion-action kinds / 20 metric keys / 30 rule keys / 5 explicitly-future items),
`01-api-exposure-inventory.md` (actual API), and `02-ux-surface-inventory.md` (actual UX) — verified
against source under `app\src\backend`, `app\src\frontend`, `app\src\electron`, `app\src\mcp-stdio-adapter`.
Every register row cites inventory rows plus verified `file:line`. Read-only audit; no other inputs used.

---

## 1. Method & join rules

**Inputs.** Only inventories 01/02/03 and the source tree (excluding `bin`, `obj`, `node_modules`, `dist`).
All line references below were re-verified in source during this join; where an inventory claim failed
verification it is moved to §6 Disconfirmed/adjusted.

**Namespace disambiguation (surface suffixes).** Overloaded names are joined with explicit surface prefixes;
a bare name is never matched across surfaces:

| Overloaded name | `rest.*` | `event.*` (hub) | `metric.*` (metric_summaries) | `mcp.res.*` | `ux.*` | `rule.*` |
|---|---|---|---|---|---|---|
| `waf-blocks` | `rest.waf.blocks.list` `/api/audits/{id}/waf-blocks` | `event.waf-blocks` (`HubEventCatalog.cs:48`) | `metric.waf-blocks` (`MetricKeys.cs:11`) | `mcp.res.waf-blocks` (`McpResourceSurface.cs:175`) | `ux.explorers.waf` (dead) | — |
| `host-throttle` | `rest.hosts.list` (`/hosts`) | `event.host-throttle` (`:51`) | `metric.host-throttle` (`:17`) | `mcp.res.host-throttle` (`:176`) | `ux.audits.report.host-throttle` (wired) | — |
| `waf-block-rate` | — | — | `metric.waf-block-rate` (`:14`) | — | — | `rule.waf-block-rate` (`FindingRuleKeys.cs:14`) |
| `redirect-matrix` | `rest.redirect-matrix.upload/results` | `event.matrix-row-validated` | `metric.redirect-matrix` (`:32`) | `mcp.res.redirect-matrix-results` (`:177`) | `ux.explorers.redirect-matrix` (dead) | `rule.redirect-matrix.*` (5 keys) |
| `ngram-analysis` | `rest.ngrams.list` | — | `metric.ngram-top` | `mcp.res.ngram-analysis` (`:174`) | `ux.explorers.ngrams` (dead) | — |
| `local-notification` | — | `event.local-notification` (`HubEventCatalog.cs:75`) | — | — | (none — dropped) | — |
| `analytics-coverage` | `rest.analytics.coverage` | — | `metric.analytics-coverage` (`:59`) | `mcp.res.analytics-coverage` (`:181`) | `ux.dashboards.analytics-coverage` (dead) | — |

Export dataset keys (`waf-blocks`, `ngrams`, `redirect-matrix`, `semantic-opportunity`, `analytics`) are
joined as `exportds.*` (`ExportModels.cs:13-48`); chart metrics consume `metric.*` keys via
`GET /api/audits/{id}/charts/{metric}` (`AuditExplorerController.cs:486`).

**Resolution vocabulary (appendix, §2.2).**
- `R` — implemented; reachable through the promised channel(s) in the shipped product.
- `R(api)` — implemented and reachable over REST (or MCP); the promised UI surface is missing or unmounted.
- `D` — implemented in code but **zero** reachable channel (dark; includes services never composed into the host).
- `P` — partial: part of the promised behavior absent or semantically divergent.
- `A` — absent: no implementation found.

**Guide-only promise rule.** `GET /api/schedules` and hub event `local-notification` are promised only by
the user guide (03 §4.1). Classification is explicit in B2-01/B2-08: *promise kept, spec silent* vs *gap*.

**One-vs-four capability ruling (single-URL inspection).** Ruling: **one capability, four surfaces** —
the capability family `inspect.single-url` (ad-hoc single-URL inspection) is counted **once** in the
170-row resolution, with four entry surfaces that are not separate capabilities:

| Surface | Route/tool | Primary purpose (spec) | WAF verdict | Finder picker |
|---|---|---|---|---|
| Single-Page Analyzer | `rest.analysis.single-page` (`AnalysisController.cs:28`) | findings + element inventory, no project | yes | yes (`ux.tools.selector-picker`) |
| URL checker | `rest.tools.url-check` (`ToolsController.cs:137`) | status/redirects/headers/indexability | yes | no |
| Render tester | `rest.tools.render-test` (`ToolsController.cs:202`) | screenshot/DOM/console artifacts | yes | yes |
| MCP tool | `mcp.tool.analyze-single-page` (`McpToolSurface.cs:67,104`) | automation projection of the SPA | yes | n/a |

Basis: all four share the WAF-fingerprint pass (03 `intel.waf-detection`: "runs in … single-page, URL check")
and the same finding model; the spec assigns each a distinct primary artifact. Gap matching treats them as
one capability row (`audits.single-page`) plus the tools row (`reports.standalone-tools`); surface-specific
defects (e.g. SPA "send to scope" never persists — 02 `tools.single-page.scope-staging`) are filed against
the surface, never double-counted as capability gaps.

**Severity scale.** critical = data loss/security breach reachable in the shipped product; high = a
documented user capability is unusable or an exposure contradicts the documented privacy/governance model;
medium = significant partial/dark capability; low = naming/doc drift or cosmetic parity.

---

## 2. B1 register — doc-only promises (no UX and/or no API)

| ID | Finding | Evidence (inventory row + verified source) | Impact | Severity | Remediation |
|---|---|---|---|---|---|
| B1-01 | Sentiment scoring (§7.12, optional, off-by-default) has no implementation at all — not a toggle, no code | 03 `audits.readability-metrics` + §4.2; grep `sentiment|Sentiment` over `app\src\backend` + `app\src\frontend` = **0 matches** (readability itself exists: `ReadabilityCalculator.cs:12`, `ContentQualityAnalyzer.cs:265-266`) | Documented optional metric silently missing; a user enabling "sentiment" finds no such switch | low — optional, disabled by default | Implement the lexicon pass behind the promised default-off toggle, or strike the promise from §7.12 |
| B1-02 | The promised **Security & protocol** rule category (mixed content, non-HTTPS pages, insecure form actions, missing security headers, absent HTTPS redirection) is entirely unimplemented | 03 `audits.rules-security`; rule-key census (`*RuleKeys.cs`, `FindingRuleKeys.cs`, 92 keys) contains **no `security.*` key** and no analyzer (grep `mixed-content\|insecure-form\|missing-security-header\|non-https\|https-redirect` = 0); `AuditEnums.cs:27-61` has no Security category | Users believe an audit checks security/protocol issues; nothing is ever emitted — false assurance in a security-adjacent area | **high** — an entire documented check family runs zero checks | Implement the five promised checks (or remove the category from docs/UI category lists until shipped) |
| B1-03 | Promised **Images** checks are partial: only alt-text exists (under Accessibility); broken image sources and missing intrinsic dimensions have no rule | 03 `audits.rules-images`; `AccessibilityRuleKeys.cs:11` `accessibility.image-alt`; only `performance.image-count/image-bytes` metrics (`PerformanceKeys.cs:48-51`); grep `broken-image\|image-dimension\|images\.` = 0 rule keys | Broken-source and missing-dimension findings never appear despite documented | medium — 2 of 4 promised checks absent | Add `images.*` rules or re-document the family as alt-text-only |
| B1-04 | Spec-only `saved_views` "user filter presets" (§9.2) exists only as the saved-**queries** store; no preset capability surface | 03 §4.2; `SchemaCatalog.Baseline.cs:185` `saved_views` table; used solely by `QueriesController.cs:12,28-29,102` (surface key `query-builder`); plugin presets also funnel to `/api/queries` | Spec promises a general filter-preset facility; only named saved queries are reachable | low — capability largely subsumed | Fold the concept into `/api/queries` docs, or expose per-surface presets |
| B1-05 | "Suppressed findings view: user-suppressed findings and rules, reversible" — no read path anywhere: no list REST op, no UI | 03 `reports.suppressed-view` + `audits.suppression` ("listed in a dedicated suppressed view"); 01 §1.14 has only `suppressions.create`/`suppressions.delete`; 02 has zero suppressed-view surface (frontend grep `suppressed` = 1 inert DTO field, `contracts/api/projects.ts:226`) | Users can suppress findings but can never see/review/reverse what is suppressed except by guessing keys | medium — reversible-suppression promise breaks at read | Add `GET /api/projects/{id}/suppressions` + suppressed view in the report workspace |
| B1-06 | `settings.thresholds` promise (WAF block-rate thresholds, AIMD parameter group, throttle-history bound, SERP pixel budgets, n-gram bounds, catch-up threshold, global audit concurrency) is exposed for **status-bar advisories only** | 03 `settings.thresholds`; `SettingsController.cs:74-115` accepts only theme/uiLanguage/database/proxy/mcp/embeddings/statusBar; the other thresholds live as code/option defaults (`WafDetectionPolicy.cs:21-24`, `SchedulingOptions.cs:21`, `HostThrottleEventRecorder.cs:24`) | Documented "locally configurable" knobs are not configurable by any surface | medium | Extend `PUT /api/settings` (or a thresholds sub-object) to the promised knob set |
| B1-07 | Per-project retention (keep-last-N / age limit) + user-visible storage reporting: pruner and policy exist, but nothing accepts a per-project policy and storage reporting has no surface | 03 `projects.retention`; `RetentionPruner.cs:63-138` + `RetentionPolicy` (`ContentStoreContracts.cs:54`) support KeepLastAudits/OlderThan, but the **only** caller is run-history pruning (`ScheduledAuditService.cs:506`); `CompressedContentStore.cs:270 GetTotalStoredBytesAsync` ("for user-visible storage reporting") has zero callers | Retention behavior promised as user-configurable is effectively fixed; storage never reported | medium | Wire per-project policy CRUD + a storage-reporting endpoint/surface |
| B1-08 | Project archive export/import (the documented DB-provider migration vehicle) is fully implemented in the data layer but **never composed or exposed** — no DI registration, no REST op, no UI | 03 `projects.archive-transfer`; `ProjectArchiveService.cs:34-263` (export/import + manifest), `ProviderSwitchOrchestrator.cs:53-113`; grep `IProjectArchiveService\|ProviderSwitchOrchestrator(` shows **no registration/caller outside their own assemblies**; absent from 01/02 entirely | Documented backup/transfer and "switching providers" workflows are unreachable; provider switching in settings cannot complete the documented archive path | **high** — a whole documented user capability + the migration path is dark | Register the service, expose `POST /api/projects/{id}/archive/export|import`, and hook the provider-switch flow |
| B1-09 | Signed-request crawler access: engine plumbing exists but the configuration path is missing — the secret store is never constructed | 03 `crawls.signed-access`; `SignedRequestHandler.cs:33` + `SignedRequestExpiryTracker.cs:29` exist; `SignedRequestSecretStore.cs:35` has **no constructor caller** outside its file (grep verified); no REST/UI route names it | Documented signed-request access (signature headers, expiry warnings) cannot be configured by a user | medium — engine-only | Expose signed-access secret config (encrypted at rest) on configurations, compose the store |
| B1-10 | User stop-word additions (`user_stopwords`, per language) have a table and reader but no write surface | 03 `intel.stopwords`; `SchemaCatalog.Additive.cs:91` `user_stopwords`; `StopWordFilter.cs:124` reads it; no REST op writes it (01 has `/api/dictionary` for spellings only) | Documented stop-word management has no way to add/remove entries | low | Add stop-word CRUD beside `/api/dictionary` or map it into the dictionary API |

### 2.2 Resolution appendix — all 170 documented capabilities

Terse resolution per 03 §2 row. Basis cites verified source or the join evidence (01/02 row refs are
"01 §x"/"02 §y"; all other refs re-verified in source during this join).

**platform (7)**

| Capability | Res | Basis |
|---|---|---|
| platform.installation | R | Electron shell + stdio adapter (`app\src\mcp-stdio-adapter`) + asset bundles present; packaging tree itself out of audit scope |
| platform.first-run | R | `FirstRunSeed.cs:31-59`, `FirstLaunchInitializer.cs:37-45`; zero-network constants |
| platform.network-boundary | R | Kestrel loopback bind `CrawlForgeBackend.cs:95`; MCP loopback-only (`McpLoopbackServer.cs:32`); outbound only DB-test/destination-test/verify-dns (01 §5) |
| platform.session-token | R | `SessionTokenMiddleware.cs:46-68` (except `/hub` gap → B4-01) |
| platform.server-gc | R | `GET /api/diagnostics/version` GC report `CrawlForgeBackend.cs:126` |
| platform.airgap | R | first-run + engine operate offline; only user-configured endpoints leave the machine |
| platform.localization | R | `settings.ui-language` wired (02 SettingsSurface); Ctrl+K palette (02 `shell.palette.open`); grid ARIA dark with explorers |

**projects (7)**

| Capability | Res | Basis |
|---|---|---|
| projects.create | R | 02 `projects.create` wired (`HubDashboard.tsx:81`) |
| projects.list | R | 02 `projects.list` wired |
| projects.update-delete | R(api) | `ProjectsController.cs:108-170`; routes unused by any surface (02 §A) |
| projects.configurations | R(api) | `ConfigurationsController.cs:54-270`; UI uses create+list only (02) |
| projects.scope-defaults | R(api) | `UnlimitedLimitsResolver.cs:71-127`; `ProjectWireMapper.cs:141-144` contentScope/profiles; UI dark |
| projects.retention | P | see B1-07 |
| projects.archive-transfer | D | see B1-08 (engine-only, never composed) |

**crawls (32)**

| Capability | Res | Basis |
|---|---|---|
| crawls.seeds | R | config fields; `ListModeIngestor.cs:106` seeds-first |
| crawls.scope | R(api) | configuration scope fields; engine classification |
| crawls.pattern-filters | R(api) | configuration fields; skip reasons recorded |
| crawls.query-param-policy | R(api) | canonicalization policy (config) |
| crawls.canonicalization | R | crawler canonicalizer + seen-set (engine) |
| crawls.limits | P | 10 parameters engine-side; REST override wire exposes 4 names vs 6 promised (→ B2-04) |
| crawls.limits-resolver | R | `UnlimitedLimitsResolver.cs:71-127` (merge, sentinel, validation) |
| crawls.politeness-robots | R | robots fetch/cache/backoff (engine) |
| crawls.user-agent | R(api) | configuration field |
| crawls.aimd | R | `AimdPolitenessController` (EngineServicesModule.cs:111) |
| crawls.aimd-pin | R(api) | `HostsController.cs:77` override; UI absent (02 §A `hosts.override`) |
| crawls.aimd-telemetry | R | `event.host-throttle` + `rest.hosts.list(includeHistory)` (`HostsController.cs:38-56`); report table wired (02) |
| crawls.frontier | R | `PriorityFrontier.cs:15-207` |
| crawls.fetching | R | `HttpFetcher.cs` + `FetchStatusClassifier.cs:28` |
| crawls.link-discovery | R | engine link extraction |
| crawls.rendering | R | `HeadlessRenderingPool.cs`; render-test wired (02) |
| crawls.rendering-options | R(api) | config: scroll/stealth/viewport-locale emulation |
| crawls.proxy-upstream | R(api) | `SettingsController` Proxy subset (`:98`); UI read-only (02 `settings.proxy-display`) |
| crawls.proxy-local | R | `LoopbackInterceptingProxy.cs:27` (background) |
| crawls.auth-credentials | R(api) | config headers/cookies + `ISecretProtector` at-rest encryption |
| crawls.signed-access | P | see B1-09 |
| crawls.sample-mode | R(api) | `SampleCrawlMode.cs:48`; `listMode` fields (`AuditsController.cs:116`) |
| crawls.virtual-robots | R(api) | configuration field |
| crawls.pre-crawl-validation | R(api) | `PreCrawlValidator.cs:19-111`, DI `EngineServicesModule.cs:101` |
| crawls.list-mode | R(api) | `urlList`/`listMode` (`AuditsController.cs:116-126`); no UI entry |
| crawls.url-rewrite | R | rewrite config + `tools.rewrite-preview` wired (02) |
| crawls.host-mapping | R(api) | `hostMapping` on both compare routes (`AuditsController.cs:322`, `ContentExplorerController.cs:65`) |
| crawls.migration-matrix | R(api) | `RedirectMatrixController.cs:58-109` + 5 verdict keys + `event.matrix-row-validated`; UI dark |
| crawls.lifecycle | R | pause/resume/cancel wired (02 `AuditViews.tsx`); state machine `AuditExecutionEngine` |
| crawls.checkpointing | R | `CrawlCheckpointManager.cs:50` |
| crawls.progress | R | `event.crawl-progress` + wired report panel (02) |
| crawls.content-store | R | `CompressedContentStore` + `IArtifactFileStore` (background) |

**audits (35)**

| Capability | Res | Basis |
|---|---|---|
| audits.start | R | `AuditsController.cs:94` + wired HubDashboard flow (02) |
| audits.reaudit | R(api) | `AuditsController.cs:281`; route unused (02 §A) |
| audits.single-page | R | `AnalysisController.cs:28` + wired SPA (02); one-vs-four ruling §1 |
| audits.schedule | P | CRUD exists (`SchedulesController.cs:114-242`); promised `overrides` field wire-absent (→ B2-03) |
| audits.schedule-execution | R | `ScheduledAuditService.cs:345-385` (catch-up exactly-once :362, `skipped-overlap` :385, `MaxConcurrentAudits` :189) |
| audits.schedule-controls | R(api) | full REST incl. guide-only `GET /api/schedules` (`SchedulesController.cs:95`); zero UI (02 §A) |
| audits.completion-actions | P | 3 kinds + save-time validation (`ScheduleCompletionAction.cs:14-23,107-132`); `local-notification` delivery dead (→ B3-04) |
| audits.pipeline | R | `AuditPipeline.cs` 8-step + phases |
| audits.rule-taxonomy | P | 8 core + 3 additive categories (`AuditEnums.cs:27-61`) vs 11 promised; images/security families → B1-02/B1-03 |
| audits.findings | R | `FindingEmitter.cs:141-144` (priority + remediationDifficulty) |
| audits.suppression | R(api) | `SuppressionsController.cs:34-71`; read side missing (→ B1-05) |
| audits.prioritization | R(api) | `RuleDefinition.cs:17-25` Priority/RemediationDifficulty seeds |
| audits.scoring | R(api) | `AuditScoreRecorder.cs:155-194` + `rest.audits.scores`; UI dark |
| audits.comparison | R(api) | `AuditsController.cs:318`; UI dark |
| audits.content-comparison | R(api) | `ContentExplorerController.cs:60`; UI dark |
| audits.element-inventory | R | `ElementInventoryExtractor.cs:92` (tables included) + head/link relations |
| audits.rules-indexability | R | `IndexabilityRuleKeys.cs:14-68`, `IndexabilityAnalyzer.cs` (orphan/sitemap-coverage as aggregates) |
| audits.rules-content-quality | R | `ContentQualityAnalyzer.cs` (titles/descriptions/headings/thin/dup) |
| audits.rules-linking | R | link-graph equity + anchor rules (`WeightedLinkEquityCalculator.cs:206`) |
| audits.rules-images | P | see B1-03 |
| audits.rules-performance | R | `PerformanceMetricContributions.cs:40-135`, `PerformanceBudgetEvaluator.cs:57-139` |
| audits.rules-security | A | see B1-02 |
| audits.rules-structured-data | R | `StructuredDataRuleKeys.cs:13-22` + rich-result eligibility |
| audits.rules-i18n | R | `StructuredDataRuleKeys.cs:25-46` (codes, reciprocity `HreflangReciprocityValidator.cs:76`) |
| audits.rules-mobile | R | `MobileFriendlinessAnalyzer.cs:11-269` (viewport/tap-targets/fonts/fit) |
| audits.rules-accessibility | R | `AccessibilityRuleKeys.cs:11-62` + rules dir + per-rule remediation |
| audits.rules-tech-detection | R | `TechnologyDetector.cs:14` + `rest.audits.technologies` |
| audits.spelling | R(api) | spelling analyzer + `rest.dictionary.*`; UI absent |
| audits.readability-metrics | P | readability/ratio/reading-time done (`ReadabilityCalculator.cs:12`); sentiment absent (→ B1-01) |
| audits.soft-404 | R | `Soft404Detector.cs:20-106`, `IndexabilityRuleKeys.cs:26` |
| audits.cwv | R | `CoreWebVitalsAnalyzer.cs:81-85`, budgets + `CodeCoverageAnalyzer.cs:149` |
| audits.custom-extraction | R(api) | `rest.content.extracted.*`; UI dark; rule authoring has no write path (02 C5) |
| audits.custom-search | R(api) | `rest.content.search-results`; UI dark (adapterKind 'search' unbound) |
| audits.js-snippets | R(api) | `rest.content.snippets`; UI dark |
| audits.response-vs-rendered | R(api) | `ResponseRenderedComparer.cs:31-97` + `render.*` delta keys; UI dark |

**reports/visualizations (25)**

| Capability | Res | Basis |
|---|---|---|
| reports.dashboard | D | `AuditDashboard.tsx` complete, unmounted (02 B1) |
| reports.chart-filters | D | `crossFilter.ts` unwired; `onCrossFilter` never bound (02 B9) |
| reports.issue-explorer | D | `coreExplorers.tsx:156` unmounted (02 B2) |
| reports.url-explorer | D | `coreExplorers.tsx:22` unmounted |
| reports.link-explorer | D | `coreExplorers.tsx:220` unmounted |
| reports.visualizations | D | `VisualizationSurface.tsx` unmounted (02 B3) |
| reports.link-equity | D | calculator + `metric.link-equity-weighted` live; UI toggle in dead graph |
| reports.graph-lod | D | `graphSampling.ts:85` + banner in dead surface |
| reports.extraction-explorer | D | `coreExplorers.tsx:292` unmounted |
| reports.scores-view | D | `ScoreTrendSurface.tsx` unmounted |
| reports.suppressed-view | A | see B1-05 |
| reports.serp-preview | R | `tools/serp/SerpPreview.tsx:358-470` wired (local-only by design, 02 C3) |
| reports.header-conflict-panel | D | URL-detail drawer (`coreExplorers.tsx:89`) unmounted |
| reports.exports | P | writers complete (`CsvExportWriter`, `ExcelExportWriter`, `HtmlReportWriter`, `PdfSummaryWriter`); UI never calls `exports.*` (→ B3-03) |
| reports.export-jobs | P | `ExportsController.cs:45-122` incl. download; "native file dialog" pickers dead (→ B3-06) |
| reports.export-datasets | R(api) | `ExportModels.cs:13-48` + `ExportDatasetReader.cs:80-93` (all 5 additive keys) |
| reports.destinations | R | 02 `tools.destinations` wired (CRUD+test, `DestinationsManager.tsx:73-133`) |
| reports.query-builder | R | standalone wired (`QueryBuilderTool.tsx:259-290`); grid-integrated panel dark (02 `grid.query-builder`) |
| reports.saved-queries | R | `rest.queries.*`; standalone tool wired (only `queries.update` caller) |
| reports.grids | D | `DataGrid.tsx` complete, unmounted with ExplorerHost |
| reports.theme | R | 02 `settings.theme` wired |
| reports.dockable-panes | R | 02 `shell.pane.layout` (split panes + persisted layout) |
| reports.spotlight | R | 02 `shell.palette.*` wired |
| reports.standalone-tools | R | ToolsWorkspace: robots/url-check/SD-check/render/sitemap/query-builder wired (02) |
| reports.selector-picker | R | `SelectorPicker.tsx:67-300` reachable in SPA/RenderTester/Scope tools |

**intelligence/SEO modules (28)**

| Capability | Res | Basis |
|---|---|---|
| intel.waf-detection | R | `WafDetectionRecorder.cs:16` + verdicts in SPA/url-check/render-test (02) |
| intel.waf-signature-manager | R(api) | `WafController.cs:156-257` CRUD; promised Settings manager UI absent (02 §A) |
| intel.waf-alerts | P | rule keys + `metric.waf-block-rate` + report badges wired (02); dashboard panel/banner/preset/preview dark |
| intel.waf-render-resolution | R | `WafRenderResolution.cs:37-84` (off by default) |
| intel.content-scoping | R | `ContentScopeResolver.cs:141` + scope diagnostics in SPA (02) |
| intel.scope-preview | R | `tools.scope-preview` wired (02 `tools.scope-preview`) |
| intel.scope-profiles | R(api) | `ContentScopeProfileStore.cs:12` + project wire profiles |
| intel.ngrams | R(api) | `NgramsController.cs:43-53`; explorer dark |
| intel.ngram-cannibalization | R(api) | `NgramsController.cs:107` + `rule.content.keyword-cannibalization` |
| intel.ngram-gaps | R(api) | `NgramsController.cs:144` + `rule.content.topical-gap` |
| intel.stopwords | P | see B1-10 |
| intel.semantic-embeddings | R(api) | `SemanticsController.cs:52` + `POST /api/settings/embedding/test` (`:329`); UI dark |
| intel.semantic-cannibalization | R(api) | `SemanticsController.cs:158` + `rule.content.semantic-cannibalization` |
| intel.semantic-link-opportunities | R(api) | `SemanticsController.cs:113` + `exportds.semantic-opportunity` |
| intel.semantic-low-relevance | R | `rule.linking.low-relevance-link` (`FindingRuleKeys.cs:56`) |
| intel.geo-llms-txt | R(api) | `GeoController.cs:41` + `rule.geo.llms-txt-*` |
| intel.geo-governance | R(api) | `GeoController.cs:80` + `AiBotGovernanceMatrix.cs:31,152` |
| intel.geo-generator | R | llms generate/validate tools wired (02 `tools.llms-*`) |
| intel.martech-inventory | R(api) | `MarTechMetricContributions.cs:49` + analytics route; explorer dark |
| intel.martech-datalayer | R(api) | `DataLayerInspector.cs:14` + `ElementGroups.DataLayerSnapshot` |
| intel.martech-consent | R(api) | `ConsentModeAnalyzer.cs:17` + `metric.consent-compliance` |
| intel.martech-rules | R | `RuleDefinition.cs:72-76` (5 `analytics.*` keys seeded) |
| intel.logs-import | R(api) | `LogsController.cs:63-127`; UI absent (emptyHint points at phantom surface, 02 B15) |
| intel.logs-bot-verification | R(api) | `logs.verify-dns` (`LogsController.cs:179`) + tiered catalogue |
| intel.logs-correlation | R(api) | `LogsController.cs:137-168` + `rule.logs.*`; analyzer UI dark |
| intel.serp-pixels | R | `ContentQualityAnalyzer.cs:279` (`metric.serp-truncation`) + SERP tool |
| intel.header-html-equivalency | R(api) | `HeaderHtmlEquivalencyAnalyzer.cs:12-242` + 3 conflict keys; panel dark |
| intel.link-zones | R(api) | `link_edges.dom_zone` + project `zoneWeights` merge (`ProjectWireMapper`) |

**automation/AI agents (8)**

| Capability | Res | Basis |
|---|---|---|
| automation.mcp-server | P | server exists but "off by default, enabled in settings" false (→ B2-13/B4-03) |
| automation.mcp-tools | R | `McpToolSurface.cs:89-110` — 8/8 promised names exact |
| automation.mcp-resources | R | `McpResourceSurface.cs:167-181` — 15/15 promised names exact |
| automation.mcp-prompts | R | `McpPromptSurface.cs:107-116` — 5/5 exact |
| automation.mcp-transports | R | stdio adapter (`mcp-stdio-adapter\src\index.ts:25`) + SSE (`McpLoopbackServer.cs:236`) + token rotation via `McpUpdateRequest.Token` |
| automation.delegated-flow | R | dispatcher (`McpJsonRpcDispatcher.cs:170-281`) + bounded resources + prompts |
| automation.action-log | P | write + prune exist; no read path (→ B3-02) |
| automation.mcp-boundary | R | enforced by tool surface (`McpToolSurface.cs:11-18`) + initialize instructions |

**diagnostics (9)**

| Capability | Res | Basis |
|---|---|---|
| diagnostics.logs | R | `DiagnosticsController.cs:37-71` + settings table wired (02) |
| diagnostics.minidumps | R | `MinidumpCrashHandler.cs:171` + bounded registry |
| diagnostics.artifacts-api | R | `GET`/`DELETE api/diagnostics/artifacts` (`:37,:61`) |
| diagnostics.metrics | R | `GET /metrics` (`HealthController.cs:70`) + `MetricFamilyCatalog.cs:14-56` (15 families = 9 documented groups) |
| diagnostics.status-bar | R | 02 `shell.statusbar.*` wired on `event.system-health` |
| diagnostics.compact | R | 02 `shell.system.compact` wired (`CompactAction.tsx:78`) |
| diagnostics.health-snapshot | R(api) | `SystemController.cs:64`; UI never calls it (benign dup of heartbeat) |
| diagnostics.recovery-screen | P | recovery window + probes wired (02 `electron.window.recovery`) but renderer shows generic shell (`?route=` ignored, 02 B14) and retry IPC unconsumed |
| diagnostics.retention | P | bounded retention (`DiagnosticArtifactRegistry.cs:202`) + purge isolation ✓; "disk usage in storage reporting" absent (same root as B1-07) |

**settings (7)**

| Capability | Res | Basis |
|---|---|---|
| settings.api | R | 02 `settings.get` wired; secrets masked (`SettingsController.cs:155` per 01) |
| settings.database-provider | P | test-connection wired (`:257`); provider **switch** path dark (orchestrator never composed → B1-08 root) |
| settings.migrations | R | `MigrationLifecycleParticipant` (`ExecutionModule.cs:35`) + ledger |
| settings.user-dictionary | R(api) | `DictionaryController.cs:37-97`; UI absent |
| settings.mcp-config | R | settings MCP block wired (02 `settings.mcp`, token display); toggle inert → B4-02 cross-ref |
| settings.ui-language | R | 02 wired |
| settings.thresholds | P | see B1-06 |

**extensions/plugins (8)**

| Capability | Res | Basis |
|---|---|---|
| plugins.discovery | R | `plugins/plugin-scanner.ts:49` + trust dialog (02 `electron.dialog.plugin-trust`) |
| plugins.manifest | R | `manifestValidator.ts:39-170` + registry validation |
| plugins.sandbox | R | `sandboxFrame.ts:86-95` + `sandboxBridge.ts:100-229` |
| plugins.hooks | R | `pluginBus.ts:40-230` (budgets + coalescing) |
| plugins.contributions | D | `grid/contributions.ts:22-70` consumed only by unmounted grids (02 `plugins.grid-contributions`) |
| plugins.permissions | P | read-data/ui-contributions grants persist (`PluginsController.cs:53`); **network** grants are unpersisted local consent (02 C6) |
| plugins.management | R(api) | `PluginsController.cs:41-163`; management UI unmounted (02 B4) |
| plugins.integrity | R | SHA-256 hashes + re-consent (`PluginsController.cs:95`, `plugins/integrity.ts:19`) |

**API/MCP surface (4)**

| Capability | Res | Basis |
|---|---|---|
| api.rest-conventions | R | `ApiControllerBase.cs` envelope/pagination/validation |
| api.health-version | R | `HealthController.cs:36-46` |
| api.hub | R | `/hubs/realtime` (`CrawlForgeHub.cs:20`) + open-string catalog |
| api.errors | R | idempotent duplicate-start + field errors (`ApiControllerBase`, `AuditsController`) |

---

## 3. B2 register — promise ↔ implementation mismatches

| ID | Finding | Evidence | Impact | Severity | Remediation |
|---|---|---|---|---|---|
| B2-01 | Promised REST ops ≈89/89 are all implemented (03 §3.1 rows ↔ 01 §1). Guide-only `GET /api/schedules` = **promise kept, spec silent** (`SchedulesController.cs:95` `HttpGet("schedules")`) | 03 §3.1 + §4.1; 01 §1.17; source verified | No missing ops; docs more accurate than specs | low (informational) | Add the route to the spec route table |
| B2-02 | Five implemented REST ops documented **nowhere** in 03: `GET /api/exports/{id}` (01 §1.15), `POST /api/destinations/{id}/deliver` (01 §1.16), `POST /api/system/shutdown` (CrawlForgeBackend.cs:131), `GET /hub` stub (CrawlForgeBackend.cs:125), `GET /api/diagnostics/version` (CrawlForgeBackend.cs:126) | 03 §3.1/§3.2 omit them; 01 lists all five | Undocumented surface (esp. shutdown); drift between docs and exposure | medium | Document or remove; add shutdown to the route contract (see B3-06) |
| B2-03 | Schedule `overrides` field promised (03 `audits.schedule`: "fields name/project/config/enabled/**overrides**/completion actions/next-due/last-run") but absent from the wire: create/update/read contracts carry no limits field | `OperationsWireContracts.cs:90-117` (ScheduleWire/Create/Update: no overrides); entity supports it (`SchedulingEntities.cs:24` `LimitOverrides`; honored at firing `ScheduledAuditService.cs:345`) but `SchedulesController` grep `LimitOverrides\|Overrides` = 0 | Per-schedule limit overrides documented and persisted but can never be set by any client | medium | Add `limitOverrides` to schedule wire contracts |
| B2-04 | Per-audit limit-override field mismatch: guide names 6 (`maxUrls, maxDepth, maxDurationSeconds, maxConcurrency, requestsPerSecond, fetchTimeoutSeconds`); REST wire accepts 4 (`MaxUrls, MaxDepth, MaxDurationSeconds, MaxResponseBytes`) — 3 promised names missing, 1 undocumented name added; MCP `start-audit` deserializes the full 6-field entity | `ProjectWireContracts.cs:8` `CrawlLimitsWire`; `AuditsController.cs:136-138`; `PolitenessContracts.cs:39` (6 fields); `McpToolSurface.cs:121-124` | REST clients cannot set concurrency/rps/timeout overrides despite docs; channel asymmetry REST≠MCP | medium | Extend `CrawlLimitsWire` to the promised 6 names (keep maxResponseBytes additive) |
| B2-05 | Logs-correlation `view` values renamed: promised `summary\|orphans\|waste\|uncrawled`; actual `summary\|orphans\|budget-waste\|uncrawled-indexable` (promised `waste`/`uncrawled` rejected) | 03 §3.1 additive params; `LogsController.cs:297-310` (`TryParseView`); stale internal comment `LogReadContracts.cs:86` still says `view=summary\|orphans\|waste\|uncrawled` | Documented parameter values 400; internal doc/code drift | low | Accept the promised aliases or fix docs + comment |
| B2-06 | Analytics `view` value renamed: promised `view=inventory\|consent\|findings`; actual `tags\|consent\|findings` (`inventory` → `tags`) | 03 §3.1; `AnalyticsController.cs:57-59` ("Use tags, consent, or findings") | `view=inventory` 400s | low | Accept `inventory` alias or fix docs |
| B2-07 | Promised `scope=site\|url` on `GET /api/audits/{id}/ngrams` does not exist; REST switches mode via `urlId`, while the MCP resource *does* accept `scope` | 03 §3.1; `NgramsController.cs:46-53` (query: urlId/zone/n/minCount/sort/order); `McpResourceSurface.cs:174` (query zone/order/scope) | Doc-following REST clients send an ignored param; REST/MCP parameter drift | low | Add `scope` alias to REST or document `urlId` semantics |
| B2-08 | Hub events: all 19 promised event names are emitted, incl. guide-only `local-notification` (**promise kept, spec silent** at the API layer). But the delivered behavior of the `local-notification` completion action is broken end-to-end (no consumer) — see B3-04. Promise-vs-reality verdict: `GET /api/schedules` = kept; `local-notification` = kept-but-unconsumed → **gap at delivery** | 03 §3.2; `HubEventCatalog.cs:16-81` (19 events + subscribe/unsubscribe); `ScheduleHubEvents.cs:22,116` | Docs' "OS notification" completion action never reaches the user | **high** (as delivery gap) | Wire the renderer/shell consumer (B3-04) |
| B2-09 | MCP names: promised 28 (8 tools + 15 resources + 5 prompts) vs actual 28 — **exact parity, no renames** | 03 §3.3; `McpToolSurface.cs:89-110`, `McpResourceSurface.cs:167-181`, `McpPromptSurface.cs:107-116` | none | low (informational) | none |
| B2-10 | Metric keys: promised 20 vs `MetricKeys.cs:8-68` = the same 20, each with a verified writer (`WafBlockRateDiagnostic.cs:101-102`, `NGramRuleAnalyzer.cs:175-181`, `LogCorrelationEngine.cs:250-253`, `GeoAggregateRecorder.cs:50-103`, `SemanticLinkingPipeline.cs:369-370`, `MarTechMetricContributions.cs:56-78`, `WeightedLinkEquityCalculator.cs:206`, `ContentQualityAnalyzer.cs:273-279`, `AimdMetricSummarizer.cs:72`, `RedirectMatrixResults.cs:155-199`, `AuditPipeline.cs:209`). `/metrics` emits 15 families = the 9 documented family groups (`MetricFamilyCatalog.cs:14-56`) | 03 §3.5/§2.8; source verified | Parity complete | low (informational) | none |
| B2-11 | Rule keys: promised 30/30 present (`FindingRuleKeys.cs:11-98` + emitters). But the promised 11-category core taxonomy is folded into 8 (`AuditEnums.cs:27-61`: i18n/mobile/tech under `StructuredData`; no Images or Security category) and the Security family has zero rules (B1-02), Images is partial (B1-03) | 03 §3.6 + `audits.rule-taxonomy`; source verified | Category filters/labels in docs don't match the shipped taxonomy; security checks silently missing | **high** (via B1-02) | Align taxonomy vocabulary; ship or strike Security/Images |
| B2-12 | Completion-action kinds 3/3 implemented exactly as documented (`local-notification`, `export-delivery`, `notification-endpoint`), validated at save (`ScheduleCompletionAction.cs:107-132`); export formats `xlsx/csv/html/pdf` ✓. Delivery reality: `local-notification` unconsumed (B3-04); `export-delivery` reachable only through schedules which have no UI (B3-03) | 03 §3.4; `CompletionActionExecutor.cs:82-112`; `SchedulesController.cs:424-482` | Kinds exist but two of three cannot produce a user-visible outcome from the shipped UI | **high** (via B3-03/04) | Fix consumers; add schedules UI |
| B2-13 | MCP transport promise "off by default, enabled in settings" / "disabling closes listeners and terminates adapters" is false: the loopback MCP server and its endpoint+token descriptor are started unconditionally at backend startup regardless of `mcpAutomationEnabled:false` and regardless of the settings toggle | 03 `automation.mcp-server`/`mcp-transports`; `McpModule.cs:86-89` (unconditional registration), `McpLifecycleParticipant.StartAsync` `:127-141` (starts server, writes `mcp-endpoint.json` incl. plaintext token); settings toggle only persists JSON (`SettingsController.cs:492-516`) — no reader | Automation surface is always listening (loopback+token); documented enable/disable contract does not exist | **high** | Gate server start/stop on the settings toggle; honor disable |
| B2-14 | Promised configurable threshold set mostly unexposed → see B1-06 (same root) | — | — | medium | — |

---

## 4. B3 register — channel parity gaps

| ID | Finding | Evidence | Impact | Severity | Remediation |
|---|---|---|---|---|---|
| B3-01 | MCP 8 lifecycle tools vs 114 REST actions is **intentional design**, not an accidental burden: spec §11.1 declares the explicit MCP boundary ("no settings, destinations, credentials, proxy, DB/embedding providers, plugins, log imports, migration matrices, GEO tools, analytics config, schedule management, configuration editing, or export/delivery via MCP"), enforced in code | 03 `automation.mcp-boundary` (§2.7) + guide "What agents can and cannot do"; `McpToolSurface.cs:11-18`; `McpJsonRpcDispatcher` initialize instructions "Loopback-only; no settings, credential, or configuration management is exposed" | none (parity expectation must not be promised in UX copy) | low (intentional-design) | Keep boundary documented in any automation UX |
| B3-02 | Automation action log is **write-only**: every MCP invocation is recorded (`McpJsonRpcDispatcher.cs:27,152` → `AutomationActionLog.cs:34`), retention prunes it (`RetentionPruner.cs:124`), `IAutomationActionLog.ListAsync` exists (`AutomationLogContracts.cs:25`, `AutomationActionLog.cs:44`) — but no REST/hub/IPC endpoint ever calls it (grep `IAutomationActionLog` shows only MCP module + DI; frontend `action-log` grep = 0) | 03 `automation.action-log` promises "user-viewable and purgeable"; 01 §5.5 confirms | Documented automation history is unviewable; agents' actions unauditable by the user | medium | Expose `GET/DELETE /api/automation/action-log` + settings view |
| B3-03 | Export pipeline disconnected end-to-end: `exports.create`/`exports.download` route keys exist only in the contract catalog (`routes.ts:69-70`) with **zero call sites**; the only "export" UX is a clipboard JSON handoff (`ExplorerHost.tsx:305-320`) and local PNG chart downloads (`EChart.tsx:96-100`); Destinations CRUD/test is wired (`DestinationsManager.tsx`) but delivery is reachable only via schedule `export-delivery` actions — and no schedules UI exists (02 §A: 9 schedule routes unused) | 02 §C1/C2 + §A; verified grep `exports\.(create\|download\|get)` over frontend = catalog-only; `ExportsController.cs:45-122` + `ExportDatasetReader.cs` complete server-side | The documented export story (xlsx/csv/html/pdf jobs, native download, destination delivery) is unusable from the shipped product | **high** | Mount export dialog wired to `exports.create`→progress→`exports.download`; surface schedules+completion actions |
| B3-04 | `event.local-notification` has no consumer anywhere: backend publishes it (`HubEventCatalog.cs:75`, `ScheduleHubEvents.cs:22,116` "publishes a local-only hub event"), but the frontend hub contract declares only 18 server events (`contracts/hub/events.ts:316-333`, grep `local-notification` over `app\src\frontend` = 0), and the OS-notification IPC (`crawlforge:notification`, `main.ts:342`) has zero renderer callers (02 B12). The `LocalNotification` type exists only as an orphan in `electron/src/types.ts:114-118` | 03 `audits.completion-actions` ("OS notification") + §3.2; source verified | The documented `local-notification` completion action silently produces nothing | **high** | Handle the event in hubDemux → `window.crawlForgeShell.showNotification` |
| B3-05 | Hub-vs-REST duplicate reads: `event.system-health` (2 s heartbeat) vs `GET /api/system/health` (identical payload, REST never called — 02 §A `system.health`); `GET /api/diagnostics/version` (GC profile + feature report) vs `GET /api/version` (product/schema/runtime) — different payloads, the former undocumented (B2-02); `GET /hub` stub vs `GET /api/health` (both supervisor probes) | 01 §5.5; 02 §6; verified `SystemController.cs:64`, `CrawlForgeBackend.cs:125-126` | Benign over-exposure; cross-references flag "API without UX" falsely | low | Document the probe trio; drop or alias `diagnostics/version` |
| B3-06 | Electron native layer built but dark: 11 IPC channels with 1 consumed (`crawlforge:get-bridge`); the whole `window.crawlForgeShell` bridge (file dialog, notification, taskbar progress, window controls, backend restart, diagnostics purge) has zero renderer consumers; `signalHydrated` never sent (startup handshake times out); `?route=` ignored so 5 of 8 catalogued window kinds would render the hub shell anyway; native file dialogs never hooked to log import / matrix upload / sitemap save / export download | 02 §4 B12-B14 + §5 C8-C10; verified `preload.ts:39-56`, `window-catalog.ts:39-120`; docs promise native download dialog (03 `reports.export-jobs`) and OS taskbar progress (03 §4.2, spec §12) | Documented native-UX duties (dialogs, taskbar progress, recovery retry, hydration gating) have no working path | **high** | Consume the bridge from the renderer (export download dialog, taskbar during crawls, recovery retry, hydration signal) |
| B3-07 | `Unsubscribe(string[])` hub method is never invoked — per-audit channel groups accumulate for the session | 02 §B11; `hubClient.ts:182`; `CrawlForgeHub.cs:55` | Unbounded group membership on long sessions | low | Call unsubscribe on audit close/navigation |
| B3-08 | REST `GET /api/audits/{id}/hosts` loader (`loadHosts`) is never invoked; the wired throttle table is fed only by `event.host-throttle`, so history (`includeHistory`) is unreachable from the UI | 02 §B10 + `audits.report.host-throttle`; `HostsController.cs:43-56` | Host throttle history exists via REST/MCP but not in the live table | low | Invoke loader on panel mount |

---

## 5. B4 register — exposure-governance findings

| ID | Finding | Evidence | Impact | Severity | Remediation |
|---|---|---|---|---|---|
| B4-01 | `/hub` stub is ungated while its own doc-comment claims otherwise: the middleware gate matches `/api/` (minus `/api/health`), `/hubs/`, `/metrics` — literal `/hub` falls through; `CrawlForgeBackend.cs:125` maps it; `SessionTokenMiddleware.cs:15-17` remarks claim "The stub `/hub` descriptor and `/api/diagnostics/version` remain token-gated" | `SessionTokenMiddleware.cs:46-53` (predicate) vs `:15-17` (claim); `CrawlForgeBackend.cs:125` | Ungated static descriptor (`name/status/protocol: hub-stub`) on loopback; low data risk, but the false comment is a trap for future changes and audits | medium | Gate `/hub` (or fix the comment) |
| B4-02 | Feature toggles are doubly inert: (a) `LocalSettings.FeatureToggles` deserializes JSON key `featureToggles` (`LocalSettings.cs:12`, Web naming) while the seed writes `localFeatureToggles` (`FirstRunSeed.cs:32`) — and the seed lands in the DB `ui_preferences_json` row while `LocalFeatureAvailability` reads the `settings.json` **file** (`LocalFeatureAvailability.cs:19-26`); (b) `LocalFeatureAvailability.IsEnabled` (`:39`) has **zero callers** (grep `IFeatureAvailability` = definition + DI registration `CrawlForgeBackend.cs:102` only); (c) `mcpAutomationEnabled:false` is seeded and never read anywhere (grep = 1 hit, the seed itself) | source verified as cited | "Toggles" in any UI/settings would move dead switches; no capability is actually flag-gated; seeded state never loads | medium | Fix key + storage location, call `IsEnabled` at the gating points, or delete the mechanism |
| B4-03 | MCP server starts unconditionally and publishes its endpoint **and plaintext bearer token** to `mcp-endpoint.json` in the data dir at every startup (`McpLifecycleParticipant.StartAsync`, `McpModule.cs:127-141`), despite docs promising off-by-default and token protection; the settings `mcp.enabled` flag is display-only (`SettingsController.cs:492-516`) | `McpModule.cs:86-89,127-141`; `CrawlForgeBackend.cs:163` (`StartAllAsync` starts all `IBackendLifecycle` with no toggle) | Loopback automation surface is always live and its token always written to disk; contradicts documented exposure model | **high** | Gate startup on the toggle; treat descriptor publication as opt-in |
| B4-04 | `POST /api/system/shutdown` reachability: session-gated (starts `/api/`; comment at `CrawlForgeBackend.cs:128-131` is accurate here); sole caller is the Electron supervisor's graceful-stop (`backend-supervisor.ts:162-174`, sends the session token); **no row in `contracts/api/routes.ts`** (02 C9) and no UI control | source verified as cited | Not UI-reachable; any loopback process holding the session token can stop the backend; contract catalog omits it so client tooling cannot see it | medium | Add route-contract row; consider tying shutdown to the owning shell PID |
| B4-05 | Diagnostic artifacts are listable and purgeable but never downloadable: `DiagnosticsController.cs:24-91` exposes only `GET` (metadata: kind/fileName/createdAt/size) and `DELETE`; no file/download route exists anywhere; access path is the on-disk directory only | `DiagnosticsController.cs:37-71`; wire `DiagnosticArtifactWire` (`:13`) exposes `FileName` only | Minidumps/logs cannot be retrieved programmatically; troubleshooting requires filesystem access (docs acknowledge "pointer to the crash-artifact directory") | low | Optional signed local download, or keep and document directory access |
| B4-06 | Single-token auth with zero roles: `SessionTokenMiddleware.cs:46-65` is the only gate (no `[Authorize]`/roles/identity anywhere — grep verified; all "Role" hits are ARIA/MCP message roles). One token therefore unlocks admin-class endpoints: `system.shutdown`, `settings/database/test` (**the only outbound-network endpoint**, `SettingsController.cs:257,117-124`), `system.compact`, `diagnostics` purge, `plugins/rescan`, `logs/{id}/verify-dns` | source verified as cited | Acceptable for a single-user loopback desktop, but there is no privilege separation: a token leak (e.g. via the plaintext `mcp-endpoint.json`/handshake file exposure) grants full admin surface | medium | Document the model; if multi-role ever lands, split read vs admin tokens |

---

## 6. Disconfirmed / adjusted

| # | Inventory claim | Verdict | Evidence |
|---|---|---|---|
| D-01 | Candidate mismatch "`cron` grammar vs real cron syntax" | **Disconfirmed as a gap.** Docs and code agree: the wire field is named `cron` but explicitly "is not cron syntax"; the parser implements exactly the promised `interval:<timespan>` / `calendar:HH:mm[;days=…]` grammar | `RecurrenceRule.cs:20-23,104`; `SchedulesController.cs:15,136` ("locked local grammar (interval:/calendar:) carried under the wire field name cron") |
| D-02 | 02 "Hub wire names: 20 (18 server events + subscribe/unsubscribe)" vs 01 "Hub: 21 (2 + 19)" | **Adjusted — both correct, join artifact.** Backend catalog = 19 events (`HubEventCatalog.cs:16-75`); client contract/hubDemux = 18; the delta is `local-notification`, which is produced but unconsumed → filed as B3-04 | `contracts/hub/events.ts:316-333`; `hubDemux.ts:295-303` |
| D-03 | 03 §4.2 "spec's §7.3 'Tables' extraction group has no guide chapter" (risk of being read as absent) | **Disconfirmed as absence.** Tables extraction is implemented | `ElementInventoryExtractor.cs:92` (`Item("tables", …)`) |
| D-04 | 03 §4.2 "optional embedded preview of crawled pages and media codecs" | **Adjusted — not filed as a gap.** Vague spec wording; preview needs are met by the content store + sandboxed artifact preview + render screenshots (02 `tools.single-page.render-inspect`, `tools.selector-picker`) | `IArtifactFileStore`; `sandboxFrame.ts` |
| D-05 | 02 dashboards request chart metrics `score-trend` and `log-hits` | **Adjusted — dark-UI defect, not a docs gap.** No writer emits those metric keys (`MetricKeys.cs` has neither; nearest are `rest.audits.scores` and `metric.log-bot-hits`); series would render empty if the dashboards were mounted | `IntelligenceDashboards.tsx` rows in 02; `LogCorrelationEngine.cs:253` |
| D-06 | Candidate B1 "OS taskbar progress (spec §12) not mentioned in the guide" | **Adjusted — not doc-only.** IPC handler + bridge method exist (`main.ts:348`, `preload.ts:43-44`); it is implemented+dark, filed under B3-06 | 01 §4; 02 §B12 |
| D-07 | 01 §5.2 middleware remark "the `/hub` stub remains token-gated" | **Confirmed as a doc/code discrepancy** (the code comment is wrong, the inventory's warning is right) → B4-01 | `SessionTokenMiddleware.cs:15-17` vs `:49-53` |
| D-08 | Guide-only promises (03 §4.1) | **Split verdict:** `GET /api/schedules` = promise kept, spec silent (B2-01); `local-notification` = promise kept at the event layer, **broken at delivery** (B2-08/B3-04) | `SchedulesController.cs:95`; `HubEventCatalog.cs:75` vs `events.ts:316-333` |
| D-09 | 03 §6.1 overloaded-name collision risk | **Adjusted — resolved by surface suffixes (§1);** no name collision produced a false gap. One nuance: `waf-block-rate` exists as `rule.*` **and** `metric.*` but is not a REST/hub name | `MetricKeys.cs:14`; `FindingRuleKeys.cs:14` |
| D-10 | 01 §3.1 "MCP tools map 1:1 onto REST start/pause/resume/cancel/reaudit/single-page/run-now" | **Confirmed** — 8 tools ↔ REST ops 1:1 (get-audit-progress ↔ `GET /api/audits/{id}` derived progress) → supports the B3-01 intentional-design ruling | `McpToolSurface.cs:89-110` |

---

## 7. Non-gap appendix — explicitly future items (NOT current gaps)

Per 03 §5 (FS-CRAWL-001 §15 open scope decisions; guide README confirms they are deliberately not
documented as available features). None of these is counted in any register or statistic:

1. **Unattended/headless execution** (CLI start without a window) — reserved; "Not a headless command line".
2. **Manual product-update path** — reserved; "No update checking of any kind exists in this version".
3. **Third-party search/analytics/backlink data enrichment** (incl. optional CWV field-performance data) — reserved to the product owner; note the shipped CWV *lab* metrics (`audits.cwv`) are a different, implemented capability.
4. **AMP analysis** — "Not required by this version".
5. **Raw-SQL administration tool** — "Not required by this version" (query builder covers querying).

---

## 8. Statistics

**Register counts**

| Category | Rows | critical | high | medium | low |
|---|---|---|---|---|---|
| B1 doc-only promises | 10 | 0 | 2 | 5 | 3 |
| B2 promise↔implementation | 14 (incl. 3 informational parity rows) | 0 | 3 | 5 | 6 |
| B3 channel parity | 8 | 0 | 3 | 1 | 4 |
| B4 exposure governance | 6 | 0 | 1 | 3 | 2 |
| **Total** | **38** | **0** | **9** | **14** | **15** |

**Resolution histogram (170 documented capabilities)**

| Resolution | Count |
|---|---|
| R — implemented + reachable | 84 |
| R(api) — API-reachable, promised UI missing/dark | 51 |
| D — implemented but dark (no reachable channel) | 14 |
| P — partial/divergent | 19 |
| A — absent | 2 |
| **Total** | **170** |

By resolution, the `A` rows are `audits.rules-security` (B1-02) and `reports.suppressed-view` (B1-05);
the `D` rows are concentrated in reports (12 unmounted dashboard/explorer/graph surfaces), plus
`projects.archive-transfer` (B1-08) and `plugins.contributions`.

**Channel parity summary**

| Surface pair | Parity |
|---|---|
| Promised REST ops vs actual | 89/89 present (100%); +5 implemented-but-undocumented ops (B2-02) |
| Promised hub events vs actual | 19/19 names emitted (incl. guide-only `local-notification`); 18/19 consumed by the client (B3-04) |
| Promised MCP items vs actual | 28/28 exact names (8 tools + 15 resources + 5 prompts) |
| Promised completion-action kinds vs actual | 3/3 implemented; 2/3 delivery paths broken/hidden (B3-03/04) |
| Promised metric keys vs recorded | 20/20 (`MetricKeys.cs` + verified writers); `/metrics` = 15 families ≙ 9 documented groups |
| Promised rule keys vs actual | 30/30 present; taxonomy 11→8 categories folded; Security family 0/5 checks (B1-02); Images 2/4 (B1-03) |
| Electron IPC | 11 channels, 1 consumed; 5/8 window kinds unopened (B3-06) |
| UX route reach (02) | 62/108 route keys reached; 46 unused — consistent with 51 `R(api)` rows above |

**Top 8 findings by severity**

1. **B1-02 (high)** — promised Security & protocol rule family is entirely unimplemented (zero rule keys, zero analyzers) while the docs advertise the category.
2. **B2-13 / B4-03 (high)** — MCP loopback server + plaintext-token descriptor start unconditionally, contradicting "off by default, enabled in settings"; the settings toggle is display-only.
3. **B3-03 (high)** — export pipeline disconnected end-to-end: `exports.create/download` have zero UI callers; "export" UX stops at a clipboard JSON; destinations can be configured/tested but only schedule actions (no UI) can ever deliver.
4. **B3-04 (high)** — `local-notification` completion action is a dead-end: emitted on the hub, absent from the client event contract, OS-notification IPC unconsumed.
5. **B1-08 (high)** — project archive export/import (documented backup + DB-provider migration vehicle) is fully implemented but never composed into the host and exposed via no channel.
6. **B3-06 (high)** — the Electron native layer (10 of 11 IPC channels, 5 of 8 window kinds, hydration handshake, native file dialogs, taskbar progress) is built but entirely dark.
7. **B4-02 (medium)** — feature toggles doubly inert (seeded key never loads from the wrong storage; `IsEnabled` has zero callers; `mcpAutomationEnabled` never read).
8. **B3-02 (medium)** — automation action log is write-only: recorded and pruned but unviewable/unpurgeable despite the documented "user-viewable and purgeable" promise.
