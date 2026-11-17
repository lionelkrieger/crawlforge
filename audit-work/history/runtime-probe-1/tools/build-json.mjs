// Builds runtime-probe.json from the probe's recorded observations.
import fs from 'node:fs';
const S = 'screenshots/';
const out = {};

out.environment = {
  probe_date: '2026-11-17',
  git_head: '18c53da',
  os: 'Windows 11 Pro 10.0.26300',
  node: 'v26.5.1',
  dotnet_sdk: '11.0.100-rc.3.26522.148 (app targets net10.0, RollForward=LatestMajor, runs on Microsoft.AspNetCore.App 11.0.0-rc.3)',
  electron: 'app/packaging/vendor/electron/electron.exe',
  build_result: 'launch-crawlforge.ps1: backend, frontend bundle and Electron shell all reported "up to date"; no rebuild; exit 0; main window "CrawlForge" loaded (route=hub).',
  debug_launch: 'Copy of launch script at audit-work/runtime-probe/tools/launch-debug.ps1 starting electron.exe with --remote-debugging-port=9222; driven via playwright-core 1.49.1 connectOverCDP.',
  ports: { fixture: 47311, cdp: 9222, backend_rest: 61219, mcp_loopback: 61222, note: 'backend port is dynamic (Port=0), read from %APPDATA%/CrawlForge/loopback-handshake.json; REST auth header X-CrawlForge-Session' },
  data_directory: '%APPDATA%/CrawlForge (pre-existing data: project "Airpower" from an earlier session)',
  logs: ['logs/launch-original.log', 'logs/launch-debug.log', 'logs/launch-transcript-debug-run.log', 'logs/electron-stdout-trimmed.log', 'logs/electron-stderr.log', 'logs/backend-structured-errors-warnings.jsonl', 'logs/backend-structured-nonhttp.jsonl'],
  shell_warning: 'electron-stderr: "[crawlforge] Main window hydration timed out; plugin scan deferred."',
};

