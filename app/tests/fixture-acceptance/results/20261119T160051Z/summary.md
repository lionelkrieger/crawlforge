# Fixture acceptance run — 20261119T160051Z (WP-08-04 harness v1.0.0)

- Fixture: http://127.0.0.1:47311
- Product git HEAD: bfd70d5aaedb541b929e66e2215be123fde48602
- Expected-results SHA-256: 9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211
- Exit code: 0 (no fail among selected traps; global safety assertions hold)
- Environment: CDP port notes=[]; fixture started_by_harness=True, reused=False. Pre-run session events: results/ENVIRONMENT-NOTES.md.

## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http | 17 | 0 | 0 | 0 |

### Mode http

| Trap | Result | Evidence |
|---|---|---|
| redirect-chain | pass | audit 38: redirectChain [{"url": "http://127.0.0.1:47311/redirect/chain-1", "statusCode": 301, "location": "http://127.0.0.1:47311/redirect/chain-2"}, {"url": "http://127.0.0.1:47311/redirect/chain-2", "statusCode": 302, "location": "http://127.0.0.1:47311/redirect/chain-3"}, {"url": "http://127.0.0.1:47311/redirect/chain-; finding redirects.chain[warning] redirects chain |
| redirect-loop | pass | audit 38: loop reported (redirects.loop[error] Redirect loop detected: 'http://127.0.0.1:47311/redirect/loop-b' -> 'http://127.0.0.1:47311/redirect/loop-a'.; redirects.loop[error] Redirect loop detected: 'http://127.0.0.1:47311/redirect/loop-b' -> 'http://127.0.0.1:47311/redirect/loop-a'.); audit completed without hanging. |
| noindex | pass | audit 38: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |
| robots-blocked | pass | audit 38: not fetched (statusCode=0), blockReason=None, finding indexability.robots-blocked[warning] indexability robots blocked. |
| canonical-conflict | pass | audit 38: canonical.header-html-conflict emitted with headerTarget='/pages/canonical-target-header.html' htmlTarget='/pages/canonical-target-html.html'; canonical.mismatch count=1; headerConflicts.conflict=True. |
| broken-links | pass | audit 38: internal 404 recorded, external checked, findings links.broken-internal[error] links broken internal; links.broken-external[warning] links broken external. |
| orphan | pass | audit 38: orphan discovered and reported (links.orphan[warning] links orphan); inboundLinks=0. |
| soft-404-server | pass | audit 38: 200 page classified soft-404 (indexability.soft-404[warning] indexability soft 404). |
| cookieless-session | pass | audit 38: session segment stripped and reported (canonicalization.session-segment-removed[notice] Cookieless session segment removed during URL normalization; the canonical URL is stored without the session i; canonicalization.session-segment-removed[notice] Cookieless session segment removed during URL normalization; the canonical URL is stored without the session i). |
| crawler-trap-filter | pass | audit 38: faceted-filter combination explosion detected/reported (trap findings=1, throttled rows=0, crawled rows=90); audit finished. |
| crawler-trap-calendar | pass | audit 38: calendar combination explosion detected/reported (trap findings=1, throttled rows=0, crawled rows=11); audit finished. |
| cookie-gate | pass | audit 38: cookie jar honored; /cookie-gate/ reached 200 after the Set-Cookie redirect. |
| structured-data-errors | pass | audit 38: structured-data validation errors reported (structured-data.malformed-block[error] structured data malformed block; structured-data.invalid[error] structured data invalid). |
| missing-alt | pass | audit 38: accessibility.image-alt emitted (accessibility.image-alt[error] Image has no alt attribute.). |
| hreflang-no-return | pass | audit 38: missing return tag reported (i18n.hreflang-missing-reciprocity[warning] i18n hreflang missing reciprocity). |
| duplicate-titles | pass | audit 38: both pages share the identical title and a duplicate-title finding is emitted (content.duplicate-title[warning] content duplicate title). |
| thin-page | pass | audit 38: content.thin-text emitted on /pages/thin.html (wordCount=6). |

## Runs (normal entry points)

- **http-ui** mode=http entry=UI Start audit button (CDP) + REST limit-bounding + REST poll project=27 config=31 audit=38 state=completed urls=135 findings=1250 duration=70.5s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 38 (UI Start audit button (CDP) + REST limit-bounding + REST poll): nonGet=[] ok=True

## UI surfaces (CR-018 walker)

Status counts: {"reachable_and_functional": 9}

| Surface | Status | Evidence |
|---|---|---|
| UI-01 Main hub › Projects | reachable_and_functional | Hub renders real local aggregates: 27 project rows, 38 audit rows, New project button present=True. Project creation through the New project dialog is exercised by the UI-entry audit. |
| UI-02 Main hub › Project overview | reachable_and_functional | Project overview rendered with project sections. |
| UI-03 Project › Configurations | reachable_and_functional | Spec path Project overview › Configurations (overview itself unreachable, UI-02): markers present ['Configurations']. No Configurations entry in rail, project row or command palette. |
| UI-04 Configuration editor | reachable_and_functional | Configuration editor tabs present: ['Scope', 'Crawl as and capture', 'Limits', 'Rules', 'Directives', 'Resources and external scope', 'Authentication and cookies', 'Click discovery and site types', 'Consent states', 'Analysis categories', 'Thresholds', 'Extraction and search', 'Advanced']. |
| UI-10 Audit run view | reachable_and_functional | Audit run view renders real run data for audit 38: state badge, progress/counters and host-throttle telemetry present; lifecycle controls mounted for active runs. |
| UI-11 Report › Dashboard | reachable_and_functional | Dashboard grids/charts render real aggregate data. |
| UI-12 Report › Issue explorer | reachable_and_functional | Report navigation entries present: ['Issue explorer', 'Issues']. |
| UI-13 Report › URL explorer and URL detail | reachable_and_functional | Report navigation entries present: ['URL explorer', 'URLs']. |
| UI-20 Export dialog | reachable_and_functional | Export control present: ['Export']. |
