# Fixture Acceptance Harness (WP-08-04)

Measures the **real CrawlForge product** against `app/tests/fixture-site` through
**normal entry points only** — UI over CDP, REST, MCP stdio adapter, schedule
run-now — and compares every trap against the frozen
`app/tests/fixture-site/expected-results.json`. It records defects; it never
fixes product code, never patches databases, never seeds state, and never edits
the fixture or the expectations. Completion authority is the measured verdict
(AGENT-INSTRUCTIONS §5.6/§12; `architecture/07-quality-and-verification/03-fixture-site.md` §2 incl. CR-023).

Design record, defect log and probe-2 reproduction notes: [DESIGN.md](DESIGN.md).
This README (WP-08-05) documents usage, modes and the evidence layout.

## Prerequisites

- Pinned .NET SDK **10.0.x** (`app/global.json`; the harness checks and pins it).
- Node.js on PATH (built-in `WebSocket`/`fetch` — no installs, no Playwright).
- Python 3.14 stdlib only (no pip installs).
- Ports: fixture **47311**, Electron CDP **9222** (falls back to **9223** when
  9222 is held — selection is netstat-truth based, with bind-failure retry).
- The harness builds and launches the product itself via `launch-debug.ps1`
  (a sanctioned **copy** of `app/launch-crawlforge.ps1`; the original is never
  modified). It runs with `CF_AGENT_ID` **unset** (canonical surface — the
  launcher exception in AGENT-INSTRUCTIONS). Stale CrawlForge/fixture processes
  holding the ports must be stopped first (the harness detects and reports).

## Quick start

```powershell
# Phase-08 gate subset (4 traps, UI-started http bulk per CR-023):
python E:\Crawlforge\app\tests\fixture-acceptance\run_fixture_acceptance.py --mode http --traps noindex,canonical-conflict,soft-404-server,missing-alt

# Full probe-2 reproduction (both capture modes, all traps, all surfaces, MCP + schedule):
python E:\Crawlforge\app\tests\fixture-acceptance\run_fixture_acceptance.py --mode http,js --traps all --surfaces all --mcp --scheduled

# Self-tests (no live app needed):
cd E:\Crawlforge\app\tests\fixture-acceptance
python -m unittest discover -s tests
```

## CLI reference

| Flag | Default | Meaning |
|---|---|---|
| `--mode` | `http` | `http` \| `js` \| `dual` \| `adaptive`; comma list allowed (`http,js`) |
| `--traps` | `all` | comma-separated trap ids, `all`, or `none` |
| `--surfaces` | `none` | comma-separated UI ids (`UI-01`…`UI-24`), `all`, or `none` — the CR-018 UI path walker |
| `--mcp` | off | also run the MCP entry-point flow (stdio adapter → tools/resources/prompts) |
| `--scheduled` | off | also run the schedule entry-point flow (create schedule → run-now → history) |
| `--tls` | off | TLS variant convenience switch; needs `--tls-cert`/`--tls-key` material |
| `--tls-cert` / `--tls-key` | none | PEM material for the fixture HTTPS variant (mixed-content trap) |
| `--rest-start` | off | start the http bulk audit via REST instead of the UI (debug; CR-023 UI start is the default) |
| `--reuse-app` | off | reuse an already-running harness app on 9222/9223 (debug; not shut down) |
| `--keep-app` | off | leave the app running after the run (debug) |
| `--results-root` | `results/` | evidence root directory |

## Entry points (CR-023)

- **http bulk = UI-started by default**: project created through the UI *New
  project* dialog over CDP; the dialog's default configuration is bounded via
  **REST PUT** `/api/configurations/{id}` (`maxUrls:220, maxDepth:6,
  maxDurationSeconds:180`) because the UI has no limit controls (UI-04 absent —
  recorded in `ui-entry.json`, never hidden); **Start audit is pressed in the
  UI**; the run view (UI-10) is inspected over CDP while running and after
  completion. Polling/data pulls are read-only REST.
- **js bulk = REST-started** (probe-2 style): `rendering.enabled=true`,
  `deviceProfile=desktop`, `captureConsole=true`; explicit limit overrides bound
  the run (crawler-trap detection is absent — recorded).
- **`--mcp`**: REST-bounded configuration, then `start-audit` →
  `get-audit-progress` → `audit-summary`/`high-severity-findings` resources →
  `audit-health-briefing` prompt through the stdio adapter.
- **`--scheduled`**: REST schedule create (interval recurrence) + `run-now`;
  run history read; the harness-owned schedule is deleted afterwards.