const surf = (status, evidence, screenshot = null, extra = {}) => ({ status, evidence, screenshot, ...extra });
out.surfaces = {
  'UI-01': surf('reachable_functional', 'Main hub lists projects (2) with counters; New project dialog (name, target URL, description) created project 2 via POST /api/projects + POST /api/projects/2/configurations; per-row Start audit issued POST /api/audits {configurationId:"2"}. Hub "Audits" panel is stale until "Refresh local aggregates".', S + 'UI-01-main-hub.png', { more_screenshots: [S + 'UI-01-new-project-dialog.png', S + 'UI-01-new-project-filled.png', S + 'UI-01-after-create.png'] }),
  'UI-02': surf('not_reachable', 'Clicking, double-clicking the project row and picking the project from the command palette all leave the Main hub unchanged; no API call, no overview.', S + 'UI-02-row-click.png'),
  'UI-03': surf('not_reachable', 'No Configurations entry anywhere (rail, palette, hub). Bundle contains no "Configurations" string.'),
  'UI-04': surf('not_reachable', 'No configuration editor. 0 of 13 tabs present. Of the 18 audit inputs in 03-crawl-engine/02 §5.1 only Seed URL is settable in the UI (New project "Target URL"); robots toggle is hard-coded true by the dialog. Capture mode, limits, scope, include/exclude, headers/cookies etc. only via REST.', null, { tabs_present: [], tabs_missing: ['Scope', 'Crawl as and capture', 'Limits', 'Rules', 'Directives', 'Resources and external scope', 'Authentication and cookies', 'Click discovery and site types', 'Consent states', 'Analysis categories', 'Thresholds', 'Extraction and search', 'Advanced'], section_5_1_inputs_in_ui: ['Seed URL(s) (New project dialog only, at creation)'] }),
  'UI-05': surf('not_reachable', 'No Integrations entry; no integration controllers in backend route list.'),
  'UI-06': surf('not_reachable', 'No Segments entry or segment picker.'),
  'UI-07': surf('not_reachable', 'No per-project Audits/retention view; the global Audits rail view (UI-10 list) is the only audit list.'),
  'UI-08': surf('not_reachable', 'No Clients entry.'),
  'UI-09': surf('not_reachable', 'No Schedules entry (backend has SchedulesController).'),
  'UI-10': surf('reachable_functional', 'Reached via hub Start audit (auto-opens) and Audits › Open report. Shows state, phase, fetched/queued counters, Pause/Cancel, host-throttle panel, live activity feed. DEFECTS: (1) renderer crashes to a blank window (uncaught TypeError reading "toFixed" of undefined in Host throttle table, e.ttfbEwmaMs) as soon as host-throttle data arrives for a running audit — reproduced twice, recovered only by reload; (2) failed audits stay shown as RUNNING with no error until manual refresh; (3) audits started via REST/MCP do not appear until manual refresh; (4) no error reason surfaced for failed audits.', S + 'UI-10-run-view-audit3.png', { more_screenshots: [S + 'UI-10-after-start.png', S + 'UI-10-audits-list.png', S + 'UI-10-run-view-audit5-js.png', S + 'UI-10-renderer-crash-blank.png', S + 'UI-10-renderer-crash-blank-2.png'] }),
  'UI-11': surf('reachable_but_placeholder', 'Report rail / Open report shows run telemetry plus a static "REPORT SURFACES … mount into this workspace from the report workpieces" paragraph; no dashboard.', S + 'UI-11-report-placeholder.png'),
  'UI-12': surf('not_reachable', 'No issue explorer navigation in the report area.'),
  'UI-13': surf('not_reachable', 'No URL explorer / URL detail navigation; data exists in REST (/api/audits/{id}/urls, /urls/{id}).'),
  'UI-14': surf('not_reachable', 'No Visualizations entry; REST /graph and /charts exist.'),
  'UI-15': surf('not_reachable', 'No Compare entry; REST /api/audits/compare exists.'),
  'UI-16': surf('not_reachable', 'None of the 19 specialist surfaces is navigable.'),
  'UI-17': surf('not_reachable', 'No Logs entry; LogsController exists in backend.'),
  'UI-18': surf('reachable_functional', 'Reached via Tools rail › Single-Page Analyzer (not a hub toolbar "Analyze a page"). Analysing /pages/thin.html returned 7 findings and 10 elements via POST /api/analysis/single-page; no thin-content finding for a 6-word page; Render toggle does not change analysis (color-contrast still "rendering disabled").', S + 'UI-18-single-page-thin.png'),
  'UI-19': surf('reachable_functional', 'Tools rail lists 12 tools (SERP preview, Render Tester + Picker, Robots tester, Sitemap generator, Content scope preview, Rewrite & host mapping, llms.txt, URL Check, Structured data checker, Query builder, Export destinations, Single-page analyzer). Render Tester rendered /pages/js-content.html with Chromium (script-injected text visible, console 404 captured). URL Check on /redirect/chain-1 reports a single hop with status 200 instead of 301>302>301.', S + 'UI-19-render-tester-run.png', { more_screenshots: fs.readdirSync('../screenshots').filter(f => f.startsWith('UI-19-')).map(f => S + f) }),
  'UI-20': surf('not_reachable', 'No Export action in any report surface. REST POST /api/exports produced valid CSV (zip of urls.csv, findings.csv) and XLSX for audit 3.'),
  'UI-21': surf('reachable_functional', 'Settings rail: Appearance, UI language, Status-bar advisories, Database (SQLite, Test connection), MCP (enable, transport, token), Embeddings (ONNX, Test provider), Proxy (read-only "Disabled"), Diagnostics (artifact list, Refresh, Purge). Missing sub-sections: export destinations (lives in Tools), browser, user dictionary, integrations credentials, configuration templates, MCP action log, release check, Plugins.', S + 'UI-21-settings.png'),
  'UI-22': surf('not_reachable', 'No Plugins entry in Settings or elsewhere.'),
  'UI-23': surf('reachable_functional', 'Status bar always visible: working set, managed heap, GC counts, WAL size, JS heap, heartbeat time, Compact action.', S + 'UI-23-status-bar.png'),
  'UI-24': surf('not_exercised', 'Shown only after repeated backend failures; not induced during this probe (would have disrupted measurement). Recovery code exists in electron/src/recovery.'),
};

