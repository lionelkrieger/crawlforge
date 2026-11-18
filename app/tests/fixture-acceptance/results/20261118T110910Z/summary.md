# Fixture acceptance run — 20261118T110910Z (WP-08-04 harness v1.0.0)

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
| noindex | pass | audit 34: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |

## Runs (normal entry points)

- **http-ui** mode=http entry=UI Start audit button (CDP) + REST limit-bounding + REST poll project=23 config=27 audit=34 state=completed urls=110 findings=909 duration=199.2s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 34 (UI Start audit button (CDP) + REST limit-bounding + REST poll): nonGet=[] ok=True
