# CrawlForge — Application Underbuild Audit (`app` vs `architecture`)

| Document control | Value |
|---|---|
| Identifier | CF-UNDERBUILD-AUDIT-001 |
| Audit date | 2026-11-17 |
| Scope | `E:\Crawlforge\app` (implementation) reconciled against `E:\Crawlforge\architecture` (authoritative specification) |
| Method | 7 parallel domain audits (full spec files read, production code traced, contracts/DI/wiring checked) + independent build/E2E/governance checks |
| Build-ledger claim under test | `buildplan/index.yaml`: 8/8 phases, 43/43 workpieces, 48/48 acceptance criteria "completed" |
| Overall verdict | **NOT 100% BUILT.** The library layer is substantial and often excellent, but integration/wiring is materially incomplete. The "all done" ledger is accurate for *inventories* (routes, tables, tool counts, files) and unreliable for *runtime behavior*. |

---

## 1. Executive summary

The implementation is **not a scaffold**: most algorithms, services, contracts, schemas and React components described in the architecture exist as real code. However, the audit found a consistent pattern that the ledger conceals:

> **Complete-looking subsystems exist as libraries and pass their unit tests, but are never registered, instantiated, invoked, or mounted in the running product.**

Four product-breaking (P0) underbuilds and thirteen high-severity (P1) underbuilds were confirmed with code evidence. The most consequential:

1. **The entire report half of the product is invisible.** Of 16 baseline report surfaces, 10 have no React component; the other 6 plus all 6 extended surfaces are defined but never mounted. The report area is a literal text placeholder saying the grids/charts/graphs "mount … from the report workpieces".
2. **The local plugin system does not run.** The Electron scanner is constructed with a `null` registry delegate ("inert insertion point"), no `PluginRegistry`/`PluginManager` is instantiated, the renderer never calls `signalHydrated`, and the management UI is never rendered. Acceptance criterion #39 is asserted by tests but not live.
3. **Rendering is inert during full audits.** `AuditExecutionEngine` hardcodes `RenderedDocument: null`; `IRenderingPool` is never injected. JS-heavy pages, post-render DOM checks, and render-based WAF resolution never occur in a real audit.
4. **Rendered Core Web Vitals / performance is dead code.** `CoreWebVitalsAnalyzer`, `PerformanceBudgetEvaluator`, `CodeCoverageAnalyzer`, `PageCompositionAnalyzer`, `LabRenderingProbe` are never registered or called; TTFB is not even modeled.
5. **Real-time and observability are half-connected.** 7 of 19 hub events have no production publisher; 10 of 15 `/metrics` families are declared but never recorded. Tests inject these names manually, so they pass without exercising a producer.

Section 4 is the prioritized master gap register. Sections 5.1–5.7 give domain detail; Section 6 lists what is genuinely solid; Section 7 gives a remediation sequence.

---

## 2. Scope and method

