# CrawlForge runtime probe: as-built snapshot against the fixture site

Probe date 2026-11-17, repository HEAD `18c53da`. This was a measurement task only. No file under `app\src`, `app\tests`, `architecture\`, `buildplan\` or `audit-work\register\` was modified. Machine-readable results are in [runtime-probe.json](runtime-probe.json). Screenshots are in [screenshots/](screenshots/), logs in [logs/](logs/), and the probe scripts in [tools/](tools/).

## Summary

**What works end to end today**
- The app builds and launches with `launch-crawlforge.ps1` (everything was up to date) and the main window loads.
- In the UI you can create a project (Main hub › New project) and press Start audit.
- The standalone tools work without a project:
  - Single-Page Analyzer
  - Render Tester, which renders JavaScript in Chromium
  - URL Check
- These also work:
  - Settings
  - Status-bar telemetry
  - The MCP stdio handshake
  - MCP tool, resource and prompt listing
  - MCP `analyze-single-page`
  - REST CSV/XLSX export of a completed audit
  - The `/metrics` endpoint

**What does not work**
- No audit started through a normal entry point (UI, REST or MCP) crawls anything. Every configuration created through the API fails within 40 ms. The error is `ArgumentOutOfRangeException: The initial rate must be a positive finite number (InitialRate=0)`.
  - The cause is a format mismatch. The API stores AIMD settings as `{"aimd":{"initialRateR0":…}}`. The engine reads `PolitenessJson` as a flat `AimdSettings` object (`InitialRate`), so it gets 0.
- To measure the rest of the pipeline, I ran two **diagnostic** audits. For two probe-only configurations, I rewrote `politeness_json` directly in SQLite to the flat format. These runs are not a normal entry point and do not count toward acceptance.
  - Even then, JS rendering is inert in full audits. The snapshot shows `renderingMode: auto`, but every page reports "no rendered tree is available". The JS run's results are identical to the HTTP run.
  - Crawler traps are not detected. The diagnostic audits only ended because of the 480 s duration cap: the HTTP run crawled 886 calendar URLs and the JS run 1373.
- The UI has no project overview, configuration editor, report surfaces, issue/URL explorers, export dialog, integrations, schedules or plugin manager.
- The audit run view crashes the whole renderer to a blank window as soon as host-throttle data arrives. The error is an uncaught `TypeError` reading `toFixed` of undefined (`ttfbEwmaMs`).

**Counts**

Surfaces (24):

| Status | Count | Surfaces |
|---|---|---|
| Reachable and functional | 6 | UI-01, UI-10 (defective), UI-18, UI-19, UI-21, UI-23 |
| Reachable but placeholder | 1 | UI-11 |
| Not reachable | 16 | UI-02 to UI-09, UI-12 to UI-17, UI-20, UI-22 |
| Not exercised | 1 | UI-24 (recovery screen was not induced) |

Traps (31) by capture mode:

| Mode | pass | partial | fail | not_testable |
|---|---|---|---|---|
| http (normal entry point: UI or MCP) | 0 | 0 | 30 | 1 |
| js (normal entry point: REST `rendering.enabled`) | 0 | 0 | 30 | 1 |
| http_diagnostic (DB-patched, audit 3) | 4 | 5 | 10 | 12 |
| js_diagnostic (DB-patched, MCP-started, audit 5) | 4 | 5 | 10 | 12 |

- **MCP flow:** listing, start, poll, completion detection and reading `audit-summary` and `high-severity-findings` all work mechanically.
  - On a normal configuration the audit fails immediately.
  - On the DB-patched configuration it ran to completion (audit 5) and the findings could be read with evidence.
  - `re-audit` returns an error (`could not be converted to System.Int64. Path: $.id`).
  - There are no project or configuration management tools.
- **`nonGet`:** stayed empty (`[]`, and `tagHits` was `[]`) after all five audits. This is vacuous for click safety, because nothing was rendered or clicked.

**Top five blockers**
1. **Audits never run from the UI, REST or MCP.** The AIMD settings are written in one format and read in another, so InitialRate is 0 and every audit fails at start. The UI also gives no error and keeps showing the audit as RUNNING.
2. **JS rendering is inert in full audits.** A rendering setting exists only in REST, and even there it changes nothing. This blocks every JS, dual and adaptive trap and criteria #50 to #54.
3. **There is no configuration editor (UI-03/UI-04) and no project overview (UI-02).** The only input the UI takes is the seed URL at project creation, so #56 and the configure column of almost every binding are unreachable.
4. **The report surfaces are not mounted (UI-11 to UI-16, UI-20).** Results exist only in REST and export files. In addition, the run view (UI-10) crashes the renderer on host-throttle data.
5. **Crawl-safety and site-type features fail on the fixture.** This covers:
   - Crawler-trap detection
   - Cookie jar
   - Sitemap discovery (orphan)
   - Redirect hop recording
   - Postback, cookieless session and ViewState checks
   - Hreflang return-tag check
   - Structured-data findings
   - Thin-page detection

## 2. Launch and environment

| Item | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26300 |
| Node | v26.5.1 |
| .NET | SDK 11.0.100-rc.3. The app targets net10.0 with `RollForward=LatestMajor` and runs on ASP.NET Core 11.0.0-rc.3 |
| Build | `launch-crawlforge.ps1`: backend, frontend and Electron shell all "up to date", exit 0 ([logs/launch-original.log](logs/launch-original.log)) |
| Main window | Loaded, title "CrawlForge", `index.html?route=hub` |
| Debug launch | Copy of the launch script at [tools/launch-debug.ps1](tools/launch-debug.ps1) with `--remote-debugging-port=9222`, driven with playwright-core 1.49.1 `connectOverCDP` |
| Backend | Dynamic loopback port (61219 in this session), read from `%APPDATA%\CrawlForge\loopback-handshake.json`. The REST header is `X-CrawlForge-Session` |
| MCP | Loopback endpoint and token read from `mcp-endpoint.json` (port 61222) |
| Fixture | `node app\tests\fixture-site\server.mjs` on 47311. `/`, `/robots.txt`, `/sitemap.xml`, `/__fixture/requests` and `/__fixture/reset` all returned 200. Reset before every audit |
| Shell warning | `[crawlforge] Main window hydration timed out; plugin scan deferred.` |

The data directory already contained project "Airpower" from an earlier session. The probe created project 2, "Fixture probe".

## 3. UI surface walk

| ID | Status | Evidence |
|---|---|---|
| UI-01 Main hub › Projects | Reachable and functional | Lists projects and counters. New project (name, target URL, description) calls `POST /api/projects` and then `POST /api/projects/2/configurations`. Start audit calls `POST /api/audits`. The hub's audit panel is stale until the palette command "Refresh local aggregates" is run. |
| UI-02 Project overview | Not reachable | Row click, double-click and picking the project in the palette all do nothing. |
| UI-03 Configurations | Not reachable | No entry anywhere. The string is absent from the bundle. |
| UI-04 Configuration editor | Not reachable | 0 of 13 tabs. Of the 18 inputs in §5.1 (`03-crawl-engine/02` "Audit inputs"), only the seed URL is settable, and only at project creation. The dialog hard-codes `respectRobotsTxt: true`. |
| UI-05 Integrations | Not reachable | No entry and no backend integration endpoints. |
| UI-06 Segments | Not reachable | No entry or picker. |
| UI-07 Audit history and retention | Not reachable | Only the global Audits list exists. |
| UI-08 Clients | Not reachable | |
| UI-09 Schedules | Not reachable | The backend has `SchedulesController`. |
| UI-10 Audit run view | Reachable and functional, but defective | Shows state, phase, counters, Pause/Cancel, host-throttle panel and live activity. Defects: (1) the renderer crashes to blank when host-throttle data arrives (reproduced twice; only a reload recovers); (2) a failed audit is shown as RUNNING with no reason; (3) audits started via REST or MCP appear only after a manual refresh. |
| UI-11 Dashboard | Reachable but placeholder | Static text: "REPORT SURFACES … mount into this workspace from the report workpieces". |
| UI-12 to UI-16 | Not reachable | No navigation inside the report area. The data exists in REST (`/urls`, `/findings`, `/links`, `/graph`, `/charts`, `/compare`). |
| UI-17 Log file analyzer | Not reachable | The backend has `LogsController`. |
| UI-18 Single-page analyzer | Reachable and functional | Reached via Tools rail › Single-Page Analyzer, not a hub toolbar. On `/pages/thin.html` it returned 7 findings and 10 elements, but no thin-content finding. The Render toggle does not change the analysis. |
| UI-19 Standalone tools | Reachable and functional | 12 tools are listed. Render Tester genuinely rendered `/pages/js-content.html`. URL Check on `/redirect/chain-1` shows a single hop with status 200 instead of 301>302>301. |
| UI-20 Export dialog | Not reachable | REST exports work: audit 3 produced a CSV zip and an XLSX. |
| UI-21 Settings | Reachable and functional | Present: Appearance, language, advisories, Database, MCP, Embeddings, Proxy (read-only), Diagnostics. Missing: browser, user dictionary, integrations credentials, configuration templates, MCP action log, release check, Plugins. |
| UI-22 Plugin manager | Not reachable | |
| UI-23 Status bar | Reachable and functional | WS, heap, GC, WAL, JS heap, heartbeat, Compact. |
| UI-24 Recovery screen | Not exercised | Repeated backend failure was not induced. |

## 4. Audit runs

| Run | Entry point and how configured | Audit | Outcome | URLs | Duration | `nonGet` / `tagHits` |
|---|---|---|---|---|---|---|
| http (normal) | UI Start audit on the UI-created config 2 | 1 | running → failed (finalizing), InitialRate=0 | 0 | 0.038 s | [] / [] |
| js (normal) | REST config 3 with `rendering.enabled=true` (no UI control exists), started via REST | 2 | failed, same exception | 0 | ~3 ms | [] / [] |
| http (MCP, normal) | MCP `start-audit` on config 2 | 4 | failed | 0 | <1 s | [] / [] |
| http_diagnostic | REST start of config 4 (politeness patched in SQLite, `maxDurationSeconds=480`) | 3 | running/crawling → analyzing at 477 s → completed at 501 s | 1061 (1058 fetched), 3391 links, 7647 findings | 503.5 s | [] / [] |
| js_diagnostic | MCP `start-audit` of config 5 (`rendering.enabled=true` plus the same patch) | 5 | completed at 511.5 s | 1548 (1545 fetched), 4365 links, 11056 findings | 509.7 s | [] / [] |

- **The failure** is in [logs/backend-structured-errors-warnings.jsonl](logs/backend-structured-errors-warnings.jsonl):

  ```
  Audit 1 failed. ArgumentOutOfRangeException: The initial rate must be a positive finite number. (Parameter 'InitialRate') Actual value was 0.
  ```

- **The diagnostic patch.** The API writes `PolitenessJson` as `{"aimd":{"initialRateR0":2,...}}` (`ProjectWireMapper.SerializeAimd`). `AuditExecutionEngine` deserializes it as `AimdSettings(InitialRate, …)`, and `AimdControllerOptions.Validate` then throws. For configs 4 and 5 only, I set `politeness_json` to `{"InitialRate":10.0,"AdditiveIncrease":0.5,"MultiplicativeDecrease":0.5,"EwmaSmoothing":0.2,"MinRate":0.1,"MaxConcurrency":4}`, which is the shape `FirstRunSeed` uses. No product file was changed.
- **The diagnostic audits finished only because of the duration cap.** The fixture's infinite calendar was never detected: 886 calendar URLs in the HTTP run and 1373 in the JS run, plus 144 filter URLs in each.
- **Rendering in full audits is inert.** Audit 5's snapshot has `renderingMode: "auto"`, yet `accessibility.color-contrast` reports "no rendered tree is available for this page" on every page. `js-content.html` has word count 2, and the rule counts match audit 3 rule for rule.
- **Data was pulled via REST** (`/urls`, `/urls/{id}`, `/links`, `/findings`, `/summary`, `/scores`, `/hosts`) into [logs/audit-3-data.json.gz](logs/audit-3-data.json.gz) and [logs/audit-5-data.json.gz](logs/audit-5-data.json.gz). It could not be viewed in the UI, because the report surfaces are not mounted.
- **REST exports** for audit 3: CSV produced `export-1.zip` (`urls.csv`, `findings.csv`) and XLSX produced `export-2.xlsx`. Both are valid.

## 5. Trap comparison

The http and js modes through normal entry points fail every trap, because the audit fails at start (mixed-content is not testable). The table below is for the diagnostic runs. The http_diagnostic and js_diagnostic results are identical, because rendering is inert.

| Trap | Result | Evidence |
|---|---|---|
| redirect-chain | partial | Source URL stored as 200 with an empty `redirectChain`. Only the final target is kept (CSV "Redirect Target"). No hops and no chain finding. |
| redirect-loop | partial | FetchError (status 0). The crawl did not hang. No loop finding. |
| noindex | pass | `indexability.noindex`. The URL row still says `indexable=true`. |
| robots-blocked | partial | Not fetched: the CSV shows NotCrawled / Disallowed. REST `blockReason` is null and there is no finding. |
| canonical-conflict | pass | `canonical.header-html-conflict` (error) and `canonical.mismatch`. |
| broken-links | partial | Internal 404 is recorded as a URL status. The external link is not checked. No broken-link findings. |
| orphan | fail | The sitemap is not used, so `/pages/orphan.html` is never discovered. |
| js-only-link | not_testable | Rendering is inert. The target was not discovered in either mode. |
| script-injected-content | not_testable | Rendering is inert. Word count 2 in both modes. |
| late-meta-change | not_testable | No dual capture. The title stays raw. |
| soft-404-server | pass | `indexability.soft-404`, emitted twice for the same URL. |
| soft-404-client-route | not_testable | Not discovered; needs rendering. |
| hash-route | not_testable | Needs rendering. |
| client-side-redirect | not_testable | Needs rendering. |
| postback-links | fail | No finding. |
| cookieless-session | fail | The `(S(abc123xyz))` segment is kept. |
| large-viewstate | fail | No finding. |
| crawler-trap-filter | fail | 144 URLs and no detection. |
| crawler-trap-calendar | fail | 886 and 1373 URLs and no detection. Stopped only by the duration cap. |
| cookie-gate | fail | FetchError (status 0). |
| consent-banner | not_testable | Needs rendering. `tagHits` was `[]`. |
| service-worker | not_testable | Needs rendering. |
| structured-data-errors | fail | No structured-data findings in the audit. |
| missing-alt | pass | `accessibility.image-alt` (error). |
| mixed-content | not_testable | Needs the HTTPS fixture. |
| hreflang-no-return | fail | `i18n.hreflang-valid` reports pass. |
| duplicate-titles | partial | Only a `content.keyword-cannibalization` cluster on "duplicate title". |
| thin-page | fail | Word count 6, but no `content.thin-text` (it fired on 5 other pages). |
| live-websocket | not_testable | Needs rendering. |
| click-only-navigation | not_testable | No click discovery. Promotions and results were not discovered. |
| never-place-bet | not_testable | No rendering or clicking. `nonGet` was `[]` (vacuous). |

**Global assertion `no-non-get-requests`: pass**, but vacuously. `nonGet` was `[]` after all five audits, without any click discovery ever running.

## 6. MCP probe

- **Setup:** client `@modelcontextprotocol/sdk` 1.32.2, spawning `node app/src/mcp-stdio-adapter/dist/index.js --endpoint <loopback> --token <token>`. Server `crawlforge` 1.0.0.
- **Tools (8):** `start-audit`, `pause-audit`, `resume-audit`, `cancel-audit`, `re-audit`, `analyze-single-page`, `run-schedule-now`, `get-audit-progress`.
  - There are no project or configuration management tools.
- **Resources (15):**
  - `audit-summary`
  - `high-severity-findings`
  - `url-detail`
  - `audit-list`
  - `schedule-status`
  - `crawl-progress`
  - `extracted-content`
  - `ngram-analysis`
  - `waf-blocks`
  - `host-throttle`
  - `redirect-matrix-results`
  - `log-correlation`
  - `semantic-link-opportunities`
  - `geo-readiness`
  - `analytics-coverage`

  Templated URIs are listed as plain resources, and `resources/templates/list` returns -32601 Method not found.
- **Prompts (5):** `audit-health-briefing`, `high-severity-triage`, `audit-delta-briefing`, `ngram-topical-briefing`, `semantic-topical-briefing`.
- **Flow on the normal config 2:** `start-audit` returned audit 4, and `get-audit-progress` immediately showed Failed with 0 fetched. `audit-summary` was readable, `high-severity-findings` was `[]`, and the prompt rendered.
- **Flow on the diagnostic config 5:** started audit 5, polled it to Completed (1545 fetched), then read `audit-summary` and `high-severity-findings` with evidence JSON, and the prompt rendered.
- **`re-audit`:** returns `isError` with "The JSON value could not be converted to System.Int64. Path: $.id".
- **`analyze-single-page`:** works. `render: true` does not change the analysis.
- **Invalid token:** rejected ("Unauthorized: invalid local token").

## 7. Acceptance criteria snapshot

| # | Result | Reason |
|---|---|---|
| 49 | fail | A UI-created configuration cannot run, and there is no config editor, results grid or UI export. The diagnostic run passes 4 of 19 applicable traps. |
| 50 | fail | No UI capture-mode control. REST rendering audits fail, and rendering is inert even when patched. |
| 51 | fail | No dual capture. |
| 52 | fail | No adaptive capture. |
| 53 | fail | Postback, cookieless and ViewState checks fail; the rest is not testable without rendering. |
| 54 | fail | No rendering or click discovery. `nonGet` is empty only vacuously. |
| 55 | fail | Traps are not detected and the cookie gate fails. |
| 56 | fail | No configuration editor. |
| 57 | fail | No MCP project or config tools. Audits fail on normal configs. Paged findings are readable only after the DB patch. |
| 58 | fail | No integrations surface or endpoints. |
| 4 | pass | The default snapshot has unlimited URL and depth ceilings. |
| 5 | pass | No license or account concept in the observed screens and payloads. |
| 7 | fail | Report surfaces are not mounted. REST exports do produce valid files. |
| 10 | pass | Single-page analysis works without a project (UI and MCP). |
| 16 | partial | stdio works and an invalid token is rejected. SSE, non-loopback and rotation were not tested. |
| 20 | partial | `/metrics` serves the exposition format on loopback. |
| 23 | partial | The MCP flow works only with the patched config, and `re-audit` errors. |
| 27 | fail | No structured-data findings in the audit. |
| 28 | pass | Readability metrics are computed, and soft 404 is flagged distinctly. |
| 33, 36, 38, 41, 42, 48 | partial | See `criteria` in the JSON. |
| 1, 3, 6 | not_testable | Not a first launch; no network monitor; pause and resume not exercised. |

## Method notes

- **Scripts** are in [tools/](tools/):
  - [tools/cdp.mjs](tools/cdp.mjs) and the `a-*.mjs` actions drive the UI.
  - [tools/rest.mjs](tools/rest.mjs), [tools/poll-audit.mjs](tools/poll-audit.mjs) and [tools/pull-audit.mjs](tools/pull-audit.mjs) cover REST.
  - [tools/mcp-list.mjs](tools/mcp-list.mjs), [tools/mcp-flow.mjs](tools/mcp-flow.mjs) and [tools/mcp-reaudit.mjs](tools/mcp-reaudit.mjs) cover MCP.
  - [tools/db.mjs](tools/db.mjs) did the SQLite read and the diagnostic patch.
  - [tools/build-json.mjs](tools/build-json.mjs) generates the JSON.
- **Side effects in the user data directory** (`%APPDATA%\CrawlForge`):
  - Project 2 "Fixture probe" with configs 2 to 5
  - Audits 1 to 5
  - Exports 1 and 2
  - The `politeness_json` patch on configs 4 and 5 only

  The pre-existing project 1 was not touched.
