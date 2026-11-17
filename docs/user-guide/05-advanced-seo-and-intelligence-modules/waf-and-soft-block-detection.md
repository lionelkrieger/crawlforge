# WAF and Soft-Block Detection

Modern Web Application Firewalls (WAFs) — Cloudflare, DataDome, Akamai, Imperva,
PerimeterX, and others — frequently answer a crawler with a CAPTCHA page, a
JavaScript interstitial, or a bot-block page served with an **HTTP 200 OK**
status. A naive crawler records those pages as healthy content: your report
fills up with false *Thin Content* findings, duplicate-title warnings, and
polluted text metrics, while the real pages were never seen.

CrawlForge inspects every response **at receipt**, fingerprints challenge pages
before they enter the content inventory, classifies the URL as `blocked` with
the sub-classification `waf-challenge`, and surfaces a dedicated WAF-Block
diagnostic. Detection is **diagnostic only**: CrawlForge never attempts to
bypass, solve, or evade a challenge. It tells you what happened, keeps the
evidence, and slows down automatically so you never hammer a protected origin.

> **Note:** Everything in this guide runs locally. The signature catalogue ships
> with the product, is never fetched from a remote service, and there is no
> reputation lookup, no update channel, and no telemetry of any kind.

---

## What counts as a soft block

A *soft block* is a challenge or denial page delivered with a success status —
the server says `200 OK`, but the body is a challenge, not your content.
CrawlForge recognizes five challenge classes:

| Challenge class | What it looks like |
|---|---|
| `captcha` | A CAPTCHA verification page |
| `js-interstitial` | A JavaScript challenge/interstitial ("checking your browser…") |
| `managed-challenge` | A managed challenge platform page (e.g. Turnstile-style widgets) |
| `bot-block` | An explicit access-denied / bot-block page |
| `rate-limit-soft-block` | A retry-later / rate-limit page served with 200 OK |

Only responses that would otherwise be recorded as `fetched` — **2xx with an
HTML content type** — are eligible for the `waf-challenge` transition. Hard
errors, redirects, robots-blocked URLs, and non-HTML resources keep their
normal classification. A challenge signature observed on a non-2xx response is
kept as an evidence annotation only; the status class is unchanged.

> **Note:** A WAF block is **not** a soft-404. The two classifications are
> distinct and mutually exclusive per URL, so blocked pages never distort your
> soft-404 heuristics.

---

## How detection works

The **WAF & Challenge Fingerprinting Pass** runs immediately upon response
receipt, *before* the page is persisted as `fetched` and before any DOM
inventory extraction. It has two stages:

1. **Stage A — cheap facets.** Response-header and cookie matching against the
   signature catalogue. No parsing required; this resolves most provider
   challenges instantly.
2. **Stage B — bounded pre-parse.** For HTML responses not resolved by Stage A,
   a lightweight parse extracts the `<title>` and scans DOM-marker and script
   facets. The parse is **time-bounded and node-bounded**, so a pathological
   document can never stall a fetch worker. This is not the full inventory
   extraction.

If nothing matches, the normal pipeline continues untouched. If a signature
matches, the URL transitions to `blocked` (details below).

### Where else the pass runs

- **Rendering mode:** the pass runs at two checkpoints — raw response receipt
  and post-render DOM capture. By default, a match at *either* checkpoint
  blocks the URL.
- **Re-audit:** the pass re-runs over stored bodies from the content store,
  fully offline. The catalogue version and your user-catalogue revision are
  snapshotted per audit, so re-audits are reproducible.
- **Single-page analysis and the URL checker tool:** both run the same pass and
  report the verdict in their results.
- **Pre-crawl validation:** the seed probe runs the pass, so a challenge on a
  seed URL surfaces as a blocker *before* the crawl starts.
- **Sample-crawl mode:** the pass is active; a high block rate in the sample
  warns you before you commit to a full run.

The pass never re-fetches, never enqueues links found inside challenge
documents, and adds no outbound traffic.

