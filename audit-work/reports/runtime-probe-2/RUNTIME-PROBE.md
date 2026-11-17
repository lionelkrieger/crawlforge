# CrawlForge runtime probe 2: as-built snapshot against the fixture site

Probe run 2026-11-17 at repository HEAD `fc9ac0f` (after `HOTFIX-01` and `HOTFIX-01b`). This was a measurement task only. No file under `app\src`, `app\tests`, `architecture\`, `buildplan\`, or `audit-work\register\` was changed. The earlier DB-patched diagnostic runs were not reused: every project, configuration, audit, export, and MCP call in this probe used a normal entry point (UI, REST, or MCP). Crawler traps are not detected, so all audits carried explicit limit overrides. Machine-readable results are in [runtime-probe.json](runtime-probe.json); screenshots are in [screenshots/](screenshots/), logs in [logs/](logs/), and probe tools in [tools/](tools/).

## Summary

**What works end to end today**

- The app launches. `launch-crawlforge.ps1` reported the Electron binary and all three build outputs up to date and loaded the main window; a scratch copy with remote debugging kept the window open for UI inspection.
- Project creation works through the UI New project dialog (project 4) and configurations can be created and bounded through REST.
- After `HOTFIX-01`, audits started through REST and MCP complete instead of failing at start. The bounded HTTP audit 11 completed 183 URLs; the bounded rendering-enabled audit 12 completed 26 URLs; the MCP-started audit 13 completed 40 URLs.
- REST audit explorer data (URLs, URL details, links, findings, summary, scores, hosts) is readable. CSV and XLSX export jobs completed and downloaded valid files.
- The MCP stdio interface lists 8 tools, 15 resources, and 5 prompts. Start, poll, completion detection, `audit-summary`, `high-severity-findings`, and a prompt all work mechanically.
- UI-01, UI-10, UI-18, UI-19, UI-21, and UI-23 are reachable and functional; UI-11 is reachable but is a placeholder.
- Single-Page Analyzer, Render Tester, URL Check, and the other standalone tool controls render without a crawl project. Status-bar telemetry and `/metrics` work.
- `GET /api/diagnostics/version` reports .NET 10.0.15 with server and concurrent GC enabled.

**What does not work**

- Rendering is inert in full audits. Configuration 8 has `rendering.enabled=true` and the audit snapshot says `renderingMode: auto`, but every rendering-dependent finding says "no rendered tree is available". The JS run is 26 URLs versus 183 for HTTP because rendering dominated the time cap; it produces no rendered DOM, links, or findings.
- There is no configuration editor (UI-03/UI-04) and no project overview (UI-02). The only UI inputs are project name, target URL, and description. No capture-mode, limit, directive, analysis, or extraction setting is settable in the UI.
- Report surfaces UI-11 to UI-16 and UI-20 are not mounted. UI-11 shows static text saying the grids and charts "mount into this workspace from the report workpieces".
- Crawler traps are not detected. The HTTP audit crawled the infinite calendar and faceted-filter combinations until the explicit 220-URL override stopped it. The cap, not the product, ended the run.
- Most site-type and safety analysis is absent: postback navigation, cookieless-session normalisation, ViewState measurement, structured-data errors, hreflang return tags, and thin-page detection on the named fixture.
- MCP has no project or configuration tools, exposes templated URIs as plain resources, and `re-audit` fails while deserialising `$.id`.

**Counts**

Surfaces (24):

| Status | Count |
|---|---:|
| Reachable and functional | 6 |
| Reachable but placeholder | 1 |
| Not reachable | 16 |
| Not applicable yet / not exercised | 1 |

Traps (31) by capture mode:

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http (REST, rendering disabled) | 4 | 5 | 10 | 12 |
| js (REST `rendering.enabled=true`; rendering inert) | 4 | 5 | 10 | 12 |

- **MCP flow:** listing works; start, poll, completion, summary, high-severity findings, and prompt rendering work on a bounded configuration. Project/configuration tools do not exist and `re-audit` returns an error.
- **`nonGet`:** stayed empty (`[]`) after audits 11, 12, and 13; `tagHits` was also `[]`. This is vacuous for click safety because rendering and click discovery are inert.
- **Global assertion `no-non-get-requests`: pass, but vacuously.**

**Five most important blockers**

1. **Full-audit rendering is inert.** The JS-enabled run creates a rendering mode but no rendered tree, blocking every JS, dual, and adaptive trap and all rendered evidence.
2. **No configuration editor or project overview.** UI-02/UI-03/UI-04 do not exist; only a New project dialog is available, so #56 and nearly every configure binding are unreachable.
3. **Report surfaces are placeholders.** UI-11 to UI-16 and UI-20 are not mounted; results exist only in REST and export files.
4. **Crawler-trap detection is absent.** Filter and calendar traps expand indefinitely; the probe's explicit 220-URL override was the only bound.
5. **Site-type and safety analysis is missing.** Postback, cookieless session, ViewState, structured data, hreflang return tags, thin page, cookie gate, and click-only navigation have no runtime evidence; MCP configuration management and `re-audit` are also broken.

## 2. Launch and environment

| Item | Value |
|---|---|
| .NET | Installed SDKs: system `11.0.100-rc.3.26522.148` and local user install `10.0.404`. `app/global.json` pins 10.0.404 with `rollForward: latestFeature`; backend reports .NET 10.0.15. |
| Build | Original launcher: Electron binary, backend, frontend, and shell all up to date, exit 0 ([logs/launch-original-utf8.log](logs/launch-original-utf8.log)). Direct build with the pinned SDK: 0 warnings, 0 errors. |
| Debug launch | Scratch copy `tools/launch-debug.ps1` adds `--remote-debugging-port=9232`, local .NET 10 PATH, proxy-duplicate avoidance, and probe log paths ([logs/launch-debug.log](logs/launch-debug.log)). |
| Main window | Loaded with title "CrawlForge" at `index.html?route=hub`; Electron log contains the known hydration-timeout warning. |
| Backend | Dynamic loopback port 49413 read from `loopback-handshake.json`; REST header `X-CrawlForge-Session`. |
| MCP | Loopback endpoint 49417 read from `mcp-endpoint.json`; stdio adapter used as an external client would. |
| Fixture | Existing server on 127.0.0.1:47311 confirmed and reused; `/__fixture/reset` called before audits 11, 12, and 13. |
| Existing data | Projects 1-3 and audits 1-10 pre-existed. Probe created project 4, configs 7-9, audits 11-13, exports 1-2. |

The fixture-server log records an `EADDRINUSE` when this probe tried to start a second server; the already-running listener was confirmed to be the CrawlForge fixture by its homepage, robots.txt, sitemap.xml, and recorder responses.

## 3. UI surface walk (UI-01 to UI-24)

Screenshots are in [screenshots/](screenshots/). UI-04 has **0 of 13** documented tabs: Scope; Crawl as and capture; Limits; Rules; Directives; Resources and external scope; Authentication and cookies; Click discovery and site types; Consent states; Analysis categories; Thresholds; Extraction and search; Advanced. Of the section 5.1 audit inputs, only the seed URL is settable, and only in the New project dialog.

| ID | Status | Evidence |
|---|---|---|
| UI-01 | reachable_and_functional | New project dialog created project 4 and a default configuration via POST /api/projects and POST /api/projects/4/configurations; Start audit controls present. Hub shows projects and audit aggregates after manual refresh. |
| UI-02 | not_reachable | Project row click, dblclick, and command-palette project pick produced no navigation and no API call; no project overview controls or text appeared. |
| UI-03 | not_reachable | No Configurations entry in rail, project row, or command palette; the section name is absent from the running shell. |
| UI-04 | not_reachable | No configuration editor: 0 of 13 documented tabs. The only seed/target input is in the New project dialog; no limit, AIMD, rendering, directive, analysis, or extraction inputs exist. |
| UI-05 | not_reachable | No Integrations navigation or controls in the running shell. |
| UI-06 | not_reachable | No Segments surface or report segment picker. |
| UI-07 | not_reachable | Global Audits rail exists, but Project overview > Audits and retention controls do not; no project-scoped history/retention surface. |
| UI-08 | not_reachable | No Clients surface or navigation. |
| UI-09 | not_reachable | No Schedules surface or navigation; schedules exist only as an MCP tool/resource. |
| UI-10 | reachable_and_functional | Opening audit 11 rendered state COMPLETED, phase finalizing, 183 fetched, 0 queued, 0 errors, 0 blocked, and a host-throttle panel without crash. Failed audit 10 displays its persisted error message after HOTFIX-01. |
| UI-11 | reachable_but_placeholder | Report area loads with crawl progress but the data area is static text: "URL / finding / link grids, charts, and the link graph mount into this workspace from the report workpieces"; no URL/finding data grid. |
| UI-12 | not_reachable | No report navigation; issue explorer is absent. |
| UI-13 | not_reachable | No URL explorer or drill-down; URL data reachable only through REST. |
| UI-14 | not_reachable | No visualization window or navigation. |
| UI-15 | not_reachable | No Compare surface or two-audit selection. |
| UI-16 | not_reachable | No specialist report navigation; none of the listed specialist surfaces is mounted. |
| UI-17 | not_reachable | No Log File Analyzer surface or import control. |
| UI-18 | reachable_and_functional | Tools rail > Single-Page Analyzer accepted a fixture URL and returned 9 findings and 10 elements through POST /api/analysis/single-page without creating a project. |
| UI-19 | reachable_and_functional | Tools rail exposes 12 standalone tools. Render Tester, URL Check, Structured Data Checker, robots tester, sitemap generator, query builder, and other listed tools render with working controls. |
| UI-20 | not_reachable | No Export dialog or report-window Export control. REST CSV and XLSX export jobs 1 and 2 completed and downloaded valid files. |
| UI-21 | reachable_and_functional | Settings renders Appearance, language, advisories, Database, MCP automation, Embeddings, Proxy, and Diagnostics. Missing from the specified surface: browser, user dictionary, integrations credentials, configuration templates, action log, release check, and Plugins navigation. |
| UI-22 | not_reachable | No plugin manager; Settings has no Plugins entry. |
| UI-23 | reachable_and_functional | Status bar visible in every window with WS, working set, heap, GC, WAL, JS heap, heartbeat, and Compact control; /metrics exposes the same backend families. |
| UI-24 | not_applicable_yet | Recovery screen is automatic after repeated backend failures; no backend failure was induced in this measurement session. |

## 4. Audit runs against the fixture site

The fixture recorder was reset before each run. Both modes were created and started through REST; the UI has no capture-mode control and its Start audit action does not accept limit overrides. Per-audit `limitOverrides` were `maxUrls=220`, `maxDepth=6`, `maxDurationSeconds=180`. Rendering mode was enabled only by REST configuration 8 (`rendering.enabled=true`, `deviceProfile=desktop`, `captureConsole=true`); there is no UI control and no dual/adaptive setting in the REST wire contract.

| Run | Entry point | Audit | Outcome | URLs | Links | Findings | Duration | nonGet / tagHits |
|---|---|---|---|---:|---:|---:|---:|---|
| http | REST config 7, UI-created project | 11 | running/crawling -> analyzing -> completed | 183 | 1641 | 1522 | 122.7 s | [] / [] |
| js | REST config 8, `rendering.enabled=true` | 12 | running/crawling -> analyzing -> completed | 26 | 48 | 194 | 197.9 s | [] / [] |
| mcp | MCP `start-audit` on REST-created bounded config 9 | 13 | running -> completed | 40 | - | - | 22.2 s poll | [] / [] |

Audit 11 evidence includes `indexability.noindex`, `canonical.header-html-conflict`, `canonical.mismatch`, `indexability.soft-404`, and `accessibility.image-alt`. Audit 12 has the same rule shape with rendering-dependent messages changed from "rendering mode is disabled" to "no rendered tree is available"; its word counts match the HTTP run. Full REST pulls are in [logs/audit-11-data.json](logs/audit-11-data.json), [logs/audit-12-data.json](logs/audit-12-data.json), and trap URL details in the matching `*-trap-details.json` files. Export jobs 1 (CSV zip) and 2 (XLSX) were created and downloaded through REST.

## 5. Compare with expected results

Verdicts are per trap per mode. JS-dependent traps are marked `not_testable` because the build cannot render in full audits, even when the REST setting is enabled. Evidence is summarised below and stored per trap in `runtime-probe.json`.

| Trap | http | js | Evidence summary |
|---|---|---|---|
| redirect-chain | partial | partial | Audit 11/12 stored the seed as 200 with redirectChain length 1 and no redirect-chain finding; the required three-hop 301>302>301 record and finding are absent. |
| redirect-loop | partial | partial | Audit 11/12 stored /redirect/loop-a as status 0 and did not hang, but emitted no loop finding or loop classification. |
| noindex | pass | pass | Audit 11/12 emitted indexability.noindex with source meta-robots and directives ["noindex","follow"]; the page was crawled (200, 154 words). |
| robots-blocked | partial | partial | Audit 11/12 did not fetch /private/blocked.html (status 0) but stored blockReason=null and emitted no robots finding; the URL is not affirmatively reported as blocked. |
| canonical-conflict | pass | pass | Audit 11/12 emitted canonical.header-html-conflict and canonical.mismatch with header target /pages/canonical-target-header.html and HTML target /pages/canonical-target-html.html. |
| broken-links | partial | partial | Audit 11/12 recorded the internal 404 URL state, but no broken-internal or broken-external finding; broken.example.invalid was not checked. |
| orphan | fail | fail | /pages/orphan.html is absent from both audits; sitemap-only discovery does not exist. |
| js-only-link | not_testable | not_testable | Rendering is inert in full audits: /pages/js-only-target.html is absent in both the http run and the rendering-enabled js run; no rendered tree is available. |
| script-injected-content | not_testable | not_testable | /pages/js-content.html reports totalWordCount=2 in both modes and all rendering-dependent findings say no rendered tree is available; rendered ~200-word content is absent. |
| late-meta-change | not_testable | not_testable | No dual-capture configuration exists through UI or REST. /pages/late-meta.html keeps its raw title and emits no late-change finding. |
| soft-404-server | pass | pass | Audit 11/12 emitted indexability.soft-404 for /pages/soft-404.html with confidence 0.9 and matched "page not found" phrases. |
| soft-404-client-route | not_testable | not_testable | /spa/does-not-exist is absent; the SPA shell is not rendered in full audits. |
| hash-route | not_testable | not_testable | Hash routes require rendering; /hash-app/ was crawled as a static 3-word page with no route capture or hash-route finding. |
| client-side-redirect | not_testable | not_testable | Requires rendering; /pages/js-redirect.html stayed at its raw shell and no client-side redirect finding was emitted. |
| postback-links | fail | fail | /pages/postback.aspx was crawled but no uncrawlable-navigation or postback finding was emitted. |
| cookieless-session | fail | fail | The URL "http://127.0.0.1:47311/(S(abc123xyz))/pages/session.aspx" is stored unchanged; no canonicalisation finding. |
| large-viewstate | fail | fail | /pages/viewstate-large.aspx has markupLength 153005 but no ViewState finding; it is misclassified as thin content. |
| crawler-trap-filter | fail | fail | Audit 11 generated numerous /trap/filter?color=... combinations with no trap detection; the 220-URL limit, not detection, bounded the run. |
| crawler-trap-calendar | fail | fail | Audit 11 followed the infinite calendar through 2027/4 with no trap detection; the 220-URL limit, not detection, bounded the run. |
| cookie-gate | fail | fail | /cookie-gate/ ended as status 0 in both modes; no successful cookie-jar navigation or loop finding was recorded. |
| consent-banner | not_testable | not_testable | Requires JS capture. Fixture tagHits stayed [] in both runs; no consent/tag finding was emitted. |
| service-worker | not_testable | not_testable | Requires JS capture; /sw-app/ has no service-worker finding. |
| structured-data-errors | fail | fail | /pages/structured-data.html was crawled but no structured-data validation finding was emitted. |
| missing-alt | pass | pass | Audit 11/12 emitted accessibility.image-alt with locator html > body:nth-of-type(1) > img:nth-of-type(1) on /pages/images.html. |
| mixed-content | not_testable | not_testable | Fixture ran over HTTP only; mixed-content expectation requires HTTPS (--tls-cert/--tls-key). |
| hreflang-no-return | fail | fail | Audit 11/12 emitted i18n.hreflang-valid (pass) with languages en/fr; no missing return-tag finding. |
| duplicate-titles | partial | partial | Both duplicate-title pages were crawled, but the only related finding is content.keyword-cannibalization; no duplicate-title finding was emitted. |
| thin-page | fail | fail | /pages/thin.html reports totalWordCount=6 but has no content.thin-text finding. |
| live-websocket | not_testable | not_testable | Requires JS rendering and network quiet detection; no rendered tree is available in full audits. |
| click-only-navigation | not_testable | not_testable | Requires click discovery; /spa/promotions and /spa/results were not discovered in the bounded js run. |
| never-place-bet | not_testable | not_testable | nonGet=[] and tagHits=[] in both runs, but rendering and click discovery are inert, so the safety assertion is vacuous rather than an exercised click-safety pass. |

**Global assertion `no-non-get-requests`: pass, vacuously.** `nonGet` was empty after every audit, but no click discovery or JS interaction occurred, so the fixture's bet/deposit/logout controls were never exercised.

## 6. MCP probe

- Client: `@modelcontextprotocol/sdk` 1.32.2 over stdio to `app/src/mcp-stdio-adapter/dist/index.js`, pointed at the backend's `mcp-endpoint.json` and token.
- Tools (8): `start-audit`, `pause-audit`, `resume-audit`, `cancel-audit`, `re-audit`, `analyze-single-page`, `run-schedule-now`, `get-audit-progress`. There are no project or configuration management tools.
- Resources (15): `audit-summary`, `high-severity-findings`, `url-detail`, `audit-list`, `schedule-status`, `crawl-progress`, `extracted-content`, `ngram-analysis`, `waf-blocks`, `host-throttle`, `redirect-matrix-results`, `log-correlation`, `semantic-link-opportunities`, `geo-readiness`, `analytics-coverage`, using the templated URIs listed in `runtime-probe.json`. `resources/templates/list` returns -32601.
- Prompts (5): `audit-health-briefing`, `high-severity-triage`, `audit-delta-briefing`, `ngram-topical-briefing`, `semantic-topical-briefing`.
- Flow: `start-audit` on bounded configuration 9 created audit 13; progress moved from Running/Crawling to Completed/Finalizing with 40 fetched; `audit-summary` and `high-severity-findings` were readable with evidence JSON; the health-briefing prompt rendered.
- `re-audit` fails with `The JSON value could not be converted to CrawlForge.Contracts.Entities.CrawlConfigurationDto. Path: $.id`.
- Because MCP exposes no limit override, configuration 9 was created through REST with `maxUrls=40`, `maxDepth=3`, `maxDurationSeconds=90`.

## 7. Acceptance criteria snapshot

| # | Result | Reason |
|---|---|---|
| 1 | not_testable | Not a first launch; the user data directory already contained projects and audits. |
| 3 | not_testable | No network monitor was installed; external traffic was not instrumented. |
| 4 | pass | Configuration 7 has maxUrls/maxDepth/maxDuration/maxResponseBytes all null in REST; limits were supplied only as per-audit overrides. |
| 5 | pass | No license, account, entitlement, or activation concept appeared in observed UI, configuration payloads, or diagnostics. |
| 7 | fail | Report surfaces are placeholders; REST exports produced valid CSV zip and XLSX files. |
| 10 | pass | Single-Page Analyzer returned 9 findings and 10 elements without creating a crawl project. |
| 16 | partial | MCP stdio handshake, listings, and calls work; SSE, token rotation, and non-loopback reachability were not exercised. |
| 19 | pass | GET /api/diagnostics/version reports .NET 10.0.15 with server=true and concurrent=true. |
| 20 | partial | GET /metrics serves Prometheus-style exposition on loopback; non-loopback exposure and identity dimensions were not fully tested. |
| 23 | partial | MCP start/poll/complete/resource-read flow works mechanically; no project/config tools and re-audit fails. |
| 27 | fail | No structured-data findings were emitted for the fixture page with JSON-LD errors. |
| 28 | pass | Content metrics and word counts were produced; server soft-404 was flagged distinctly. |
| 33 | partial | Standalone robots tester, sitemap generator, and query builder controls render; executions were not exhaustively exercised. |
| 36 | partial | UI-10 host-throttle panel and MCP host-throttle resource exist; AIMD adaptation was not exercised. |
| 38 | partial | MCP ngram-analysis resource is listed; ngram data and cannibalization endpoints were not read in this pass. |
| 41 | partial | Status-bar heartbeat/heap/GC/WAL and /metrics were observed; the Compact action was not exercised. |
| 42 | partial | Render Tester + Picker renders, but point-and-click selector generation was not exercised. |
| 48 | partial | canonical.header-html-conflict was emitted with dual evidence; X-Robots and hreflang header/HTML equivalency were not observed. |
| 49 | fail | Project creation works through UI, but there is no UI configuration editor or results grid; REST CSV/XLSX exports work. |
| 50 | fail | No UI capture-mode control; rendering-enabled REST audits complete but rendering is inert. |
| 51 | fail | No dual capture configuration is available through UI or REST. |
| 52 | fail | No adaptive capture or manual per-pattern override is exposed through normal entry points. |
| 53 | fail | Postback, cookieless-session, ViewState, and rendered soft-404 checks have no runtime evidence; JS-dependent cases not testable. |
| 54 | fail | No click discovery or render settle is exercised; nonGet=[] is vacuous. |
| 55 | fail | Crawler traps are not detected; only the explicit 220-URL limit bounded the run. Cookie gate fails. |
| 56 | fail | Configuration editor UI-03/UI-04 is not reachable. |
| 57 | fail | MCP listing, start, poll, and resource reads work, but there are no project/configuration tools, findings are not paged through MCP, and re-audit errors. |
| 58 | fail | No integrations surface or integration sync endpoints were observed. |

Criteria not listed were not exercised against the fixture in this session. Trap evidence for criteria #49-#58 is in section 5; capability-level runtime evidence for every capability named in the traps or bound to a checked surface is in `runtime-probe.json`.

## Method notes

- No database reads or writes were used for measurement. No product code, fixture file, or architecture document was modified.
- Probe tools are in [tools/](tools/), including the original helper set plus `pull-trap-details.mjs`, which reads URL details through REST only. `db.mjs` from the earlier probe was deliberately not copied.
- Workspace side effects are limited to `audit-work/reports/runtime-probe-2/` and normal product data created through UI/REST/MCP: project 4, configurations 7-9, audits 11-13, exports 1-2.