const modes = ['http', 'js', 'http_diagnostic', 'js_diagnostic'];
out.runs = {
  http: { entry_point: 'UI (hub Start audit) and MCP start-audit, configuration created by the UI New project dialog (config 2)', audit_ids: [1, 4], state_transitions: ['running', 'failed (finalizing) after 0.038 s'], completed: false, url_count: 0, duration_s: 0.038, failure: 'ArgumentOutOfRangeException: The initial rate must be a positive finite number. (Parameter InitialRate) Actual value was 0.', nonGet: [], tagHits: [] },
  js: { entry_point: 'REST: POST /api/projects/2/configurations with rendering.enabled=true (config 3; stored rendering_mode=1/auto), POST /api/audits', how_enabled: 'Only REST (rendering.enabled) — no UI control exists', audit_ids: [2], state_transitions: ['running', 'failed after ~3 ms'], completed: false, url_count: 0, failure: 'same InitialRate=0 exception', nonGet: [], tagHits: [] },
  http_diagnostic: { entry_point: 'REST start (POST /api/audits) of config 4. NOT a normal entry point: politeness_json of this probe-only config was rewritten directly in SQLite to the flat AimdSettings shape {"InitialRate":10.0,...} the engine deserialises; maxDurationSeconds=480 set via REST', audit_id: 3, state_transitions: [{ t: 0, state: 'running', phase: 'crawling' }, { t: 477.3, state: 'running', phase: 'analyzing' }, { t: 501.3, state: 'completed', phase: 'finalizing' }], completed: true, completed_by: 'maxDurationSeconds=480 cap (crawler traps never detected)', url_count: 1061, fetched: 1058, links: 3391, findings: 7647, duration_s: 503.5, trap_url_counts: { '/trap/calendar': 886, '/trap/filter': 144 }, throttle: { throttledHostCount: 1, meanEffectiveRate: 5.14, adjustmentCount: 205 }, nonGet: [], tagHits: [], data: 'logs/audit-3-data.json.gz' },
  js_diagnostic: { entry_point: 'MCP start-audit (projectId 2, configurationId 5) via the stdio adapter; config 5 = rendering.enabled=true via REST + same DB politeness patch + maxDurationSeconds=480', audit_id: 5, state_transitions: ['running/crawling', 'completed/finalizing at 511.5 s'], completed: true, completed_by: 'maxDurationSeconds cap', url_count: 1548, fetched: 1545, links: 4365, findings: 11056, duration_s: 509.7, trap_url_counts: { '/trap/calendar': 1373, '/trap/filter': 144 }, rendering_evidence: 'snapshot renderingMode=auto, yet every page reports "no rendered tree is available for this page"; js-content.html wordCount 2; findings by rule identical to http_diagnostic', nonGet: [], tagHits: [], data: 'logs/audit-5-data.json.gz' },
};

