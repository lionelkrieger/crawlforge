| Document control | Value |
|---|---|
| Identifier | FS-CRAWL-002 |
| Title | CrawlForge Extensions & UI Stack — Advanced Crawl Intelligence, Content Scoping, Extensibility & Frontend UI Architecture |
| Version | 1.1 |
| Status | Issued for clean-room implementation |
| Parent specification | FS-CRAWL-001 v1.4 — `functional-specification.md` (normative baseline; this document does not modify it) |
| Audience | Clean-room development team (no access to reference artifacts) |
| Classification | Abstract functional specification — contains no code and no proprietary expression |
| Derivation | New design artifacts extending the abstract FS-CRAWL-001 baseline; no reference application consulted |
| Workspace specification set | `functional-specification.md` (FS-CRAWL-001, core clean-room functional baseline) + `crawlforge-extensions-and-ui-stack.md` (FS-CRAWL-002, this document) |

| Revision history | Change summary |
|---|---|
| 1.0 | Initial issue: WAF soft-block detection on 200 OK responses; AIMD auto-adaptive politeness controller; content-area scoping and boilerplate stripping; site-wide N-gram and topical density analyzer; local UI plugin architecture and lifecycle hooks; real-time memory profiling and DOM virtualization mandates; mandated frontend technology stack; acceptance criteria #35–#42 appended to FS-CRAWL-001 §14 |
| 1.1 | Six new modules (§§10–15): List Mode, URL rewriting & migration redirect-matrix validator; local server-log analyzer & true-orphan discovery; local vector embeddings & semantic internal linking (activates `url_embeddings`); AI-search (GEO) readiness, `llms.txt` validator & bot-governance matrix; MarTech/analytics/tag-integrity auditor; contextual link weighting, pixel-width SERP measurement & header/HTML equivalency. Acceptance criteria #43–#48 appended; Appendices A–E extended. All v1.0 sections (§1–§9, Appendices A–E) preserved. Citation-notation corrections applied (local refs prefixed `FS-CRAWL-002 §n`; one route misquote fixed) |

---

## 1. Document Control and Integration Map

### 1.1 Purpose and relationship to the parent specification

This document is the companion extension specification to **FS-CRAWL-001 v1.4**. FS-CRAWL-001 remains the normative baseline for the core product: process model, crawl engine, audit engine, persistence, reporting, REST/hub contract, local MCP server, and desktop shell. FS-CRAWL-002 v1.0 specified five advanced capability modules (WAF soft-block detection, AIMD adaptive politeness, content-area scoping, site-wide N-gram analysis, local UI plugins), two cross-cutting frontend mandates (memory telemetry and DOM virtualization), and the concrete frontend technology stack that realizes FS-CRAWL-001 §12. **Version 1.1 adds six further modules (§§10–15):** List Mode with URL rewriting and a migration redirect-matrix validator; a 100% offline server-log analyzer with true-orphan discovery; a strictly local vector-embedding and semantic internal-linking pipeline that activates the dormant `url_embeddings` schema; AI-search (GEO) readiness with `llms.txt` validation and a bot-governance matrix; a MarTech/analytics/tag-integrity auditor; and contextual link weighting with pixel-width SERP measurement and HTTP-header/HTML directive equivalency.

The two documents are read together as one specification set. Every extension in this document is **additive**: it hooks into an identified parent contract without altering the parent's existing semantics. An implementation that satisfies FS-CRAWL-001 alone remains conformant to the baseline; an implementation that satisfies both documents is conformant to the extended product.

### 1.2 Clean-room protocol (Chinese Wall maintenance)

The Chinese Wall established in **FS-CRAWL-001 §1.2** applies in full to this document and to all implementation work derived from it:

- This document conveys **abstract behavior only**: business rules, workflows, data shapes, mathematical principles, architectural contracts, and UI component specifications. It contains no source code, no identifier names taken from the reference application, no proprietary algorithms, and no literal strings, endpoints, ports, or configuration values copied from reference artifacts.
- All names, routes, schemas, taxonomies, defaults, hook contracts, manifest fields, and event names in this document are **new design artifacts** created for the target product.
- **Technology vocabulary.** Publicly documented open-source library and platform names — Mantine, TanStack Table, TanStack Virtual, AG Grid Community, Apache ECharts, Sigma.js, Graphology, Finder (`@medv/finder`), React, Electron, SQLite, Prometheus-class exposition — are used as technology vocabulary only, under the same convention FS-CRAWL-001 §1.2 applies to Electron-class, ASP.NET Core-class, and SignalR-class names and to the standard `System.GC.Server` runtime setting (FS-CRAWL-001 §13). Mandating a publicly available open-source library is an architectural choice, not a reproduction of proprietary expression.
- **WAF provider names.** Cloudflare, DataDome, Akamai, Imperva, and PerimeterX appear solely as classification labels for publicly observable challenge behavior of third-party services. Signature-catalog entries are authored from publicly documented or publicly observable response characteristics of those services; none are copied from reference artifacts. The marker examples in §2.3.2 are illustrative, non-exhaustive examples of public marker classes.
- **v1.1 technology and vendor vocabulary.** ONNX, Ollama / LM Studio-class local daemons, log format names (Apache/Nginx Combined, IIS W3C, Cloudflare exports), MarTech product names (Google Tag Manager, Google Analytics 4, Google Ads, Meta Pixel, LinkedIn Insight, Microsoft Clarity), and crawler agent tokens (Googlebot, Bingbot, OAI-SearchBot, GPTBot, ClaudeBot, Google-Extended, PerplexityBot, CCBot, Applebot) are used solely as publicly documented technology vocabulary and classification labels for detection targets. All catalogue entries, schemas, routes, verdict taxonomies, and defaults built around them remain new design artifacts; the `llms.txt` convention is treated as a publicly documented community standard.
- The implementation team shall not read, decompile, or otherwise consult the reference application, its binaries, or the dirty-room analysis documents. Clarifications are routed through the architect as abstract behavioral questions only.
- **Detection is diagnostic, not adversarial.** This specification defines detection, classification, and reporting of challenge pages only. No CAPTCHA solving, challenge bypass, evasion automation, or credential abuse is specified or permitted. The optional stealth rendering profile of FS-CRAWL-001 §5.6 remains a user-configured rendering option, not a WAF-bypass tool.

### 1.3 Integration map

Each FS-CRAWL-002 module extends the following FS-CRAWL-001 subsystems. Bare `§` references throughout this document mean **FS-CRAWL-001** unless explicitly prefixed.

