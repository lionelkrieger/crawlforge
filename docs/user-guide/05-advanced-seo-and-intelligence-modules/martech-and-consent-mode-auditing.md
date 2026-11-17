# MarTech & Consent Mode Auditing

CrawlForge's MarTech auditor answers the questions tag managers and analytics engineers usually answer with a browser, a proxy, and a spreadsheet:

- **What tags and containers are actually deployed** across the site — GTM, GA4, Google Ads, Meta Pixel, LinkedIn Insight, Microsoft Clarity, and your own custom beacons?
- **Where are they missing, duplicated, or misconfigured?**
- **Does Google Consent Mode v2 behave correctly** — are default consent states declared before tags fire, and do the consent signals on the wire match the declared timeline?

All evidence comes from what CrawlForge already observes during a crawl: parsed HTML and, when enabled, the local intercepting proxy's network capture. **No additional fetches are made, no vendor service is consulted, and no analytics account is connected.** CrawlForge does not import data from Google Analytics, Meta, or any other third-party platform — it audits the tags as they exist on your pages.

## Tag and container inventory

### The bundled detection catalogue

Detection uses a **bundled, versioned catalogue** that ships with the product and updates only with product updates. It covers:

| Vendor | Identifier pattern |
|---|---|
| Google Tag Manager | `GTM-*` container IDs |
| Google Analytics 4 | `G-*` measurement IDs |
| Google Ads | `AW-*` conversion IDs |
| Meta Pixel | Pixel detections |
| LinkedIn Insight | Insight tag detections |
| Microsoft Clarity | Clarity detections |

On top of the bundled catalogue you can define **custom user-defined beacon patterns** (see below). There is no remote tag registry — the catalogue is a packaged asset, and your patterns are local configuration.

### Detection sources

Inventory is built from two sources, both part of the crawl's existing capture paths:

1. **HTML parsing** — the element-inventory stage of the audit pipeline examines script sources and inline markers in the parsed document.
2. **Local proxy network capture** — when the built-in local intercepting proxy is enabled, rendered page traffic is routed through it (loopback-only), and observed network hits feed the inventory.

Each detection records its **detection source**:

| Source value | Meaning |
|---|---|
| `dom` | Found in the parsed document only |
| `network` | Observed in proxy-captured network hits only |
| `both` | Found in both |

> **Tip:** A `network`-only detection often means a tag is injected dynamically (e.g. by another container) rather than present in the served HTML. A `dom`-only detection with no hits is exactly what the optional `analytics.no-hits-observed` diagnostic looks for.

### What each detection records

Every detection row carries:

| Field | Notes |
|---|---|
| Vendor | Catalogue vendor or your custom pattern's label |
| Container/pixel ID | e.g. the `GTM-*`, `G-*`, or `AW-*` identifier |
| Detection source | `dom` \| `network` \| `both` |
| Placement | head / body |
| Load flags | async / defer |
| Classification | First-party vs third-party |
| Evidence | JSON evidence supporting the detection |

Detections are stored per URL in the `analytics_detections` table, indexed by vendor and by container ID for fast filtering.

### Custom beacon patterns

For vendors outside the bundled catalogue (or in-house beacons), define **custom beacon patterns** in the audit configuration. Patterns are expressions over:

- **script sources**,
- **inline markers**, and
- **network hit URLs**.

Pattern evaluation follows the same discipline as custom content search, including **time bounds** — a pathological pattern cannot stall the crawl. Custom patterns are local configuration; they are never uploaded anywhere.

## `dataLayer` inspection (rendering mode)

In **rendering mode**, CrawlForge snapshots the post-render `window.dataLayer` array for each page. This is the array as it actually exists after the page's scripts have run — not a guess from static markup.

The capture is **mandatorily bounded**:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Captured events (first N) | 100 | Configurable; capture is always bounded | The snapshot stores the **total event count** plus the **first N events** |

Capture is engine-level, with the same isolation and time-bound discipline as custom JavaScript snippets — a misbehaving page cannot hang the audit, and a capture problem on one page never fails the audit or other pages.

The snapshot persists as the `datalayer_snapshot` element group (bounded event count + first N events) and is browsable in the **bounded `dataLayer` event inspector** in the Analytics & Tagging Explorer.