// Trap results.
const NT_JS = 'not_testable';
const failStart = { result: 'fail', evidence: 'Audit fails at start (InitialRate=0); 0 URLs crawled.' };
const diag = {
  'redirect-chain': ['partial', 'URL row /redirect/chain-1 status 200, redirectChain [] in URL detail, no chain finding; only the final target is kept (CSV "Redirect Target" = /pages/redirect-target.html). Hops 301>302>301 not recorded.'],
  'redirect-loop': ['partial', 'URL /redirect/loop-a recorded as FetchError (status 0, statusClass 5xx); crawl did not hang; no loop finding.'],
  'noindex': ['pass', 'indexability.noindex (notice) on /pages/noindex.html. Note: URL row still has indexable=true.'],
  'robots-blocked': ['partial', 'Not fetched: CSV export shows Status NotCrawled, Robots State Disallowed. But REST URL row has blockReason null, status 0/1xx, and no robots finding.'],
  'canonical-conflict': ['pass', 'canonical.header-html-conflict (error) and canonical.mismatch (warning) on /pages/canonical-conflict.html.'],
  'broken-links': ['partial', 'Internal /pages/does-not-exist.html crawled as 404 (ClientError); external https://broken.example.invalid/page stored as a link but not checked; no broken-link finding for either.'],
  'orphan': ['fail', '/pages/orphan.html never appears: sitemap.xml not used for discovery; no orphan finding.'],
  'js-only-link': [NT_JS, 'Requires JS capture; rendering is inert in full audits. Observed in both modes: /pages/js-only-target.html not discovered.'],
  'script-injected-content': [NT_JS, 'Requires JS capture; rendering inert. js-content.html wordCount 2 in both modes; flagged content.thin-text instead.'],
  'late-meta-change': [NT_JS, 'Requires dual capture (does not exist). Title stays "Raw title before script"; no rendered comparison.'],
  'soft-404-server': ['pass', 'indexability.soft-404 (warning) on /pages/soft-404.html (emitted twice for the same URL).'],
  'soft-404-client-route': [NT_JS, 'Requires JS capture; /spa/does-not-exist not discovered.'],
  'hash-route': [NT_JS, 'Requires JS capture; /hash-app/ crawled only as raw HTML; no hash-route finding.'],
  'client-side-redirect': [NT_JS, 'Requires JS capture; /pages/js-redirect-target.html not discovered; no client-side redirect finding.'],
  'postback-links': ['fail', '/pages/postback.aspx crawled; no postback / uncrawlable-navigation finding (only content.spelling).'],
  'cookieless-session': ['fail', 'URL stored as /(S(abc123xyz))/pages/session.aspx; session segment not removed; no finding.'],
  'large-viewstate': ['fail', 'No ViewState finding on /pages/viewstate-large.aspx (only content.thin-text).'],
  'crawler-trap-filter': ['fail', '144 /trap/filter URLs crawled; no trap detection or finding; audit ended only by the 480 s duration cap.'],
  'crawler-trap-calendar': ['fail', '886 (http) / 1373 (js) /trap/calendar URLs crawled; no trap detection; audit ended only by the 480 s duration cap.'],
  'cookie-gate': ['fail', '/cookie-gate/ recorded as FetchError (status 0); cookie jar did not yield the 200.'],
  'consent-banner': [NT_JS, 'Requires JS capture; tagHits stayed empty because nothing rendered.'],
  'service-worker': [NT_JS, 'Requires JS capture; no service-worker finding.'],
  'structured-data-errors': ['fail', 'No structured-data finding on /pages/structured-data.html in the audit.'],
  'missing-alt': ['pass', 'accessibility.image-alt (error) "Image has no alt attribute." on /pages/images.html; empty-alt image not flagged.'],
  'mixed-content': ['not_testable', 'Requires HTTPS fixture (--tls-cert/--tls-key); probe served HTTP.'],
  'hreflang-no-return': ['fail', 'Both hreflang-en and hreflang-fr crawled; i18n.hreflang-valid (pass) emitted for the English page; missing return tag not reported.'],
  'duplicate-titles': ['partial', 'No duplicate-title rule fired; only content.keyword-cannibalization with term "duplicate title" (zone title) clustering dup-title-a and dup-title-b.'],
  'thin-page': ['fail', '/pages/thin.html wordCount 6 but no content.thin-text finding (that rule fired on 5 other pages).'],
  'live-websocket': [NT_JS, 'Requires JS capture with a wait rule; nothing rendered.'],
  'click-only-navigation': [NT_JS, 'Requires JS + click discovery (absent). /spa/markets found only through a plain link; /spa/promotions and /spa/results not discovered; no non-crawlable navigation inventory.'],
  'never-place-bet': [NT_JS, 'Requires JS + click discovery. nonGet stayed [] in every run, but vacuously: no page was rendered or clicked.'],
};
const expected = JSON.parse(fs.readFileSync('E:/Crawlforge/app/tests/fixture-site/expected-results.json', 'utf8'));
out.traps = {};
for (const t of expected.traps) {
  const [r, e] = diag[t.id];
  const normal = t.id === 'mixed-content' ? { result: 'not_testable', evidence: 'Requires HTTPS; and the audit fails at start anyway.' } : failStart;
  out.traps[t.id] = { http: normal, js: normal, http_diagnostic: { result: r, evidence: e }, js_diagnostic: { result: r, evidence: e + (r !== NT_JS ? ' Identical in js_diagnostic (rendering inert).' : '') } };
}
out.global_assertions = { 'no-non-get-requests': { result: 'pass', evidence: 'GET /__fixture/requests returned nonGet [] and tagHits [] after audits 1, 2, 3, 4 and 5 (vacuous for click safety: no rendering or click discovery ran).' } };
out.trap_counts = {};
for (const m of modes) { const c = {}; for (const t of Object.values(out.traps)) c[t[m].result] = (c[t[m].result] ?? 0) + 1; out.trap_counts[m] = c; }