---

## The signature catalogue

The catalogue is a **bundled, versioned, locally maintainable** data asset —
the same pattern as technology detection. It ships with the product, updates
only with product updates, and is extended by your own local entries. Adding a
signature is a data change, never an engine change.

### Recognized providers

| Provider label | Typical header / cookie markers | Typical page markers |
|---|---|---|
| `cloudflare` | `cf-*` diagnostic header family (ray identifiers, mitigation headers); managed-challenge cookie family (`__cf_bm`, `cf_clearance` class) | "Just a moment…" interstitial title family; challenge-platform script paths; Turnstile widget markers |
| `datadome` | DataDome session cookie class; `x-dd-*` response header family | CAPTCHA page titles/markers naming the provider; provider client-script markers |
| `akamai` | Bot-manager cookie family (`ak_bmsc`, `_abck` class); server token values | "Access denied" pages with reference-number structure; bot-manager script markers |
| `imperva` | Incident-session cookie family (`visid_incap_*`, `incap_ses_*` class) | Block pages with incident-ID structure; provider resource/script markers |
| `perimeterx` | `_px*` cookie family; `x-px-*` header family | "Access to this page has been denied" phrase class; provider client-script markers |
| `generic` | — | Retry-later / access-denied / verify-you-are-human phrase classes combined with challenge-form structure (corroborating facets required) |

User-defined provider labels are also supported for in-house or regional
protection systems.

### Signature fields

Each catalogue entry defines:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Signature key | — | Unique identifier | User entries may override bundled entries by key |
| Provider label | — | One of the six labels above, or user-defined | Attribution source |
| Challenge class | — | One of the five classes above | Recorded on the detection |
| Facets | — | Ordered list of (facet type, pattern, weight) | Facet types: `response-header`, `cookie`, `title`, `dom-marker`, `script` |
| Confidence threshold | `0.8` | 0–1 | Minimum normalized score for a verdict |
| Enabled flag | On | On/Off | Local toggle per entry |

**Scoring:** matched facet weights sum to a raw score normalized to 0–1. A
signature matches when its score reaches its threshold.

**Attribution:** the detected provider is the provider of the highest-scoring
matched signature; ties resolve deterministically by catalogue order, then
signature key. Stacked providers (for example a CDN in front of a WAF) record
the primary provider plus **all** matched signature keys in the evidence.

---

## Corroboration: guarding against false positives

A block verdict is a strong claim, so the catalogue enforces a corroboration
guard:

- A verdict requires facets of **at least two independent facet types**
  (for example a provider cookie *and* a challenge-script marker), **or** one
  **high-specificity facet** — a provider-specific cookie or header marker.
- **Generic title-only phrase matches never block on their own.** A page whose
  title merely says "Access denied" produces a Notice-level
  `suspected-challenge` annotation instead — visible, but not a block.
- Default bundled entries are authored to satisfy this guard.

> **Tip:** If you author custom signatures, pair a page-text facet with a
> header, cookie, DOM, or script facet. Single-facet text rules will annotate,
> not block — by design.

---

## What happens to a blocked URL

On a match, the URL state transitions to **`blocked`** — never `fetched` —
with the sub-classification `waf-challenge`, the provider name, the challenge
class, matched signature keys, confidence, detection source (`raw` or
`rendered`), and a bounded evidence snapshot (matched header/cookie names,
title excerpt, DOM/script marker excerpts).

The consequences are deliberate:

- **No retry.** A challenge is not a transient failure. The retry budget is
  not consumed and the URL is not re-fetched within the audit.
- **Link discovery suppressed.** No URLs are enqueued from a challenge
  document; inbound edges to the URL are retained.
- **Evidence retained.** The body is stored in the content store as evidence
  and referenced by the detection record — it is never parsed into the
  content inventory.
- **Counters.** The parent `blocked` counter increments, and an additive
  `wafBlocked` sub-counter is exposed in crawl progress.