- **dual / adaptive**: no wire-contract support exists today
  (`RenderingSettingsWire` has no `captureMode`/`renderingMode`). The harness
  MEASURES this (POST → read-back → fields not persisted) and marks all traps
  `not_testable` for those modes; no audit is run under a false label.
- **TLS without material**: `mixed-content` is `not_testable` (the product has
  no certificate-trust control); nothing is faked.

## Verdicts and exit codes

Per trap: `pass` (expected finding/state present) · `partial` (some required
evidence present, a required finding/classification missing) · `fail` (expected
detection absent though the trap was exercised) · `not_testable` (the capture
mode cannot exercise the trap). Verdicts are **measured from live evidence**
(`harness/verdicts.py`), never hardcoded; rendering inertness and click-discovery
inertness are detected from evidence, so a future conforming build is evaluated
normally.

Acceptance-matrix pooling follows the probe-2 convention: `modes.{http,js}.traps`
pool the session's bulk runs, while each run record keeps its own per-run
verdicts (`runs[].traps`) — nothing is hidden (DESIGN.md §pooling).

| Exit code | Meaning |
|---|---|
| `0` | no `fail` among the SELECTED traps; global safety assertions hold |
| `1` | a selected trap measured `fail` (the full 31-trap matrix exits 1 by design on the current build — 10 fails are recorded product defects) |
| `2` | harness error (launch/CDP/fixture failure; evidence + traceback in `harness-error.json`) |

Global safety after **every** audit: `GET /__fixture/requests` → `nonGet == []`
(the crawler must stay GET-only); `tagHits` inspected for the consent-banner
trap. Surface statuses are recorded but do not drive the exit code.

**Known product defect affecting runs** (recorded, lead-approved handling): the
configuration-snapshot wire-vs-entity race (`AuditsController.cs:140-160` vs
`AuditExecutionEngine.cs:497/436`) can fail an audit at start with the
`CrawlConfigurationDto` / `$.id` conversion error. The harness retries **only**
on that exact signature (failed <10 s, 0 URLs), max 3 attempts through the same
entry point; every attempt stays in `start_attempts` + `product_defects`. All
attempts failing keeps the verdicts `fail` and exits non-zero. See DESIGN.md.

## Evidence layout — `results/<timestamp>/`

| File | Content |
|---|---|
| `summary.md` | human-readable run summary (fixture URL, product git HEAD, expected-results SHA-256, exit code, counts) |
| `verdicts.json` | machine-readable verdicts: `modes.{http,js}.traps` acceptance matrix, `runs[]` per-run records (entry point, audit ids, limit overrides, transitions, `start_attempts`, per-run trap verdicts), `product_defects`, safety results |
| `surfaces.json` | UI walker verdicts per surface (`functional` / `placeholder` / `not_reachable` / `not_applicable_yet`) with path + evidence (only when `--surfaces` ≠ none) |
| `ui-entry.json` | CR-023 proof: captured `POST /api/projects` + `POST /api/audits`, project/configuration ids, REST limit-bounding payload + read-back, start-click note, `proven` flag |
| `audit-ids.json` | every audit id created by the run, with entry point |
| `fixture-request-log.json` | recorder snapshot(s): `nonGet`, `tagHits` |
| `mcp-flow.json` / `scheduled-flow.json` | entry-point flow evidence when `--mcp` / `--scheduled` |
| `audit-<id>-data.json` | raw REST pulls per audit (urls/findings/links/summary) |
| `screenshots/` | CDP screenshots: UI-01 dialog, UI-10 running/completed, walker surfaces |
| `electron-stdout.log` / `electron-stderr.log` / `launch-console.log` / `fixture-server.log` | process logs |
| `app-shutdown.json` | identified-tree shutdown proof (`residual: []`, `ports_free: true`) |
| `harness-error.json` | on exit 2: error + traceback |

`results/.gitignore` keeps verdict JSON/summaries/screenshots committable while
excluding bulky raw logs; token-bearing artifacts are never committed.

## Data safety

Only clearly named harness projects are created (`Fixture harness …`); the
owner's real projects are never touched; nothing is deleted except
harness-owned schedules. The harness shuts down only its own identified process
tree and verifies the ports are free afterwards.

## Relationship to the gates

`app/tools/gates/run-phase-gates.ps1` (WP-08-05) executes each phase's
`orchestrator_gates`, including this harness for the phase's trap set. The
phase-08 gate subset is the 4-trap command above; later phases add js/dual/
adaptive modes, TLS, MCP and surface walks per their YAML.