| FS-CRAWL-002 module | Extends (FS-CRAWL-001) | Nature of the extension |
|---|---|---|
| §2 WAF & challenge detection | §4.4 (backoff interplay), §5.3 (URL states), §5.4 (fetching), §5.6 (rendering), §7.2 (pipeline), §7.4 (taxonomy), §7.12 (soft-404 distinction), §7.15 (catalog pattern), §8.1–§8.2 (aggregates, surfaces), §9.2 (schema), §10.2–§10.3 (REST, hub), §11.4 (MCP resources) | Pre-audit fingerprinting pass at response receipt; `blocked` sub-classification `waf-challenge` with provider attribution; dedicated diagnostic findings; `waf_detections` and `waf_user_signatures` tables; WAF REST routes, hub event, and MCP resource |
| §3 AIMD politeness controller | §4.1 (resolver), §4.2 (ceilings), §4.4 (politeness), §5.1 (inputs), §5.3 (per-host sub-queues), §5.4 (timing), §5.7 (checkpointing), §9.2 (schema), §10.2–§10.3 (REST, hub), §11.4 (MCP) | Per-host additive-increase/multiplicative-decrease rate and concurrency control under resolved ceilings; `host_throttle_events` table; `host-throttle` hub event; host telemetry REST and MCP resource |
| §4 Content-area scoping | §5.1 (inputs), §7.2 (pipeline), §7.3 (inventory), §7.5 (shingles), §7.8 (spelling), §7.9 (selector contract, preview), §7.12 (text metrics), §7.16 (custom search), §9.2 (schema), §10.2 (REST) | Supplies the content-area selector configuration contract that §7.12 and §7.16 already reference; Scoped Visible Text Tree feeding all text passes; `content_scope_profiles` table; scope-preview tool |
| §5 N-gram & topical density analyzer | §7.2 (pipeline), §7.3 (inventory), §7.4 (taxonomy), §7.5 (findings), §8.1–§8.2 (aggregates, surfaces), §8.4 (export), §9.2 (schema), §10.2 (REST), §11.4 (MCP) | Per-language tokenization and stop-word pipeline; four-zone 1/2/3-gram TF/DF analysis; cannibalization and topical-gap findings; `ngram_frequencies` table and `ngram_site_summaries` aggregate view; N-Gram Explorer surface; `ngram-analysis` MCP resource |
| §6 Local UI plugin architecture | §2.3 (startup), §2.6 (network boundary), §3 (exclusions), §8.2 (report surfaces), §8.7 (query builder), §9.2 (schema), §10.2 (REST), §12 (shell) | Local plugin directories scanned at shell startup; sandboxed JS/HTML modules with zero default network; deterministic `onLoad` / `onTabActivate` / `onDataUpdate` / `onCrawlComplete` hook contract; `ui_plugins` registry |
| §7 Memory profiling & virtualization | §8.2 (explorers), §10.3 (hub), §10.5 (metrics), §12 (shell), §13 (scale, responsiveness) | Hard DOM-row mount caps for every tabular explorer; 2-second `system-health` heartbeat; persistent status bar; manual Compact / Trim Working Set action |
| §8 Mandated frontend stack | §2.1 (process model), §8.2–§8.3 (surfaces, charts), §8.7 (tools), §12 (shell), §13 (packaging, accessibility) | Concrete Electron-renderer architecture: Mantine UI, TanStack Table + Virtual (or AG Grid Community), Apache ECharts, Sigma.js + Graphology, Finder selector picker |
| §9 Acceptance criteria | §14 (criteria #1–#34) | Appends criteria #35–#48 |
| §10 List Mode, URL rewriting & migration redirect matrix | §5.1 (inputs), §5.2 (canonicalization), §5.4 (redirect chains), §8.2 (comparison surfaces), §8.7 (tools), §9.2 (schema), §10.2–§10.3 (REST, hub), §11.4 (MCP) | List-seed ingestion with bounded discovery; regex URL rewriting; staging↔production host mapping; `redirect_mappings` table; migration verdict findings; matrix REST routes, hub event, and MCP resource |
| §11 Server-log analyzer & true orphan discovery | §7.4 (taxonomy), §8.1 (aggregates), §8.2 (surfaces), §9.2 (schema), §2.6 (boundary), §10.2–§10.3 (REST, hub), §11.4 (MCP) | Offline streaming log ingestion; bot classification with bundled CIDR verification; crawl-vs-log correlation (true orphans, budget waste, uncrawled indexable); `log_imports` and `log_url_aggregates` tables; Log File Analyzer surface |
| §12 Local vector embeddings & semantic linking | §2.4 (vector-similarity capability), §9.2 (`url_embeddings` activation), §2.6 (loopback boundary), §7.4–§7.5 (findings), §8.1–§8.2 (aggregates, surfaces), §10.2–§10.3 (REST, hub), §11.4 (MCP) | Bundled ONNX or loopback-daemon embedding providers; semantic cannibalization, link opportunities, low-relevance links; `semantic_link_opportunities` and `semantic_clusters` tables |
| §13 AI-search (GEO) readiness & bot governance | §4.4 (robots alongside), §7.4 (taxonomy), §8.2 (surfaces), §8.7 (tools), §7.3 (inventory), §9.2 (schema), §10.2 (REST), §11.4 (MCP) | `llms.txt` / `llms-full.txt` discovery and validation; agent-profile governance matrix (search vs RAG vs training); `governance_matrix` table; llms.txt Generator & Validator tool |
| §14 MarTech, analytics & tag integrity | §5.6 (rendering), §5.9 (proxy capture), §7.3 (inventory), §7.4 (taxonomy), §8.2 (surfaces), §9.2 (schema), §10.2 (REST), §11.4 (MCP) | Tag/container inventory from DOM + proxy capture; `dataLayer` and Consent Mode v2 inspection; `Analytics & Tagging` category rules; `analytics_detections` table; Analytics & Tagging Explorer |
| §15 Contextual link weighting, SERP pixels & header equivalency | §7.3 (head inventory), §7.4 (taxonomy), §8.1 (link equity), §8.2 (surfaces), §9.2 (`link_edges`), §10.2 (REST), §11.4 (MCP); FS-CRAWL-002 §4 (scoping) | DOM-zone link classification and weighted PageRank; pixel-width title/description truncation with SERP preview; header-vs-HTML directive conflict findings; `link_edges.dom_zone` column |

Subsystem coverage check: §4 Limits & Politeness (modules §3, §10, §13; politeness §4.4), §5 Crawl Engine (modules §2, §3, §10, §14), §7 Audit Engine (modules §2, §4, §5, §11, §12, §13, §14, §15), §8 Reporting (modules §2, §4, §5, §6, §7, §8, §10, §11, §12, §13, §14, §15), §9 Data Model (modules §2, §3, §4, §5, §6, §10, §11, §12, §13, §14, §15), §10 REST/Hub Contract (modules §2, §3, §4, §5, §6, §7, §10, §11, §12, §13, §14, §15), §11 Local MCP Server (modules §2, §3, §5, §10, §11, §12, §13, §14, §15), §12 Desktop Shell (modules §6, §7, §8).

### 1.4 Precedence, compatibility, and invariants

1. **Exclusions prevail.** FS-CRAWL-001 §3 (Excluded Subsystems) prevails over every requirement in this document. No extension may introduce machine identity, licensing, accounts, remote bridging, remote feature flags, telemetry, server-gated functionality, or entitlement-based limits.
2. **Network boundary unchanged.** FS-CRAWL-001 §2.6 is not widened. Extensions add no new outbound traffic classes: signature and stop-word catalogs are bundled/local (FS-CRAWL-002 §2, §5); plugin network access is zero by default and only ever reaches user-granted destinations (FS-CRAWL-002 §6); memory telemetry never leaves the machine (FS-CRAWL-002 §7).
3. **Additive contract evolution only.** New tables and columns arrive through versioned, provider-agnostic migrations (§2.4, §9.4 pattern). New REST routes, hub events, and MCP resources are added alongside existing ones. Existing routes, events, and payloads keep their parent semantics; extensions add optional fields only, never rename or repurpose parent fields.
4. **Baseline-preserving defaults.** With no extension configuration, behavior matches FS-CRAWL-001 exactly: content scoping is inactive until selectors are configured (full visible text, as baseline); no plugins are installed; WAF detection and AIMD are correctness/politeness safeguards enabled by default (§2.5, §9.4) with local disable toggles; the N-gram category is enabled by default within mandatory bounds (FS-CRAWL-002 §5.6); virtualization and telemetry mandates (FS-CRAWL-002 §7) are unconditional.
5. **Limits resolution unchanged.** All extension parameters merge through the §4.1 priority order (per-audit override → project default → application default → built-in profile) and validate per §4.3. No code path consults any remote service or entitlement.
6. **Provider agnosticism.** Every new schema object is logical (§2.4, §9.1); both the embedded file-based and the optional external client-server provider implement it. Aggregate views are materialized as precomputed read models where the provider lacks native view support (§8.1 pattern).
7. **Scale invariant.** Every extension must hold at the §13 target scale (several hundred thousand URLs; §7 of this document raises the UI bar to 500,000+ rows at 60 FPS) with bounded memory (§5.8, §9.6 streaming patterns).
8. **Privacy invariant.** No identity, machine, or account dimensions appear in any new event, metric, log, or plugin surface (§3 rows 1 and 6; §10.5).
9. **Loopback-local services and offline ingestion (v1.1).** The v1.1 modules add no off-machine traffic. Log ingestion reads local disk only (FS-CRAWL-002 §11); embedding providers are a bundled offline model or a **user-configured loopback-only daemon** whose address validation rejects any non-loopback endpoint (FS-CRAWL-002 §12) — loopback connections stay inside the machine boundary and are user-owned local infrastructure, analogous to the user-configured external database pattern (§2.4) but never off-machine. The single optional exception is the **user-triggered, off-by-default DNS verification** of log bot hits (FS-CRAWL-002 §11.3): DNS queries only, explicit per-import consent, never automatic, locally logged; with it disabled the log module generates zero outbound traffic. Bundled bot CIDR ranges, agent profiles, and font-metric tables are packaged assets updated only with product updates (§13).

### 1.5 Conventions and terminology

- **shall / must** = mandatory; **should** = recommended; **may** = optional.
- Bare `§n` references are FS-CRAWL-001 sections; `FS-CRAWL-002 §n` or "this §n" references are local. Table, route, and event names quoted from the parent are exact.
- Schemas follow §9.1 conventions: surrogate integer keys, UTC timestamps, JSON text columns for flexible payloads, soft delete where the parent pattern applies.
- REST additions follow §10.1 conventions: loopback base URL, JSON bodies, uniform response envelope (data payload, optional pagination metadata, optional error object), offset/limit pagination, filtering and sorting on list endpoints.
- Finding severities use the parent vocabulary: **Error, Warning, Notice, Pass** (§7.5). New rule keys live in the parent taxonomy categories (§7.4) unless stated otherwise.
- **Scoped Visible Text Tree** (FS-CRAWL-002 §4.3): the derived, scope-filtered visible-text view of a parsed document. **Challenge page** (FS-CRAWL-002 §2): a WAF-served CAPTCHA, interstitial, or bot-block document. **Effective rate** (FS-CRAWL-002 §3): the AIMD-controlled per-host request rate currently applied by the scheduler.

### 1.6 Extension scope exclusions (negative requirements)

The following are out of scope for the extension set and shall not be implemented (inheriting §3):

| # | Excluded capability | Prohibited behavior |
|---|---|---|
| E1 | Remote plugin registry or marketplace | No plugin browsing, download, auto-update, or remote code loading of any kind; plugins are local user-authored artifacts only (FS-CRAWL-002 §6) |
| E2 | Remote signature/stop-word catalog services | No runtime fetch of WAF signatures, stop-word lists, or fingerprint updates; bundled assets update only with product updates; user additions are local (FS-CRAWL-002 §2.3, §5.2) |
| E3 | Challenge solving or bypass | No CAPTCHA solving, challenge replay, token forging, or evasion automation; detection and reporting only (FS-CRAWL-002 §2) |
| E4 | Cloud reputation or blocklist services | No consultation of remote IP/domain reputation services for WAF decisions; all verdicts derive from the local catalog and the observed response (FS-CRAWL-002 §2) |
| E5 | Plugin backend mutation | Plugins receive read-only data and UI contribution rights; they never invoke crawl lifecycle or configuration mutations (FS-CRAWL-002 §6.4) |
| E6 | New identity or entitlement concepts | No plugin licensing, accounts, or feature gating; the plugin system is free, local, and ungated (§3 rows 2, 3, 8) |
| E7 | Remote embedding / vector / LLM API services | No cloud embedding endpoints, no remote vector databases, no hosted LLM APIs; embeddings come only from the bundled offline model or a user-configured loopback daemon (FS-CRAWL-002 §12) |
| E8 | Remote log enrichment or bot-verification services | No remote IP-reputation, bot-list, or log-enrichment fetches; bot CIDR ranges and agent profiles are bundled assets; DNS verification is the only optional, user-triggered, off-by-default network action (FS-CRAWL-002 §11) |
| E9 | Remote GEO / llms.txt registries or publishing | No remote `llms.txt` registry, no auto-publishing of generated files, no AI-vendor submission endpoints; generation writes local files only (FS-CRAWL-002 §13) |

---

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

## 3. AIMD Auto-Adaptive Politeness Controller

**Extends FS-CRAWL-001 §4.2, §4.4, §5.3.** Additional hooks: §4.1, §4.3, §5.1, §5.4, §5.7, §9.2, §10.2, §10.3, §10.5, §11.4.

### 3.1 Problem and placement

Static token-bucket rate limits either under-utilize fast servers or overwhelm fragile origins before HTTP 429/503 errors appear. The **AIMD controller** operates **per host sub-queue (§5.3)**, between politeness spacing (§4.4) and worker dispatch. It continuously adapts the effective per-host request rate and concurrency from observed Time-To-First-Byte (TTFB) and response health, using Additive Increase / Multiplicative Decrease congestion control.

Placement invariants:

- The controller **never exceeds** the resolved URL throughput ceiling or worker concurrency ceiling (§4.2, resolved via §4.1). It formalizes the parent's "per-host concurrency is still bounded adaptively" clause (§4.4) for the unset-ceiling case.
- Robots `crawl-delay` directives (§4.4) remain an **inviolable floor** on request spacing, independent of AIMD.
- Token-bucket spacing enforcement (§4.4) is unchanged; AIMD sets the bucket's refill rate.
- Disabling AIMD (local toggle, per audit or globally) reverts exactly to parent static behavior.

### 3.2 Observables

- **TTFB** is measured for every fetch: time from request start to first response byte. This is an additive clarification of the §5.4 "timing" record; in rendering mode the document request's TTFB is used (consistent with §7.11).
- **Rolling baseline `B_h`** per host: exponentially weighted moving average of healthy-sample TTFB (smoothing factor λ, default 0.2), established over a warm-up of M samples (default 10). Samples that trigger a decrease never update the baseline; the baseline is frozen during cooldown.
- **Response health:** 2xx/3xx without a challenge match = healthy. HTTP 429/503 = congestion signals. A `waf-challenge` detection (FS-CRAWL-002 §2) = congestion signal. Other errors follow parent backoff (§4.4) and do not contribute to increase streaks.

### 3.3 Parameters

All parameters are local configuration, merged through the §4.1 priority order, overridable per audit via the §5.1 *Politeness options* input, and validated per §4.3 (invalid overrides rejected with a descriptive local error; a running audit keeps its snapshot).

| Parameter | Symbol | Built-in default | Semantics |
|---|---|---|---|
| AIMD enabled | — | On | Off → parent static politeness behavior |
| Initial rate | R₀ | 1 req/s | Conservative starting rate per host |
| Additive step | α | 0.5 req/s | Linear increase per qualified success window |
| Multiplicative factor | β | 0.5 | Immediate rate halving on congestion |
| Rate floor | R_min | 0.1 req/s | Effective rate never drops below (≈ 1 request / 10 s) |
| Adaptive ceiling | R_max-auto | 10 req/s | Ceiling used only when the resolved §4.2 throughput ceiling is Unlimited |
| Success streak | K | 5 | Consecutive healthy responses required per increase |
| TTFB absolute threshold | T_abs | 800 ms | Increase-eligibility ceiling on every streak sample |
| Latency multiplier | κ | 2.5 | Decrease trigger: TTFB > κ × rolling baseline |
| Cooldown | D | 10 s | No increases for this duration after a decrease |
| Other-5xx decrease | — | Off | Optional extension of decrease triggers to 5xx beyond 429/503 |

### 3.4 Control law (mathematical contract)

Let `R_h` be the effective rate and `C_h` the effective concurrency for host h. The following recurrences define the required behavior; concrete data structures are an implementation choice.

**Additive Increase.** When K consecutive responses from host h are healthy, every sample's TTFB ≤ T_abs, and no cooldown is active:

> R_h ← min(R_h + α, R_cap_h);  streak ← 0

where `R_cap_h` = the resolved §4.2 per-host throughput ceiling when set, else R_max-auto; additionally R_h ≤ 1 / crawl-delay whenever robots declares a crawl-delay (§4.4). When the §4.2 ceiling is global, the scheduler additionally ensures the sum of active host rates never exceeds it.

**Multiplicative Decrease.** Immediately on any trigger in { TTFB > κ × B_h; WAF soft-block detected (FS-CRAWL-002 §2); HTTP 429; HTTP 503 } (plus other 5xx when enabled):

> R_h ← max(β × R_h, R_min);  C_h ← max(1, ⌊β × C_h⌋);  streak ← 0;  cooldown ← D

**Retry-after interplay (§4.4):** an explicit retry-after hint pauses the host sub-queue for max(retry-after, D); the rate decrease still applies.

**Concurrency coupling:** C_h = clamp(⌈R_h × L_h⌉, 1, C_share_h), where L_h is the EWMA of full response latency and C_share_h is a work-conserving fair share of the §4.2 worker concurrency across active hosts (redistribution is an implementation choice). Invariant: Σ C_h ≤ the resolved worker concurrency ceiling.

**Cooldown expiry:** increases resume; the baseline re-warms from new healthy samples.

**Determinism:** given the same response sequence and parameters, the adjustment sequence is reproducible — a testability requirement exercised by criterion #36.

### 3.5 Lifecycle, checkpointing, and recovery

- Controller state per (audit, host): R_h, C_h, B_h, streak, cooldown-until, last-adjustment metadata.
- **Pause (§5.7):** state is serialized into the `crawl_checkpoints` payload (additive field, §9.2); **resume** rehydrates it exactly.
- **Crash recovery (§2.3, §2.7):** state is re-derived conservatively — hosts restart at R₀ with fresh warm-up. No scheduling state exists only in memory.
- **Completion:** final per-host state is summarized into `metric_summaries` under the `host-throttle` metric key (§8.1 pattern).

### 3.6 Persistence (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `host_throttle_events` | New table | Audit reference; host; timestamp; trigger (`additive-increase`, `latency-spike`, `waf-soft-block`, `http-429`, `http-503`, `retry-after`, `cooldown-expiry`, `manual-override`); rate before/after; concurrency before/after; TTFB sample; baseline at event | Bounded: latest N events per host per audit retained (default 500, configurable); pruned with retention (§9.5); index (audit, host, timestamp) |
| `crawl_checkpoints` | Additive payload field | Serialized per-host controller state | FS-CRAWL-002 §3.5 pause/resume |
| `metric_summaries` | New metric key | `host-throttle` (dimensions: host) | Final-state summary |

### 3.7 Telemetry and contracts (extends §10.2, §10.3, §10.5, §11.4)

- **New hub event `host-throttle`** (server → shell): audit identifier; host; trigger; rate before/after; concurrency before/after; TTFB sample; rolling baseline. Emission: on every adjustment, coalesced to at most one message per second per host (parent cadence discipline, §5.7).
- **`crawl-progress` additive fields:** throttled-host count and mean effective rate.
- **REST:** GET `/api/audits/{id}/hosts` — current per-host controller state and adjustment summaries; `includeHistory=true` adds recent events with pagination. POST `/api/audits/{id}/hosts/{host}/override` — pin a manual rate or clear the pin; a pinned host suspends AIMD until cleared and emits a `manual-override` event.
- **MCP resource `host-throttle`** (§11.4 address pattern): audit reference + optional host filter; read-only current state and recent adjustments. The `crawl-progress` resource gains the throttle summary fields.
- **Local metrics (§10.5):** additive families — per-host effective rate and concurrency gauges, adjustment counters by trigger. Host is a local technical dimension; no identity dimensions (§3 rows 1, 6).

### 3.8 UI requirements

- **Live per-host throttle panel** (report window): effective-rate sparkline with TTFB baseline overlay (Apache ECharts, FS-CRAWL-002 §8), trigger badges, and a virtualized adjustment log (FS-CRAWL-002 §7).
- **Manual pin control** per host, with clear visual state when AIMD is suspended for that host.
- **Audit configuration form:** AIMD parameter group with client-side validation mirroring §4.3 (Mantine forms, FS-CRAWL-002 §8).
- **Dashboard:** throttled-host count card during running audits.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #36.

---

## 4. Content Area Scoping and Boilerplate Stripping

**Extends FS-CRAWL-001 §7.2, §7.8, §7.9, §7.12, §7.16.** Additional hooks: §5.1, §7.3, §7.5, §8.2, §9.2, §9.6, §10.2.

### 4.1 Purpose and parent-contract gap

FS-CRAWL-001 §7.12 already states: *"All text metrics operate on content-area-scoped text when a content-area selector is configured (section 7.9)"*, and §7.16 targets *"visible text (content-area-scoped when configured)"*. The parent, however, defines custom extraction rules (§7.9) without a first-class scoping configuration. This module supplies that missing contract: **project-level and audit-level Include / Exclude Content Area selectors** and the **Scoped Visible Text Tree** they produce, applied before every text-sensitive pass.

### 4.2 Configuration model

- **Two configuration levels**, merged through the §4.1 priority order: an audit-level scope overrides the project-level default; an absent audit scope inherits the project default; no configured scope anywhere → baseline behavior (full visible text).
- A **content scope** consists of:
  - an ordered **Include list** of selectors,
  - an ordered **Exclude list** of selectors,
  - a per-entry **selector language**: CSS selector or XPath (declared per entry; both may be mixed within one scope).
- Example exclude entries (boilerplate): `nav`, `header`, `footer`, `aside`, `.cookie-banner`, `.mega-menu`, `[role=navigation]`, `[role=banner]`, `[role=contentinfo]`, `.sidebar`, `.newsletter-signup`, `.social-share`.
- **Bundled boilerplate preset:** a named, read-only preset containing the common boilerplate selectors above. Applying it is an explicit user action; nothing is stripped automatically, preserving baseline behavior (FS-CRAWL-002 §1.4 rule 4).
- **Evaluation semantics (deterministic):**
  1. Include list empty → the whole document body is the candidate region.
  2. Include list non-empty → the candidate region is the union of subtrees matched by include selectors, in document order.
  3. Exclude selectors are applied **after** inclusion, subtracting matched subtrees from the candidate region; **exclude wins** on any conflict.
  4. Nested matches are deduplicated: an ancestor match subsumes its descendants; text is never counted twice.
  5. The result preserves document order.
- **Validation and bounds:** selector syntax is validated at configuration time; invalid selectors are rejected with a descriptive local error (§4.3 pattern). Selector evaluation is time-bounded per page (the §7.9 bound philosophy); evaluation failures are isolated, logged as rule-level diagnostics, and never fail the audit. A live preview against a sample URL is provided before the crawl starts (mirroring the §7.9 rule-preview pattern).

### 4.3 Scoped Visible Text Tree

**Definition:** a derived, scope-filtered visible-text view of the parsed document, produced in the §7.2 pipeline **after DOM parsing (step 2) and before text-sensitive inventory and rule passes (steps 3–4)**.

Construction rules:

- The FS-CRAWL-002 §4.2 scope is applied to the parsed tree; in rendering mode (§5.6) it is applied to the **post-render** tree.
- Visibility extraction follows standard semantics: script, style, noscript, and template content is excluded; elements hidden by standard markup/attribute semantics are excluded; in rendering mode, computed visibility is used when available.
- The tree is a **derived view, not a persisted artifact**: it is re-derived from stored bodies (§9.6) on re-audit without network access.

**Consumers (all operate on the Scoped Visible Text Tree when a scope is configured):**

| Pass | Parent hook |
|---|---|
| Visible word count / text statistics | §7.3 (Text statistics group) |
| Flesch-class readability, grade level, reading time, text-to-HTML ratio | §7.12 |
| Spelling checks | §7.8 |
| Shingle near-duplicate detection | §7.5 |
| Custom content search — visible-text target | §7.16 |
| N-gram analysis — body and headings zones | FS-CRAWL-002 §5 |

**Non-consumers (unchanged by scoping):** document-head facts (title, meta, canonical, alternates), the full heading outline structure, link discovery (§5.5), anchors, images, structured data, resources, and tables (§7.3). Scoping affects **text metrics only**. The text-to-HTML ratio keeps the **full markup size as denominator** (scoped visible-text length relative to full document markup), so boilerplate removal raises the ratio as intended. Raw-markup search targets (§7.16) are never scoped.

### 4.4 Storage, diagnostics, and re-audit (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `content_scope_profiles` | New table | Project reference; profile name; include selectors JSON (entry: language, expression, order); exclude selectors JSON; preset reference (nullable); created/updated timestamps | Reusable project-level scopes; configuration data, not pruned by audit retention |
| `crawl_configurations` | Additive field | Content scope (inline override or profile reference) | §5.1 input set extension |
| `audits` | Additive snapshot field | Resolved content scope | Snapshot pattern (§9.2): later edits never mutate history |
| `url_elements` | New element group | `content_scope` payload: include-match count, exclude-match count, full word count, scoped word count, removed word count | Additive to the §7.3 inventory; no schema change (payload JSON) |
| `metric_summaries` | New metric key | `scoped-word-count` distribution | §8.1 aggregate pattern |

Re-audit (§7.2) re-derives scoped trees and all dependent metrics from the content store (§9.6) under the audit's snapshotted scope — no re-fetching. Changing the scope and re-auditing recomputes findings; deltas surface through audit comparison (§8.2).

### 4.5 Reporting behavior

- **URL explorer (§8.2):** scoped and full word-count columns; a scope-diagnostics panel in URL detail (matched regions, removed counts).
- **Finding evidence (§7.5):** text-based findings note the scoped basis in evidence so judgments are reproducible.
- **Content comparison (§8.2):** each audit uses its own snapshotted scope; the comparison surface labels which scope produced each side.
- **Custom extraction (§7.9):** extraction rules continue to run against the full parsed document (their own path scoping is unchanged); scoping and extraction are orthogonal.

### 4.6 REST and tool additions (extends §10.2, §8.7)

- POST `/api/projects/{id}/configurations` and PUT `/api/configurations/{id}` accept the additive `contentScope` field; PUT `/api/projects/{id}` accepts project-level scope defaults and profile references.
- **New tool endpoint:** POST `/api/tools/scope-preview` — input: target URL (or stored artifact reference), include/exclude selector lists; output: matched-region summary, word counts before/after, sample scoped text, per-selector match counts. A stateless member of the §8.7 tool family, also embedded in the single-page analyzer (§7.7).
- POST `/api/analysis/single-page` results include scope diagnostics when a scope is supplied.
- MCP `url-detail` resource gains the `content_scope` diagnostics payload (additive).

### 4.7 UI requirements

- **Scope editor** in project settings and audit configuration: include/exclude lists with per-entry CSS/XPath language toggle, drag reorder, preset chips, syntax validation, and live preview against a sample URL.
- **Point-and-click capture:** the Finder selector picker (FS-CRAWL-002 §8.6) sends captured selectors directly into the include or exclude list.
- Scope state is visible in the audit header ("Scoped: 2 include / 7 exclude") so reports are never silently misread.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #37.

---

## 5. Site-Wide N-Gram and Topical Density Analyzer

**Extends FS-CRAWL-001 §7.2, §8.2, §9.2, §10.2, §11.4.** Additional hooks: §7.3, §7.4, §7.5, §8.1, §8.4, §9.6, §13.

### 5.1 Purpose and pipeline placement

The analyzer extracts 1-gram, 2-gram, and 3-gram phrase frequencies across four distinct zones per URL, computes per-URL term frequency (TF) and site-wide document frequency (DF), and surfaces **keyword cannibalization** and **topical gaps** as findings and report surfaces.

Pipeline placement (§7.2):

- **Per-URL tokenization and counting** run in the per-URL pass (step 4) during incremental execution, over the same parsed document as the built-in inventory — no additional fetches.
- **Site-wide DF, cannibalization, and gap passes** are cross-URL aggregation passes (step 5) over indexed views, executed in full mode after completion.
- **Re-audit** recomputes everything from stored bodies (§9.6) without network access.
- URLs blocked as `waf-challenge` (FS-CRAWL-002 §2) and non-HTML records are excluded.

### 5.2 Tokenization and stop-word pipeline

Per page, in order:

1. **Language selection:** the page's declared language (§7.3 Text statistics); fallback to the user's preferred checking language — the same selection rule as spelling dictionaries (§7.8).
2. **Tokenization:** Unicode-aware word tokenization (letter/digit sequences); locale-aware handling of apostrophes and hyphens; case folding; configurable minimum token length (default 2); optional numeric-token exclusion.
3. **Sentence segmentation:** n-grams never cross sentence boundaries or zone boundaries.
4. **Stop-word filtering:** bundled per-language stop-word lists, shipped as offline locale assets (§13 packaging, the §7.8 dictionary pattern); never fetched remotely. **User stop-word additions** are stored locally per language (`user_stopwords` table) and applied alongside the bundled list. Stop words are removed **before** n-gram construction: contiguity is defined over the filtered token stream.
5. **N-gram construction:** contiguous 1-, 2-, and 3-grams within each zone (FS-CRAWL-002 §5.3), keyed by their normalized joined form.

### 5.3 Zones

Four distinct zones per URL, counted separately:

| Zone key | Source | Scoping |
|---|---|---|
| `body` | Scoped Visible Text Tree body text | FS-CRAWL-002 §4 scope applied |
| `headings` | H1–H6 text within the scoped tree | FS-CRAWL-002 §4 scope applied |
| `title` | Full document title | Never scoped (head fact) |
| `inbound-anchor` | Anchor text of internal link edges targeting the URL (`link_edges`, §9.2) | As recorded; scoping does not apply (anchors belong to source pages) |

### 5.4 Metrics

- **Raw count:** occurrences of an n-gram in a (URL, zone, order).
- **TF (term frequency):** count ÷ total n-grams of the same order in that zone for the URL (normalized 0–1).
- **DF (document frequency):** number of distinct URLs containing the n-gram — computed per zone and as an any-zone union.
- **Site-wide total frequency:** sum of raw counts across URLs.
- **Topical density:** TF × log(1 + N / DF), where N is the audit's analyzed-URL count — a TF-IDF-class weighting; the mathematical principle is specified, exact smoothing is an implementation choice.
- **Primary terms:** per URL, the highest-density 2-gram and 3-gram in the `title` zone and in H1 text (from `headings`), using configurable zone weights (defaults: title 2.0, H1 1.5).

### 5.5 Cannibalization and topical-gap detection

- **Keyword cannibalization:** cluster **indexable** URLs (indexability flags, §9.2 `url_records`; §7.4) whose primary 2-gram or 3-gram in Titles/H1s is identical after normalization. A cluster of ≥ 2 URLs emits a finding:
  - Rule key `content.keyword-cannibalization`, category *Content quality* (§7.4), severity **Warning**.
  - Evidence: shared n-gram, order, zone, competing URL list with per-URL TF, site DF, and canonical targets.
  - When every cluster member declares the same canonical target, severity downgrades to **Notice** (consolidation already signaled).
- **Topical gaps:** n-grams with inbound-anchor DF ≥ a configurable threshold (default 3) and zero DF in the `title` and `headings` zones site-wide — anchor demand without on-page supply. Emits rule key `content.topical-gap`, severity **Notice**, evidence: term, anchor DF, sample source URLs.
- Thresholds (minimum cluster size, minimum DF, minimum TF, indexable-only toggle) are local audit configuration; suppression follows §7.5.

### 5.6 Mandatory bounds and schema additions (extends §9.2)

Bounds (the §7.9 philosophy — excess is discarded and counted, never fatal):

- Per URL, per zone, per order: retain the **top K terms by count** (default 250, configurable); tail discards are counted and surfaced as diagnostics.
- Site summaries retain terms with DF ≥ 2 plus the top M terms by total frequency (default 100,000) — bounded storage at 500,000+ URL scale.

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `ngram_frequencies` | New table | Audit reference; URL reference; zone (`body`, `headings`, `title`, `inbound-anchor`); order (1, 2, 3); term text; raw count; TF | Unique (audit, URL, zone, order, term); indexes (audit, zone, order, term) and (audit, URL); pruned with retention (§9.5) |
| `ngram_site_summaries` | New aggregate view | Audit reference; zone; order; term; DF; total frequency; mean TF; primary-term URL count | Logical aggregate view, materialized as a precomputed read-model table on both providers (§2.4 command/query separation, §8.1 pattern) so explorer reads are constant-cost; index (audit, zone, order, DF descending) |
| `user_stopwords` | New table | Language; term; added-at timestamp | Configuration data; mirrors the `user_dictionary` pattern (§9.2); not pruned by audit retention |
| `metric_summaries` | New metric keys | `ngram-top` (dimensions: zone, order); `cannibalization-clusters`; `topical-gaps` | Dashboard cards and charts |

### 5.7 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| N-gram frequencies | GET `/api/audits/{id}/ngrams` | `scope=site\|url`; zone; order; term search; URL filter; min DF; sorting (TF, DF, frequency); pagination | Site-summary rows (`scope=site`) or per-URL rows (`scope=url`) |
| Cannibalization clusters | GET `/api/audits/{id}/ngrams/cannibalization` | Order; zone; minimum cluster size; pagination | Clusters: shared primary n-gram, competing indexable URLs with TF/DF/canonical evidence |
| Topical gaps | GET `/api/audits/{id}/ngrams/gaps` | Minimum anchor DF; pagination | Gap terms with anchor DF and sample source URLs |

Extended existing routes: POST `/api/exports` accepts an `ngrams` dataset selection (site summaries, per-URL frequencies, cannibalization clusters — §8.4 spreadsheet/CSV pattern); GET `/api/audits/{id}/charts/{metric}` accepts `ngram-top`; GET `/api/audits/{id}/findings` filters the new rule keys without contract change.

### 5.8 MCP additions (extends §11.4)

- **New resource `ngram-analysis`** (address pattern: resource kind + audit reference + optional filters): filters `scope`, `zone`, `order`, `term`, `urlId`, `minDf`, pagination. Content: site-summary rows, per-URL rows, and top cannibalization clusters — a bounded, read-only projection of the REST endpoints.
- **Optional new prompt `ngram-topical-briefing`** (parameter: audit reference): composes `ngram-analysis` with `audit-summary` into an agent-ready topical briefing; executes no side effects.
- The MCP boundary is preserved (§11.1): no stop-word or configuration management via MCP; `url-detail` gains top primary terms as an additive field.

### 5.9 UI requirements — N-Gram Explorer

A new report surface, additive to the §8.2 catalog:

- **N-Gram Explorer:** zone tabs (Body / Headings / Title / Inbound Anchor), order toggle (1/2/3), term search, sortable TF / DF / frequency / density columns, drill-down to the URLs carrying a term, and a per-URL n-gram panel in URL detail.
- **Cannibalization view:** cluster cards listing competing URLs with canonical hints and one-click navigation to the URL explorer filtered to the cluster.
- **Topical gaps view:** gap terms with anchor-demand evidence.
- **Charts:** top-terms bar charts, DF distribution histogram, topical treemap (Apache ECharts, FS-CRAWL-002 §8); chart filters propagate to the explorer (§8.3).
- The grid obeys the virtualization mandate (FS-CRAWL-002 §7) at site-summary scale.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #38.

---

## 6. Local UI Plugin Architecture and Lifecycle Hooks

**Extends FS-CRAWL-001 §8.2, §12.** Additional hooks: §2.3, §2.6, §3, §8.7, §9.2, §10.2, §10.3, §10.5.

### 6.1 Principles

Plugins extend the **frontend UI only**: custom report tabs, custom column formatters, and custom filter presets. They contain no backend domain logic, never touch the crawl pipeline, and never mutate backend state (FS-CRAWL-002 §1.6 E5). The system is local-only and sandboxed, with **zero external network access unless explicitly permitted by user destination rules**. There is no marketplace, registry, or remote code channel (FS-CRAWL-002 §1.6 E1).

### 6.2 Plugin directories and discovery

- **Per-user directory:** `%APPDATA%\CrawlForge\plugins\` on Windows; platform equivalents per §13 portability: `~/Library/Application Support/CrawlForge/plugins` (macOS), `~/.config/CrawlForge/plugins` (Linux).
- **Workspace-local directory:** `plugins\` at the workspace root; loaded only while that workspace/project is active.
- **Discovery:** scanned at shell startup. Insertion into the §2.3 startup sequence: after step 5 (main window loads, UI hydrates), the shell scans both directories, validates manifests, loads enabled plugins, and invokes `onLoad`. Plugin failures never block the main window or the backend. A manual **rescan** action exists in settings.
- **Structure:** each plugin is a directory containing a `plugin.json` manifest plus local module files (JS entry, optional HTML/CSS/assets). All code is local; remote code loading is prohibited.
- **Precedence and trust:** on plugin-id collision, the workspace-local plugin overrides the per-user one, with a visible UI warning. Workspace plugins require a one-time local trust prompt per workspace before first load.

### 6.3 Manifest contract (`plugin.json`)

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | Unique, lowercase, reverse-DNS-style identifier (new design artifact) |
| `name`, `version`, `description` | Yes | Display name; semantic version; short description |
| `author` | No | Free text; no identity verification of any kind |
| `engine.minAppVersion` | Yes | Minimum product version; incompatible plugins are disabled with a diagnostic |
| `entry` | Yes | Relative path to the local JS module |
| `styles` | No | Relative path to local CSS |
| `permissions.network` | No | `{ mode: "none" \| "destinations", destinationRefs: [...] }` — default `none`. `destinations` references user-configured export destinations (§8.5) or explicit user host-allowlist entries; every reference requires a user grant (FS-CRAWL-002 §6.4) |
| `contributes.tabs` | No | Declared report tabs: id, label, optional icon key |
| `contributes.columnFormatters` | No | Declared formatters: target explorer, column key (including dynamic extraction columns, §7.9) |
| `contributes.filterPresets` | No | Declared presets: name, query-builder rule tree (§8.7) |
| `localization` | No | Label map per UI language (§13 localization) |

Invalid manifests disable the plugin with a local diagnostic; they never fail shell startup. Manifests are validated against this contract before any module executes.

### 6.4 Sandbox and capability contract

- **Isolation:** plugin modules execute in an isolated renderer context (sandboxed frame with no same-origin access to the host UI, no Node integration, no filesystem access, no backend credentials). Communication with the host crosses a message bridge with schema-validated payloads only.
- **Host-exposed context API** (the entire capability surface):

| Capability | Contract |
|---|---|
| `app.getInfo()` | Product version, plugin id, UI language |
| `api.get(path, params)` | Read-only GET access to the loopback API (§10), restricted to a data-endpoint allowlist; no POST/PUT/DELETE; no lifecycle or configuration mutation (FS-CRAWL-002 §1.6 E5) |
| `hub.subscribe(eventKinds, handler)` | Host-relayed hub events (§10.3), coalesced per FS-CRAWL-002 §6.5; the plugin never opens its own socket |
| `ui.registerTab / registerColumnFormatter / registerFilterPreset` | Contribution registration per FS-CRAWL-002 §6.6 |
| `storage.get / storage.set` | Plugin-local key-value storage, bounded (1 MiB per plugin), persisted locally in a per-user plugin-state directory |
| `log.info / warn / error` | Local diagnostics only (§10.5 pattern); never transmitted |

- **Column formatters** are pure functions returning sanitized display descriptors (text, tooltip, badge/severity token, in-app link) — never raw HTML; the host renders descriptors, preventing injection.
- **Network enforcement:** the sandbox CSP blocks all connections by default. When the user grants declared destinations, plugin requests route through a host-enforced fetch allowlist matching exactly the granted destination rules (§8.5 destinations or explicit host entries). Non-granted requests are blocked and logged locally. Grants are persisted per plugin and revocable at any time; revocation takes effect immediately.
- **Integrity:** manifest and module file hashes are recorded at first enable; changed files trigger a re-consent prompt before the next load. No remote signing, no remote verification service.

### 6.5 Lifecycle hook contract (deterministic, exposed by the frontend state store)

| Hook | Trigger | Payload | Timeout | Failure behavior |
|---|---|---|---|---|
| `onLoad(context)` | Once at activation (shell startup or user enable) | The FS-CRAWL-002 §6.4 context object; registers tabs, column formatters, filter presets | 5,000 ms | Plugin quarantined with a local diagnostic; shell unaffected |
| `onTabActivate(auditId, filterState)` | User opens the plugin's registered tab | Current audit identifier; active filter state (query-builder-compatible, §8.7) | 3,000 ms | Tab shows a local error state; plugin remains loaded |
| `onDataUpdate(progressBatch)` | Throttled real-time stream during a crawl | Coalesced batch of `crawl-progress`, `url-batch`, `findings-updated` payloads (§10.3) | 1,000 ms budget | Batch dropped; never blocks the hub |
| `onCrawlComplete(auditSummary)` | Audit reaches `completed` (§5.7; `audit-completed` event, §10.3) | Audit summary — the dashboard-summary payload shape (GET `/api/audits/{id}/summary`) | 5,000 ms | Logged locally; never fails the audit |
| `onTabDeactivate` / `onUnload` (optional) | Tab closed / plugin disabled or shell shutdown | — | 2,000 ms | Cleanup skipped; host reclaims resources |

Determinism and isolation rules:

- Discovery order is sorted by (source precedence, plugin id); hooks execute in registration order; `onLoad` registrations complete before the tab strip is finalized. Identical inputs produce identical registration outcomes.
- `onDataUpdate` is delivered **at most once per second per plugin** (parent cadence discipline, §5.7), only while the plugin tab is visible or the plugin declared background interest at load; backpressure uses drop-oldest coalescing.
- `onCrawlComplete` fires exactly once per audit per plugin.
- An unhandled hook error disables the plugin for the session with a diagnostic entry; three consecutive failures quarantine it until the user re-enables. A plugin failure never crashes the shell, never affects a crawl, and never corrupts host state.

### 6.6 UI contribution contracts

- **Report tab:** appears in the report-window tab strip (§8.2, §12) with the manifest label; lazy-loaded on first activation; tabular content obeys the virtualization mandate (FS-CRAWL-002 §7).
- **Column formatter:** targets an explorer column (including dynamic custom-extraction columns, §7.9); the host invokes it per visible row only (virtualization-safe); descriptors are sanitized (FS-CRAWL-002 §6.4).
- **Filter preset:** a named query-builder rule tree (§8.7); applying it sets the explorer filter state; saving it persists through the existing `/api/queries` routes on explicit user action.

### 6.7 Registry, management, and REST (extends §9.2, §10.2, §12)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `ui_plugins` | New table | Plugin id; name; version; source kind (`user` \| `workspace`); directory path; enabled flag; permissions JSON (grants); manifest hash; installed-at; last-loaded-at | Local registry; configuration data, not pruned by audit retention |

- **Settings surface (§12):** plugin list with id, name, version, source directory, enabled toggle, granted permissions, diagnostics, open-folder, rescan, and remove actions; a per-plugin permission consent dialog enumerating declared destinations before any grant.
- **REST:** GET `/api/plugins` (registry + load state); PUT `/api/plugins/{id}` (enabled flag, permission grants/revocations); POST `/api/plugins/rescan`; DELETE `/api/plugins/{id}` (registry removal; files remain on disk for user deletion via open-folder).
- Plugin errors and hook failures are recorded in local diagnostics (§10.5 pattern), user-viewable and purgeable.

### 6.8 Security and privacy invariants

- No telemetry, no remote code, no identity dimensions (§3 rows 1, 6; FS-CRAWL-002 §1.6).
- Zero plugin network traffic without an explicit user grant; grants reach only user destination rules (§2.6 boundary preserved).
- Plugins cannot read host storage, backend credentials, or other plugins' state; the bridge is the only channel.
- Disabling a plugin unloads its module, revokes its hub relays, and removes its contributions immediately.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #39.

---

## 7. Real-Time Memory Profiling and UI Virtualization Mandates

**Extends FS-CRAWL-001 §10.3, §12, §13.** Additional hooks: §8.2, §10.1, §10.2, §10.5.

### 7.1 Strict DOM virtualization

**Mandate:** every tabular explorer renders **only visible viewport rows plus a small overscan buffer**. The mounted DOM row count shall never exceed **50 rows** (target band 30–50 including overscan), with row recycling. This applies to:

- URL explorer, Issue explorer, Custom extraction explorer (§8.2);
- **Link explorer** — formalized here as the first-class tabular surface over the parent link dataset (GET `/api/audits/{id}/links`, §10.2; inbound/outbound views in URL detail);
- **N-Gram Explorer** (FS-CRAWL-002 §5) and WAF detection lists (FS-CRAWL-002 §2);
- plugin report tabs with tabular content (FS-CRAWL-002 §6).

Performance and data-windowing contract:

| Requirement | Contract |
|---|---|
| Scroll performance | Sustained **60 FPS at 500,000+ rows** (p95 frame time ≤ 16.7 ms during continuous scrolling) on commodity desktop hardware (§13) |
| Server-side windowing | Rows fetched via offset/limit pagination (§10.1); default page size 200; resident client cache ≤ 20 pages (≈ 4,000 rows) with LRU eviction; total row count from pagination metadata; the full dataset is never held client-side |
| Row geometry | Estimated row heights with a bounded dynamic-measurement cache; scroll-position ↔ offset mapping stays stable under measurement updates |
| Horizontal virtualization | Wide dynamic-column datasets (§7.9 busiest-page expansion) render only visible columns plus overscan; pinned columns always mounted, pin count bounded (default ≤ 4) |
| Sorting & filtering | Multi-column sorting and filtering execute server-side (§10.2); "instant" filtering is debounced (150–300 ms); query-builder rules (§8.7) compose into the same server filters |
| Accessibility | Virtualization preserves keyboard navigation, focus retention, and ARIA row semantics (`aria-rowcount` / `aria-rowindex`) per §13 |
| Charts & graphs | No per-datum SVG DOM: canvas/WebGL rendering only (FS-CRAWL-002 §8.4, §8.5) |

### 7.2 Live status-bar telemetry

- **New hub event `system-health`** (server → shell), emitted **every 2 seconds** (small jitter permitted) whenever the shell is connected — during audits and while idle, independent of per-audit subscriptions. This extends §10.3 alongside the existing `crawl-progress` event, whose ≤ 1/s cadence (§5.7) is unchanged.

| Payload field | Source | Semantics |
|---|---|---|
| `timestamp` | Backend | UTC emission time |
| `backendWorkingSetBytes` | Backend OS counters | Backend process working-set RAM |
| `managedHeapBytes` | Backend runtime | Managed heap size under the mandated server-GC profile (§13) |
| `gcGen0/1/2Counts` | Backend runtime | GC generation collection counts, aggregated across server-GC heaps |
| `sqliteWalBytes` | Embedded provider | Write-ahead log file size (§2.4); null when the external provider is active |
| `auditId` (optional) | Backend | Present while an audit is running |

- **Frontend JS heap** is measured locally in the renderer at each heartbeat tick via the standard performance-memory API where available (otherwise reported as unavailable). It is composed into the status bar client-side and never sent to the backend.
- **Persistent bottom status bar** in the main hub and report windows (§12): backend working set, managed heap, GC generation counts, WAL size, frontend JS heap; crawl throughput appended while running. Advisory color thresholds are locally configurable (defaults: working-set advisory 2 GiB, WAL advisory 256 MiB); advisories never trigger automatic destructive actions.
- **Manual "Compact / Trim Working Set" action** (status-bar button and command palette, FS-CRAWL-002 §8.2): POST `/api/system/compact` performs managed GC compaction, a SQLite WAL checkpoint (truncate mode, embedded provider), and an OS working-set trim where the platform supports it. It returns before/after snapshots and the actions performed; is rate-limited (≥ 10 s between invocations); never interrupts an in-flight audit or transaction; and is logged locally.
- **On-demand snapshot:** GET `/api/system/health` returns the same payload as the heartbeat.

### 7.3 Metrics integration (extends §10.5)

The parent metric family "runtime memory and garbage-collection statistics" is formalized into: working-set bytes, managed-heap bytes, GC generation counters, and WAL-size gauges, exposed on GET `/metrics` in the standardized local exposition. Labels remain local technical dimensions only; no identity dimensions (§3 rows 1, 6). Telemetry never leaves the machine.

### 7.4 Frontend memory hygiene

- Hub ingestion uses bounded ring buffers (e.g. recent-activity ≤ 1,000 entries); counters are aggregated, never accumulated as raw event history.
- Charts dispose on unmount; graph models are windowed (FS-CRAWL-002 §8.5); window caches are LRU-bounded (FS-CRAWL-002 §7.1).
- Plugin `onDataUpdate` relays are coalesced and dropped under backpressure (FS-CRAWL-002 §6.5); listeners detach on tab deactivation.
- No surface retains full-dataset arrays; every large list is server-windowed.

### 7.5 Performance budgets

| Budget | Target |
|---|---|
| Tabular scrolling at 500k+ rows | 60 FPS, p95 frame ≤ 16.7 ms; mounted DOM rows ≤ 50 |
| Heartbeat cadence / overhead | Every 2 s; < 1% CPU overhead |
| Status-bar freshness | ≤ 2 s from backend sample to display |
| Compact action | < 5 s typical; before/after snapshot returned |
| Dashboard reads | < 1 s at target scale (parent §13, unchanged) |

**Acceptance hooks:** FS-CRAWL-002 §9, criteria #40 and #41.

---

## 8. Mandated Frontend Technology Stack

**Concrete architecture for FS-CRAWL-001 §12.** Additional hooks: §2.1, §2.2, §8.2, §8.3, §8.7, §13.

### 8.1 Renderer architecture

- The Electron renderer (§2.1) loads only the local UI bundle (§12). `contextIsolation` is enabled, Node integration is disabled in the renderer, and a minimal preload bridge exposes only shell duties (native file dialogs, backend port discovery per §2.2).
- **Component runtime:** React (required by the mandated Mantine component system), authored in a typed JavaScript superset; built by a local bundler (Vite-class) into an offline bundle. No remote content, no CDN, no remote fonts (§12).
- **State:** a single normalized frontend state store (Zustand-class lightweight store) with selector subscriptions; it is the contract surface for plugin lifecycle hooks (FS-CRAWL-002 §6.5). One WebSocket hub connection (§2.2) feeds an event demultiplexer with bounded ring buffers (FS-CRAWL-002 §7.4).
- **Routing:** local (hash/memory) routing only; window state persists per §12.

### 8.2 UI component system — Mantine UI (v7/v8)

- **Dark-first enterprise theme:** dark by default, light optional; persisted in `application_settings` theme (§9.2).
- **AppShell layout:** header (audit switcher, global search), navbar (projects, reports, tools, settings), and the persistent bottom status bar (FS-CRAWL-002 §7.2).
- **Dockable split panes:** resizable, dockable pane system for explorer + URL-detail side-by-side work; layouts persist per window (§12 window state). The concrete dock-manager implementation is an implementation choice within this contract.
- **Ctrl+K Spotlight command palette:** fuzzy search over navigation targets, audits, saved queries (§8.7), standalone tools (§8.7), settings actions, and plugin-registered commands (FS-CRAWL-002 §6); fully keyboard-operable (§13 accessibility).
- **Complex configuration forms:** audit configuration (§5.1), custom extraction rules (§7.9), content scope (FS-CRAWL-002 §4), AIMD parameters (FS-CRAWL-002 §3), WAF signatures (FS-CRAWL-002 §2), plugin permissions (FS-CRAWL-002 §6). Client-side validation mirrors §4.3; server field-level errors (§10.4) render inline.

### 8.3 Virtualized data grids — TanStack Table + TanStack Virtual (or AG Grid Community)

**Mandate:** TanStack Table + TanStack Virtual (headless pair, primary choice) **or** AG Grid Community with its server-side row model. Either implementation must satisfy the full capability checklist and the FS-CRAWL-002 §7.1 virtualization contract:

| Capability | Requirement |
|---|---|
| Scale | 1M+ rows via the windowed server-side row model (FS-CRAWL-002 §7.1); never a full client dataset |
| Column pinning | Left/right pinning with bounded pin count |
| Dynamic columns | Custom-extraction per-rule columns, multi-valued expansion to the busiest page (§7.9, §8.2) |
| Sorting | Multi-column sorting, executed server-side (§10.2) |
| Filtering | Instant (debounced) filtering; query-builder integration (§8.7) |
| Interaction | Row selection, column resize/reorder, density modes, keyboard grid navigation (§13) |
| Export handoff | Selection/filter state handed to the export flow (§8.4) |

### 8.4 High-density charting — Apache ECharts

- **Canvas rendering** (WebGL extension for very large series); **no SVG DOM** for large datasets — preventing DOM freezing at scale.
- Chart families: histograms (response time, word count, CWV distributions §7.11), treemaps (section treemap §8.2), score trends (§8.1 `audit_scores`), stacked bars (severity/category §8.1), line/time-series, pie/donut (§8.3).
- Data arrives pre-bucketed from GET `/api/audits/{id}/charts/{metric}` (§10.2); interactive chart filters propagate to the underlying explorers (§8.3).
- Theme synchronized with the Mantine dark-first palette; lazy initialization, dispose on unmount, resize-observer driven (FS-CRAWL-002 §7.4).

### 8.5 Network and link graph visualization — Sigma.js + Graphology

- **WebGL force-directed rendering** (ForceAtlas2-class layout) for the internal-link network graph and crawl-depth map (§8.2 Visualizations); layout computation runs in a Web Worker so the UI thread never blocks.
- Data source: GET `/api/audits/{id}/graph` (§10.2) with graph type and depth/size limits.
- **Level-of-detail budget:** default render cap 20,000 nodes / 60,000 edges (configurable); beyond the cap the server aggregates/samples and the UI shows a "showing X of Y" banner. Graphology holds the bounded in-memory graph model.
- Node sizing by link-equity percentile (§8.1); coloring by status class or depth; hover/selection synchronized with the URL explorer; depth-map variant uses a hierarchical/radial layout.

### 8.6 Visual CSS/XPath selector picker — Finder (`@medv/finder`)

- Embedded into the **Single-Page Analyzer (§7.7)** and the **Render Tester (§8.7)**: the user points and clicks any rendered DOM element to auto-generate a **resilient CSS selector** (shortest unique path with class/id optimization); an **XPath** alternative is generated deterministically on request.
- Interaction: hover highlight overlay, click to capture, live match-count preview, resilience options (prefer stable ids/attributes; avoid positional fragility where a unique alternative exists).
- **"Send to" actions:** captured selectors flow directly into a Custom Extraction rule (§7.9) or a Content Area Scoping include/exclude list (FS-CRAWL-002 §4).
- The picker operates inside the sandboxed embedded preview (§12: loads only the crawled target) against the post-render DOM, communicating through the message bridge; it works offline against stored rendered artifacts (§9.6).

### 8.7 Stack summary

| Concern | Mandated library / class | Role | Parent hook |
|---|---|---|---|
| Component system | Mantine UI v7/v8 (React) | Dark-first shell, dockable panes, Ctrl+K palette, forms | §12, §8.2 |
| Data grids | TanStack Table + TanStack Virtual, or AG Grid Community | 1M+ row virtualized explorers, pinning, dynamic columns | §8.2, §7.9 |
| Charts | Apache ECharts (Canvas/WebGL) | Histograms, treemaps, CWV distributions, score trends | §8.3, §7.11, §8.1 |
| Link graphs | Sigma.js + Graphology | WebGL force-directed link network, depth maps | §8.2 |
| Selector picker | Finder (`@medv/finder`) | Point-and-click resilient CSS/XPath generation | §7.7, §8.7, §7.9 |
| State store | Zustand-class normalized store | Selector subscriptions; plugin hook surface | §12, FS-CRAWL-002 §6 |
| Shell | Electron (contextIsolation, no renderer Node) | Window host, local bundle, preload bridge | §2.1, §2.2 |

### 8.8 State store contract (plugin hook foundation)

Normalized slices: settings, projects, audits, windowed URL/finding/link/n-gram pages, host-throttle state, system-health, plugin registry. Selector subscriptions drive rendering; the hub demultiplexer updates slices immutably; the plugin bridge (FS-CRAWL-002 §6.4) relays coalesced read-only slices and hub events. No slice ever holds a full dataset (FS-CRAWL-002 §7.4).

### 8.9 Offline, packaging, and licensing mandates

- All libraries and assets bundle locally; no CDN, no remote fonts or icons (local icon set); CSP `default-src 'self'` with exactly two exception classes: the sandboxed crawl preview (§12) and user-granted plugin destinations (FS-CRAWL-002 §6.4).
- Permissive open-source license compliance; a local attribution file ships with the bundle.
- The §13 packaging asset manifest gains: UI bundle, local icon set, attribution file, bundled WAF signature catalogue (FS-CRAWL-002 §2.3), and per-language stop-word lists (FS-CRAWL-002 §5.2).
- Every new surface is localizable through the parent localization framework (§13); plugin labels localize via manifest maps (FS-CRAWL-002 §6.3).

---

## 9. Additional Acceptance Criteria

**Appends to FS-CRAWL-001 §14** (which ends at criterion #34). Criteria #35–#48 below are numbered continuously with the parent list and are mandatory for the extended product. Criteria #35–#42 cover the v1.0 modules (§§2–8); criteria #43–#48 cover the v1.1 modules (§§10–15).

35. **WAF 200 OK detection.** Against a test origin serving a challenge page with provider markers under HTTP 200 OK, the URL is recorded `blocked` with sub-classification `waf-challenge` and the correct provider name — never `fetched`; no thin-content or duplicate-title finding is emitted for it; the dedicated WAF-Block diagnostic alert appears; detection runs fully offline from the bundled catalogue; re-audit re-detects from the stored body without network access; a clean 200 page matching only a single generic title phrase is not blocked (corroboration guard, FS-CRAWL-002 §2.3.3).
36. **AIMD rate adaptation.** Against a simulated origin, consecutive healthy responses below the TTFB threshold raise the effective per-host rate by α up to the resolved §4.2 ceiling (never above it; robots crawl-delay always honored); a TTFB spike above κ × rolling baseline, a WAF soft-block, or an HTTP 429/503 immediately halves the rate (× β) down to the floor; `host-throttle` adjustments stream over the hub with trigger codes; pause/resume preserves controller state; disabling AIMD reverts exactly to static parent behavior.
37. **Boilerplate scoping.** Configured Include/Exclude selectors (CSS and XPath) produce a Scoped Visible Text Tree; word counts, Flesch readability, spelling, shingle near-duplicate detection, custom content search, and N-gram body/headings zones all operate on scoped text; head facts and link discovery are unchanged; invalid selectors are rejected at configuration time; re-audit re-derives scoped metrics from the content store without network access; an unconfigured project reproduces baseline results exactly.
38. **N-gram cannibalization detection.** Two indexable URLs sharing an identical primary 2-gram/3-gram in Titles/H1s produce a cannibalization finding with cluster evidence; `ngram_frequencies` and `ngram_site_summaries` are populated with per-zone TF and site-wide DF; GET `/api/audits/{id}/ngrams` and GET `/api/audits/{id}/ngrams/cannibalization` return the data; the MCP `ngram-analysis` resource reads it; per-language stop-word filtering is honored; mandatory bounds discard-and-count without failing the audit.
39. **Local plugin lifecycle execution.** A local plugin with a valid manifest loads at shell startup; `onLoad` registers a report tab, a column formatter, and a filter preset; `onTabActivate` fires with the audit identifier and filter state; `onDataUpdate` arrives coalesced at most once per second during a crawl; `onCrawlComplete` fires exactly once when the audit reaches `completed`; network access is zero without a grant and restricted to user destination rules with one; a failing plugin is isolated and never crashes the shell or affects the audit.
40. **60 FPS virtual scrolling at 500k rows.** With 500,000+ URL rows, the URL explorer (and the Issue, Link, Custom Extraction, and N-Gram explorers) mount at most 50 DOM rows and sustain 60 FPS scrolling (p95 frame time ≤ 16.7 ms); multi-column sorting, instant filtering, column pinning, and dynamic extraction columns remain functional; keyboard navigation and ARIA row semantics are preserved.
41. **Live memory status-bar telemetry.** A `system-health` heartbeat arrives every 2 seconds reporting backend working-set RAM, managed heap size, GC generation counts, and SQLite WAL file size; frontend JS heap usage appears in the persistent bottom status bar; the manual Compact / Trim Working Set action reduces the working set and checkpoints the WAL, reporting before/after snapshots; the same families appear on GET `/metrics`; no telemetry is transmitted off the machine.
42. **Point-and-click CSS selector generation.** In the Single-Page Analyzer and Render Tester, clicking a rendered element generates a resilient CSS selector (with an XPath alternative) and a correct match-count preview; captured selectors can be sent into a Custom Extraction rule and into Content Area Scoping include/exclude lists; the preview sandbox loads only the crawled target; generation works offline against stored rendered artifacts.
43. **List Mode & Redirect Migration Matrix.** A `.txt`, `.csv`, or clipboard URL list ingests as seeds with discovery source `list`; `list-only` crawls at depth 0 with link discovery disabled, and `list-bounded` skips off-list discoveries; a two-column migration matrix fetches every Source_URL, records the full hop chain, and emits the correct verdict among `redirect-matrix.pass`, `redirect-matrix.target-mismatch`, `redirect-matrix.chain-exceeded`, `redirect-matrix.final-non-200`, and `redirect-matrix.canonical-mismatch` against fixtures; regex rewrite rules transform canonicalization deterministically with invalid patterns rejected; staging↔production host mapping aligns audit and content comparison by path.
44. **Offline Server Log import & correlation.** Apache/Nginx Combined, IIS W3C, and Cloudflare JSON/CSV logs (plain and gzip) import from local disk with zero network calls; bot families classify with offline CIDR verification and DNS verification occurs only on explicit user trigger; correlation against an audit produces True Orphans (search-bot hits never discovered by the crawl), Crawl Budget Waste (bot hits on non-200/non-indexable/parameter-trap URLs), and Uncrawled Indexable Pages (indexable URLs with zero search-bot hits); the Log File Analyzer surface is virtualized and the MCP `log-correlation` resource reads the same data.
45. **Local embeddings & semantic internal linking.** The bundled ONNX model or a loopback daemon populates `url_embeddings` (non-loopback endpoints are rejected at configuration); semantic cannibalization fires at cosine ≥ 0.88; link opportunities in the 0.75–0.87 band exclude pairs already linked in `link_edges` and include anchor candidates from the target's top N-grams; low-relevance existing links are flagged below the floor; provider failure never fails the audit; no off-machine traffic occurs.
46. **`llms.txt` validation & AI governance matrix.** `/llms.txt` and `/llms-full.txt` are probed at crawl start alongside robots.txt; structural validation and broken-link findings are emitted; the governance matrix evaluates robots.txt, `X-Robots-Tag`, and meta directives across the bundled agent profiles and reports search-indexing vs AI-RAG-citation vs LLM-training verdicts per path prefix/URL; the Generator & Validator tool produces a standards-conformant `llms.txt` from top-scoring indexable pages grouped by directory, exported locally only.
47. **Analytics, GTM/GA4 & Consent Mode auditing.** Tag/container inventory (GTM, GA4, Google Ads, Meta Pixel, LinkedIn Insight, Clarity, custom patterns) is extracted from DOM parsing and local proxy capture; in rendering mode the post-render `dataLayer` snapshot and Consent Mode v2 defaults/updates (`ad_storage`, `analytics_storage`, `ad_user_data`, `ad_personalization`) are captured and correlated with `gcs`/`gcd` hit parameters; `analytics.missing-container`, `analytics.duplicate-container`, `analytics.datalayer-malformed`, and `analytics.consent-mode-missing` fire correctly on fixtures; the Analytics & Tagging Explorer and MCP `analytics-coverage` resource present the results.
48. **Zone-weighted equity, SERP pixels & header equivalency.** Every `link_edge` carries a `dom_zone` classification; weighted link equity differs from unweighted according to configured zone weights with in-content links weighted highest; pixel-width measurement emits `title-pixel-truncation` and `description-pixel-truncation` against configured budgets with a live SERP & Social Card Preview; `X-Robots-Tag`/`Link` header vs HTML conflicts emit `indexability.header-html-conflict`, `canonical.header-html-conflict`, and `hreflang.header-html-conflict` as Error findings with dual evidence.

---

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

## 11. Local Server Log File Analyzer & True Orphan Discovery

**Extends FS-CRAWL-001 §7.4, §8.1, §8.2, §9.2.** Additional hooks: §2.6, §2.7, §5.2, §9.5, §10.2, §10.3, §11.4, §12, §13.

### 11.1 Purpose and network boundary

Import server access logs from local disk and correlate bot traffic against crawl results to reveal **true orphans**, **crawl-budget waste**, and **uncrawled indexable pages**. Ingestion, parsing, classification, and correlation are **100% offline — zero network calls**. The sole optional exception is user-triggered DNS verification (FS-CRAWL-002 §11.3), off by default, disclosed and consented per import (FS-CRAWL-002 §1.4 rule 9).

### 11.2 Formats and streaming ingestion

- Supported formats: **Apache/Nginx Combined**, **IIS W3C** (header-driven field mapping), **Cloudflare JSON/CSV export files** (user-provided files, never API pulls). Format auto-detection with manual override.
- Plain or **gzip-compressed** files (additional codecs an implementation choice); streamed line-by-line with bounded memory (the §5.8 streaming philosophy) — file size is bounded only by disk.
- Malformed lines are counted and skipped with a bounded error sample; timestamps normalized to UTC; the user selects the **analysis time window**; per-format field mapping is configurable.
- Imports run as a background service (§2.7 pattern) with hub progress (FS-CRAWL-002 §11.8); an import never blocks or fails a crawl.

### 11.3 Bot verification and classification

- **Bundled, versioned user-agent pattern catalogue** (the §7.15 asset pattern): families **Googlebot Smartphone, Googlebot Desktop, Bingbot, OAI-SearchBot, GPTBot, ClaudeBot, PerplexityBot, Applebot**, plus `other-bot`, `human`, `unknown` buckets. Each family carries a purpose class: `search`, `ai-search`, or `ai-training` (shared vocabulary with FS-CRAWL-002 §13).
- **Verification tiers:**
  1. `claimed` — user-agent pattern match only.
  2. `cidr-verified` — offline match of the hit's IP against **bundled published CIDR ranges** per family.
  3. `dns-verified` — optional **user-triggered** reverse-then-forward DNS confirmation.
- Bundled CIDR ranges are packaged assets updated only with product updates; user-local CIDR additions persist in `application_settings` (JSON). No remote fetch of ranges or reputation data (FS-CRAWL-002 §1.6 E8).
- DNS verification: explicit per-import consent flag required; bounded sample size; DNS queries only (system/user-configured resolver); results logged locally; never automatic.

### 11.4 Aggregation

Per (import, canonical URL): total hits; hits by bot family; search-bot hits; status-class distribution; first/last seen; bytes served when present. URL canonicalization joins through §5.2 under the target audit's query policy. Aggregates persist in `log_url_aggregates` (FS-CRAWL-002 §11.6), indexed for correlation and virtualized browsing; pruned with retention (§9.5).

### 11.5 Crawl-vs-log correlation (extends §7.4, §8.1)

Join log aggregates against `url_records` (§9.2) for a chosen audit of the project. Three correlation views, each recomputable on demand against any project audit (indexed read-model join):

| View | Definition | Finding |
|---|---|---|
| **True Orphans** | URLs with search-bot hits in the window that were **never discovered** by the crawl (absent from the seen-set) | `logs.true-orphan`, **Warning** — evidence: hit count, families, sample timestamps; scope-filtered to project hosts, out-of-scope URLs reported separately |
| **Crawl Budget Waste** | Search-bot hit frequency ≥ threshold (default 10) on non-200 URLs, parameter traps (high query-variant counts), or non-indexable pages | `logs.crawl-budget-waste`, **Warning** — evidence: status mix, indexability, variant count |
| **Uncrawled Indexable Pages** | Indexable URLs found in the crawl with **zero** search-bot hits in the window | `logs.uncrawled-indexable`, **Notice** — evidence: crawl depth, inlink count; window-length caveat surfaced |

- All three rule keys live in a new additive category **Log Intelligence** (additive to the §7.4 taxonomy); suppression follows §7.5.
- Aggregates: `metric_summaries` keys `log-true-orphans`, `log-budget-waste`, `log-uncrawled-indexable`, `log-bot-hits` (dimensions: family, verification tier).

### 11.6 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `log_imports` | New table | Project reference; file name; format; compression; size bytes; lines parsed/accepted/rejected; window start/end; verification mode; status (`importing` \| `ready` \| `failed`); imported-at | Index (project, imported-at); pruned with project retention (§9.5) |
| `log_url_aggregates` | New table | Import reference; canonical URL; host; total hits; search-bot hits; hits-by-family JSON; status-class counts JSON; first/last seen; bytes | Unique (import, canonical URL); indexes (import, host), (import, search-bot hits descending); pruned with the import |

### 11.7 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Import log file | POST `/api/projects/{id}/logs/import` | File upload or local path reference; format; time window; verification mode | Import identifier (immediate; progress over hub) |
| List imports | GET `/api/projects/{id}/logs` | Pagination | Import records with status and counters |
| Purge import | DELETE `/api/logs/{id}` | — | Confirmation; aggregates removed transactionally |
| Correlation | GET `/api/audits/{id}/logs/correlation` | Import reference; `view=summary\|orphans\|waste\|uncrawled`; filters; pagination | Correlation rows with evidence |
| DNS verification | POST `/api/logs/{id}/verify-dns` | Explicit consent flag; bounded sample size | Verification results per sampled IP |

**New MCP resource `log-correlation`** (audit reference; import reference; view; pagination): read-only projection of the correlation endpoint. Import management is not exposed via MCP (§11.1 boundary).

### 11.8 Hub additions (extends §10.3)

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| `log-import-progress` | Server → shell | Import identifier; lines parsed/accepted/rejected; phase | At most once per second while importing |
| `log-import-completed` | Server → shell | Import identifier; final counters; correlation summary counts | Once per import finalization |

### 11.9 UI requirements — Log File Analyzer

A new report surface, additive to the §8.2 catalog:

- Import manager: drag-drop / native file dialog, format auto-detect, window picker, verification-tier badges, progress display.
- Correlation tabs (True Orphans / Budget Waste / Uncrawled Indexable), each virtualized per FS-CRAWL-002 §7.1, with evidence drill-down to URL detail.
- Bot-family breakdown charts and hit-timeline histogram (Apache ECharts, FS-CRAWL-002 §8.4).
- DNS verification action with an explicit consent dialog showing exactly what will be queried.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #44.

---

## 12. Local Vector Embeddings & Semantic Internal Linking Pipeline

**Activates FS-CRAWL-001 §2.4 (vector-similarity capability) and §9.2 (`url_embeddings`).** Additional hooks: §2.6, §2.7, §5.8, §7.4, §7.5, §8.1, §8.2, §9.6, §10.2, §10.3, §11.4, §13.

### 12.1 Purpose — activating the dormant schema

FS-CRAWL-001 §2.4 mandates vector-similarity search as a required database capability, and §9.2 defines `url_embeddings` (audit reference; URL reference; embedding payload; model tag) — but the parent specifies no pipeline that populates or queries it. This module is that pipeline: **strictly local** embedding generation and three semantic audit passes over the resulting vectors.

### 12.2 Embedding providers (strictly local)

| Provider class | Contract |
|---|---|
| **Bundled local ONNX runtime model** | Packaged model asset (§13 packaging manifest); offline CPU/GPU execution; model tag and vector dimension recorded with every embedding; no network of any kind |
| **Loopback local LLM/embedding daemon** | Ollama / LM Studio-class daemon at a **user-configured loopback endpoint** (e.g. `http://127.0.0.1:11434` or any configurable loopback port). Configuration validation **rejects any non-loopback address** (§4.3 pattern). Connection-test action mirrors the §8.5 / §2.4 test-connection pattern. The daemon is user-owned local infrastructure; no API keys to remote services, no cloud fallback |

- Provider selection lives in settings (`application_settings`); the active provider and model tag are snapshotted per audit.
- If the provider is unavailable, semantic passes are marked **unavailable** with a local diagnostic; audits never fail because of embeddings.
- No remote embedding API, remote vector store, or hosted LLM service is permitted (FS-CRAWL-002 §1.6 E7; §2.6 boundary preserved — loopback stays on-machine, FS-CRAWL-002 §1.4 rule 9).

### 12.3 Input source and chunking

- Per **indexable** URL, the embedding input is the **Scoped Visible Text Tree (FS-CRAWL-002 §4.3) + Title + H1**, assembled through a deterministic template (title, H1, then scoped body).
- Bounded chunking: maximum chunk length with optional overlap (implementation choice within bounds); long documents produce multiple chunks whose vectors are pooled by a deterministic rule (mean pooling specified as the default).
- **Incremental re-embedding:** on re-audit, only URLs whose stored content hash changed (`content_artifacts`, §9.2) are re-embedded; the pass is idempotent and resumable.
- Mixing model tags within one audit is prohibited: changing the model requires a full re-embed of that audit's vectors.

### 12.4 Storage and query

- `url_embeddings` (§9.2) is populated in transactional batches (§5.8); the embedding payload is the provider's vector serialization, the model tag identifies provider + model + dimension.
- Similarity metric: **cosine similarity**, executed through the database provider's vector-similarity capability (§2.4) on both providers; nearest-neighbour queries are indexed and bounded (top-K).

### 12.5 Semantic audit passes

| Pass | Definition | Deliverable |
|---|---|---|
| **Semantic cannibalization** | Pairs/clusters of indexable URLs whose cosine similarity ≥ a configurable high threshold (default **0.88**); clustering over the similarity graph (connected-components-class; implementation choice) | Finding `content.semantic-cannibalization`, category *Content quality* (§7.4), severity **Warning** (**Notice** when all members share one canonical target); evidence: similarity scores, cluster members, canonical hints. Complements exact N-gram cannibalization (FS-CRAWL-002 §5.5); when both fire for the same URL set, evidence cross-references the other pass |
| **Automated internal link opportunities** | For each indexable URL, top-K semantically related pages (default K = 10) within a configurable similarity band (default **0.75–0.87**) that **do not currently share a link** in `link_edges` (§9.2) in either direction | Persisted recommendations: source URL, target URL, similarity score, and **candidate anchor phrases drawn from the target's top N-grams** (FS-CRAWL-002 §5); report dataset + export, not findings by default |
| **Semantic relevance of existing links** | Internal edges whose source/target cosine similarity falls below a configurable low-relevance floor (default **0.35**) | Finding `linking.low-relevance-link`, category *Linking* (§7.4), severity **Notice**; evidence: similarity, anchor text, DOM zone (FS-CRAWL-002 §15) |

### 12.6 Execution and bounds

- Runs as a background service (§2.7 pattern) after crawl completion or during incremental idle; bounded concurrency; provider request timeouts; per-URL failures isolated, counted, and surfaced as diagnostics — never fatal (§7.9 bound philosophy).
- Progress streams over the hub (FS-CRAWL-002 §12.9); the job is resumable via content-hash comparison (FS-CRAWL-002 §12.3).
- Opportunity output is bounded: top-K per source URL; excess discarded and counted.

### 12.7 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `url_embeddings` | **Activated** (parent table) | Audit reference; URL reference; embedding payload; model tag | Populated and queried per FS-CRAWL-002 §12.4; no schema change |
| `semantic_link_opportunities` | New table | Audit reference; source URL reference; target URL reference; similarity; anchor candidates JSON; computed-at | Unique (audit, source, target); indexes (audit, source), (audit, similarity descending); pruned with retention (§9.5) |
| `semantic_clusters` | New table | Audit reference; cluster reference; URL reference; similarity to cluster centroid; primary-member flag | Index (audit, cluster); pruned with retention (§9.5) |
| `audits` | Additive snapshot fields | Embedding provider, model tag, thresholds (cannibalization, band, floor) | Snapshot pattern (§9.2) |
| `metric_summaries` | New metric keys | `semantic-clusters`; `semantic-opportunities` | Dashboard cards |

### 12.8 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Run / re-run embeddings | POST `/api/audits/{id}/semantics/run` | Provider override (optional); force re-embed flag | Job accepted (immediate; progress over hub) |
| Similar pages | GET `/api/audits/{id}/semantics/similar` | URL reference; top-K; minimum similarity | Ranked similar URLs with scores |
| Link opportunities | GET `/api/audits/{id}/semantics/link-opportunities` | Source filter; similarity band; pagination | Source, target, similarity, anchor candidates |
| Semantic clusters | GET `/api/audits/{id}/semantics/clusters` | Minimum cluster size; pagination | Clusters with members and centroid similarities |
| Provider test | POST `/api/settings/embedding/test` | Provider configuration | Reachability + model/dimension probe (loopback validation enforced) |

**New MCP resource `semantic-link-opportunities`** (audit reference; source filter; band; pagination): read-only projection of the opportunities endpoint. **Optional new prompt `semantic-topical-briefing`** (audit reference): composes `semantic-link-opportunities` with `audit-summary`; no side effects. Provider configuration is not exposed via MCP (§11.1 boundary).

### 12.9 Hub additions (extends §10.3)

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| `embedding-progress` | Server → shell | Audit identifier; embedded count; total eligible; provider; phase | At most once per second while running |
| `embedding-completed` | Server → shell | Audit identifier; final counts; pass summaries (clusters, opportunities, low-relevance links) | Once per job finalization |

### 12.10 UI requirements

A new **Semantic** report surface, additive to the §8.2 catalog:

- Link-opportunities grid (source, target, similarity, anchor candidates), virtualized per FS-CRAWL-002 §7.1, with one-click navigation to source/target URL detail.
- Cluster view with a similarity-edge graph overlay (Sigma.js + Graphology, FS-CRAWL-002 §8.5) under the same LOD budgets.
- Per-URL "semantically related" panel in URL detail; cannibalization findings cross-linked to the N-gram cannibalization view (FS-CRAWL-002 §5.9).
- Provider settings with connection-test feedback and explicit loopback-only validation errors.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #45.

---

## 13. AI Search (GEO) Readiness, `llms.txt` Validator & Bot Governance Matrix

**Extends FS-CRAWL-001 §4.4, §7.4, §8.2, §8.7.** Additional hooks: §5.1, §5.3, §7.3, §9.2, §10.2, §11.4.

### 13.1 `llms.txt` and `llms-full.txt` discovery and validation

- **At crawl start, alongside the robots.txt fetch (§4.4):** probe `/llms.txt` and `/llms-full.txt` on each in-scope host. Both are recorded as ordinary `url_records` (§9.2) with their status and content type.
- **Structural validation** of a retrieved `llms.txt`: H1 title present; blockquote summary present; H2 sections containing link lists. Structural failures emit `geo.llms-txt-malformed` (**Warning**) with per-element evidence.
- **Linked documentation/markdown URLs:** every link in the file is enqueued as a discovered URL (§5.3) when in scope and robots-allowed, otherwise verified for status; broken or non-200 targets emit `geo.llms-txt-broken-link` (**Warning**) per target.
- **Missing files:** `geo.llms-txt-missing` (**Notice** — the convention is optional); an absent `llms-full.txt` while `llms.txt` exists is a Notice annotation.
- All `geo.*` rule keys live in a new additive category **AI Search (GEO)** (additive to the §7.4 taxonomy); suppression follows §7.5.

### 13.2 AI crawler governance matrix

- **Bundled, versioned agent-profile catalogue** (the §7.15 asset pattern), each profile carrying an agent token and a purpose class:

| Agent | Purpose class |
|---|---|
| `Googlebot`, `Bingbot` | `search-index` |
| `OAI-SearchBot`, `PerplexityBot` | `ai-rag-citation` |
| `GPTBot`, `ClaudeBot`, `Google-Extended`, `CCBot`, `Applebot-Extended` | `llm-training` |

- **Inputs evaluated per agent:** robots.txt rules (the §4.4 parser), `X-Robots-Tag` response headers (including agent-specific directives and `noai` / `noimageai`), and HTML `<meta name="robots">` plus agent-specific meta tags (§7.3).
- **Evaluation:** for each agent × path prefix (prefixes derived from robots rules and the URL tree) × sampled URLs, compute allow/deny/unknown verdicts for three purposes: **search indexing**, **AI RAG citation**, **LLM training**.
- **Contradictions** (e.g. robots allows an agent while `X-Robots-Tag` or meta denies it) emit `geo.governance-conflict` (**Notice**) with both evidence values.
- **Site-wide matrix surface:** rows = path prefixes / URLs, columns = agents, purpose toggles (indexing / RAG / training); virtualized per FS-CRAWL-002 §7.1. The matrix is a report, not a gate — it never alters crawl behavior.

### 13.3 Standalone tool — `llms.txt` Generator & Validator (extends §8.7)

- **Generate:** from an audit's top-scoring indexable pages — ranking contract: indexable, no Error findings, descending content-quality composite (word count, readability in range, title/description present; exact composite an implementation choice) — grouped by directory/category. Output structure: H1 site name; blockquote summary (site meta description or user input); H2 sections per group with markdown link lists (page title + absolute URL). Optional `llms-full.txt` variant adds bounded content excerpts.
- **Validate:** pasted/uploaded body or a URL → structural verdict (FS-CRAWL-002 §13.1 rules) plus link verification.
- Output is a **local file export** (§8.4 pattern); nothing is ever auto-published or submitted to any vendor (FS-CRAWL-002 §1.6 E9).

### 13.4 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `url_elements` | New element group | `llms_structure` payload: H1/blockquote/section presence, linked-URL count, broken count | Additive to the §7.3 inventory; payload JSON |
| `governance_matrix` | New table | Audit reference; agent key; scope kind (`prefix` \| `url`); scope value; purpose (`search-index` \| `ai-rag-citation` \| `llm-training`); verdict; evidence JSON | Indexes (audit, agent), (audit, scope value); pruned with retention (§9.5) |
| `audits` | Additive snapshot fields | Agent-catalogue version; GEO toggles | Snapshot pattern (§9.2) |
| `metric_summaries` | New metric keys | `geo-readiness` (dimension: check); `governance-coverage` | Dashboard cards |

### 13.5 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| llms.txt results | GET `/api/audits/{id}/geo/llms-txt` | Host filter | Per-host file status, structural verdict, linked/broken counts |
| Governance matrix | GET `/api/audits/{id}/geo/governance` | Agent; purpose; scope filters; pagination | Matrix rows with verdicts and evidence |
| Generate llms.txt | POST `/api/tools/llms-generate` | Audit reference; grouping options; site metadata; full variant flag | Generated file (local export) |
| Validate llms.txt | POST `/api/tools/llms-validate` | Markup body or URL | Structural verdict + link-check results |

**New MCP resource `geo-readiness`** (audit reference; agent filter): read-only llms.txt validation results plus governance summary. Tool invocation and configuration are not exposed via MCP (§11.1 boundary).

### 13.6 UI requirements

A new **GEO Readiness** report surface, additive to the §8.2 catalog:

- `llms.txt` / `llms-full.txt` status cards per host with structural evidence and broken-link drill-down.
- Governance matrix grid (agent columns, purpose toggles, conflict badges), virtualized per FS-CRAWL-002 §7.1.
- Generator & Validator entry in the standalone tools window (§12), with preview before export.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #46.

---

## 14. MarTech, Analytics & Tag Integrity Auditor

**Extends FS-CRAWL-001 §5.6, §5.9, §7.3, §7.4, §8.2.** Additional hooks: §7.2, §7.17, §9.2, §10.2, §11.4.

### 14.1 Automated tag and container inventory

- **Sources:** HTML parsing (§7.2 step 3) and local proxy network capture (§5.6 rendering capture, §5.9 proxy routing) — no additional fetches beyond the parent's existing capture paths.
- **Bundled, versioned detection catalogue** (the §7.15 asset pattern) covering: **Google Tag Manager** (`GTM-*` container IDs), **Google Analytics 4** (`G-*` measurement IDs), **Google Ads** (`AW-*` conversion IDs), **Meta Pixel**, **LinkedIn Insight**, **Microsoft Clarity** — plus **custom user-defined beacon patterns** (pattern expressions over script sources, inline markers, and network hit URLs; the §7.16 pattern discipline with time bounds).
- Each detection records: vendor; container/pixel ID; detection source (`dom` | `network` | `both`); placement (head/body); load flags (async/defer); first-party vs third-party classification.
- The catalogue is a packaged asset updated only with product updates; user patterns are local configuration. No remote tag registry or vendor service is consulted.

### 14.2 `dataLayer` and Consent Mode v2 inspection (rendering mode)

- **`dataLayer` capture:** in rendering mode (§5.6), the post-render `window.dataLayer` array is snapshotted with mandatory bounds — event count plus the first N events (default 100, configurable); capture is engine-level with the same isolation and time-bound discipline as custom snippets (§7.17).
- **Consent Mode v2 state:** default and update commands for `ad_storage`, `analytics_storage`, `ad_user_data`, `ad_personalization` are extracted into an ordered timeline (defaults first, then updates with sequence markers).
- **Network correlation:** consent hit parameters (`gcs`, `gcd`) observed in proxy-captured network requests (§5.9) are correlated against the consent timeline; tags firing **before or without** a default consent declaration are detected.
- Without rendering mode, inventory still runs from static HTML and any proxy capture; consent/dataLayer passes are marked unavailable with a diagnostic.

### 14.3 Built-in audit rules — new category `Analytics & Tagging`

| Rule key | Condition | Severity |
|---|---|---|
| `analytics.missing-container` | Indexable HTML pages missing the site's primary GTM/GA4 ID (primary = most frequent across indexable pages; user override configurable) | **Warning** |
| `analytics.duplicate-container` | The same container ID injected multiple times on a page, or duplicate `page_view` requests on initial load (network capture) | **Warning** |
| `analytics.datalayer-malformed` | Syntax errors or missing required keys in `dataLayer` events, validated against an optional user-supplied JSON schema (stored in audit configuration; bounded validation) | **Warning** |
| `analytics.consent-mode-missing` | Tags firing prior to or without default consent state declarations, where Consent Mode auditing is enabled | **Warning** |
| `analytics.no-hits-observed` (optional diagnostic) | Container present in DOM but zero corresponding network hits observed while capture is enabled | **Notice** |

The category **Analytics & Tagging** is additive to the §7.4 taxonomy; suppression follows §7.5; evidence carries container IDs, event excerpts (bounded), and consent-timeline markers.

### 14.4 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `analytics_detections` | New table | Audit reference; URL reference; vendor; container/pixel ID; detection source; placement; load flags; evidence JSON | Indexes (audit, vendor), (audit, container ID); pruned with retention (§9.5) |
| `url_elements` | New element groups | `consent_state` payload (defaults/updates timeline, `gcs`/`gcd` observations); `datalayer_snapshot` payload (bounded event count + first N events) | Additive to the §7.3 inventory; payload JSON |
| `crawl_configurations` | Additive fields | Consent-auditing toggle; primary-container override; `dataLayer` JSON schema; custom beacon patterns | §5.1 input-set extension |
| `metric_summaries` | New metric keys | `analytics-coverage` (dimension: vendor); `consent-compliance` | Dashboard cards |

### 14.5 REST and MCP additions (extends §10.2, §11.4)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Analytics detections | GET `/api/audits/{id}/analytics` | `view=inventory\|consent\|findings`; vendor/container filters; pagination | Detection rows, consent timelines, or findings |
| Container coverage | GET `/api/audits/{id}/analytics/coverage` | Vendor filter | Per-container coverage across indexable pages (present/missing counts) |

**New MCP resource `analytics-coverage`** (audit reference; vendor filter): read-only coverage projection. Hub: detections ride the existing `findings-updated` and `url-batch` events (§10.3) — no new event required.

### 14.6 UI requirements — Analytics & Tagging Explorer

A new report surface, additive to the §8.2 catalog:

- Container inventory grid (vendor, ID, coverage, placement), virtualized per FS-CRAWL-002 §7.1.
- Per-URL tag/consent timeline: default consent declarations, updates, and first tag fire on one axis; `gcs`/`gcd` hit markers.
- Coverage treemap by directory (Apache ECharts, FS-CRAWL-002 §8.4); bounded `dataLayer` event inspector.
- Configuration forms for consent auditing, primary-container override, JSON schema, and custom beacon patterns (Mantine forms, FS-CRAWL-002 §8.2).

**Acceptance hook:** FS-CRAWL-002 §9, criterion #47.

---

## 15. Contextual Link Weighting, Pixel-Width SERP Measurement & Header/HTML Equivalency

**Extends FS-CRAWL-001 §7.3, §7.4, §8.1, §8.2 and FS-CRAWL-002 §4.** Additional hooks: §5.4, §9.2, §9.3, §10.2, §11.4.

### 15.1 DOM-zone link classification

- Every extracted `link_edge` (§9.2) is classified by the DOM zone of its source anchor: **`in-content`, `navigation`, `header`, `footer`, `sidebar`, `breadcrumb`**.
- Classification uses landmark elements/roles and the Content Area Scoping rules (FS-CRAWL-002 §4): anchors inside the scoped content region classify as `in-content`; boilerplate landmarks classify by deterministic precedence — breadcrumb markers > `nav`/`role=navigation` > `header`/`role=banner` > `footer`/`role=contentinfo` > `aside`/`role=complementary` (sidebar) > default `in-content` for body anchors matching no boilerplate landmark.
- Persisted as the additive column **`link_edges.dom_zone`** with zone evidence; classification runs during link inventory and is re-derived from stored bodies on re-audit (§9.6).

### 15.2 Weighted link equity (extends §8.1)

- **Zone weights** are configurable per audit; built-in defaults: `in-content` **1.0**, `breadcrumb` **0.6**, `navigation` **0.3**, `sidebar` **0.3**, `header` **0.2**, `footer` **0.1** — editorial in-content links transfer significantly more weight than boilerplate.
- The internal PageRank / link-equity computation (§8.1) runs in a **weighted variant**: each edge contributes its zone weight (standard iterative computation with configurable damping; mathematical principle specified, implementation choice). The unweighted distribution is retained for comparability.
- Outputs: per-URL weighted equity score, percentile, and concentration, persisted under `metric_summaries` key `link-equity-weighted`; graph node sizing offers a weighted/unweighted toggle (FS-CRAWL-002 §8.5).

### 15.3 Pixel-width SERP and social card measurement (extends §7.3, §7.4)

- **Measurement:** rendered pixel widths of `<title>` and `<meta name="description">` are computed from **bundled per-locale font-metric tables** (standard search-result font metrics; packaged offline assets, no web-font fetch). Width = summed glyph advances with the search engine's ellipsis behavior.
- **Configurable budgets** (defaults are approximations of standard search-result rendering): title desktop **~580 px**; title mobile **~920 px** with two-line wrap; description desktop **~920 px** two-line; description mobile **~920 px** two-line.
- **Findings:** `title-pixel-truncation` and `description-pixel-truncation`, category *Content quality* (§7.4), severity **Warning**; evidence: computed pixels, budget, overflow, estimated truncation point. Character-count checks (§7.4) remain; pixel checks are additive.
- **Live SERP & Social Card Preview** in the URL explorer: desktop/mobile SERP snippet (title, URL, description with ellipsis at the measured truncation point) plus social card from the Open Graph/Twitter facts (§7.3). Rendered locally; preview images come from the content store when captured (§9.6); no remote fetch.
- Measurements persist as the `url_elements` group `serp_metrics`.

### 15.4 HTTP header vs HTML directive equivalency (extends §7.3, §7.4)

Reconcile response-header directives (recorded per §5.4) against their HTML counterparts in the inventory (§7.3):

| Header source | HTML counterpart | Conflict finding (severity **Error**) |
|---|---|---|
| `X-Robots-Tag` (per directive, per agent) | `<meta name="robots">` / agent-specific meta | `indexability.header-html-conflict` — category *Indexability & status* |
| `Link: rel="canonical"` | `<link rel="canonical">` | `canonical.header-html-conflict` — category *Indexability & status* |
| `Link: rel="alternate"; hreflang="..."` | `<link rel="alternate" hreflang>` | `hreflang.header-html-conflict` — category *Internationalization* |

- Evidence carries both values and a directive-level diff; agreement may record a Pass annotation. Parsed header directives persist as the `url_elements` group `header_directives`.
- Conflicts are high-severity because search engines may honor either source; the finding never auto-resolves the conflict.

### 15.5 Schema and API additions (extends §9.2, §10.2, §11.4)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `link_edges` | Additive column | `dom_zone` (six-zone enum); zone evidence | Parent link indexes (§9.3) unchanged; additive index (audit, dom_zone) |
| `url_elements` | New element groups | `serp_metrics` (pixel widths, budgets, truncation flags); `header_directives` (parsed header directives) | Additive to the §7.3 inventory; payload JSON |
| `crawl_configurations` | Additive fields | Zone weights; SERP pixel budgets | §5.1 input-set extension |
| `metric_summaries` | New metric keys | `link-equity-weighted` (dimensions: URL); `serp-truncation` | §8.1 aggregate pattern |

REST: GET `/api/audits/{id}/links` gains a `domZone` filter and weighted-equity fields; GET `/api/audits/{id}/urls/{urlId}` gains `serp_metrics` and header-conflict detail; GET `/api/audits/{id}/charts/{metric}` accepts `link-equity-weighted`. MCP `url-detail` gains the same additive fields. No new hub events (aggregates ride existing events).

### 15.6 UI requirements

- Link explorer: zone badges and zone filter; weighted/unweighted equity toggle in Visualizations (node sizing, FS-CRAWL-002 §8.5).
- URL detail: **SERP & Social Card Preview** panel with desktop/mobile toggle and live truncation highlight; header/HTML conflict panel with side-by-side directive diff.
- All tabular content obeys FS-CRAWL-002 §7.1.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #48.

---

## Appendix A — Additive Data Model Catalog (extends FS-CRAWL-001 §9.2)

All objects follow §9.1 conventions and are provider-agnostic (§2.4). Audit-scoped tables are pruned transactionally with retention (§9.5); configuration tables are not. The "Defined in" column references **FS-CRAWL-002** sections.

| Object | Kind | Defined in | Purpose |
|---|---|---|---|
| `waf_detections` | Table | §2.6 | Per-URL WAF challenge detections with provider, class, evidence |
| `waf_user_signatures` | Table | §2.6 | User-local signature catalogue entries |
| `host_throttle_events` | Table | §3.6 | Bounded AIMD adjustment history per (audit, host) |
| `content_scope_profiles` | Table | §4.4 | Reusable project-level include/exclude scopes |
| `ngram_frequencies` | Table | §5.6 | Per-URL, per-zone, per-order n-gram counts and TF |
| `ngram_site_summaries` | Aggregate view (materialized read model) | §5.6 | Site-wide DF, total frequency, mean TF, primary-term counts |
| `user_stopwords` | Table | §5.6 | Per-language user stop-word additions |
| `ui_plugins` | Table | §6.7 | Local plugin registry, grants, integrity hashes |
| `redirect_mappings` | Table | §10.6 | Migration matrix rows (Source_URL, Expected_Target_URL) per audit |
| `log_imports` | Table | §11.6 | Log import records: format, window, counters, verification mode, status |
| `log_url_aggregates` | Table | §11.6 | Per-(import, canonical URL) hit aggregates by bot family and status |
| `semantic_link_opportunities` | Table | §12.7 | Bounded top-K semantic link recommendations with anchor candidates |
| `semantic_clusters` | Table | §12.7 | Semantic cannibalization cluster membership and centroid similarity |
| `governance_matrix` | Table | §13.4 | Agent × scope × purpose allow/deny verdicts with evidence |
| `analytics_detections` | Table | §14.4 | Per-URL tag/container detections (vendor, ID, source, placement) |
| `url_embeddings` | **Activated** parent table | §12.7 | Populated and queried by the local embedding pipeline (no schema change) |
| `url_records` | Additive columns / enum value | §2.6, §10.6 | Block reason, WAF provider, challenge class; discovery source gains `list` |
| `link_edges` | Additive column | §15.5 | `dom_zone` six-zone classification with zone evidence |
| `audits` | Additive snapshot fields | §2.6, §4.4, §10.6, §12.7, §13.4 | Catalogue version/revision, detection policy, resolved content scope; list mode/rewrite/host mapping/matrix; embedding provider/model/thresholds; agent-catalogue version, GEO toggles |
| `crawl_configurations` | Additive fields | §4.4, §10.6, §14.4, §15.5 | Content scope (inline or profile reference); AIMD and WAF settings ride the existing politeness/limit override fields (§5.1); list mode, URL list, rewrite rules, host mapping; consent toggle, primary-container override, dataLayer schema, beacon patterns; zone weights, SERP budgets |
| `crawl_checkpoints` | Additive payload field | §3.6 | Serialized per-host AIMD controller state |
| `url_elements` | New element groups | §4.4, §13.4, §14.4, §15.5 | `content_scope` diagnostics; `llms_structure`; `consent_state` and `datalayer_snapshot` (bounded); `serp_metrics` and `header_directives` |
| `metric_summaries` | New metric keys | §2.6, §3.6, §4.4, §5.6, §10.6, §11.5, §12.7, §13.4, §14.4, §15.5 | v1.0: `waf-blocks`, `waf-block-rate`, `host-throttle`, `scoped-word-count`, `ngram-top`, `cannibalization-clusters`, `topical-gaps`. v1.1: `redirect-matrix`, `log-true-orphans`, `log-budget-waste`, `log-uncrawled-indexable`, `log-bot-hits`, `semantic-clusters`, `semantic-opportunities`, `geo-readiness`, `governance-coverage`, `analytics-coverage`, `consent-compliance`, `link-equity-weighted`, `serp-truncation` |
| `findings` | New rule keys / categories | §2.4.5, §5.5, §10.3, §11.5, §12.5, §13.1–§13.2, §14.3, §15.3–§15.4 | v1.0: `waf-challenge-detected`, `waf-block-rate`, `content.keyword-cannibalization`, `content.topical-gap` (plus Notice annotations `challenge-resolved-by-render`, `suspected-challenge`). v1.1: `redirect-matrix.pass` / `.target-mismatch` / `.chain-exceeded` / `.final-non-200` / `.canonical-mismatch`; `logs.true-orphan`, `logs.crawl-budget-waste`, `logs.uncrawled-indexable`; `content.semantic-cannibalization`; `linking.low-relevance-link`; `geo.llms-txt-missing`, `geo.llms-txt-malformed`, `geo.llms-txt-broken-link`, `geo.governance-conflict`; `analytics.missing-container`, `analytics.duplicate-container`, `analytics.datalayer-malformed`, `analytics.consent-mode-missing`, `analytics.no-hits-observed`; `title-pixel-truncation`, `description-pixel-truncation`; `indexability.header-html-conflict`, `canonical.header-html-conflict`, `hreflang.header-html-conflict`. New additive categories: **Log Intelligence**, **AI Search (GEO)**, **Analytics & Tagging** |
| `application_settings` | Additive preference fields | §2, §3, §6, §7, §11.3, §12.2, §15.3 | AIMD defaults, WAF toggle, status-bar thresholds, plugin preferences; embedding provider configuration; user-local bot CIDR additions; SERP budget defaults |

Required new indexes: `waf_detections` (audit, provider) and unique (audit, URL); `host_throttle_events` (audit, host, timestamp); `ngram_frequencies` unique (audit, URL, zone, order, term) plus (audit, zone, order, term) and (audit, URL); `ngram_site_summaries` (audit, zone, order, DF descending); `redirect_mappings` unique (audit, source URL) plus (audit, ordinal); `log_imports` (project, imported-at); `log_url_aggregates` unique (import, canonical URL) plus (import, host) and (import, search-bot hits descending); `semantic_link_opportunities` unique (audit, source, target) plus (audit, source) and (audit, similarity descending); `semantic_clusters` (audit, cluster); `governance_matrix` (audit, agent) and (audit, scope value); `analytics_detections` (audit, vendor) and (audit, container ID); `link_edges` additive (audit, dom_zone). Parent indexes (§9.3) are unchanged.

## Appendix B — Additive REST Contract (extends FS-CRAWL-001 §10.2)

All routes follow §10.1 conventions (JSON envelope, offset/limit pagination, filtering, sorting; loopback only). The "Defined in" column references **FS-CRAWL-002** sections.

| Method & path | Defined in | Purpose |
|---|---|---|
| GET `/api/audits/{id}/waf-blocks` | §2.7 | WAF detection list with evidence |
| GET `/api/audits/{id}/waf-blocks/summary` | §2.7 | Provider/class counts, block rate |
| GET `/api/waf-signatures` | §2.7 | Effective catalogue (bundled version + user entries) |
| POST `/api/waf-signatures` | §2.7 | Create user signature |
| PUT `/api/waf-signatures/{key}` | §2.7 | Update user signature |
| DELETE `/api/waf-signatures/{key}` | §2.7 | Delete user entry / restore bundled default |
| GET `/api/audits/{id}/hosts` | §3.7 | Per-host AIMD state; optional adjustment history |
| POST `/api/audits/{id}/hosts/{host}/override` | §3.7 | Pin/clear manual host rate |
| GET `/api/audits/{id}/ngrams` | §5.7 | Site-summary or per-URL n-gram rows |
| GET `/api/audits/{id}/ngrams/cannibalization` | §5.7 | Cannibalization clusters |
| GET `/api/audits/{id}/ngrams/gaps` | §5.7 | Topical gaps |
| POST `/api/tools/scope-preview` | §4.6 | Scope preview against a sample URL/artifact |
| GET `/api/plugins` | §6.7 | Plugin registry and load state |
| PUT `/api/plugins/{id}` | §6.7 | Enable/disable; grant/revoke permissions |
| POST `/api/plugins/rescan` | §6.7 | Rescan plugin directories |
| DELETE `/api/plugins/{id}` | §6.7 | Remove registry entry |
| GET `/api/system/health` | §7.2 | On-demand memory/WAL snapshot |
| POST `/api/system/compact` | §7.2 | Compact / Trim Working Set action |
| POST `/api/audits/{id}/redirect-matrix` | §10.7 | Upload migration mapping; auto-seed list-mode audit |
| GET `/api/audits/{id}/redirect-matrix/results` | §10.7 | Per-row verdicts, hop chains, canonical evidence |
| POST `/api/tools/rewrite-preview` | §10.7 | Sample URL rewriting before/after |
| POST `/api/projects/{id}/logs/import` | §11.7 | Import log file (immediate identifier; hub progress) |
| GET `/api/projects/{id}/logs` | §11.7 | List log imports |
| DELETE `/api/logs/{id}` | §11.7 | Purge import + aggregates |
| GET `/api/audits/{id}/logs/correlation` | §11.7 | Orphans / budget waste / uncrawled indexable views |
| POST `/api/logs/{id}/verify-dns` | §11.7 | Optional user-triggered DNS bot verification |
| POST `/api/audits/{id}/semantics/run` | §12.8 | Run / re-run local embedding job |
| GET `/api/audits/{id}/semantics/similar` | §12.8 | Top-K similar URLs by cosine |
| GET `/api/audits/{id}/semantics/link-opportunities` | §12.8 | Semantic internal-link recommendations |
| GET `/api/audits/{id}/semantics/clusters` | §12.8 | Semantic cannibalization clusters |
| POST `/api/settings/embedding/test` | §12.8 | Loopback provider connection/model test |
| GET `/api/audits/{id}/geo/llms-txt` | §13.5 | Per-host llms.txt validation results |
| GET `/api/audits/{id}/geo/governance` | §13.5 | Bot-governance matrix rows |
| POST `/api/tools/llms-generate` | §13.5 | Generate llms.txt / llms-full.txt (local export) |
| POST `/api/tools/llms-validate` | §13.5 | Validate llms.txt body or URL |
| GET `/api/audits/{id}/analytics` | §14.5 | Tag inventory / consent / findings views |
| GET `/api/audits/{id}/analytics/coverage` | §14.5 | Per-container coverage across indexable pages |

Extended existing routes (additive fields/filters only): GET `/api/audits/{id}/urls` (blockReason, wafProvider filters); GET `/api/audits/{id}/urls/{urlId}` (WAF detection, scope diagnostics, top n-grams, `serp_metrics`, header-conflict detail, semantic relations); GET `/api/audits/{id}/summary` (WAF counts, throttle summary); GET `/api/audits/{id}/charts/{metric}` (`waf-blocks`, `ngram-top`, `link-equity-weighted` metrics); GET `/api/audits/{id}/links` (`domZone` filter, weighted-equity fields); POST `/api/analysis/single-page` and POST `/api/tools/url-check` (WAF verdict, scope diagnostics); POST `/api/tools/render-test` (post-render WAF verdict, picker support); POST `/api/exports` (`waf-blocks`, `ngrams`, `redirect-matrix`, semantic-opportunity, analytics datasets); POST `/api/audits` (`urlList`, `listMode`); POST/PUT configuration routes and PUT `/api/projects/{id}` (contentScope, rewrite/host-mapping, consent/beacon, zone-weight/SERP fields); GET `/api/audits/compare` and GET `/api/audits/compare/content` (`hostMapping`); GET `/metrics` (memory/GC/WAL families).

## Appendix C — Additive Real-Time Hub Events (extends FS-CRAWL-001 §10.3)

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| `waf-blocks` | Server → shell | Audit identifier; batch of (URL identifier, canonical URL, provider, challenge class, confidence) | Throttled batches, at most once per second |
| `host-throttle` | Server → shell | Audit identifier; host; trigger; rate before/after; concurrency before/after; TTFB sample; rolling baseline | On every adjustment, coalesced ≤ 1/s per host |
| `system-health` | Server → shell | Timestamp; backend working-set bytes; managed heap bytes; GC generation counts; WAL bytes (nullable); optional audit identifier | Every 2 seconds while the shell is connected, idle or running |
| `matrix-row-validated` | Server → shell | Audit identifier; batch of (URL identifier, source URL, verdict) | Throttled batches, at most once per second (FS-CRAWL-002 §10.8) |
| `log-import-progress` | Server → shell | Import identifier; lines parsed/accepted/rejected; phase | At most once per second while importing (FS-CRAWL-002 §11.8) |
| `log-import-completed` | Server → shell | Import identifier; final counters; correlation summary counts | Once per import finalization (FS-CRAWL-002 §11.8) |
| `embedding-progress` | Server → shell | Audit identifier; embedded count; total eligible; provider; phase | At most once per second while running (FS-CRAWL-002 §12.9) |
| `embedding-completed` | Server → shell | Audit identifier; final counts; pass summaries (clusters, opportunities, low-relevance links) | Once per job finalization (FS-CRAWL-002 §12.9) |

Additive payload fields on parent events: `crawl-progress` gains `wafBlocked`, throttled-host count, mean effective rate, and optional matrix counters (validated/total) while a migration matrix is attached; `url-batch` gains optional block reason/provider on blocked entries; `audit-completed` final counters gain the WAF sub-count. Analytics detections and GEO findings ride the existing `findings-updated` / `url-batch` events (no new events for FS-CRAWL-002 §§13–15). Parent emission rules and the subscribe/unsubscribe contract are unchanged.

## Appendix D — Additive MCP Surface (extends FS-CRAWL-001 §11.4)

Read-only resources following the parent address pattern (resource kind + audit reference + optional filters). No new tools; lifecycle tools (§11.3) are unchanged. The §11.1 boundary holds: no settings, signature, stop-word, plugin, destination, credential, proxy, provider, log-import, embedding-provider, migration-matrix, GEO-tool, or analytics-configuration management via MCP.

| Resource / prompt | Addressed by | Content |
|---|---|---|
| `ngram-analysis` (resource) | Audit reference; scope, zone, order, term, URL, min-DF filters; pagination | Site summaries, per-URL frequencies, top cannibalization clusters (bounded) |
| `waf-blocks` (resource) | Audit reference; provider/class filters; pagination | Detection rows with evidence + summary counts |
| `host-throttle` (resource) | Audit reference; optional host filter | Current per-host controller state + recent adjustments |
| `redirect-matrix-results` (resource) | Audit reference; verdict filter; pagination | Migration verdicts, hop chains, canonical evidence (FS-CRAWL-002 §10.8) |
| `log-correlation` (resource) | Audit reference; import reference; view; pagination | True orphans, budget waste, uncrawled indexable rows (FS-CRAWL-002 §11.7) |
| `semantic-link-opportunities` (resource) | Audit reference; source filter; similarity band; pagination | Source, target, similarity, anchor candidates (FS-CRAWL-002 §12.8) |
| `geo-readiness` (resource) | Audit reference; agent filter | llms.txt validation results + governance summary (FS-CRAWL-002 §13.5) |
| `analytics-coverage` (resource) | Audit reference; vendor filter | Per-container coverage across indexable pages (FS-CRAWL-002 §14.5) |
| `ngram-topical-briefing` (prompt, optional) | Audit reference | Composes `ngram-analysis` + `audit-summary` into an agent-ready topical briefing; no side effects |
| `semantic-topical-briefing` (prompt, optional) | Audit reference | Composes `semantic-link-opportunities` + `audit-summary`; no side effects (FS-CRAWL-002 §12.8) |

Additive payload fields on parent resources: `audit-summary` (WAF counts), `crawl-progress` (throttle summary, matrix counters), `url-detail` (WAF detection, `content_scope` diagnostics, top primary n-grams, `serp_metrics`, header-conflict detail, semantic relations). Every invocation remains logged in `automation_action_log` (§9.2, §11.5).

## Appendix E — Cross-Reference Index

The right column references **FS-CRAWL-002** sections and appendices.

| FS-CRAWL-001 contract | Extended by (FS-CRAWL-002) |
|---|---|
| §1.2 Chinese Wall | §1.2 (maintained verbatim in force) |
| §2.3 Startup sequence | §6.2 (plugin scan insertion) |
| §2.4 Persistence / providers | §2.6, §3.6, §4.4, §5.6, §6.7, §12 (vector-similarity capability and `url_embeddings` activation), Appendix A (provider-agnostic additions) |
| §2.6 Network boundary | §1.4 (rule 9), §2.3.4, §5.2, §6.4, §6.8, §11.1, §12.2 (no widening; loopback-only daemons; offline log ingestion) |
| §3 Excluded subsystems | §1.4, §1.6 (extension exclusions E1–E9) |
| §4.1–§4.3 Limits resolver | §3.3 (AIMD parameters merge identically), §10.2 (list-mode limits) |
| §4.2 Configuration parameters | §3.1, §3.4 (ceilings bound AIMD), §10.3 (redirect-hop ceiling in matrix verdicts) |
| §4.4 Politeness | §2.5, §3 (AIMD formalizes adaptive bounding; crawl-delay floor; retry-after interplay), §13.1 (llms.txt probed alongside robots) |
| §5.1 Audit inputs | §2.5, §3.3, §4.2 (WAF policy, AIMD options, content scope), §10.2 (URL list seed), §14.4 (consent/beacon config), §15.5 (zone weights, SERP budgets) |
| §5.2 Canonicalization / dedup | §10.2, §10.4 (list ingestion, regex rewriting), §11.4 (log URL joins) |
| §5.3 Frontier / URL states | §2.4.2 (`blocked` + `waf-challenge`), §3 (per-host sub-queue control), §10.2 (list-only / list-bounded discovery) |
| §5.4 Fetching | §2.2 (fingerprinting pass at receipt), §3.2 (TTFB timing clarification), §10.3 (full hop-chain evaluation), §15.4 (header directive reconciliation) |
| §5.5 Link discovery | §10.2 (discovery disabled/bounded in list mode) |
| §5.6 Rendering mode | §2.2, §2.4.3, §4.3 (post-render checkpoints and scoping), §14.1–§14.2 (dataLayer / Consent Mode capture) |
| §5.7 Lifecycle / progress | §2.4.2, §3.5, §6.5, §7.2 (counters, checkpoints, hooks, cadence) |
| §5.9 Proxy / traffic routing | §14.1–§14.2 (network hit capture for tag and consent auditing) |
| §7.2 Audit pipeline | §2.2, §4.3, §5.1 (pass placement), §14.1 (tag inventory during parsing) |
| §7.3 Element inventory | §4.3, §4.4, §5.3 (text statistics, `content_scope` group, zones), §13.4 (`llms_structure`), §14.4 (`consent_state`, `datalayer_snapshot`), §15.3–§15.4 (`serp_metrics`, `header_directives`) |
| §7.4 Rule taxonomy | §2.4.5, §5.5 (new rule keys in existing categories), §10.3 (migration verdicts), §11.5 (Log Intelligence), §12.5 (semantic passes), §13.1–§13.2 (AI Search (GEO)), §14.3 (Analytics & Tagging), §15.3–§15.4 (pixel truncation, header conflicts) |
| §7.5 Finding model / shingles | §2.4.4, §4.3, §5.5 (exclusions, scoped shingles, suppression), §12.5 (semantic cannibalization cross-references) |
| §7.7 Single-page analysis | §2.2, §4.6, §8.6 (WAF verdict, scope preview, picker) |
| §7.8 Spelling | §4.3, §5.2 (scoped text; language-selection pattern) |
| §7.9 Custom extraction | §4 (selector contract gap filled), §6.6, §8.3, §8.6 (dynamic columns, formatter targets, picker send-to) |
| §7.12 Readability / soft-404 | §2.4.4 (WAF ≠ soft-404), §4.3 (scoped text metrics) |
| §7.15 Technology detection | §2.3 (catalogue asset pattern), §11.3 (bot UA catalogue), §13.2 (agent profiles), §14.1 (tag signatures) |
| §7.16 Custom search | §4.3 (scoped visible-text target), §10.4, §14.1 (time-bounded pattern discipline) |
| §8.1 Aggregation / scoring | §2.5, §2.6, §3.6, §4.4, §5.6 (new metric keys; coverage denominators), §10.3 (matrix aggregates), §11.5 (log aggregates), §15.2 (weighted link equity) |
| §8.2 Report surfaces | §2.9, §4.5, §5.9, §6.6, §7.1, §8 (WAF panel, N-Gram Explorer, Link explorer formalization, plugin tabs, virtualization), §10.3/§10.5 (Migration Matrix, host-mapped comparison), §11.9 (Log File Analyzer), §12.10 (Semantic), §13.6 (GEO Readiness), §14.6 (Analytics & Tagging Explorer), §15.6 (SERP preview, zone filters) |
| §8.3 Charts | §8.4 (ECharts realization) |
| §8.4 Export | §2.7, §5.7 (new datasets), §10.7 (redirect-matrix dataset), §13.3 (llms.txt local file export) |
| §8.5 Export destinations | §6.4 (plugin network grants reference destination rules) |
| §8.7 Standalone tools | §4.6 (scope-preview), §8.6 (Render Tester picker), §10.7 (rewrite-preview), §13.3 (llms.txt Generator & Validator) |
| §9.2 Table catalog | Appendix A (all additive objects) |
| §9.3 Indexes | §15.5 (additive `link_edges` zone index; parent indexes unchanged) |
| §9.5 Retention | §2.6, §3.6, §5.6 (new audit-scoped tables pruned), §10.6, §11.6, §12.7, §13.4, §14.4 (v1.1 tables pruned) |
| §9.6 Content store | §2.2, §2.4.2, §4.4, §5.1, §8.6 (offline re-runs, evidence retention), §12.3 (content-hash re-embedding), §15.1/§15.3 (reclassification, preview images) |
| §10.2 REST mapping | Appendix B (all additive routes) |
| §10.3 Real-time hub | Appendix C (all additive events) |
| §10.5 Metrics / diagnostics | §3.7, §7.3 (memory/GC/WAL families; plugin diagnostics) |
| §11.1–§11.6 MCP server | Appendix D (resources/prompts; boundary preserved) |
| §12 Desktop shell | §6 (plugins), §7.2 (status bar), §8 (concrete stack), §10.2/§11.2 (native file dialogs), §13.6 (tools window) |
| §13 Non-functional requirements | §7 (scale/responsiveness budgets), §8.9 (packaging additions), §11.3/§12.2/§13.2/§15.3 (bundled CIDR, model, agent-profile, font-metric assets) |
| §14 Acceptance criteria #1–#34 | §9 (appends #35–#48) |

---

*End of FS-CRAWL-002 v1.1. This document extends but never modifies FS-CRAWL-001 v1.4 (`functional-specification.md`).*