### Audit passes that skip blocked URLs

| Excluded pass | Why |
|---|---|
| Thin content, duplicate/length-out-of-range titles and descriptions, near-duplicate clustering | The body is a challenge, not content |
| Readability, reading time, text-to-HTML ratio, soft-404 heuristics | A WAF block is not a soft-404 |
| Spelling checks, custom content search (visible-text targets), N-gram analysis | No real text to analyze |
| Sitemap-coverage judgments | Treated as *unverifiable*, not *missing* |

Retained: header-based security/protocol checks may still evaluate the recorded
headers; technology detection may record the WAF/CDN technology itself;
indexability flags mark the URL non-indexable-by-block. Content-category
scores compute over indexable, non-blocked URLs, so blocked pages never
distort content coverage.

---

## What you see in the UI

### Dashboard

- A **WAF-block panel**: provider breakdown, block rate, and a trend sparkline.
- A **persistent alert banner** while the site-wide `waf-block-rate` alert is
  active.

### Findings

| Rule key | Scope | Severity | Trigger |
|---|---|---|---|
| `waf-challenge-detected` | Per URL | Warning | Every blocked URL; category *Indexability & status*; evidence facets plus generic remediation guidance |
| `waf-block-rate` | Site-wide | Error | WAF-blocked share of attempted fetches ≥ threshold (default **5%**, minimum sample **20** attempts) |
| `waf-block-rate` | Site-wide | Warning | Block rate ≥ **1%** (configurable) |

Both thresholds are local configuration. Suppression and prioritization follow
the standard finding model: suppressed findings are excluded from default
counts and scoring but remain inspectable.

### URL explorer and URL detail

- A **`blocked: waf-challenge` filter preset**, plus a provider column and
  badge.
- The URL detail view shows the evidence facets and a **sandboxed preview** of
  the stored challenge body.
- The URL list API gains `blockReason` and `wafProvider` filter values; the
  audit summary includes WAF counts.

### Real-time progress

While an audit runs, the hub emits a `waf-blocks` event (throttled batches at
most once per second) carrying URL, provider, challenge class, and confidence;
`crawl-progress` carries the `wafBlocked` sub-counter alongside the parent
blocked counter.

---

## How AIMD backoff responds

Every `waf-challenge` detection is an **immediate multiplicative-decrease
trigger** for the host in the AIMD auto-adaptive politeness controller. The
controller treats a soft block exactly like a congestion signal:

1. The effective per-host request rate is **halved immediately**
   (multiplicative factor β, default `0.5`), never dropping below the rate
   floor (default `0.1` req/s ≈ 1 request per 10 seconds).
2. Per-host concurrency is reduced with the same factor (minimum 1).
3. The success streak resets and a **cooldown** begins (default `10` s) — no
   rate increases during cooldown.
4. After cooldown, the rate recovers only through **additive increase**:
   `K` consecutive healthy responses (default `5`), each with TTFB at or below
   the absolute threshold (default `800` ms), raise the rate by the additive
   step (default `0.5` req/s) up to the applicable ceiling.

The adjustment is recorded as a `waf-soft-block` trigger in the host throttle
event log and streamed over the `host-throttle` hub event. The live per-host
throttle panel shows the effective-rate sparkline with the TTFB baseline
overlay, trigger badges, and the adjustment log; you can pin a manual rate per
host (which suspends AIMD for that host until cleared).

> **Note:** HTTP 429/503 backoff remains the parent crawler's exponential
> backoff (honoring `Retry-After` hints). WAF soft-blocks on 200 OK are handled
> by this module and feed AIMD — they never enter the transient-retry path.

> **Tip:** Robots `crawl-delay` directives remain an inviolable floor on
> request spacing, independent of AIMD. AIMD never exceeds your resolved
> throughput or concurrency ceilings; disabling AIMD (per audit or globally)
> reverts exactly to static politeness behavior.

---

## Tuning the catalogue

