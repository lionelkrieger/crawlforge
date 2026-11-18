# WP-08-04 Fixture Acceptance Harness — design notes

Builder: cf-wp-08-04 · Phase 08, wave 8B · Status: see `results/` evidence.
(`README.md` in this folder is owned by WP-08-05 / lead; this file is the
builder's design record.)

## Purpose

Measure the REAL CrawlForge product against `app/tests/fixture-site` through
**normal entry points only** (UI over CDP, REST, MCP stdio adapter, schedule
run-now) and compare every trap against `expected-results.json`. The harness
records defects; it never fixes product code, never patches databases, never
seeds state, and never edits the fixture or expectations. Completion authority
is the measured verdict, not builder self-declaration (AGENT-INSTRUCTIONS §5.6,
§12; 07/03 §2 acceptance rule incl. CR-023 entry points).

## Layout

```
run_fixture_acceptance.py     CLI + orchestration (Python 3.14 stdlib only)
launch-debug.ps1              COPY of app/launch-crawlforge.ps1; only diffs:
                              -CdpPort appends --remote-debugging-port, Start-Process
                              + stdout/stderr redirects into results/, pinned .NET 10
                              PATH, duplicate-case proxy env cleanup (probe-2 pattern).
                              The original launcher is never modified.
harness/
  rest.py                     loopback-handshake discovery + X-CrawlForge-Session REST
  fixture.py                  fixture lifecycle (47311), reset/requests recorder,
                              no-non-get-requests assertion, tagHits inspection
  launch.py                   SDK check, CDP port pick (9222→9223), launcher invocation
                              with CF_AGENT_ID UNSET (sanctioned normal entry point),
                              readiness wait, identified-tree-only shutdown
  cdp.py                      Python driver for node/cdp.mjs --serve (JSON lines)
  audits.py                   project/config creation, REST/UI/MCP/schedule starts,
                              polling, REST data pulls, rendering-inertness and
                              click-discovery DETECTION from evidence
  verdicts.py                 per-trap expectation logic (measured, never hardcoded)
  ui_walker.py                UI-01..UI-24 spec-path walker (CR-018)
  evidence.py                 results/<timestamp>/ writer + human summary
  mcp.py                      driver for node/mcp_client.mjs
node/
  cdp.mjs                     raw CDP client on Node built-in WebSocket+fetch (no
                              playwright, no installs): eval/click/fill/key/shot/events
  mcp_client.mjs              MCP Client via workspace-hoisted @modelcontextprotocol/sdk
                              (resolved from app/node_modules by walking up; verified
                              v1.32.2). Spawns the stdio adapter with --endpoint/--token
                              from %APPDATA%/CrawlForge/mcp-endpoint.json.
tests/                        stdlib unittest self-tests (see below)
results/<timestamp>/          evidence (verdicts.json, surfaces.json, summary.md,
                              audit-ids.json, fixture-request-log.json, ui-entry.json,
                              mcp-*.json, screenshots/, raw audit pulls, logs)
```

## Entry points (CR-023 / 07/03 §2)

- **http bulk audit = UI-started by default.** Project created through the UI
  New project dialog over CDP (`new-project-button` → `project-name-input` /
  `project-url-input` / `project-description-input` → `create-project-submit`);
  the dialog's default configuration is then **bounded via REST PUT
  /api/configurations/{id}** with `{maxUrls:220, maxDepth:6, maxDurationSeconds:180}`
  and read back to verify. The UI has NO limit controls (UI-04 absent) — this
  REST bounding is recorded in `ui-entry.json`, never hidden. **Start audit is
  pressed in the UI** (`start-audit-{projectId}`); the run view (UI-10) is
  inspected over CDP while running and after completion. Polling and data pulls
  use REST (read-only). `--rest-start` forces a REST start for debugging.
- **js bulk audit = REST-started** (probe-2 style): configuration with
  `rendering.enabled=true, deviceProfile=desktop, captureConsole=true`; the UI
  has no capture-mode control (recorded). Same explicit limit overrides —
  REQUIRED because crawler-trap detection is absent: the cap, not the product,
  bounds the run (recorded in every trap-fail evidence for the trap families).
- **--mcp**: project + REST-bounded configuration (`maxUrls:40, maxDepth:3,
  maxDurationSeconds:90` — probe-2 §6 pattern; the `start-audit` tool schema
  now advertises optional `limitOverrides` (see `mcp-list.json`), but the
  harness does not rely on it — REST bounding is safe under either behaviour), then
  `start-audit` → `get-audit-progress` polling → `audit-summary` /
  `high-severity-findings` resources → `audit-health-briefing` prompt through
  the stdio adapter. MCP gaps (no project/config tools, templated-URI
  `resources/templates/list` error, `re-audit` `$.id` deserialization error) are
  recorded as evidence, not harness failures.
- **--scheduled**: REST `POST /api/projects/{id}/schedules` (interval recurrence,
  enabled) + `POST /api/schedules/{id}/run-now`; run history read; the
  harness-owned schedule is deleted afterwards (existing data never touched).
  UI-09 absence recorded.
- **dual / adaptive**: no wire-contract support exists today
  (`RenderingSettingsWire` = enabled/deviceProfile/infiniteScroll/captureConsole
  only; ProjectWireContracts.cs:23). The harness MEASURES this: it POSTs a
  configuration carrying `captureMode`/`renderingMode` fields, reads it back,
  records that the fields are not persisted, and marks **all** traps
  `not_testable` for that mode (an unrunnable mode cannot produce trap evidence;
  running a js audit under a dual label would misrepresent the entry point).
  No audit is run for these modes.

## Verdict semantics (probe-2 §5 conventions)

`pass` = expected finding/state present · `partial` = some required evidence
present but a required finding/classification missing · `fail` = expected
detection absent though the trap was exercised (or a page that should be
discovered is absent, e.g. the sitemap-only orphan) · `not_testable` = the
capture mode cannot exercise the trap.

Gating order in `evaluate_trap`:
1. dual/adaptive mode → not_testable (measured wire absence).
2. capture='dual' in http/js → not_testable; capture='js' in http → not_testable.
3. `requires_tls` without TLS → not_testable.
4. capture='js' in js mode with **DETECTED** rendering inertness → not_testable.
   Inertness is measured, never assumed: `'no rendered tree is available'`
   findings AND `/pages/js-only-target.html` absent AND js-content word count
   at the raw-markup value (~2). If a future build renders for real, the js
   traps are evaluated normally.
5. `never-place-bet` is additionally vacuous (not_testable) when click discovery
   produced no exercised navigation — only `/spa/promotions` and `/spa/results`
   count as click-only views (`/spa/markets` is plain-linked from
   `/live/odds.html`, measured in probe-2). The global `nonGet==[]` assertion is
   still checked after EVERY audit regardless.

Rule-key matching uses the product taxonomy (IndexabilityRuleKeys,
StructuredDataRuleKeys, AccessibilityRuleKeys, FindingRuleKeys) plus tolerant
substring probes so future conforming detectors are picked up. Audit-level
findings (e.g. `content.keyword-cannibalization`, `urlId=null`) are matched via
`evidence.details.members[].url`.

### Acceptance matrix pooling (probe-2 convention — documented interpretation)

probe-2's §5 trap table cites "Audit 11/12" jointly: its http and js columns
are identical for all 31 traps because verdicts were evaluated over the pooled
session evidence (e.g. `broken-links` is partial in the js column although the
time-cap-truncated js run never crawled `/pages/does-not-exist.html` — the
internal 404 state was recorded by the http run). The harness reproduces this
convention: `modes.{http,js}.traps` pools the session's bulk runs
(`merge_audit_data`), while **each run record keeps its own per-run measured
verdicts** (`runs[].traps`), so nothing is hidden — the js run's truncation stays
visible. A single-mode invocation pools only its own run(s). Exit code is driven
by the acceptance matrix + entry-run verdicts for the SELECTED traps + the
global safety assertion; surface statuses are recorded but do not drive exit
(per dispatch brief).

## Product defect handling — configuration-snapshot race (lead-approved)

Measured 2026-11-18 (full proof run, js audit 18): audits can fail at start
with `The JSON value could not be converted to
CrawlForge.Contracts.Entities.CrawlConfigurationDto. Path: $.id`.
Root cause (read-only source inspection, independent lead verification):
`AuditsController.StartAsync` (AuditsController.cs:140-160) starts the engine
(entity-DTO snapshot, numeric id) and THEN overwrites
`audits.configuration_snapshot_json` with the WIRE snapshot (string id);
`RunAuditAsync` (AuditExecutionEngine.cs:497) deserializes with `PayloadJson`,
which cannot read string ids — whichever snapshot the engine reads first
decides success/failure. Probabilistic at start; deterministic for re-audit
(engine.cs:436-438 reads the stored WIRE snapshot — probe-2 §6's `$.id` error,
same root-cause family). probe-2's audits 11/12/13 all won the race, so this is
a newly-exposed probabilistic start defect, not a harness regression.

Binding conditions (lead, 2026-11-18):
1. Retry ONLY on the exact signature: state failed <10 s, 0 URLs fetched,
   errorMessage containing the CrawlConfigurationDto/$.id conversion error
   (`is_snapshot_race`). Any other failure stands as-is — no generic
   retry-until-pass.
2. Max 2 retries (3 attempts) on the SAME configuration through the SAME
   normal entry point (UI Start button for the http bulk audit, REST for js,
   MCP `start-audit` flow for --mcp, schedule run-now for --scheduled). Every
   attempt (audit id, state, errorMessage, timing, safety) stays in the run
   record (`start_attempts`) AND `verdicts.json → product_defects` + summary.
3. All attempts fail → that mode's verdicts stay fail, exit non-zero, lead
   messaged immediately; never drop the mode or substitute another mode's data.
4. The pooled acceptance matrix may use the completed js run; per-run verdicts
   are also recorded; failed audit 18 remains visible as defect evidence
   (results/20261117T235434Z/audit-18-data.json).
5. No product code changes — record only. Suggested routing (lead):
   WP-09-06 (Api controllers) + WP-09-11 (Host execution), phase-09 dispatch.

Self-tests: `tests/test_start_retry.py` (signature exactness incl. negative
cases; retry-then-success records the failed attempt; all-fail returns the
failed result; non-race failures are never retried; fixture reset + safety
check before/after EACH attempt).

## UI-interaction determinism (lead send-back 2026-11-18, fixed)

The lead's independent re-run of the exact verification command failed exit 2
("UI project creation did not produce a project row",
results/20261118T002104Z): the submit click never fired — a Mantine modal
animation moved the button between rect read and input dispatch, and the old
click/fill returned `{clicked:false}` / `{ok:false}` which the callers ignored;
a fixed post-submit sleep masked the miss. Fixes (UI-interaction scope only;
verdict logic untouched):

- `node/cdp.mjs click`: re-find the element and read a FRESH rect immediately
  before each dispatch (up to 3 attempts), verify landing via
  `document.activeElement`, and fall back to real DOM `click()`/`dblclick`
  events if input events keep missing. If the element existed before dispatch
  and vanished after (its own action removed it, e.g. submit closes the
  modal), report `clicked:true, vanishedAfterDispatch:true` — the caller's
  evidence wait verifies the effect. Persistent failure is returned to the
  caller, never swallowed.
- `node/cdp.mjs fill`: read the value back after a controlled-input settle and
  report `ok` only when it equals the requested value.
- `harness/cdp.py click/fill`: `_retry_action` asserts the success key, retries
  with 0.4 s backoff until a 10 s timeout, then raises `CdpError` (transport
  errors propagate immediately). `must_wait_for` raises on timeout.
- `run_fixture_acceptance.py ui_entry_audit`: fixed post-submit/post-start
  sleeps replaced by EVIDENCE waits — poll (≤25 s) for the POST /api/projects
  network event OR the named project row via REST; poll (≤20 s) for a NEW
  audit id > baseline for the config OR POST /api/audits. On failure the
  modal/hub visible state (dump) + screenshot are captured into ui-entry.json
  BEFORE raising. Hub row visibility gets a bounded 6 s refresh window before
  the recorded REST fallback decision.
- Walker call sites audited: UI-02 records each click attempt's CdpError
  explicitly and still exercises the remaining affordances; UI-04 records
  not_reachable with evidence when the dialog does not open; UI-10's open
  click failure is recorded per attempt and classification stays dump-driven;
  UI-19 already recorded per-tool errors. No call site silently ignores a
  failed interaction any more.
- Regression tests: `tests/test_cdp_retry.py` (retry-then-success, persistent
  failure raises, fill value mismatch raises, transport error propagates
  without retry, must_wait_for semantics) — CDP child mocked, no live app.

### Root cause of the start-audit failure (live-probe verified)

`HubDashboard.startAudit` (HubDashboard.tsx:213-246) starts the audit and then
calls `navigation.openAudit(...)`, which navigates away from the hub — so the
Start audit button legitimately VANISHES mid-click. The old click helper
treated the post-dispatch disappearance as a miss and retried until timeout
(run 20261118T003239Z: "element not found: testid=start-audit-17", 25 attempts,
even though the audit had started). Two further product behaviours were
measured with a live CDP probe (temporary, removed):
- The hub project list does NOT auto-refresh on external changes and `nav-hub`
  does not reload it; only the New project dialog's submit (`loadProjects()`)
  and shell start load it. The UI-entry flow creates through the dialog, so the
  new row IS refreshed into the hub.
- The hub window is bounded (frontend DEFAULT_LIST_LIMIT=50, backend default
  limit 50, ascending). With the accumulated harness list (<50) every row
  renders; if the list ever exceeds the window the newest row can fall off.

Fixes built on the click/fill hardening above:
- `node/cdp.mjs click`: if the element existed before dispatch and is gone
  after, return `clicked:true, vanishedAfterDispatch:true` (its own action
  removed it); the caller's evidence wait verifies the effect.
- `ui_entry_audit`: wait for the SPECIFIC `start-audit-{pid}` button (≤12 s)
  before deciding reachability; if it never renders, REST fallback with
  `proven=false` (recorded product behaviour, never a silent pass). `ui_start_fn`
  dispatches the click inside try/except and treats the NEW audit row via REST
  (id > baseline) as the SOURCE OF TRUTH for success — a click-helper error
  caused by the button's own navigation no longer fails the flow; if no audit
  appears within 20 s the hub state (dump + screenshot) is captured before
  raising. Project creation likewise verifies via the REST project row, not a
  fixed sleep.

Acceptance: exact verification command run 3 consecutive times from cold
start, all exit 0 (results dirs reported to lead).

## UI walker (CR-018 / 06/04 §1, §3)

Every surface UI-01..UI-24 is attempted along its SPEC path from the main hub
over CDP; classification is computed from what actually renders (dump text,
buttons, `data-testid` inventory, screenshots), never assumed from CODEMAP.
Report-navigation surfaces match **navigation affordances only** (buttons /
testids) — telemetry labels like "WAF" or "URLs / sec" in the run view do not
satisfy a report-surface path. Placeholder/empty frames are never functional
(CR-018): UI-11's static "mount into this workspace from the report workpieces"
text classifies `reachable_but_placeholder`. UI-24 is `not_applicable_yet` —
backend failure is NOT induced. The walker never clicks fixture betting
controls (fixture pages are only fetched server-side by the single-page
analyzer tool).

## TLS variant

`--tls-cert/--tls-key` pass through to `server.mjs` (HTTPS fixture). Bare
`--tls` without cert material runs over HTTP and leaves `mixed-content`
`not_testable`, with a recorded note: a self-signed pair is deliberately NOT
generated because the product fetcher has no normal-entry-point control to
trust an untrusted certificate — a self-signed TLS fixture would fail for the
wrong reason (cert validation, not mixed-content detection).

## Lifecycle & data safety

- Cold-start re-runnable: starts the fixture (47311) when free, verifies
  identity before reusing an existing listener; launches the product via the
  debug launcher copy; shuts down what it started (CDP `Browser.close` →
  graceful before-quit drain — a closed browser socket counts as success
  because Electron often exits before replying; fallback `taskkill /T` ONLY on
  the Electron tree identified by the harness's own `--remote-debugging-port`
  argument, plus its backend child). `--reuse-app` / `--keep-app` exist for
  debugging only; the default lifecycle is cold-start + full cleanup.
- **CDP port selection is kernel-truth based.** A ghost listener on 9222 (dead
  PID 17600 holding the socket) is INTERMITTENT: a connect() probe can report
  the port free while Electron's `bind()` still fails (`tcp_socket_win 0x2740`,
  `Cannot start http server for devtools` — measured 2026-11-18 01:41, see
  `results/ENVIRONMENT-NOTES.md`). `pick_cdp_port()` therefore consults
  `netstat -ano` LISTENING entries and skips ANY held port (ghosts are never
  killed — there is no process to kill); `wait_app_ready()` fails fast on the
  devtools bind error; `launch_and_wait()` shuts down only its own failed
  instance and retries the other port. Session environment events (orphaned
  pre-probe-2 fixture PID 7568 identity-verified and stopped before the first
  run; ghost 9222; 47312 reserved for cf-wp-08-03) are recorded in
  `results/ENVIRONMENT-NOTES.md`.
