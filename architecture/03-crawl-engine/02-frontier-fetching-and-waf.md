# Frontier, Fetching, WAF Detection & List Mode

> **CrawlForge modular architecture — area of concern: `03-crawl-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §5 heading & introduction, §5.1, §5.2, §5.3, §5.4, §5.5, §5.7, §5.8 |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | §2 (complete section), §10 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5. Crawl Engine — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 5. Crawl Engine

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.1 Audit inputs — verbatim and unabridged**

### 5.1 Audit inputs

| Input | Required | Description |
|---|---|---|
| Seed URL(s) | Yes | One or more starting URLs; the crawl scope is derived from them unless overridden |
| Scope definition | Yes | Host-only, include-subdomains, or whole-domain; optional path-prefix rules |
| Depth ceiling | No | Overrides the limits default for this audit |
| Inclusion patterns | No | URL patterns that must match for a URL to be crawled |
| Exclusion patterns | No | URL patterns that disqualify a URL (evaluated after inclusion) |
| Query-parameter policy | No | Keep all, keep a whitelist, or strip all query parameters during canonicalization |
| Rendering mode | No | Plain HTTP fetch, or JavaScript-rendered fetch (section 5.6) |
| Limit overrides | No | Any subset of the parameters in section 4.2 |
| Custom request headers / cookies | No | Optional credentials for the target site, stored locally |
| Sitemap seeds | No | Additional sitemap URLs to expand at start |
| Politeness options | No | Robots compliance toggle, per-host throttle, user-agent string |
| Proxy routing | No | Upstream proxy and/or the built-in local intercepting proxy (section 5.9) |
| Custom extraction rules | No | User-defined content extraction rules applied during the crawl (section 7.9) |
| Custom search items | No | User-defined content search items applied during the crawl (section 7.16) |
| JavaScript snippets | No | User-defined in-page scripts for rendering mode (section 7.17) |
| Sample-crawl mode | No | A depth/page-bounded sampling run for a quick preview, using the same pipeline and limits resolution |
| Virtual robots override | No | A user-supplied robots.txt body used in place of the live one for this audit |
| Signed-request access | No | Optional signature material applied to in-scope requests, with expiry tracking and automatic re-application (section 5.10) |
| Pre-crawl validation | No | Resolve and probe the start URL(s) before crawling (protocol, redirect, TLS, login-gate detection) and surface blockers before work begins |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.2 URL canonicalization and deduplication — verbatim and unabridged**

### 5.2 URL canonicalization and deduplication

Before a URL enters the frontier it is canonicalized:

- Scheme and host are lowercased; default ports are removed; fragments are discarded.
- Trailing-slash policy is applied consistently; percent-encoding is normalized.
- Query parameters are sorted, filtered by the configured policy, and known session-tracking parameters are stripped.
- Relative references are resolved against the document base, honoring any declared base override.

A persistent **seen-set** keyed by canonical URL guarantees each URL is fetched at most once per audit.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.3 Frontier and scheduling — verbatim and unabridged**

### 5.3 Frontier and scheduling

- A **priority frontier** orders work: seeds first, then a breadth-leaning order with depth as tiebreaker.
- **Per-host sub-queues** enforce politeness and throughput ceilings independently of global concurrency.
- Sitemap entries are treated as *discovered* URLs and scheduled like any other discovery.
- Per-URL states: `queued → fetching → fetched | redirected | error | blocked | skipped`, each transition persisted.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.4 Fetching — verbatim and unabridged**

### 5.4 Fetching

- Workers pull URLs under the concurrency and throughput ceilings and fetch them with a configurable HTTP client, honoring the configured proxy routing (section 5.9).
- Redirects are followed up to the hop ceiling; **every hop is recorded** so full chains and loops can be reconstructed.
- Recorded per response: final status, content type, size, timing, selected headers (caching, security, redirection), and TLS outcome.
- Content-type routing: HTML is parsed; XML sitemaps are expanded; robots files update host rules; stylesheets, scripts, images, and fonts are recorded as resource metadata; other types are recorded without parsing.
- Transient failures are retried within the retry budget using exponential backoff; permanent failures are classified (name resolution, TLS, timeout, HTTP status, oversize, robots-blocked, proxy failure) and stored with the URL record.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.5 Link discovery — verbatim and unabridged**

### 5.5 Link discovery

- From HTML: anchor and image-map references, document-level link relations (canonical, alternates, stylesheets, next/previous), and optionally embedded frame sources and meta-refresh targets.
- From sitemaps: URL entries with their declared metadata; sitemap indexes are expanded recursively.
- Classification: **in-scope** URLs are enqueued; **external** URLs are recorded as external links only; **excluded** URLs are recorded as skipped with the reason.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.7 Lifecycle and control — verbatim and unabridged**

### 5.7 Lifecycle and control

- Audit states: `configuring → running → (paused ⇄ running) → completed | cancelled | failed | interrupted`.
- **Pause** checkpoints the frontier, seen-set, and counters; **resume** rehydrates them; **cancel** drains workers and finalizes partial data; **interrupted** (process loss) is recoverable from the last checkpoint.
- Progress is emitted continuously, throttled to at most one message per second: counters (queued, fetched, errors, blocked), throughput, elapsed time, estimated remainder, and current phase.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.8 Storage during crawl — verbatim and unabridged**

### 5.8 Storage during crawl

- URL records, link edges, and extracted elements are persisted incrementally in transactional batches; the engine never requires the full dataset in memory.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 2. Soft-Block and WAF Challenge Detection on 200 OK Responses — complete section, verbatim and unabridged**

## 2. Soft-Block and WAF Challenge Detection on 200 OK Responses

**Extends FS-CRAWL-001 §5.3, §5.4, §7.12.** Additional hooks: §4.4, §5.1, §5.6, §7.2, §7.4, §7.5, §7.15, §8.1, §8.2, §9.2, §9.6, §10.2, §10.3, §11.4.

### 2.1 Problem and objective

Modern Web Application Firewalls (Cloudflare, DataDome, Akamai, Imperva, PerimeterX) frequently serve CAPTCHA, interstitial challenge, or bot-block pages with an **HTTP 200 OK** status. Without inspection, the crawler records these as healthy fetched pages, producing false *Thin Content* and duplicate-title findings, polluted text metrics, and wasted audit budget.

Objective: detect challenge pages **at response receipt**, classify the URL as `blocked` with sub-classification `waf-challenge` and the detected provider name, exclude it from content-quality passes, and surface a dedicated WAF-Block diagnostic alert. Detection is diagnostic only (FS-CRAWL-002 §1.2, §1.6 E3).

### 2.2 Pipeline placement — pre-audit WAF & Challenge Fingerprinting Pass

The pass executes **immediately upon response receipt in §5.4, before DOM inventory extraction** (§7.2 step 3, §7.3). Ordering contract:

1. The fetch worker receives the response; final status, content type, size, timing, selected headers, and TLS outcome are recorded exactly as §5.4 specifies.
2. The **WAF & Challenge Fingerprinting Pass** runs before content-type routing persists the page as `fetched`:
   - **Stage A (cheap facets):** response-header and cookie facet matching against the signature catalogue (FS-CRAWL-002 §2.3). No parsing required.
   - **Stage B (bounded pre-parse):** for HTML responses not resolved by Stage A, a lightweight bounded parse extracts the title element and scans DOM-marker and script facets. The parse is time-bounded and node-bounded so pathological documents cannot stall fetch workers (the same bound philosophy as §7.9); it is not the full inventory extraction.
3. **Verdict routing:** a challenge match triggers the FS-CRAWL-002 §2.4 state transition; no match continues the normal parent pipeline (content-type routing §5.4 → inventory §7.3 → rule passes §7.2 steps 4–8).

Additional execution contexts:

- **Rendering mode (§5.6):** the pass runs at two checkpoints — raw response receipt and post-render DOM capture. A match at either checkpoint blocks the URL, except under the optional render-resolution policy (FS-CRAWL-002 §2.4.3).
- **Re-audit (§7.2 step 1, §9.6):** the pass re-runs over stored bodies from the content store, fully offline. The catalogue version and user-catalogue revision are snapshotted per audit (FS-CRAWL-002 §2.3.4) so re-audits are reproducible; a re-audit under an updated catalogue may reclassify previously fetched URLs, surfaced as ordinary finding deltas (§8.2 audit comparison).
- **Single-page analysis (§7.7) and URL checker (§8.7):** both run the same pass and report the verdict in their results.
- The pass never re-fetches, never enqueues links found in challenge documents, and adds no outbound traffic.

### 2.3 Signature catalogue

#### 2.3.1 Structure

A **bundled, versioned, locally maintainable** catalogue, following the asset pattern of technology detection (§7.15): shipped with the product, never fetched from a remote service at runtime, updated only with product updates, extended by local user entries. The catalogue is data, not engine logic — adding a signature is a data change (the §7.6 principle).

Each signature entry defines:

| Field | Semantics |
|---|---|
| Signature key | Unique identifier (new design artifact); user entries may override bundled entries by key |
| Provider label | One of `cloudflare`, `datadome`, `akamai`, `imperva`, `perimeterx`, `generic`, or a user-defined label |
| Challenge class | One of `captcha`, `js-interstitial`, `managed-challenge`, `bot-block`, `rate-limit-soft-block` |
| Facets | Ordered list of (facet type, pattern, weight). Facet types: **response-header** (header-name pattern + optional value pattern), **cookie** (name pattern + optional value pattern), **title** (text pattern), **dom-marker** (element/attribute selector pattern), **script** (source-URL pattern or inline-marker pattern) |
| Confidence threshold | Minimum normalized score (0–1) for a verdict; default 0.8 |
| Enabled flag | Local toggle per entry |

#### 2.3.2 Illustrative public marker classes

Non-exhaustive examples of publicly observable marker classes, provided to ground catalogue authoring. Entries are new design artifacts authored from public behavior of the named third-party services (FS-CRAWL-002 §1.2), not copied from reference artifacts.

| Provider | Header / cookie marker classes | Title / DOM / script marker classes |
|---|---|---|
| Cloudflare | `cf-*` diagnostic header family (e.g. ray identifiers; a mitigation header carrying a challenge value); managed-challenge cookie family (`__cf_bm`, `cf_clearance` class) | Interstitial title phrase class ("just a moment" family); challenge-platform script path class; embedded turnstile widget markers |
| DataDome | DataDome session cookie class; `x-dd-*` response header family | CAPTCHA page title/markers naming the provider; provider client-script markers |
| Akamai | Bot-manager cookie family (`ak_bmsc`, `_abck` class); server token values | "Access denied" block pages with reference-number structure; bot-manager script markers |
| Imperva | Incident-session cookie family (`visid_incap_*`, `incap_ses_*` class) | Block pages with incident-ID structure; provider resource/script markers |
| PerimeterX | `_px*` cookie family; `x-px-*` header family | "Access to this page has been denied" phrase class; provider client-script markers |
| Generic | — | Retry-later / access-denied / verify-you-are-human phrase classes combined with challenge-form structure (requires corroborating facets, FS-CRAWL-002 §2.3.3) |

#### 2.3.3 Confidence, attribution, and false-positive guards

- **Scoring:** matched facet weights sum to a raw score normalized to 0–1. A signature matches when its score ≥ its threshold.
- **Attribution:** the detected provider is the provider of the highest-scoring matched signature; ties resolve deterministically by catalogue order, then signature key. Stacked providers (e.g. CDN plus WAF) record the primary provider plus all matched signature keys in evidence.
- **Corroboration guard:** a block verdict requires either facets of **at least two independent facet types** or one high-specificity facet (a provider-specific cookie or header marker). Generic title-only phrase matches never block on their own; they produce a Notice-level `suspected-challenge` annotation instead. Default bundled entries are authored to satisfy this guard.
- **Status eligibility:** only responses that would otherwise be recorded `fetched` (2xx with HTML content type) can transition to `waf-challenge` (FS-CRAWL-002 §2.4.1).

#### 2.3.4 Maintenance, provenance, and diagnostics

- Bundled catalogue file ships as a packaged asset (§13 packaging manifest); its version is recorded in every audit snapshot.
- **User-local entries** are created, edited, enabled/disabled, and deleted in settings (FS-CRAWL-002 §2.9) and persisted in the `waf_user_signatures` table (FS-CRAWL-002 §2.6), on either database provider. The effective catalogue = bundled entries + user entries, with user entries overriding bundled entries by key. A user-catalogue revision counter is snapshotted per audit.
- Per-signature match counts are aggregated per audit so users can tune or disable noisy entries; diagnostics are local-only.
- No remote catalogue fetch, no reputation service, no update channel (FS-CRAWL-002 §1.6 E2, E4).

### 2.4 Decision logic and state transition

#### 2.4.1 Eligibility

The pass evaluates responses that would otherwise be recorded `fetched`. Hard errors, redirects, robots-blocked URLs, and non-HTML resources keep their parent classification (§5.4). A challenge signature observed on a non-2xx response is recorded as an evidence annotation only; the status class is unchanged.

#### 2.4.2 Transition to blocked

On a match, the URL state in §5.3 transitions to **`blocked`** — never `fetched` — with:

- **Block sub-classification:** `waf-challenge` (a new sub-classification of the parent `blocked` state; robots-blocked remains distinct).
- **Provider name** and **challenge class** from the winning signature.
- Matched signature keys, confidence, detection source (`raw` or `rendered`), and a bounded **evidence snapshot** (matched header/cookie names, title excerpt, DOM/script marker excerpts; length-capped per the §7.9 truncation philosophy).

The transition is persisted like every §5.3 transition. Consequences:

- **No retry:** a challenge response is not a transient failure; the retry budget (§4.2) is not consumed and the URL is not re-fetched within the audit.
- **Link discovery suppressed (§5.5):** no URLs are enqueued from a challenge document; inbound edges to the URL are retained.
- **Evidence retention (§9.6):** the body is stored in the content store as evidence and referenced by the detection record; it is never parsed into the content inventory.
- **Counters:** the parent `blocked` counter (§5.7, `crawl-progress` §10.3) increments; an additive `wafBlocked` sub-counter is exposed (FS-CRAWL-002 §2.8).

#### 2.4.3 Render-resolution policy (optional, off by default)

When rendering mode (§5.6) is enabled and the audit explicitly opts in: a **raw-only** match whose post-render DOM passes the fingerprinting pass clean is recorded `fetched` with an informational annotation `challenge-resolved-by-render` (Notice finding) instead of blocked. A post-render match always blocks. The default policy remains strict: a match at either checkpoint blocks.

#### 2.4.4 Audit-pass exclusions

URLs blocked as `waf-challenge` are excluded from:

- Content-quality passes: thin content, duplicate/length-out-of-range titles and descriptions, near-duplicate shingle clustering (§7.4, §7.5).
- Readability, reading time, text-to-HTML ratio, and soft-404 content heuristics (§7.12). **A WAF block is not a soft-404**; the classifications are distinct and mutually exclusive per URL.
- Spelling checks (§7.8), custom content search visible-text targets (§7.16), and N-gram analysis (FS-CRAWL-002 §5).
- Sitemap-coverage judgments (§7.4) treat them as *unverifiable*, not *missing*.

Retained: header-based security/protocol checks (§7.4) may evaluate the recorded headers; technology detection (§7.15) may record the WAF/CDN technology itself; indexability flags mark the URL non-indexable-by-block.

#### 2.4.5 Dedicated WAF-Block diagnostic alert

- **Per-URL finding:** rule key `waf-challenge-detected`, category *Indexability & status* (§7.4), severity **Warning**, evidence per FS-CRAWL-002 §2.4.2, generic remediation guidance (verify crawler access permissions with the site owner, adjust scope or supplied credentials via §5.1 — never bypass guidance).
- **Site-wide alert:** rule key `waf-block-rate`, site-wide scope marker (§7.5): severity **Error** when the WAF-blocked share of attempted fetches ≥ a configurable threshold (default 5%, minimum sample 20 attempts); severity **Warning** at ≥ 1% (configurable). This is the dedicated WAF-Block diagnostic alert surfaced on the dashboard (FS-CRAWL-002 §2.9).
- Suppression and prioritization follow §7.5; suppressed findings are excluded from default counts and scoring (§8.1).

### 2.5 Interaction with other modules and parent flows

- **AIMD (FS-CRAWL-002 §3):** every `waf-challenge` detection is an immediate multiplicative-decrease trigger for the host.
- **Pre-crawl validation (§5.1):** the seed probe runs the pass; a challenge on a seed URL surfaces as a blocker before the crawl starts.
- **Sample-crawl mode (§5.1):** the pass is active; a high block rate in the sample warns the user before a full run.
- **Scoring (§8.1):** WAF findings contribute under *Indexability & status*; content-category scores compute over indexable, non-blocked URLs so blocked pages do not distort content coverage.
- **Backoff interplay (§4.4):** HTTP 429/503 backoff remains the parent's; WAF soft-blocks on 200 OK are handled by this module and feed AIMD, not the transient-retry path.

### 2.6 Data model additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `waf_detections` | New table | Audit reference; URL reference; provider; challenge class; matched signature keys JSON; confidence; detection source (raw/rendered); evidence JSON; detected-at timestamp | One row per blocked URL per audit; unique (audit, URL); indexes (audit, provider), (audit, URL); pruned with retention (§9.5) |
| `waf_user_signatures` | New table | Signature key; provider label; challenge class; facets JSON; threshold; enabled flag; created/updated timestamps | User-local catalogue entries (FS-CRAWL-002 §2.3.4); configuration data — not pruned by audit retention |
| `url_records` | Additive columns | Block reason (`robots` \| `waf-challenge` \| `other` \| null); WAF provider (nullable); challenge class (nullable) | Extends the parent status-class/robots-state fields; existing columns unchanged |
| `audits` | Additive snapshot fields | Catalogue version; user-catalogue revision; detection policy flags (enabled, render-resolution) | Snapshot pattern per §9.2 so later catalogue edits never mutate history |
| `metric_summaries` | New metric keys | `waf-blocks` (dimensions: provider, challenge class); `waf-block-rate` | Precomputed aggregates per §8.1 |

### 2.7 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| List WAF detections | GET `/api/audits/{id}/waf-blocks` | Provider, challenge-class, URL filters; sorting; pagination | Detection rows with evidence |
| WAF summary | GET `/api/audits/{id}/waf-blocks/summary` | — | Counts by provider/class, block rate, first/last detection timestamps |
| Read effective catalogue | GET `/api/waf-signatures` | Include-disabled flag | Bundled catalogue version + effective entries (patterns are local configuration, not secrets) |
| Create user signature | POST `/api/waf-signatures` | Signature fields per FS-CRAWL-002 §2.3.1 | Created entry |
| Update user signature | PUT `/api/waf-signatures/{key}` | Any subset of signature fields | Updated entry |
| Delete user signature | DELETE `/api/waf-signatures/{key}` | — | Confirmation; bundled default restored when overriding entry removed |

Extended existing routes (additive fields only): GET `/api/audits/{id}/urls` gains `blockReason` and `wafProvider` filter values; GET `/api/audits/{id}/urls/{urlId}` includes the detection detail; GET `/api/audits/{id}/summary` includes WAF counts; POST `/api/analysis/single-page` and POST `/api/tools/url-check` include the WAF verdict; POST `/api/exports` accepts a `waf-blocks` dataset selection (§8.4); GET `/api/audits/{id}/charts/{metric}` accepts the `waf-blocks` metric.

### 2.8 Real-time hub and MCP additions (extends §10.3, §11.4)

- **New hub event `waf-blocks`** (server → shell): audit identifier; batch of (URL identifier, canonical URL, provider, challenge class, confidence). Emission: throttled batches at most once per second, mirroring the `url-batch` discipline.
- **`url-batch` additive fields:** optional block reason and provider on blocked entries.
- **`crawl-progress` additive field:** `wafBlocked` sub-counter alongside the parent blocked counter.
- **New MCP resource `waf-blocks`** (§11.4 address pattern: resource kind + audit reference + optional filters): provider/class filters and pagination; read-only projection of the REST detection list and summary. Signature-catalogue management is **not** exposed via MCP (§11.1 boundary: no settings management).
- `audit-summary` and `url-detail` resources gain the same additive fields as their REST counterparts.

### 2.9 UI requirements

- **Dashboard (§8.2):** WAF-block panel — provider breakdown, block rate, trend sparkline — and a persistent alert banner while the site-wide `waf-block-rate` alert is active.
- **URL explorer (§8.2):** a `blocked: waf-challenge` filter preset, provider column and badge; the URL detail view shows evidence facets and a sandboxed preview of the stored challenge body (§12 embedded-preview rule).
- **Settings (§12):** signature-catalogue manager — bundled version display, user-entry CRUD, per-entry enable toggles, per-signature match diagnostics.
- All tabular surfaces obey the virtualization mandate (FS-CRAWL-002 §7); charts use the mandated stack (FS-CRAWL-002 §8).

**Acceptance hook:** FS-CRAWL-002 §9, criterion #35.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 10. List Mode, URL Rewriting & Migration Redirect Matrix Validator — complete section, verbatim and unabridged**

## 10. List Mode, URL Rewriting & Migration Redirect Matrix Validator

**Extends FS-CRAWL-001 §5.1, §5.2, §5.4, §8.2, §8.7.** Additional hooks: §4.1, §4.2, §5.3, §5.5, §7.4, §9.2, §10.2, §10.3, §11.4.

### 10.1 Purpose

Three related capabilities for migration and targeted auditing: (a) **List Mode** — crawl a user-supplied URL list instead of discovering from seeds; (b) a **Redirect Migration Matrix Validator** that verifies old→new URL mappings hop-by-hop; (c) **on-the-fly URL rewriting** and **staging↔production host mapping** for canonicalization and comparison.

### 10.2 List Mode ingestion (extends §5.1)

- New audit input: **URL list seed**, supplied as `.txt` (one URL per line), `.csv` (configurable URL column, automatic header detection), or clipboard paste; files are selected via the native dialog (§12).
- Ingestion pipeline: parse → per-entry validation (absolute http(s) URLs; malformed entries are rejected into a bounded per-line diagnostic list and never fail the audit) → canonicalization (§5.2, including the rewrite rules of FS-CRAWL-002 §10.4) → seen-set deduplication (§5.2) → enqueue as seeds with discovery source `list`.
- **Discovery modes:**
  - `list-only`: depth ceiling forced to 0; link discovery (§5.5) and sitemap expansion disabled; only uploaded URLs are crawled.
  - `list-bounded`: discovery runs, but only URLs present in the uploaded list (after canonicalization) are enqueued; off-list discoveries are recorded as `skipped` with reason `out-of-list`.
- Limits resolution is unchanged (§4.1): total/per-domain ceilings, timeouts, and retry budgets still apply. Pre-crawl validation (§5.1) probes a bounded sample of list entries; WAF verdicts (FS-CRAWL-002 §2) apply as usual.

### 10.3 Redirect Migration Matrix Validator

- **Mapping upload:** two columns — `Source_URL`, `Expected_Target_URL` — from `.csv`, `.txt`, or clipboard. Rows persist in `redirect_mappings` (FS-CRAWL-002 §10.6) attached to the audit; the upload auto-configures a `list-only` audit seeded from the Source column (or attaches to an existing list-mode audit).
- **Execution:** every Source_URL is fetched; the full redirect hop chain is recorded (§5.4 records every hop). Evaluation compares the **final destination** and the final page's **canonical target** against Expected_Target_URL using canonicalized comparison (§5.2 plus the audit's query policy; strictness configurable: `exact` | `normalized`).
- **Verdict precedence** — one primary verdict per row, first failure wins; all evidence is retained regardless:
  1. `redirect-matrix.chain-exceeded` — hop count exceeds the §4.2 redirect-hop ceiling, or a loop is detected. Severity **Error**.
  2. `redirect-matrix.final-non-200` — final status outside 2xx. Severity **Error**.
  3. `redirect-matrix.target-mismatch` — final destination ≠ expected target. Severity **Error**.
  4. `redirect-matrix.canonical-mismatch` — final page declares a canonical that differs from the expected target. Severity **Warning**.
  5. `redirect-matrix.pass` — all checks satisfied. Severity **Pass**.
- All five rule keys live in the *Indexability & status* category (§7.4); evidence carries the full hop chain, final status, and observed canonical.
- Sources blocked as `waf-challenge` (FS-CRAWL-002 §2) are marked **unverifiable** (blocked annotation) and excluded from pass/fail rates.
- **Migration Matrix surface** (additive to §8.2): verdict columns, hop-chain drill-down, verdict filters, and spreadsheet/CSV export (§8.4). Aggregates: `metric_summaries` key `redirect-matrix` dimensioned by verdict (§8.1).

### 10.4 On-the-fly URL rewriting (extends §5.2)

- Ordered **regex find/replace rules** applied during canonicalization, before seen-set keying. Rule fields: pattern, replacement, scope (`seeds-only` | `all-discovered`), enabled flag.
- Deterministic evaluation order; time-bounded pattern evaluation (the §7.16 bound philosophy); invalid patterns rejected at configuration time (§4.3); a rewrite may never produce a non-http(s) scheme.
- Per-rule rewrite counters are recorded as diagnostics; the rule set is snapshotted per audit (§9.2 snapshot pattern).
- Preview tool: POST `/api/tools/rewrite-preview` (FS-CRAWL-002 §10.7) shows sample URLs before/after.

### 10.5 Staging-vs-production host mapping (extends §8.2)

- **Host-mapping rules:** source host → target host, with optional path-prefix transform (e.g. `staging.example.com` → `www.example.com`).
- Audit comparison and content comparison (§8.2) align URLs by **path** (plus query per policy) after host mapping; GET `/api/audits/compare` and GET `/api/audits/compare/content` accept an additive `hostMapping` parameter. Surfaces label the mapped hosts; without a mapping, comparison behavior is exactly the parent's.
- The matrix validator may use the same mapping when expected targets live on the production host while the crawl runs against staging.

### 10.6 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `redirect_mappings` | New table | Audit reference; source canonical URL; expected target URL; ordinal; upload batch reference; created-at | Unique (audit, source URL); index (audit, ordinal); pruned with retention (§9.5) |
| `crawl_configurations` | Additive fields | List mode (`list-only` \| `list-bounded` \| off); URL list reference; rewrite rules JSON; host-mapping JSON | §5.1 input-set extension |
| `audits` | Additive snapshot fields | Resolved list mode, rewrite rules, host mapping, matrix attachment | Snapshot pattern (§9.2) |
| `url_records` | Additive enum value | Discovery source gains `list` | Existing column; no schema change |
| `metric_summaries` | New metric key | `redirect-matrix` (dimension: verdict) | §8.1 aggregate pattern |

### 10.7 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Upload migration matrix | POST `/api/audits/{id}/redirect-matrix` | Mapping rows (Source_URL, Expected_Target_URL); strictness; seed-mode flag | Created mapping count; audit seeding result |
| Matrix results | GET `/api/audits/{id}/redirect-matrix/results` | Verdict filter; URL search; sorting; pagination | Per-row verdict, hop chain, final status, canonical evidence |
| Rewrite preview | POST `/api/tools/rewrite-preview` | Sample URLs; rewrite rules | Before/after canonicalization per rule |

Extended existing routes: POST `/api/audits` accepts `urlList` + `listMode`; configuration routes accept rewrite/host-mapping fields; GET `/api/audits/compare` and GET `/api/audits/compare/content` accept `hostMapping`; POST `/api/exports` accepts a `redirect-matrix` dataset (§8.4).

### 10.8 Hub and MCP additions (extends §10.3, §11.4)

- **New hub event `matrix-row-validated`** (server → shell): audit identifier; batch of (URL identifier, source URL, verdict). Throttled batches at most once per second.
- `crawl-progress` additive optional matrix counters (validated/total) while a matrix is attached.
- **New MCP resource `redirect-matrix-results`** (audit reference; verdict filter; pagination): read-only projection of the results endpoint.

### 10.9 UI requirements

- Audit configuration: list-ingestion panel (file picker / clipboard paste, preview table with per-line validation diagnostics, discovery-mode selector).
- Migration Matrix surface with verdict badges and hop-chain visualization; matrix upload wizard.
- Rewrite-rule editor with live preview; host-mapping editor embedded in the comparison surfaces.
- All tabular content obeys FS-CRAWL-002 §7.1.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #43.

---