> **Note:** Because only the first N events are stored, very event-heavy pages may have later events outside the snapshot. The total event count is always recorded, so you can see when truncation occurred. Raise the cap in configuration when you need deeper inspection.

## Consent Mode v2 auditing

### The four consent types

CrawlForge extracts Consent Mode v2 **default and update commands** for the four consent types:

- `ad_storage`
- `analytics_storage`
- `ad_user_data`
- `ad_personalization`

### The consent timeline

Extracted commands are arranged into an **ordered timeline**:

1. **Default declarations first.**
2. **Updates follow, with sequence markers** preserving their order.

This timeline is the reference against which tag firing is judged. It persists as the `consent_state` element group, together with the observed `gcs`/`gcd` network observations.

### Network correlation: `gcs` and `gcd`

When the local proxy captures network requests, CrawlForge extracts the **consent hit parameters `gcs` and `gcd`** carried on those requests and **correlates them against the consent timeline**.

The per-URL tag/consent timeline in the Explorer puts everything on one axis:

- default consent declarations,
- consent updates,
- the **first tag fire**, and
- **`gcs`/`gcd` hit markers** on the captured requests.

This lets you answer, visually and per URL: *did the consent state declared in the page match the consent signals actually sent on the wire, and in what order?*

### Tags firing before or without consent

The core compliance check: **tags firing before or without a default consent declaration are detected.** When Consent Mode auditing is enabled, this emits `analytics.consent-mode-missing` (**Warning**) — see the rules reference below.

> **Warning:** A tag that fires before any default consent declaration is the classic Consent Mode v2 misconfiguration. On the timeline, look for the first tag fire marker positioned **before** the first default declaration — or for hits with no default declaration anywhere on the timeline.

### Without rendering mode

Consent and `dataLayer` inspection require rendering mode. Without it:

- **Inventory still runs** from static HTML and any proxy capture.
- The **consent and `dataLayer` passes are marked unavailable with a diagnostic** — you will see an explicit "unavailable" state, not silently empty results.

If consent compliance matters for the engagement, configure the audit with rendering mode enabled (and the local intercepting proxy for network correlation).

## Analytics rules reference

All rules live in the additive **Analytics & Tagging** category. Evidence carries container IDs, bounded event excerpts, and consent-timeline markers.

| Rule key | Severity | Condition |
|---|---|---|
| `analytics.missing-container` | Warning | Indexable HTML pages missing the site's **primary GTM/GA4 ID**. Primary = the most frequent ID across indexable pages; a user override is configurable. |
| `analytics.duplicate-container` | Warning | The same container ID injected multiple times on a page, **or** duplicate `page_view` requests on initial load (from network capture). |
| `analytics.datalayer-malformed` | Warning | Syntax errors or missing required keys in `dataLayer` events, validated against an optional user-supplied JSON schema (stored in audit configuration; bounded validation). |
| `analytics.consent-mode-missing` | Warning | Tags firing prior to or without default consent state declarations, **where Consent Mode auditing is enabled**. |
| `analytics.no-hits-observed` | Notice | Optional diagnostic: container present in DOM but zero corresponding network hits observed while capture is enabled. |

How to read each rule operationally:

- **`analytics.missing-container`** — coverage holes. The primary ID is inferred from frequency, so on sites with genuinely different containers per section, set the **primary-container override** to avoid false expectations.
- **`analytics.duplicate-container`** — double counting risk. Check both injection duplicates (DOM) and duplicate `page_view` hits on initial load (network).
- **`analytics.datalayer-malformed`** — data-quality risk for downstream reporting. Supply your event schema to make the check match your actual tracking plan.
- **`analytics.consent-mode-missing`** — compliance risk. Only evaluated when the consent-auditing toggle is enabled.
- **`analytics.no-hits-observed`** — a container that exists in markup but never fires. Often a consent gate, a blocking rule, or a misconfigured trigger; the diagnostic is optional and requires capture to be enabled.

Suppression follows the standard finding model: suppress an individual finding or a whole rule at project scope; suppressed findings are excluded from default counts and scoring, remain inspectable, and persist across re-audits.

