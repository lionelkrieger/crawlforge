# Fixture acceptance run — 20261118T095402Z (WP-08-04 harness v1.0.0)

- Fixture: http://127.0.0.1:47311
- Product git HEAD: 10a1e5a086acd8f3ca0c5c3d1f1c86edbe59fd8b
- Expected-results SHA-256: 9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211
- Exit code: 0 (no fail among selected traps; global safety assertions hold)
- Environment: CDP port notes=[]; fixture started_by_harness=True, reused=False. Pre-run session events: results/ENVIRONMENT-NOTES.md.

## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http | 4 | 0 | 0 | 0 |

### Mode http

| Trap | Result | Evidence |
|---|---|---|
| noindex | pass | audit 31: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |
| canonical-conflict | pass | audit 31: canonical.header-html-conflict emitted with headerTarget='/pages/canonical-target-header.html' htmlTarget='/pages/canonical-target-html.html'; canonical.mismatch count=1; headerConflicts.conflict=True. |
| soft-404-server | pass | audit 31: 200 page classified soft-404 (indexability.soft-404[warning] indexability soft 404; indexability.soft-404[warning] indexability soft 404). |
| missing-alt | pass | audit 31: accessibility.image-alt emitted (accessibility.image-alt[error] Image has no alt attribute.). |

## Runs (normal entry points)

- **http-ui** mode=http entry=UI Start audit button (CDP) + REST limit-bounding + REST poll project=20 config=24 audit=31 state=completed urls=174 findings=1428 duration=197.4s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 31 (UI Start audit button (CDP) + REST limit-bounding + REST poll): nonGet=[] ok=True
