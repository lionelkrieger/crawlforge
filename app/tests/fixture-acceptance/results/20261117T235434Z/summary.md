# Fixture acceptance run — 20261117T235434Z (WP-08-04 harness v1.0.0)

- Fixture: http://127.0.0.1:47311
- Product git HEAD: 93e07d33aa7f911801e301b949f5a077220efd1c
- Expected-results SHA-256: 9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211
- Exit code: 1 (42 selected trap verdict(s) FAIL: http:redirect-loop, http:orphan, http:postback-links, http:cookieless-session, http:large-viewstate, http:crawler-trap-filter, http:crawler-trap-calendar, http:cookie-gate, http:structured-data-errors, http:hreflang-no-return, http:thin-page, js:redirect-loop…)
- Environment: CDP port notes=['port 9222 held by an unresponsive ghost listener (netstat pids=[17600], no live process); skipped — binding over it fails (devtools 0x2740)']; fixture started_by_harness=True, reused=False. Pre-run session events: results/ENVIRONMENT-NOTES.md.

## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http | 4 | 4 | 11 | 12 |
| js | 4 | 4 | 11 | 12 |

### Mode http

| Trap | Result | Evidence |
|---|---|---|
| redirect-chain | partial | audit 17+18: /redirect/chain-1 stored as statusCode=200 (title='Redirect chain target'), redirectChain length=1 statuses=[200]; the required hop-by-hop 301>302>301 record and a redirect-chain finding are absent (findings on URL: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark |
| redirect-loop | fail | audit 17+18: audit did not complete cleanly (state=incomplete); loop handling unproven. |
| noindex | pass | audit 17+18: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |
| robots-blocked | partial | audit 17+18: /private/blocked.html was not fetched (statusCode=0) but blockReason=None and no robots finding was emitted (findings on URL: none); the URL is not affirmatively reported as blocked by robots.txt. |
| canonical-conflict | pass | audit 17+18: canonical.header-html-conflict emitted with headerTarget='/pages/canonical-target-header.html' htmlTarget='/pages/canonical-target-html.html'; canonical.mismatch count=1; headerConflicts.conflict=True. |
| broken-links | partial | audit 17+18: internal 404 recorded for /pages/does-not-exist.html (statusCode=404), but no broken-internal/broken-external finding (rule probe: none); broken.example.invalid checked=False (rows=0). |
| orphan | fail | audit 17+18: /pages/orphan.html is absent from the URL inventory although it is listed in /sitemap.xml; sitemap-only discovery does not exist, so the orphan can never be reported. |
| js-only-link | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| script-injected-content | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| late-meta-change | not_testable | trap requires dual capture; --mode http cannot exercise it (expected-results.json capture='dual'). |
| soft-404-server | pass | audit 17+18: 200 page classified soft-404 (indexability.soft-404[warning] indexability soft 404; indexability.soft-404[warning] indexability soft 404). |
| soft-404-client-route | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| hash-route | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| client-side-redirect | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| postback-links | fail | audit 17+18: /pages/postback.aspx crawled statusCode=200 but the two javascript:__doPostBack links are not reported as uncrawlable navigation (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role="main").; |
| cookieless-session | fail | audit 17+18: cookieless-session URL stored unchanged (raw session-segment rows=1, e.g. http://127.0.0.1:47311/(S(abc123xyz))/pages/session.aspx); no session-segment normalization or finding. |
| large-viewstate | fail | audit 17+18: /pages/viewstate-large.aspx crawled statusCode=200 (~152 KB __VIEWSTATE hidden input) but no ViewState size measurement or >100 KB finding (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role |
| crawler-trap-filter | fail | audit 17+18: 144 faceted-filter combination URLs crawled with no trap detection or throttling (trap findings=0); the run was bounded only by the explicit limit overrides {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null} — the cap, not the product, ended the run. |
| crawler-trap-calendar | fail | audit 17+18: 11 calendar combination URLs crawled with no trap detection or throttling (trap findings=0); the run was bounded only by the explicit limit overrides {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null} — the cap, not the product, ended the run. |
| cookie-gate | fail | audit 17+18: /cookie-gate/ stored statusCode=0 with no successful cookie-gated navigation and no loop finding (findings: none). |
| consent-banner | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| service-worker | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| structured-data-errors | fail | audit 17+18: /pages/structured-data.html crawled statusCode=200 (Article missing required properties + malformed JSON-LD block) but no structured-data validation finding (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (ma |
| missing-alt | pass | audit 17+18: accessibility.image-alt emitted (accessibility.image-alt[error] Image has no alt attribute.). |
| mixed-content | not_testable | trap flags requires_tls; the fixture is served over plain HTTP (no --tls-cert/--tls-key), so mixed content cannot exist. |
| hreflang-no-return | fail | audit 17+18: /pages/hreflang-en.html crawled but no missing-return-tag finding; i18n.hreflang-valid pass findings=1 (emitting a pass while reciprocity is missing). |
| duplicate-titles | partial | audit 17+18: both pages crawled with identical title 'Duplicate title'; content.keyword-cannibalization emitted (1) but no duplicate-title finding. |
| thin-page | fail | audit 17+18: /pages/thin.html has totalWordCount=6 but no content.thin-text finding on this URL (findings on URL: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role="main").; content.metrics[notice] content metric |
| live-websocket | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| click-only-navigation | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |
| never-place-bet | not_testable | trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised. |

### Mode js

| Trap | Result | Evidence |
|---|---|---|
| redirect-chain | partial | audit 17+18: /redirect/chain-1 stored as statusCode=200 (title='Redirect chain target'), redirectChain length=1 statuses=[200]; the required hop-by-hop 301>302>301 record and a redirect-chain finding are absent (findings on URL: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark |
| redirect-loop | fail | audit 17+18: audit did not complete cleanly (state=incomplete); loop handling unproven. |
| noindex | pass | audit 17+18: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |
| robots-blocked | partial | audit 17+18: /private/blocked.html was not fetched (statusCode=0) but blockReason=None and no robots finding was emitted (findings on URL: none); the URL is not affirmatively reported as blocked by robots.txt. |
| canonical-conflict | pass | audit 17+18: canonical.header-html-conflict emitted with headerTarget='/pages/canonical-target-header.html' htmlTarget='/pages/canonical-target-html.html'; canonical.mismatch count=1; headerConflicts.conflict=True. |
| broken-links | partial | audit 17+18: internal 404 recorded for /pages/does-not-exist.html (statusCode=404), but no broken-internal/broken-external finding (rule probe: none); broken.example.invalid checked=False (rows=0). |
| orphan | fail | audit 17+18: /pages/orphan.html is absent from the URL inventory although it is listed in /sitemap.xml; sitemap-only discovery does not exist, so the orphan can never be reported. |
| js-only-link | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); /pages/js-only-target.html is absent although rendering.enabled=true. |
| script-injected-content | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); /pages/js-content.html totalWordCount=2 (raw markup only). |
| late-meta-change | not_testable | trap requires dual capture; --mode js cannot exercise it (expected-results.json capture='dual'). |
| soft-404-server | pass | audit 17+18: 200 page classified soft-404 (indexability.soft-404[warning] indexability soft 404; indexability.soft-404[warning] indexability soft 404). |
| soft-404-client-route | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); /spa/does-not-exist is absent; the SPA shell is not rendered. |
| hash-route | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); hash views are not rendered or captured. |
| client-side-redirect | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); the meta-refresh JS redirect is not executed in a rendered tree. |
| postback-links | fail | audit 17+18: /pages/postback.aspx crawled statusCode=200 but the two javascript:__doPostBack links are not reported as uncrawlable navigation (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role="main").; |
| cookieless-session | fail | audit 17+18: cookieless-session URL stored unchanged (raw session-segment rows=1, e.g. http://127.0.0.1:47311/(S(abc123xyz))/pages/session.aspx); no session-segment normalization or finding. |
| large-viewstate | fail | audit 17+18: /pages/viewstate-large.aspx crawled statusCode=200 (~152 KB __VIEWSTATE hidden input) but no ViewState size measurement or >100 KB finding (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role |
| crawler-trap-filter | fail | audit 17+18: 144 faceted-filter combination URLs crawled with no trap detection or throttling (trap findings=0); the run was bounded only by the explicit limit overrides {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null} — the cap, not the product, ended the run. |
| crawler-trap-calendar | fail | audit 17+18: 11 calendar combination URLs crawled with no trap detection or throttling (trap findings=0); the run was bounded only by the explicit limit overrides {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null} — the cap, not the product, ended the run. |
| cookie-gate | fail | audit 17+18: /cookie-gate/ stored statusCode=0 with no successful cookie-gated navigation and no loop finding (findings: none). |
| consent-banner | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); fixture tagHits=0; the banner script never runs. |
| service-worker | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); service-worker registration requires a rendered page. |
| structured-data-errors | fail | audit 17+18: /pages/structured-data.html crawled statusCode=200 (Article missing required properties + malformed JSON-LD block) but no structured-data validation finding (findings: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (ma |
| missing-alt | pass | audit 17+18: accessibility.image-alt emitted (accessibility.image-alt[error] Image has no alt attribute.). |
| mixed-content | not_testable | trap flags requires_tls; the fixture is served over plain HTTP (no --tls-cert/--tls-key), so mixed content cannot exist. |
| hreflang-no-return | fail | audit 17+18: /pages/hreflang-en.html crawled but no missing-return-tag finding; i18n.hreflang-valid pass findings=1 (emitting a pass while reciprocity is missing). |
| duplicate-titles | partial | audit 17+18: both pages crawled with identical title 'Duplicate title'; content.keyword-cannibalization emitted (1) but no duplicate-title finding. |
| thin-page | fail | audit 17+18: /pages/thin.html has totalWordCount=6 but no content.thin-text finding on this URL (findings on URL: accessibility.color-contrast[notice] Check 'accessibility.color-contrast' requires rendering (layout/computed style) and was not evaluated: renderi; accessibility.landmark-main[warning] The page has no main landmark (main element or role="main").; content.metrics[notice] content metric |
| live-websocket | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); the websocket feed requires a rendered page and network-quiet detection. |
| click-only-navigation | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); click discovery is inert; SPA views were not discovered. |
| never-place-bet | not_testable | rendering is DETECTED inert in this js run (rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None); click discovery is inert, so the safety assertion is vacuous rather than an exercised click-safety pass. nonGet=[], tagHits=0 (global safety assertion still checked separately). |

## Runs (normal entry points)

- **http-ui** mode=http entry=UI Start audit button (CDP) + REST limit-bounding + REST poll project=8 config=13 audit=17 state=completed urls=186 findings=1522 duration=88.6s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=2
  - safety: nonGet == [] after audit
- **js-rest** mode=js entry=REST POST /api/audits project=9 config=14 audit=18 state=failed urls=0 findings=0 duration=2.0s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=True; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit
- **mcp** mode=http entry=MCP stdio adapter start-audit (REST-bounded config) project=10 config=15 audit=19 state=completed urls=43 findings=307 duration=0.0s limits={"maxUrls": 40, "maxDepth": 3, "maxDurationSeconds": 90, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=2
  - safety: nonGet == [] after audit
- **scheduled** mode=http entry=REST POST /api/projects/{id}/schedules + /api/schedules/{id}/run-now project=11 config=16 audit=21 state=completed urls=43 findings=307 duration=22.2s limits={"maxUrls": 40, "maxDepth": 3, "maxDurationSeconds": 90, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=2
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 17 (UI Start audit button (CDP) + REST limit-bounding + REST poll): nonGet=[] ok=True
  - audit 18 (REST POST /api/audits): nonGet=[] ok=True
  - audit 19 (MCP stdio adapter start-audit (REST-bounded config)): nonGet=[] ok=True
  - audit 21 (REST POST /api/projects/{id}/schedules + /api/schedules/{id}/run-now): nonGet=[] ok=True

## UI surfaces (CR-018 walker)

Status counts: {"reachable_and_functional": 6, "not_reachable": 16, "reachable_but_placeholder": 1, "not_applicable_yet": 1}

| Surface | Status | Evidence |
|---|---|---|
| UI-01 Main hub › Projects | reachable_and_functional | Hub renders real local aggregates: 8 project rows, 17 audit rows, New project button present=True. Project creation through the New project dialog is exercised by the UI-entry audit. |
| UI-02 Main hub › Project overview | not_reachable | Project row click, dblclick and command-palette project pick produced no project overview: no overview controls/text appeared; API calls during attempts=[]; final url=file:///E:/Crawlforge/app/src/frontend/dist/index.html?route=hub. |
| UI-03 Project › Configurations | not_reachable | Spec path Project overview › Configurations (overview itself unreachable, UI-02): none of the markers ['Configurations'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audi |
| UI-04 Configuration editor | not_reachable | No configuration editor: 0 of 13 documented tabs. The only configuration inputs are the New project dialog fields ["project-name-input", "project-url-input", "project-description-input"]; no limit, AIMD, rendering, directive, analysis or extraction inputs exist. Audit limits must be bounded via REST |
| UI-05 Project › Integrations | not_reachable | Spec path Project overview › Integrations: none of the markers ['Integrations'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audit", "Start audit", "Start audit", "Start  |
| UI-06 Project › Segments | not_reachable | Spec path Project overview › Segments / report segment picker: none of the markers ['Segments'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audit", "Start audit", "Start |
| UI-07 Project › Audit history and retention | not_reachable | Project overview › Audits and retention policy do not exist. The global Audits rail (nav-audits present=True) is a different surface and does not satisfy the project-scoped path. |
| UI-08 Main hub › Clients | not_reachable | Spec path Main hub › Clients: none of the markers ['Clients'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audit", "Start audit", "Start audit", "Start audit", "Start aud |
| UI-09 Main hub › Schedules | not_reachable | Spec path Main hub › Schedules: none of the markers ['Schedules'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audit", "Start audit", "Start audit", "Start audit", "Start |
| UI-10 Audit run view | reachable_and_functional | Audit run view renders real run data for audit 17: state badge, progress/counters and host-throttle telemetry present; lifecycle controls mounted for active runs. |
| UI-11 Report › Dashboard | reachable_but_placeholder | Report workspace shows the static placeholder text 'mount into this workspace from the report workpieces' and no dashboard data grid/chart. Live telemetry above it is real, but the Dashboard surface itself is an empty frame (CR-018: never functional). |
| UI-12 Report › Issue explorer | not_reachable | Report window navigation has no entry for any of ['Issue explorer', 'Issues'] (buttons/testids only; telemetry labels do not count); the report frame exposes only live telemetry and the placeholder pane (see UI-11). |
| UI-13 Report › URL explorer and URL detail | not_reachable | Report window navigation has no entry for any of ['URL explorer', 'URLs'] (buttons/testids only; telemetry labels do not count); the report frame exposes only live telemetry and the placeholder pane (see UI-11). |
| UI-14 Visualization window | not_reachable | Report window navigation has no entry for any of ['Visualizations', 'Link graph'] (buttons/testids only; telemetry labels do not count); the report frame exposes only live telemetry and the placeholder pane (see UI-11). |
| UI-15 Report › Comparison (audit and content) | not_reachable | Report window navigation has no entry for any of ['Compare', 'Comparison'] (buttons/testids only; telemetry labels do not count); the report frame exposes only live telemetry and the placeholder pane (see UI-11). |
| UI-16 Report › Specialist surfaces | not_reachable | None of the 19 specialist surfaces has a navigation entry in the report window. |
| UI-17 Log file analyzer | not_reachable | Spec path Project overview › Logs: none of the markers ['Logs', 'Log file analyzer'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "New project", "Start audit", "Start audit", "Start audit", "S |
| UI-18 Single-page analyzer | reachable_and_functional | Single-Page Analyzer ran against http://127.0.0.1:47311/pages/noindex.html without a crawl project: finding count='7', findings list rendered=True; API calls=["POST /api/analysis/single-page {\"url\":\"http://127.0.0.1:47311/pages/noindex.html\",\"render\":false}"]. |
| UI-19 Standalone tools | reachable_and_functional | Standalone tools workspace: 12/12 tool panels mount with real controls without a crawl project. |
| UI-20 Export dialog | not_reachable | No Export control in the report window (buttons/testids). REST export jobs work (probe-2 §7) but no UI export dialog is mounted. |
| UI-21 Settings | reachable_and_functional | Settings surface renders sections ['Appearance', 'Status-bar advisories', 'Database', 'Automation interface', 'Embeddings', 'Proxy', 'Diagnostics']; documented sections missing: ['Export destinations', 'Browser', 'User dictionary', 'Integrations credentials', 'Configuration templates', 'Release chec |
| UI-22 Plugin manager | not_reachable | Spec path Settings › Plugins: none of the markers ['Plugins', 'Plugin manager'] appear in the running shell (rail buttons=["Open command palette (Ctrl+K)", "Main hub", "Audits", "Report", "Tools", "Settings", "Toggle activity pane", "", "", "", "", "Save advisories (1.50 GiB / 256 MiB)", "Test conne |
| UI-23 Status bar | reachable_and_functional | Status bar visible with live telemetry (WS='WS207 MiB', Heap='Heap29.3 MiB') and the Compact action. |
| UI-24 Recovery screen | not_applicable_yet | Recovery screen is shown automatically after repeated backend failures; the backend is healthy and acceptance must NOT induce a backend failure to reach it. Not exercised. |