- Read in full: all 26 architecture domain files (via 7 domain agents), with acceptance criteria and open decisions included.
- Traced production code under `app\src\backend` (14 C# projects), `app\src\frontend`, `app\src\electron`, `app\src\mcp-stdio-adapter`, plus test projects.
- Distinguished **Implemented** (production call site exists), **Partial** (component exists but reduced/unwired), **Dead code** (class exists, no production caller), **Absent** (no code), and **Stub**.
- Deliberately did **not** trust the build ledger, acceptance-catalog evidence pointers, or tests that manually inject the thing they validate.
- Several agent findings were independently spot-checked (see Appendix A).

---

## 3. Verification caveats (important)

| Check | Result |
|---|---|
| Historical `e2e-run.log` (14 Nov 22:21) | 63 tests, 3 failed: `McpDelegationE2ETests.ToolLevelFailures_AreLoggedAsFailed` (D7, "expected 2 failed tools/call entries, got 0") and 2 × `MySqlParityE2ETests` (`no such column: contributing_counts_json`). |
| Are those failures current? | **Likely patched after the log**: `ProviderSwitchOrchestrator.cs` (22:34) and `MySqlParityE2ETests.cs` (22:52) now avoid that column; `McpJsonRpcDispatcher.cs` modified 22:23. But **not re-verified** — see next row. |
| Fresh E2E run (`fresh-e2e-run.log`) | **Could not execute.** Build fails in `CrawlForge.Host`: `scoped css bundles do not have matching endpoints: _framework/Microsoft.NET.Sdk.BlazorWeb.DotNetWatch…` under the only installed SDK (.NET 11.0.100-rc preview). Treat as a build-reproducibility concern, not proof of underbuild. |
| Frontend typecheck + production build | **PASS** (`tsc --noEmit` exit 0; `vite build` exit 0, 845 modules). The UI compiles; its problem is wiring, not compilation. |
| Governance gates | All wave gate reports `passed=true, violations=0` at app-root scope; the 289-violation "baseline scan" hits are all in `package-lock.json`/artifacts, not app source. |
| Ledger vs reality | Ledger is correct on inventories (all 51 baseline + 37 additive REST routes present; all 8 MCP tools / 15 resources / 5 prompts present; all 40 logical schema objects present). It is wrong on runtime behavior (Sections 4–5). |

---

## 4. Master gap register (prioritized)

P0 = product-breaking / feature effectively absent. P1 = high. P2 = medium. P3 = low.

| ID | Pri | Gap | Domain | Spec reference | Evidence (abridged) |
|---|---|---|---|---|---|
| G01 | **P0** | Report surfaces never mounted; 10/16 baseline surfaces have no component at all | Frontend | 06/02 §8.2 | `AuditViews.tsx:311-321` literal placeholder; no mount of `ExplorerHost`/dashboards/graphs; `dashboards/README.md:58-68` "Shell wiring (for the integrator)" |
| G02 | **P0** | Plugin system non-functional at runtime (criterion #39) | Foundation + Frontend | 06/03 §6, #39 | `electron/src/main.ts:300` `new PluginScanner(..., null)` "inert insertion point"; no `PluginRegistry`/`PluginManager` in `ShellContext`; `signalHydrated` 0 frontend callers; `PluginManagementSurface` never rendered |
| G03 | **P0** | Rendering inert during full audits | Crawl | 03 §5.6 | `AuditExecutionEngine.cs:1476` `new PageAuditContext(..., null, null, null)`; `_renderingPool` never injected; `IRenderingPool` used only by single-page + tools |
| G04 | **P0** | CWV / performance subsystem entirely dead code (incl. no TTFB) | Core audit | 04/03 §7.11 | `CoreWebVitalsAnalyzer`, `PerformanceBudgetEvaluator`, `CodeCoverageAnalyzer`, `PageCompositionAnalyzer`, `LabRenderingProbe` have zero production callers; `LabVitalsContracts.cs:75-82` |
| G05 | **P1** | 7 of 19 hub events never published | API/Hub | 05/02 §10.3, App. C | `url-batch`, `findings-updated`, `audit-failed`, `backend-lifecycle`, `waf-blocks`, `embedding-progress`, `embedding-completed` exist only as catalog constants |
| G06 | **P1** | 10 of 15 metric families never recorded; absent from `/metrics` | API/Metrics | 05/04 §10.5, §3.7 | `MetricFamilyCatalog.cs:13-32,49-56` declares them; no `Record()` call; `PrometheusMetricsExposition.cs:73-76` skips empty families |
| G07 | **P1** | WAF→AIMD trigger broken; crawl-delay floor and Retry-After not wired | Crawl | 03/01 §3.2/§3.4/§4.4 | `AuditExecutionEngine.cs:708-709`, `HttpFetcher.cs:373-375` pass `IsChallenge: false`; controller created without `crawlDelaySource` (`:587`); `AimdResponseObservation` never constructed in production |
| G08 | **P1** | Sitemap ingestion entirely absent | Crawl | 03 §4.4, 02 §5.1/5.4/5.5 | No sitemap parser/seed expansion in backend (only export `SitemapWriter`) |
| G09 | **P1** | Proxy routing and signed requests have no effect on crawls | Crawl | 03 §5.9/§5.10 | `CrawlTargetHttpClientFactory.cs:15-19` plain handler; `ILoopbackInterceptingProxy`/`ISignedRequestHandler` never supplied to engine fetcher |
| G10 | **P1** | List Mode broken through normal audit creation | Crawl | 02 §10.2 | `AuditsController.cs:140` starts audit before persisting `UrlListJson` (`:154-160`); engine reads only `SeedsJson` (`:636`) |
| G11 | **P1** | Structured-data validation not part of an audit | Core audit | 04/02 §7.13 | `StructuredDataValidator` reachable only from `ToolsController.cs:185`; no per-audit findings/explorer |
| G12 | **P1** | Rule taxonomy mismatched; no Images or Security & protocol rules | Core audit | 04/01 §7.4 | `AuditEnums.cs:27-61` 11 values differ from spec; no mixed-content/HTTPS/security-header/image rules anywhere |
| G13 | **P1** | Custom search / JS snippet advanced options unreachable from configuration | Core audit | 04/04 §7.16/§7.17 | Options in `CustomSearchOptions`/`JsSnippetOptions` never populated; engine parses only minimal records (`AuditExecutionEngine.cs:1151-1159`) |
| G14 | **P1** | Crawl-time `llms.txt` / `llms-full.txt` probe absent (GEO readiness mostly false "missing") | Intelligence | 04/06 §13.1 | 0 `llms` references in `CrawlForge.Crawler`; analyzer only reads pre-existing `url_records` (`GeoReadinessAnalyzer.cs:403-409`) |
| G15 | **P1** | Native-fault crashes produce no minidump; recovery can't list individual dumps | Foundation | 01/02 §2.3, #21 | Hook only at `CrawlForgeBackend.cs:149-160` on managed `UnhandledException`; `recovery-service.ts:67-89` non-recursive scan vs `diagnostics/minidumps/` |
| G16 | **P1** | Divergent test-only MySQL schema authority; live MySQL parity not continuously verified; MySQL capability families unwired | Data | 02 §2.4, rule 6 | `MySqlLogicalSchema.cs:120-144` uses non-existent columns (`schedule_id`, `artifact_kind`, `agent_key`, `import_id`); live tests self-skip; `MySqlDialect` builders only used by tests |
| G17 | **P1** | MCP settings surface disconnected from MCP runtime | API/MCP | 05/03 §11.1/§11.6 | `McpModule.cs:127-141` starts unconditionally (ignores `mcp.enabled`); settings token in `SettingsController.cs` separate from real `FileMcpTokenStore` → rotation doesn't invalidate clients |
| G18 | **P2** | Configuration CRUD invisible in UI; per-project settings cannot be viewed/edited | Frontend (owner note) | `project-configuration-review.md` §1 | Routes/types exist; no screen uses them ("Default configuration" created silently) |
| G19 | **P2** | Plugin capability surface incomplete; no hook timeouts, 3-strike quarantine, 1 MiB storage | Frontend | 06/03 §6.4/§6.5 | `sandboxProtocol.ts:29-39` 6 vs 11 capabilities; no 5000/3000/1000/2000 ms timeouts; `markFailed()` only sets state |
| G20 | **P2** | Window routes / shell duties unused; no splash/recovery/visualization/analyzer renderer content | Frontend | 06/01 §12 | 8 windows catalogued; `App.tsx:22-33` ignores `?route=`; 0 frontend calls to `window.crawlForgeShell` |
| G21 | **P2** | Main-renderer CSP not enforced | Frontend | 06/01 §8.1/§8.9 | No CSP meta in `index.html`; no `onHeadersReceived` handler; only plugin-frame CSP exists |
| G22 | **P2** | Semantic run endpoint violates async-job contract; band 0.5 not 0.75–0.87; cosine in-memory not DB vector; snapshot fields never written | Intelligence | 04/05 §12.4/5/7/8 | `SemanticsController.cs:18` 0.5; `:52-62` synchronous; `SemanticLinkingPipeline.cs:236-257` O(N²) in memory |
| G23 | **P2** | Near-duplicate cross-URL detection dead code | Core audit | 04/01 §7.2 | `ShingleNearDuplicateDetector` has no production caller |
| G24 | **P2** | Content-scope config validation not enforced; per-entry selector language not declared; `text_statistics` full count | Core audit | 04/01 §4.2/4.3 | `ContentScopeValidator` not called by any controller; auto-detect instead of declared language |
| G25 | **P2** | Single-page rendered analysis disables render-dependent checks | Core audit | 04/01 §7.7 | `SinglePageAnalyzer.cs:161` sets `RenderingMode.None` even when rendered DOM produced |
| G26 | **P2** | WAF spec drift: 3 facets not 5, no high-specificity single facet, no HTML gating, wrong finding category, challenge bodies not retained | Crawl | 02 §2.2/2.3/2.4/2.9 | `WafMarker.cs:10-20`; `WafDetectionPolicy.cs:44-46`; `AuditExecutionEngine.cs:712-715,747` |
| G27 | **P2** | Redirect chains not persisted; pre-crawl validation not invoked; per-audit canonicalization options ignored | Crawl | 02 §5.1/5.2/5.4 | `FetchContextScope.Current` never set; `IPreCrawlValidator` registered but unused; only default `UrlCanonicalizer` |
| G28 | **P2** | Offline packaging incomplete: stdio adapter not bundled, Electron "not-bundled"/stale report | Foundation | 01/02 §13, §8.9 | `PackagingVerificationTests.cs:69-79`; `packaging-report.json:93-103` |
| G29 | **P3** | First-run additive defaults not backfilled on incremental migration; empty ContentStore test project; `MAX_WINDOWED_PAGES=24` vs ≤20; `/api/version` hardcodes `local-default`; transient plaintext tokens after crash; negative requirements enforced only by static lint | Multiple | various | see domain sections |

---

## 5. Domain findings (condensed)

### 5.1 Foundation, governance, security, quality (architecture 01 + 07)
- **Solid**: 8 core + 9 extension exclusions encoded as 20 governance rules with planted-violation fixtures and live scans at 0 violations; loopback-only Kestrel + per-launch token + ephemeral fallback; real AES-256-GCM secret protector with app-generated entropy for all 5 credential classes; zero idle outbound and no-identity-store proven by runtime monitors; Server GC configured and reported at `/api/diagnostics/version`; genuine `dbghelp` minidump path; Electron supervisor with health loop/backoff/graceful drain; all 48 criteria have intact evidence pointers.
- **Gaps**: G02, G15, G28, plus precedence invariant 5 only unit-level (recording double, not production resolver); `/api/version` hardcoded `local-default`; transient plaintext handshake tokens can survive a crash.

### 5.2 Data and persistence (architecture 02)
- **Solid**: all 25 baseline + 14 additive tables + `ngram_site_summaries` materialized read model + activated `url_embeddings`; v1–v4 idempotent ledger-fenced migrations; WAL + CQRS single-writer; aggregation/scoring into DB; Brotli + SHA-256 content store with dedup and cross-audit reuse; transactional retention; ZIP archive export/import across all 31 tables; all 20 metric keys and 6 element groups produced.
- **Gaps**: G16, plus low-severity first-run additive backfill edge case and an empty `CrawlForge.Data.ContentStore.Tests` project.

### 5.3 Crawl engine (architecture 03)
- **Solid**: AIMD constants and full control law (increase/decrease/concurrency/cooldown/retry-after/manual pin/checkpoint) genuinely coded; limits resolver with sentinels, priority merge and validation; WAF thresholds (0.8, 2 facets, 5%/1%, min 20); redirect-matrix 5-tier precedence; scheduled audits (catch-up, no-overlap, history, completion actions, restart reconciliation).
- **Gaps**: G03, G07–G10, G26, G27; additionally only 3–4 of 10 §4.2 limit parameters are exposed through the live API.

### 5.4 Core audit engine (architecture 04 files 01–04)
- **Solid**: 8-step pipeline, element inventory, finding model + reversible suppression, spelling (Hunspell), readability/text metrics, soft-404, technology detection from 50-entry local catalogue, response-vs-rendered diff, DOM-zone classification with exact weights + weighted equity, SERP pixel measurement, header/HTML equivalency, accessibility (7 families/17 rules), content-scope resolver with the exact 5-step evaluation.
- **Gaps**: G04, G11–G13, G23–G25; plus sentiment (optional) deferred; SERP emits only one desktop variant; `header_directives` inventory stores raw headers rather than parsed directives.

### 5.5 Advanced intelligence (architecture 04 files 05–07)
- **Solid**: 4-zone N-grams with exact TF·log(1+N/DF), 2.0/1.5 weights, top-K 250 / DF≥2 / top-M 100000 bounds; bundled ONNX (384-dim) + loopback daemon providers with strict loopback validation; content-hash incremental re-embed; GEO validator/governance matrix/generator; MarTech full catalogue + Consent Mode v2 `gcs`/`gcd`; log analyzer 4 formats + gzip + bundled CIDRs + 3 correlations.
- **Gaps**: G14, G22; plus N-gram config toggles dead, no REST `term` filter, no log multipart upload, PerplexityBot has empty CIDR list, query-variant "trap" approximated by `Contains('?')`.

### 5.6 API, hub, MCP, metrics (architecture 05)
- **Solid**: all 51 baseline + 37 additive REST routes and 15 extended route fields present; SignalR hub correctly mapped at `/hubs/realtime` (the `/hub` "hub-stub" descriptor is a harmless compatibility route); hub core with latest-wins coalescing, per-audit routing and 2s heartbeat; MCP 2 transports + 8 tools + 15 resources + 5 prompts + handshake + token rotation + action log; `/metrics` pull-only loopback exposition; diagnostics artifacts, minidump-on-managed-crash, compact action.
- **Gaps**: G05, G06, G17; tests inject event names/metric samples manually, masking missing producers.

### 5.7 Frontend, Electron, UI, plugins (architecture 06)
- **Solid**: mandated stack installed and used (React 19, Mantine 8, Zustand, ECharts, Sigma+Graphology, @medv/finder, TanStack Table/Virtual); strict ≤50 mounted DOM rows with recycling, 200-row pages, 20-page grid LRU, 150–300ms debounce, ARIA + keyboard; real-browser 500k-row benchmark; all 6 baseline + 3 extended standalone tools mounted and reachable; status-bar telemetry, heartbeat handling, JS heap, Compact action; sandbox library, network-grant guard, integrity hashing.
- **Gaps**: G01, G02, G18–G21; plus command palette lacks saved queries/tools/plugin commands, store windowed-cache bound 24 vs 20, no consolidated attribution file.

---

## 6. What is genuinely built (no action)

The audit is not a claim that the app is empty. The following are real, substantial implementations:
- Persistence: 40-object catalog, versioned migrations, WAL/CQRS, content store, retention, archive.
- Algorithms: AIMD, WAF thresholds, redirect-matrix precedence, N-grams, embeddings, log correlation, weighted link equity, SERP metrics, content scoping, structured-data validation (as a library).
- REST/MCP inventories: complete.
- Security: secret protection, loopback boundary, zero-idle-outbound proof, governance analyzer.
- Frontend infrastructure: virtualization engine, grid, charts, graphs, picker, sandbox, tools, telemetry.
- Scheduling, lifecycle, checkpointing, minidump handler (managed path).

The underbuild is concentrated in **integration, mounting, emission, recording, and wiring** — not in the absence of core code.

---

## 7. Recommended remediation sequence

1. **Make the product usable end-to-end (P0)**
   - Mount `ExplorerHost` + dashboards + graphs in `AuditViews.tsx`; add report sub-views and navigation for all 16+6 surfaces; add missing 10 surface components.
   - Bind a real `PluginRegistryDelegate` in Electron `main.ts`; instantiate `PluginRegistry`/`PluginManager` in `ShellContext`; render `PluginManagementSurface`; call `signalHydrated()` after bootstrap; add a real hook-firing E2E test.
   - Wire `IRenderingPool` into `AuditExecutionEngine` fetch loop; set `RenderedDocument`; run rendered WAF checkpoint.
   - Register and invoke the 5 CWV/performance analyzers; add TTFB to `LabVitalsContracts`.
2. **Restore real-time and observability (P1)**
   - Implement publishers for the 7 missing hub events; replace catalog-only tests with domain-driven emission tests.
   - Wire `IMetricsExposition.Record()` for the 10 missing families; remove HELP lines until producers exist.
3. **Restore crawl correctness (P1)**
   - Feed `IsChallenge`, crawl-delay and Retry-After into AIMD; add sitemap ingestion; wire proxy + signed requests; fix List Mode ordering; apply per-audit canonicalization and expose all 10 limit parameters.
4. **Restore audit completeness (P1)**
   - Wire structured-data validator into audit; add Images/Security rule families; expose custom-search/JS options; add `llms.txt` crawl-time probe.
5. **Harden platform (P1–P2)**
   - Supervisor-side native crash dump + recursive recovery listing; consolidate MySQL schema authority and add live-CI parity; connect MCP settings to runtime token/enabled flag; complete plugin capabilities/timeouts/quarantine; enforce shell CSP; complete packaging manifest.
6. **Process fix (root cause)**
   - Change the build ledger so a workpiece may be marked "completed" only when a *production-path* test (not a plumbing/mock test) demonstrates the behavior. Add "wiring audits" to the wave gates; the project's own `project-configuration-review.md` already requests this.

---

## Appendix A — Independent spot-checks performed

- Report placeholder text and plugin `null` delegate, `signalHydrated` 0 callers, `PluginManagementSurface` never rendered — confirmed.
- CWV/performance analyzers: only self-definitions + contract interfaces, no DI registration/call — confirmed.
- Structured-data validator: only `ToolsController` + DI registration — confirmed.
- `CrawlForge.Crawler` has 0 `llms` references; `SemanticsController` uses 0.5 — confirmed.
- 6 of 7 "never emitted" hub events have 0 literal sites outside catalog/service; `waf-blocks` sites are metric/resource names, not a HubEvent — confirmed.
- Divergent MySQL schema uses `schedule_id`/`artifact_kind`/`agent_key`/`import_id` while shipped schema uses `schedule_definition_id`/`kind`/`agent_name`/`log_import_id` — confirmed.
- `AuditExecutionEngine.cs:1476` hardcodes `RenderedDocument: null` — confirmed.
- Frontend `tsc --noEmit` and `vite build` both exit 0 — confirmed.
- Fresh backend E2E blocked at build by preview-SDK static-web-assets error — confirmed.

*End of CF-UNDERBUILD-AUDIT-001. No files in `app` or `architecture` were modified.*