const mcpList = JSON.parse(fs.readFileSync('../logs/mcp-list.json', 'utf8'));
out.mcp = {
  transport: 'stdio adapter app/src/mcp-stdio-adapter/dist/index.js → loopback endpoint from %APPDATA%/CrawlForge/mcp-endpoint.json; client @modelcontextprotocol/sdk 1.32.2',
  server: mcpList.server,
  tools: mcpList.tools.tools.map(t => t.name),
  resources: mcpList.resources.resources.map(r => `${r.name} ${r.uri}`),
  resource_templates: 'resources/templates/list → MCP error -32601 Method not found (templated URIs are listed as plain resources)',
  prompts: mcpList.prompts.prompts.map(p => p.name),
  project_or_configuration_tools: 'absent — no tool creates or edits projects or configurations',
  flow_result: {
    normal_config: 'start-audit ok (audit 4) → get-audit-progress immediately Failed/Finalizing with 0 fetched → audit-summary readable → high-severity-findings [] → audit-health-briefing prompt ok. Flow mechanics work; audit does not run.',
    diagnostic_config: 'start-audit (audit 5, config 5) → polled Running → Completed at 511.5 s (fetched 1545) → audit-summary and high-severity-findings read with evidence JSON → prompt ok.',
    re_audit: 'tool error: "The JSON value could not be converted to System.Int64. Path: $.id" (isError true)',
    analyze_single_page: 'works; render=true makes no difference to analysis (color-contrast still "rendering mode is disabled")',
    invalid_token: 'adapter exits with "Unauthorized: invalid local token" (rejected)',
  },
  logs: ['logs/mcp-list.json', 'logs/mcp-flow-normal-config.json', 'logs/mcp-flow-diag-js.json'],
};

