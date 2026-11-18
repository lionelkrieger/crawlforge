# Fixture acceptance run — 20261118T111304Z (WP-08-04 harness v1.0.0)

- Fixture: http://127.0.0.1:47311
- Product git HEAD: d84304edadd3b5e7cff4c0d8955f2ac51af0073e
- Expected-results SHA-256: 9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211
- Exit code: 0 (no fail among selected traps; global safety assertions hold)
- Environment: CDP port notes=[]; fixture started_by_harness=True, reused=False. Pre-run session events: results/ENVIRONMENT-NOTES.md.

## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http | 1 | 0 | 0 | 0 |

### Mode http

| Trap | Result | Evidence |
|---|---|---|
| noindex | pass | audit 35: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |

## Runs (normal entry points)

- **http-rest** mode=http entry=REST POST /api/audits project=24 config=28 audit=35 state=completed urls=186 findings=1522 duration=159.0s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 35 (REST POST /api/audits): nonGet=[] ok=True

## UI surfaces (CR-018 walker)

Status counts: {"reachable_and_functional": 2}

| Surface | Status | Evidence |
|---|---|---|
| UI-01 Main hub › Projects | reachable_and_functional | Hub renders real local aggregates: 23 project rows, 34 audit rows, New project button present=True. Project creation through the New project dialog is exercised by the UI-entry audit. |
| UI-10 Audit run view | reachable_and_functional | Audit run view renders real run data for audit 35: state badge, progress/counters and host-throttle telemetry present; lifecycle controls mounted for active runs. |