- Real data directory (normal entry point). Only clearly named
  "Fixture harness ..." projects are created; existing projects/audits/configs
  (incl. "Airpower" and probe data) are never modified or deleted. The
  harness-owned schedule created by `--scheduled` is deleted after its run.
- Fixture recorder: `GET /__fixture/reset` before EACH audit;
  `GET /__fixture/requests` after EACH audit; `nonGet==[]` asserted (criterion
  #54); `tagHits` inspected for consent-banner evidence.

## Self-tests (contract_tests)

`python -m unittest discover -s tests -v` (from this folder):
- **HarnessVerdictShapeTests** (`test_verdict_shape.py`): verdict doc schema
  validated against expected-results.json structure — every trap id present,
  valid verdict enum, non-empty evidence, modes covered, counts consistent;
  also validates every on-disk `results/*/verdicts.json` and `surfaces.json`.
- **FixtureSafetyAssertionTests** (`test_fixture_safety.py`): `nonGet==[]`
  passes; synthetic POST `/api/bet` / `/api/deposit` / `/api/logout` cases MUST
  fail the assertion; malformed recorder fails; tagHits pre-consent inspection.
- **VerdictLogicTests** (`test_verdict_logic.py`): probe-2-shaped synthetic REST
  payloads reproduce 4/5/10/12 in http AND js; mutation tests prove verdicts
  flip when measured evidence changes (orphan discovered+reported → pass,
  duplicate-title finding → pass, thin-text on the URL → pass, robots
  blockReason → pass, working rendering → js-only-link pass, pooling recovers
  cross-run evidence). Nothing is hardcoded.

## Evidence retention

`results/<timestamp>/`: `verdicts.json` (machine-readable, per mode per trap
result+evidence+audit ids), `surfaces.json`, `summary.md`, `audit-ids.json`,
`fixture-request-log.json`, `ui-entry.json`, `mcp-list.json` / `mcp-flow.json` /
`mcp-reaudit.json`, `scheduled-flow.json`, `wire-probe-<mode>.json`,
`pooled-audit-data.json`, `app-shutdown.json`, `screenshots/*.png`,
`audit-<id>-data.json` (raw REST pulls), `*.log`.
`results/.gitignore` keeps bulky binaries (screenshots, raw pulls, logs) on disk
only; verdict JSONs and small logs are committed.

## Exit codes

0 = no fail among selected traps, safety holds · 1 = ≥1 selected trap FAIL or
safety assertion failure (a full matrix run exits 1 today — measured reality) ·
2 = harness/infrastructure error.