const C = (result, reason) => ({ result, reason });
out.criteria = {
  1: C('not_testable', 'Not a first launch; existing data directory.'),
  3: C('not_testable', 'Network monitoring not performed in this session.'),
  4: C('pass', 'Default configuration snapshot: maxUrls/maxDepth null (unlimited); diagnostic crawls ran uncapped apart from the duration limit added for the probe.'),
  5: C('pass', 'No license/account concept in observed screens or payloads (observed surfaces only).'),
  6: C('not_testable', 'Pause/resume/crash recovery not exercised (time-box).'),
  7: C('fail', 'Report surfaces not mounted; REST CSV/XLSX exports produce valid local files.'),
  10: C('pass', 'Single-page analysis returned findings without a project (UI Tools and MCP analyze-single-page).'),
  16: C('partial', 'stdio handshake works and invalid token is rejected; SSE transport, non-loopback and rotation not tested.'),
  20: C('partial', '/metrics serves exposition format on loopback (requires session header); push/identity not audited.'),
  23: C('partial', 'start/poll/complete/read high-severity via MCP works only with a DB-patched config; re-audit tool errors; action log not visible in UI.'),
  27: C('fail', 'Audit emits no structured-data findings on the fixture structured-data page.'),
  28: C('pass', 'Readability, reading time, text-to-HTML computed (content.metrics); soft-404 flagged distinctly.'),
  33: C('partial', 'Standalone tools present and reachable; robots tester, sitemap generator, query builder not functionally exercised.'),
  36: C('partial', 'Host throttle summary shows 205/159 AIMD adjustments; UI throttle panel crashes the renderer.'),
  38: C('partial', 'content.keyword-cannibalization emitted with cluster evidence (title/headings 2-grams); n-gram endpoints and MCP resource not read.'),
  41: C('partial', 'Status bar shows WS, heap, GC, WAL, JS heap and heartbeat; /metrics has the families; Compact not exercised.'),
  42: C('partial', 'Render Tester shows sandboxed picker and selector preview; click-to-capture not exercised.'),
  48: C('partial', 'canonical.header-html-conflict emitted as error on the conflict page; zone weights present on link edges; pixel truncation only seen in SERP tool.'),
  49: C('fail', 'UI-created config cannot run (InitialRate=0); no config editor; no results grid; export only via REST. Diagnostic run: 4 pass / 5 partial / 10 fail on applicable traps.'),
  50: C('fail', 'No UI capture-mode control; REST rendering.enabled audits fail at start; with DB patch, rendering is inert in full audits.'),
  51: C('fail', 'No dual capture or raw-vs-rendered comparison.'),
  52: C('fail', 'No adaptive capture.'),
  53: C('fail', 'Postback, cookieless session, ViewState fail; hash route, client redirect, rendered soft 404 not testable (no rendering).'),
  54: C('fail', 'No rendering or click discovery; nonGet empty only vacuously.'),
  55: C('fail', 'Filter and calendar traps not detected (886-1373 calendar URLs); cookie gate fetch error.'),
  56: C('fail', 'No configuration editor in the UI.'),
  57: C('fail', 'MCP has no project/configuration tools; audit via MCP fails on a normal config; paged findings with evidence readable only after DB patch.'),
  58: C('fail', 'No integrations surface or backend integration endpoints observed.'),
};