## Configuring the audit

The following configuration fields live in the audit configuration (`crawl_configurations`) and are editable through the Explorer's configuration forms:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Consent-auditing toggle | Local configuration | On/off | Gates `analytics.consent-mode-missing` evaluation |
| Primary-container override | Auto: most frequent GTM/GA4 ID across indexable pages | User-configurable | Overrides the inferred primary for `analytics.missing-container` |
| `dataLayer` JSON schema | Optional (none supplied) | Bounded validation | Stored in audit configuration; used by `analytics.datalayer-malformed` |
| Custom beacon patterns | None | Time-bounded pattern evaluation | Expressions over script sources, inline markers, network hit URLs |
| Captured `dataLayer` events (N) | 100 | Configurable; always bounded | Total event count is recorded regardless of N |

Rendering mode and the local intercepting proxy are configured through the standard audit inputs:

- **Rendering mode** — optional; selectable per audit or per URL pattern. Required for `dataLayer` and consent passes.
- **Local intercepting proxy** — optional; a forward proxy bound to the **loopback interface only**. When enabled, rendered page traffic routes through it and provides the request/response capture used for network-side detection and `gcs`/`gcd` correlation. It is a crawl-traffic tool, not a tunnel: it opens no non-loopback listener.

> **Note:** Any proxy credentials you configure are user-owned secrets, encrypted at rest. The proxy never performs enrollment or liveness reporting, and no remote proxy auto-configuration exists.

## The Analytics & Tagging Explorer

The **Analytics & Tagging Explorer** is a dedicated report surface with four working areas:

1. **Container inventory grid** — vendor, ID, coverage, placement; virtualized (at most 50 mounted DOM rows, server-side paging with default page size 200), so it stays responsive across large sites.
2. **Per-URL tag/consent timeline** — default consent declarations, updates, and first tag fire on one axis, with `gcs`/`gcd` hit markers.
3. **Coverage treemap by directory** — spot whole sections of the site where a container is absent; plus the **bounded `dataLayer` event inspector**.
4. **Configuration forms** — consent auditing, primary-container override, JSON schema, and custom beacon patterns.

Dashboard cards are backed by precomputed aggregates:

| Metric key | Dimension | What it summarizes |
|---|---|---|
| `analytics-coverage` | vendor | Container coverage across indexable pages |
| `consent-compliance` | — | Consent-auditing results |

During a crawl, detections ride the existing `findings-updated` and `url-batch` hub events — no new event channel is introduced.

## Step-by-step: auditing tags and consent

1. **Configure the audit for full visibility.** Enable rendering mode; enable the local intercepting proxy for network capture; enable the consent-auditing toggle. Optionally set the primary-container override, supply your `dataLayer` JSON schema, and add custom beacon patterns for non-bundled vendors.
2. **Run the crawl.** Inventory builds from HTML parsing and proxy capture as pages are fetched; consent and `dataLayer` passes run in rendering mode.
3. **Open the Analytics & Tagging Explorer inventory grid.** Filter by vendor or container ID. For each container, check coverage, placement (head/body), load flags (async/defer), and first-party vs third-party classification.
4. **Read detection sources.** `both` is the healthy baseline for a statically deployed tag. Investigate `dom`-only rows (tag present, never observed firing) and `network`-only rows (dynamic injection).
5. **Check coverage.** Use the coverage view (per-container present/missing counts across indexable pages) and the directory treemap to find sections missing the primary container. Triage `analytics.missing-container` Warnings; if the site legitimately uses different containers per section, set the primary-container override and re-audit.
6. **Inspect the consent timeline per URL.** Confirm default declarations for `ad_storage`, `analytics_storage`, `ad_user_data`, and `ad_personalization` appear **before** the first tag fire; then read updates in sequence order.
7. **Correlate the wire signals.** Check the `gcs`/`gcd` hit markers against the timeline — the signals sent on captured requests should be consistent with the declared consent states at the time of each hit.
8. **Triage `analytics.consent-mode-missing`.** Each Warning names a page where tags fired before or without a default declaration. Fix the consent default ordering in the site's tag setup, then re-run.
9. **Inspect `dataLayer` events.** Open the bounded event inspector; review the captured events against your tracking plan. Triage `analytics.datalayer-malformed` Warnings (syntax errors or missing required keys per your supplied schema).
10. **Triage duplicates.** For `analytics.duplicate-container`, check whether the same ID is injected twice in the DOM or whether duplicate `page_view` requests fire on initial load — both inflate metrics.
11. **Review optional diagnostics.** `analytics.no-hits-observed` Notices flag containers present in DOM with zero observed hits while capture was enabled — decide whether each is an intentional consent gate or a broken trigger.
12. **Handle non-rendered runs honestly.** If the audit ran without rendering mode, consent/`dataLayer` results are marked unavailable with a diagnostic. Re-run with rendering enabled before drawing consent conclusions.
13. **Export and report.** Include the analytics dataset in a local export (spreadsheet workbook, CSV per dataset, HTML bundle, or PDF summary). Suppress findings that reflect accepted, deliberate configuration at project scope.