Open the **signature-catalogue manager** in Settings:

1. Review the **bundled catalogue version** displayed at the top.
2. Browse effective entries (bundled + your own), with per-entry enable
   toggles.
3. Create, edit, or delete **user-local entries**. User entries override
   bundled entries by signature key; deleting an overriding entry restores the
   bundled default.
4. Use the **per-signature match diagnostics** (match counts aggregated per
   audit) to find noisy entries, then tune their facets/threshold or disable
   them.

All catalogue data is local configuration. The effective catalogue and your
user-catalogue revision are snapshotted into every audit, so later edits never
mutate history. A re-audit under an updated catalogue may reclassify previously
fetched URLs — those reclassifications surface as ordinary finding deltas in
audit comparison.

> **Warning:** There is no remote catalogue fetch, no reputation service, and
> no update channel. Catalogue improvements arrive only with product updates
> or your own local entries.

---

## Working with a protected site

CrawlForge's remediation guidance is deliberately non-bypass:

1. **Verify crawler access permissions with the site owner.** If you audit a
   client site, ask them to allowlist your crawler's user-agent or source IP.
2. **Adjust scope or supplied credentials.** Custom request headers/cookies
   (audit inputs) can carry authorized session material for the target site;
   they are stored locally, encrypted at rest.
3. **Run a sample crawl first.** The pass is active in sample mode; a high
   block rate warns you before a full run.
4. **Check pre-crawl validation.** A challenge on a seed URL is surfaced as a
   blocker before work begins.

### Optional render-resolution policy

When rendering mode is enabled, an audit may **explicitly opt in** to the
render-resolution policy (off by default): a **raw-only** match whose
post-render DOM passes the fingerprinting pass clean is recorded `fetched`
with an informational `challenge-resolved-by-render` Notice instead of
blocked. A post-render match **always** blocks. The default policy remains
strict: a match at either checkpoint blocks.

> **Warning:** Opt in only when you understand the site's behavior. The strict
> default exists because a rendered challenge page can still be a block.

---

## Privacy and offline behavior

- The catalogue is a packaged local asset; detection adds **no outbound
  traffic** and never re-fetches.
- Re-audits run the pass over stored bodies — fully offline, air-gapped
  capable.
- Diagnostics (match counts, evidence snapshots) are local-only.
- All API surfaces are loopback-only; the WAF detection list and summary are
  also available as a read-only MCP resource (`waf-blocks`). Catalogue
  management is intentionally **not** exposed via MCP.

---

## REST quick reference

| Operation | Method & path |
|---|---|
| List WAF detections (provider/class/URL filters, sorting, pagination) | GET `/api/audits/{id}/waf-blocks` |
| WAF summary (counts by provider/class, block rate, first/last detection) | GET `/api/audits/{id}/waf-blocks/summary` |
| Read effective catalogue (include-disabled flag) | GET `/api/waf-signatures` |
| Create user signature | POST `/api/waf-signatures` |
| Update user signature | PUT `/api/waf-signatures/{key}` |
| Delete user signature (restores bundled default) | DELETE `/api/waf-signatures/{key}` |
| Per-host AIMD state and adjustment history | GET `/api/audits/{id}/hosts` |
| Pin / clear a manual host rate | POST `/api/audits/{id}/hosts/{host}/override` |

Extended routes: URL list filters (`blockReason`, `wafProvider`), URL detail
(detection detail), audit summary (WAF counts), single-page analysis and URL
check (WAF verdict), exports (`waf-blocks` dataset), charts (`waf-blocks`
metric). Metric keys: `waf-blocks` (dimensions: provider, challenge class) and
`waf-block-rate`.

---

## Related guides

- [Content scoping and boilerplate stripping](content-scoping-and-boilerplate-stripping.md)
- [N-grams and topical density](n-grams-and-topical-density.md)
- [Semantic internal linking](semantic-internal-linking.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md)

[← Documentation portal](../README.md)