const cap = (result, evidence) => ({ evidence_level: 'runtime', result, evidence });
out.capabilities = {
  'CAP-A-001': cap('partial', 'HTTP crawling works only with DB-patched politeness; normal configs fail at start.'),
  'CAP-A-004': cap('fails', 'rendering.enabled=true stored as RenderMode.Auto but no page in a full audit has a rendered tree; Render Tester tool does render.'),
  'CAP-A-006': cap('partial', 'Disallowed URL not fetched (CSV Robots State Disallowed) but not reported via REST/finding.'),
  'CAP-A-007': cap('works', 'indexability.noindex emitted.'),
  'CAP-A-010': cap('fails', 'Redirect hops not recorded; only final target.'),
  'CAP-A-011': cap('partial', 'Loop ends as FetchError, crawl continues; not reported as loop.'),
  'CAP-A-013': cap('fails', 'Cookieless session segment not removed.'),
  'CAP-A-035': cap('fails', 'No raw vs rendered comparison in audits.'),
  'CAP-A-X09': cap('fails', 'Only non-JS effective; capture mode not settable in UI.'),
  'CAP-A-X10': cap('fails', 'No rendering in full audits.'),
  'CAP-A-X12': cap('fails', 'Cookie-gate page ends as FetchError.'),
  'CAP-A-X13': cap('fails', 'Calendar/filter traps not detected.'),
  'CAP-A-X15': cap('fails', 'No postback finding.'),
  'CAP-A-X16': cap('fails', 'No hash-route handling observed.'),
  'CAP-A-X17': cap('absent', 'No click discovery / non-crawlable navigation inventory.'),
  'CAP-A-X18': cap('absent', 'No service-worker finding.'),
  'CAP-A-X19': cap('absent', 'No consent-state rendering; no UI.'),
  'CAP-B-036': cap('works', 'noindex finding emitted (URL row indexable flag inconsistent).'),
  'CAP-B-037': cap('partial', 'Robots state stored in export only.'),
  'CAP-B-038': cap('works', 'canonical.mismatch emitted.'),
  'CAP-B-X02': cap('works', 'canonical.header-html-conflict emitted.'),
  'CAP-B-040': cap('partial', 'Only via keyword-cannibalization cluster.'),
  'CAP-B-046': cap('partial', '404 target recorded; no broken-link finding.'),
  'CAP-B-047': cap('fails', 'External link not checked.'),
  'CAP-B-048': cap('fails', 'Sitemap-only orphan never discovered.'),
  'CAP-E-122': cap('fails', 'Same as CAP-B-048.'),
  'CAP-B-049': cap('fails', 'No chain finding.'),
  'CAP-B-050': cap('fails', 'No loop finding.'),
  'CAP-B-055': cap('works', 'accessibility.image-alt error.'),
  'CAP-D1-101': cap('works', 'accessibility.image-alt error.'),
  'CAP-B-060': cap('fails', 'hreflang-valid pass emitted despite missing return tag.'),
  'CAP-B-061': cap('partial', 'content.thin-text fires on some pages but not on the 6-word thin page.'),
  'CAP-F-148': cap('partial', 'Same as CAP-B-061.'),
  'CAP-B-067': cap('fails', 'No structured-data findings in audits.'),
  'CAP-B2-075': cap('fails', 'Same as CAP-B-067.'),
  'CAP-B-X03': cap('partial', 'Server soft 404 detected (duplicated finding); client-route soft 404 not testable.'),
  'CAP-B-X05': cap('fails', 'No client-side redirect detection.'),
  'CAP-C-X05': cap('fails', 'No ViewState finding.'),
  'CAP-D2-114': cap('absent', 'Not testable over HTTP; no mixed-content rule seen.'),
  'CAP-H-X06': cap('absent', 'No tag/consent coverage in audits; tagHits empty.'),
  'CAP-G-X01': cap('works', 'Single-page analyzer and render tester work in UI-18/UI-19.'),
  'CAP-H-168': cap('absent', 'UI-11 dashboard is a placeholder.'),
  'CAP-H-169': cap('absent', 'UI-13 not reachable (REST URL inventory works).'),
  'CAP-H-173': cap('partial', 'CSV export via REST only; no UI-20.'),
  'CAP-H-174': cap('partial', 'XLSX export via REST only; no UI-20.'),
  'CAP-H-185': cap('partial', 'MCP server reachable via stdio; settings toggle present; action log not in UI.'),
  'CAP-H-190': cap('works', 'UI-01 lists and creates projects.'),
  'CAP-K-X01': cap('absent', 'No UI-03/UI-04.'),
  'CAP-L-X05': cap('partial', 'Status bar and hub events present; live activity feed only after re-subscribe; throttle event crashes renderer.'),
  'CAP-A-032': cap('partial', 'Pause button present in UI-10; not exercised.'),
  'CAP-A-X01': cap('partial', 'AIMD adjusts (205 adjustments) once politeness JSON is in the flat shape; API-written shape breaks it.'),
};

fs.writeFileSync('../runtime-probe.json', JSON.stringify(out, null, 2));
console.log(JSON.stringify(out.trap_counts), Object.values(out.surfaces).reduce((a, s) => (a[s.status] = (a[s.status] ?? 0) + 1, a), {}));