> **Tip:** Steps 6–8 are the consent-compliance core. If you only have time for one pass, run it on the templates that carry the most traffic — the coverage treemap tells you which directories those are.

## REST and MCP reference

All endpoints are loopback-only, JSON, with the standard response envelope and offset/limit pagination.

| Operation | Method & path | Input | Output |
|---|---|---|---|
| Analytics detections | `GET /api/audits/{id}/analytics` | `view=inventory\|consent\|findings`; vendor/container filters; pagination | Detection rows, consent timelines, or findings |
| Container coverage | `GET /api/audits/{id}/analytics/coverage` | Vendor filter | Per-container coverage across indexable pages (present/missing counts) |

The three views of the analytics endpoint map to the workflow: `inventory` for the tag grid, `consent` for per-URL timelines, `findings` for the rule results. Analytics findings also appear in the generic findings list (`GET /api/audits/{id}/findings`) under the **Analytics & Tagging** category.

Exports: `POST /api/exports` accepts the **analytics** dataset selection; the resulting file is downloaded locally via `GET /api/exports/{id}/file` (or delivered to a destination you explicitly configured yourself).

### MCP access for local agents

With the optional local MCP interface enabled (off by default), the read-only **`analytics-coverage`** resource exposes the per-container coverage projection, addressed by audit reference with an optional vendor filter.

The MCP boundary holds: **analytics configuration management is not exposed via MCP** — toggles, schemas, overrides, and beacon patterns are managed in the UI/REST surface only.

## Where the data lives, and retention

| Object | Content | Lifecycle |
|---|---|---|
| `analytics_detections` | Audit reference; URL reference; vendor; container/pixel ID; detection source; placement; load flags; evidence JSON | Pruned with audit retention |
| `url_elements` → `consent_state` | Defaults/updates timeline; `gcs`/`gcd` observations (JSON payload) | Pruned with audit retention |
| `url_elements` → `datalayer_snapshot` | Bounded event count + first N events (JSON payload) | Pruned with audit retention |
| `crawl_configurations` | Consent-auditing toggle; primary-container override; `dataLayer` JSON schema; custom beacon patterns | Configuration — not pruned by audit retention |
| `metric_summaries` | `analytics-coverage` (dimension: vendor); `consent-compliance` | Dashboard card aggregates |

Retention is user-configurable per project (keep the last N audits, or an age limit); pruning is transactional. All inventory, consent, and `dataLayer` data stays on your machine — zero telemetry, no vendor calls.

## Related guides

- [AI Search (GEO) Readiness](ai-geo-readiness.md) — the sibling intelligence module for `llms.txt` and AI bot governance.
- [Offline Server Log Analyzer](offline-server-log-analyzer.md) — correlate what bots actually requested with what your crawl found.
- [WAF & Soft-Block Detection](waf-and-soft-block-detection.md) — challenge pages can masquerade as healthy 200s; keep them out of your tag inventory.
- [Content Scoping & Boilerplate Stripping](content-scoping-and-boilerplate-stripping.md) — how CrawlForge separates content from chrome during parsing.
- [Dashboards & Virtualized Explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md) — how report surfaces, filters, and virtualized grids work.

[← Documentation portal](../README.md)
