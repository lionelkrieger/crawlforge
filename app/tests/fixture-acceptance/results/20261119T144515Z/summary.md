# Fixture acceptance run — 20261119T144515Z (WP-08-04 harness v1.0.0)

- Fixture: http://127.0.0.1:47311
- Product git HEAD: bfd70d5aaedb541b929e66e2215be123fde48602
- Expected-results SHA-256: 9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211
- Exit code: 0 (no fail among selected traps; global safety assertions hold)
- Environment: CDP port notes=[]; fixture started_by_harness=True, reused=False. Pre-run session events: results/ENVIRONMENT-NOTES.md.

## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)

| Mode | pass | partial | fail | not_testable |
|---|---:|---:|---:|---:|
| http | 13 | 4 | 0 | 0 |

### Mode http

| Trap | Result | Evidence |
|---|---|---|
| redirect-chain | pass | audit 37: redirectChain [{"url": "http://127.0.0.1:47311/redirect/chain-1", "statusCode": 301, "location": "http://127.0.0.1:47311/redirect/chain-2"}, {"url": "http://127.0.0.1:47311/redirect/chain-2", "statusCode": 302, "location": "http://127.0.0.1:47311/redirect/chain-3"}, {"url": "http://127.0.0.1:47311/redirect/chain-; finding redirects.chain[warning] redirects chain |
| redirect-loop | partial | audit 37: /redirect/loop-a stored as statusCode=0 (statusClass=5xx); the crawl did not hang (audit completed), but no loop finding or loop classification was emitted (findings on URL: links.broken-internal[error] links broken internal). |
| noindex | pass | audit 37: crawled 200 (words=154); indexability.noindex emitted (directives=['noindex', 'follow']); indexability.noindex[notice] indexability noindex |
| robots-blocked | pass | audit 37: not fetched (statusCode=0), blockReason=None, finding indexability.robots-blocked[warning] indexability robots blocked. |
| canonical-conflict | pass | audit 37: canonical.header-html-conflict emitted with headerTarget='/pages/canonical-target-header.html' htmlTarget='/pages/canonical-target-html.html'; canonical.mismatch count=1; headerConflicts.conflict=True. |
| broken-links | partial | audit 37: internal 404 recorded for /pages/does-not-exist.html (statusCode=404), but no broken-internal/broken-external finding (rule probe: links.broken-internal[error] links broken internal; links.broken-internal[error] links broken internal); broken.example.invalid checked=False (rows=0). |
| orphan | pass | audit 37: orphan discovered and reported (links.orphan[warning] links orphan); inboundLinks=0. |
| soft-404-server | pass | audit 37: 200 page classified soft-404 (indexability.soft-404[warning] indexability soft 404). |
| cookieless-session | partial | audit 37: URL normalized to /pages/session.aspx but no normalization report. |
| crawler-trap-filter | pass | audit 37: faceted-filter combination explosion detected/reported (trap findings=1, throttled rows=0, crawled rows=90); audit finished. |
| crawler-trap-calendar | pass | audit 37: calendar combination explosion detected/reported (trap findings=1, throttled rows=0, crawled rows=11); audit finished. |
| cookie-gate | partial | audit 37: redirect recorded (1 hops) but gate not passed (statusCode=302). |
| structured-data-errors | pass | audit 37: structured-data validation errors reported (structured-data.malformed-block[error] structured data malformed block; structured-data.invalid[error] structured data invalid). |
| missing-alt | pass | audit 37: accessibility.image-alt emitted (accessibility.image-alt[error] Image has no alt attribute.). |
| hreflang-no-return | pass | audit 37: missing return tag reported (i18n.hreflang-missing-reciprocity[warning] i18n hreflang missing reciprocity). |
| duplicate-titles | pass | audit 37: both pages share the identical title and a duplicate-title finding is emitted (content.duplicate-title[warning] content duplicate title). |
| thin-page | pass | audit 37: content.thin-text emitted on /pages/thin.html (wordCount=6). |

## Runs (normal entry points)

- **http-ui** mode=http entry=UI Start audit button (CDP) + REST limit-bounding + REST poll project=26 config=30 audit=37 state=completed urls=134 findings=1241 duration=62.4s limits={"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": null}
  - rendering inertness: rendering.enabled=False; 'no rendered tree is available' findings=0; /pages/js-only-target.html present=False; /pages/js-content.html totalWordCount=None
  - safety: nonGet == [] after audit

## Global assertions

- no-non-get-requests: ok=True
  - audit 37 (UI Start audit button (CDP) + REST limit-bounding + REST poll): nonGet=[] ok=True

## UI surfaces (CR-018 walker)

Status counts: {"reachable_and_functional": 9}

| Surface | Status | Evidence |
|---|---|---|
| UI-01 Main hub › Projects | reachable_and_functional | Hub renders real local aggregates: 26 project rows, 37 audit rows, New project button present=True. Project creation through the New project dialog is exercised by the UI-entry audit. |
| UI-02 Main hub › Project overview | reachable_and_functional | Project overview rendered with project sections. |
| UI-03 Project › Configurations | reachable_and_functional | Spec path Project overview › Configurations (overview itself unreachable, UI-02): markers present ['Configurations']. No Configurations entry in rail, project row or command palette. |
| UI-04 Configuration editor | reachable_and_functional | Configuration editor tabs present: ['Scope', 'Crawl as and capture', 'Limits', 'Rules', 'Directives', 'Resources and external scope', 'Authentication and cookies', 'Click discovery and site types', 'Consent states', 'Analysis categories', 'Thresholds', 'Extraction and search', 'Advanced']. |
| UI-10 Audit run view | reachable_and_functional | Audit run view renders real run data for audit 37: state badge, progress/counters and host-throttle telemetry present; lifecycle controls mounted for active runs. |
| UI-11 Report › Dashboard | reachable_and_functional | Dashboard grids/charts render real aggregate data. |
| UI-12 Report › Issue explorer | reachable_and_functional | Report navigation entries present: ['Issue explorer', 'Issues']. |
| UI-13 Report › URL explorer and URL detail | reachable_and_functional | Report navigation entries present: ['URL explorer', 'URLs']. |
| UI-20 Export dialog | reachable_and_functional | Export control present: ['Export']. |
