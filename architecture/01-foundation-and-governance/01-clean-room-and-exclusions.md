# Clean-Room Protocol, Governance & Exclusions

> **CrawlForge modular architecture — area of concern: `01-foundation-and-governance`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | Document control & revision history (preamble), §1 (complete section), §3 (complete section) |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | Document control & revision history (preamble), §1 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: Document control & revision history (preamble, verbatim)**

# Functional Specification — Standalone Desktop Website Crawler & Auditor

| Document control | Value |
|---|---|
| Identifier | FS-CRAWL-001 |
| Version | 1.4 |
| Status | Issued for clean-room implementation — full capability-parity audit applied |
| Audience | Clean-room development team (no access to reference artifacts) |
| Classification | Abstract functional specification — contains no code and no proprietary expression |
| Derivation | Abstracted from dirty-room architectural analyses of a reference desktop application |

| Revision history | Change summary |
|---|---|
| 1.0 | Initial clean-room specification |
| 1.1 | Gap-analysis amendments: scheduled audits, content store, spelling checks, user-configured export destinations, local automation interface, persistence patterns, localization |
| 1.2 | QA round-2 amendments: configurable database providers, runtime performance profile, local metrics endpoint, local crash artifacts, local proxy routing, MCP mandate with agent transports, tool/resource definitions, and delegated control flows |
| 1.3 | Parity amendment: user-defined Custom Content Extraction — rules, typed values, mandatory bounds, dynamic-column explorer, export, and MCP resource |
| 1.4 | Capability-parity audit: accessibility auditing; rendered performance and Core Web Vitals with budgets and code coverage; readability and content metrics; soft-404 detection; technology detection; custom content search; custom JavaScript snippets; response-vs-rendered comparison; audit scoring; finding suppression; standalone diagnostic tools and sitemap generator; interactive query builder; signed-request crawler access; sample-crawl mode; virtual robots; console capture; infinite-scroll rendering; pre-crawl start-URL validation. Scope-sensitive items recorded as open decisions |

---

---

> **Verbatim source — FS-CRAWL-001 v1.4: 1. Purpose and Clean-Room Protocol — complete section, verbatim and unabridged**

## 1. Purpose and Clean-Room Protocol

### 1.1 Purpose

This document defines the complete functional behavior of a standalone, non-commercial desktop application that crawls websites, audits their technical and content quality, and presents interactive reports locally. A separate implementation team shall be able to build the product from this document alone, without ever consulting the reference application.

### 1.2 Chinese Wall rules

- This document conveys **abstract behavior only**: business rules, workflows, data shapes, mathematical principles, and architectural constraints.
- It contains no source code, no identifier names taken from the reference application, no proprietary algorithms, and no literal strings, endpoints, ports, or configuration values copied from the reference artifacts. Standard, publicly documented platform and protocol names (runtime settings, open standards) are used as technology vocabulary only.
- All names, routes, schemas, taxonomies, and defaults in this document are **new design artifacts** created for the target product.
- The implementation team shall not read, decompile, or otherwise consult the reference application, its binaries, or the dirty-room analysis documents. Clarifications are routed through the architect as abstract behavioral questions only.
- The target product is an independent work. Its similarity to any existing product is limited to generic, industry-standard crawler and auditor functionality.

### 1.3 Product principles

- Personal desktop use; single user; no accounts, no sign-in, no subscription or entitlement concepts of any kind.
- Fully functional with no network connectivity, except for the act of crawling target websites itself, optional user-configured export destinations (section 8.5), and an optional user-configured external database server (section 2.4) — all off by default.
- Every feature is enabled by default; no remote configuration channel exists.
- All application data remains on the local machine by default. Data leaves the machine only toward services the user has explicitly configured themselves (export destinations, an external database server), via transfers the user initiates or pre-authorizes on a schedule.

---

---

> **Verbatim source — FS-CRAWL-001 v1.4: 3. Excluded Subsystems (Negative Requirements) — complete section, verbatim and unabridged**

## 3. Excluded Subsystems (Negative Requirements)

The following capabilities are **out of scope and shall not be implemented**. If any requirement elsewhere in this document appears to conflict with this section, this section prevails.

| # | Excluded capability | Prohibited behavior in the target product |
|---|---|---|
| 1 | Machine identity and OS fingerprinting | No reading of OS registration stores, management instrumentation services, platform I/O registries, hardware serials, or any OS-level machine identifier. No persistent hardware-derived identity of any kind is generated, stored, or transmitted. |
| 2 | Licensing client and state model | No license, subscription, trial, or seat concepts; no entitlement state machine; no license cache files; no validation, activation, deactivation, expiry, grace periods, or periodic recheck services; no identity-bearing authentication headers toward any vendor service (user-configured destinations use the user's own credentials, section 8.5). |
| 3 | Account system | No sign-in, registration, password reset, or user profile. |
| 4 | Remote control plane and relay | No installation enrollment, no cloud relay bridging local tool servers to remote clients, no heartbeat or liveness reporting, no remote credential issuance or rotation, no remote revocation channel. This exclusion targets **remote bridging only**: a loopback-only local automation interface implementing a local MCP server with locally generated credentials (section 11) is explicitly permitted. |
| 5 | Remote feature flags | No remote flag resolution or proxying. All features are enabled locally by default. |
| 6 | Telemetry and remote diagnostics | No usage analytics, no remote crash reporting, no identity-dimension log enrichment, and **no automatic transmission of any diagnostic data**. Explicitly permitted: local structured logs and locally generated **native diagnostic minidumps** for user troubleshooting. These artifacts are produced and stored only on the local filesystem, are never transmitted automatically, and are user-viewable and purgeable (sections 10.5, 12). |
| 7 | Server-gated functionality | No report-viewing authorization checks, no server-provided audit configuration, no vendor cloud audit logging or alerting, no account-bound update feeds, no marketing or newsletter hooks. These exclusions target vendor-operated services; destinations and notification endpoints the user configures themselves (sections 6.5, 8.5) are not vendor services and are permitted. |
| 8 | Entitlement-based limits | No crawl limit, feature, or report may vary with license type, account state, or any remote input. |

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: Document control & revision history (preamble, verbatim)**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 1. Document Control and Integration Map — complete section, verbatim and unabridged**

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
