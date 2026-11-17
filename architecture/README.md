# CrawlForge — Modular Architecture Documentation

| Document control | Value |
|---|---|
| Identifier | FS-CRAWL-ARCH (modular decomposition) |
| Title | CrawlForge Architecture Hierarchy — Domain-Organized Decomposition of FS-CRAWL-001 & FS-CRAWL-002 |
| Version | 2.0 |
| Status | **Master (living).** Amended only through change records (`changes/INDEX.md`). |
| Baseline sources (historical, frozen) | `architecture/baseline/functional-specification.md` (FS-CRAWL-001 v1.4) · `architecture/baseline/crawlforge-extensions-and-ui-stack.md` (FS-CRAWL-002 v1.1) |
| Scope | v1.0: structural modularization only (no implementation plans, build sequences or code scaffolds). v2.0: living master specification; build plans are derived from it. |
| Authority | This hierarchy is the authoritative specification. It was derived losslessly from FS-CRAWL-001 v1.4 and FS-CRAWL-002 v1.1 (see `baseline/`) and is amended only through change records. v1.0 fidelity guarantee: every rule, formula, default, table, route, event, MCP tool/resource/prompt and acceptance criterion from both baseline documents was reproduced verbatim (see section 4). |
| Chinese Wall | Maintained. All clean-room constraints, precedence invariants (FS-CRAWL-002 §1.4 rules 1–9), and negative requirements (FS-CRAWL-001 §1.2, §3; FS-CRAWL-002 §1.2, §1.6 E1–E9) are preserved in full. |

> **This README is the only summarizing document.** It is the Architectural Summary, Directory Guide, and Fidelity Verification Ledger. Every other file in this hierarchy is a verbatim, unabridged reproduction of source text. The two root master files remain **untouched** as the immutable sources of truth.

> **v2.0 (2026-11-17, CR-001):** the baseline documents are frozen in `baseline/` as historical record. From v2.0 this hierarchy is the living master. Domain-file text reproduced from the baseline remains in force unless a change record amends it; amendments are marked inline and the replaced text is kept in an amendment history block. Capability traceability lines (`> Capabilities: ...`) link sections to the capability register in `audit-work/register/` (see `TRACEABILITY.md`).

---

## 0. Change Protocol (from v2.0)

Operating rules: `audit-work/SOURCE-OF-TRUTH-LOOP.md`. In summary:

1. No architecture text is edited without a change record `changes/CR-nnn.md`, typed `addition`, `amendment`, `traceability` or `clarification`, citing the owner decisions (`audit-work/decisions-log.md`, `D-nnn`) and register capabilities/findings (`CAP-...`, `F-nnnn`) that drive it.
2. Only CRs with status `approved` in `changes/INDEX.md` may be applied. The owner approves.
3. Amended text is marked inline `**[Amended by CR-nnn, D-nnn]**`; the replaced text moves to an "Amendment history" block at the end of that domain file.
4. Every spec section carries a traceability line under its heading: `> Capabilities: CAP-... · Decisions: D-... · Change records: CR-...`. `TRACEABILITY.md` is generated from these lines by `audit-work/register/check_traceability.py`.
5. One CR, one git commit.

## 1. System Overview

CrawlForge is a **standalone, offline-first desktop website crawler and auditor**. An Electron shell spawns and supervises a local ASP.NET Core backend; the two communicate over loopback REST and a real-time WebSocket-style hub. All persistence is local (an embedded file-based database by default, with an optional user-owned external client-server provider). The product crawls a target site, runs a large battery of SEO/accessibility/performance/content audit rules, and renders every report offline from local aggregates. There are **no accounts, no licenses, no entitlements, no telemetry, and no vendor services**; outbound traffic is confined to user-configured crawl targets, proxies, export destinations, and an optional external database.

The specification is delivered as two companion documents that are read together as one set:

- **FS-CRAWL-001 v1.4** — the normative core baseline: process model, crawl engine, audit engine, persistence, reporting, REST/hub contract, local MCP server, and desktop shell (§1–§15, ending at acceptance criterion #34).
- **FS-CRAWL-002 v1.1** — the additive extension: WAF soft-block detection, AIMD adaptive politeness, content-area scoping, N-gram analysis, local UI plugins, memory/virtualization mandates, the concrete frontend stack (§§2–8), plus six v1.1 modules — List Mode & redirect-matrix validation, offline server-log intelligence, local vector embeddings & semantic linking, AI-search (GEO) readiness & bot governance, MarTech/analytics/tag auditing, and contextual link weighting / pixel-SERP / header equivalency (§§10–15). It appends acceptance criteria #35–#48 and Additive Appendices A–E. **Every extension is additive**: it hooks an identified parent contract without altering the parent's semantics.

This hierarchy reorganizes the union of both documents into **seven areas of concern**. Each domain file seamlessly unifies the baseline (FS-CRAWL-001) and extended (FS-CRAWL-002) requirements for its area, so a reader can open one file and see the complete, current contract for that concern.

### 1.1 Unified architecture diagram

```
                 ┌──────────────────────────────────────────────────────────────┐
                 │  06 · FRONTEND SHELL & UI  (Electron renderer)                │
                 │  React + Mantine v7/v8 + Zustand store                        │
                 │  Apache ECharts · Sigma.js + Graphology · Finder picker       │
                 │  50-row DOM virtualization (60 FPS @ 500k+) · local plugins   │
                 │  report surfaces · explorers · standalone tools · status bar  │
                 └───────────────▲──────────────────────────────┬───────────────┘
                                 │ REST (loopback) + hub events  │ local UI assets only
                 ┌───────────────┴──────────────────────────────▼───────────────┐
                 │  05 · API, REAL-TIME HUB & LOCAL MCP                          │
                 │  REST contract · WebSocket hub · pull-only /metrics           │
                 │  local MCP server (stdio + loopback SSE) · delegated agents   │
                 └───────────────▲──────────────────────────────┬───────────────┘
                                 │                              │
          ┌──────────────────────┴───────────┐   ┌─────────────▼──────────────────────────┐
          │  03 · CRAWL ENGINE               │   │  04 · AUDIT & ANALYSIS ENGINE          │
          │  limits resolver · AIMD          │──▶│  8-step pipeline · content scoping     │
          │  politeness · frontier · fetch   │bodies│ rule taxonomy · finding model        │
          │  WAF 200-OK detection · List Mode│   │  SEO/indexability · a11y · CWV         │
          │  rendering · proxy · schedules   │   │  custom extraction/search/JS           │
          └──────────────▲───────────────────┘   │  N-grams · vector semantics · GEO      │
                         │                       │  MarTech/tag audit · log analyzer      │
                         │                       └─────────────▲──────────────────────────┘
                         │                                     │ read/write + aggregates
                 ┌───────┴─────────────────────────────────────┴───────────────┐
                 │  02 · DATA & PERSISTENCE                                     │
                 │  SQLite / MySQL providers · WAL · CQRS read/write split      │
                 │  required extensions · unified schema catalog · content store│
                 │  transactional retention · archive import/export             │
                 └──────────────────────────────────────────────────────────────┘

  ══════════════════════════════════════════════════════════════════════════════
   CROSS-CUTTING FOUNDATIONS (apply to every layer above)
   01 · FOUNDATION & GOVERNANCE — Chinese Wall · precedence invariants (rules 1–9)
        negative requirements (#1–8, E1–E9) · process model & lifecycle · Server GC
        network boundary & security (loopback-only, encryption at rest, DNS consent)
   07 · QUALITY & VERIFICATION — 48 mandatory acceptance criteria (#1–#48)
        open product-D-017s (#1–#5)
  ══════════════════════════════════════════════════════════════════════════════
```

### 1.2 How the areas of concern interact

- **01 Foundation & Governance** is the constitution: the Chinese Wall, the additive-only precedence invariants, and the negative requirements constrain every other layer. The process model (Electron shell + ASP.NET Core backend) and the loopback network boundary are the physical envelope everything runs inside.
- **03 Crawl Engine** produces raw facts (URL records, link edges, stored bodies/DOM/screenshots) and writes them through **02 Data & Persistence**. The AIMD controller and WAF detection shape *how* fetching happens; List Mode and the redirect matrix shape *what* is fetched.
- **04 Audit & Analysis Engine** consumes the stored artifacts (never re-fetching for re-audit) and emits findings, element inventory, and aggregates back into **02**. Content scoping defines the text all content metrics operate on; the N-gram, vector, GEO, MarTech, and log modules are additive analysis passes over the same dataset.
- **02 Data & Persistence** is the single source of truth: the unified schema catalog (25 baseline tables + 15 additive tables/views + activated `url_embeddings`), the content store, and retention. Aggregation and scoring (§8.1) live here so dashboards read precomputed aggregates.
- **05 API / Hub / MCP** exposes everything read-mostly to the shell and to local agents: REST for CRUD and lifecycle, the hub for throttled real-time progress, `/metrics` for pull-only diagnostics, and the local MCP server for delegated agent flows. It adds no capability beyond the local boundary.
- **06 Frontend Shell & UI** renders every surface offline from local aggregates, under the strict 50-row virtualization mandate and the mandated technology stack, and hosts the local plugin system.
- **07 Quality & Verification** is the acceptance gate: the 48 criteria and the open scope decisions that bound what is (and is not) required.

---

## 2. Directory Map & Area-of-Concern Index

```
E:\Crawlforge\architecture\
│   README.md                                   ← this file (summary, guide, traceability, fidelity ledger)
├───01-foundation-and-governance\
│       01-clean-room-and-exclusions.md
│       02-process-model-and-lifecycle.md
│       03-network-boundary-and-security.md
├───02-data-and-persistence\
│       01-database-providers-and-cqrs.md
│       02-complete-schema-catalog.md
│       03-content-store-and-retention.md
├───03-crawl-engine\
│       01-limits-and-aimd-politeness.md
│       02-frontier-fetching-and-waf.md
│       03-rendering-and-proxy-routing.md
│       04-scheduled-audits.md
├───04-audit-and-analysis-engine\
│       01-pipeline-and-content-scoping.md
│       02-core-seo-and-indexability-rules.md
│       03-accessibility-and-cwv-performance.md
│       04-custom-extraction-search-js.md
│       05-ngrams-and-vector-semantics.md
│       06-ai-geo-and-martech-auditing.md
│       07-log-file-analyzer.md
├───05-api-hub-and-mcp\
│       01-rest-api-contract.md
│       02-realtime-websocket-hub.md
│       03-local-mcp-server-and-agents.md
│       04-metrics-and-diagnostics.md
├───06-frontend-shell-and-ui\
│       01-electron-shell-and-ui-stack.md
│       02-virtualization-and-explorers.md
│       03-local-ui-plugin-system.md
└───07-quality-and-verification\
        01-master-acceptance-criteria.md
        02-open-scope-decisions.md
```

**26 domain files + this README = 27 Markdown files.** The table below states the exact functional domain, tables, and contracts inside each file.

### 01 · Foundation & Governance

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-clean-room-and-exclusions.md` | Purpose, Chinese Wall, precedence & negative requirements | Both document-control preambles; FS-001 §1 (purpose, Chinese Wall rules, product principles) & §3 (negative requirements #1–#8); FS-002 §1.1–§1.6 (integration map, precedence invariants **rules 1–9**, conventions, exclusions **E1–E9**). |
| `02-process-model-and-lifecycle.md` | Process model, lifecycle, runtime profile, packaging | FS-001 §2.1–§2.3, §2.5, §2.7 (Electron + ASP.NET Core process model, channels, startup/shutdown/crash recovery, feature availability, background services) & §13 (Server GC profile, NFRs); FS-002 §6.2 (plugin scan insertion) & §8.9 (offline packaging/licensing manifest). |
| `03-network-boundary-and-security.md` | Loopback boundary, outbound rules, secrets | FS-001 §2.6 (network boundary) & §9.1 (encryption-at-rest conventions); FS-002 §1.4 **rule 9** (loopback-only daemons + optional DNS consent), §6.4 (plugin sandbox/capability), §6.8 (plugin privacy invariants), §11.1 (offline log boundary), §12.2 (loopback-only embedding providers). |

### 02 · Data & Persistence

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-database-providers-and-cqrs.md` | Providers, CQRS, extensions, aggregation & scoring | FS-001 §2.4 (SQLite/MySQL provider contracts, WAL, CQRS read/write split, required regex/delimited-text/vector-similarity/math-stats extensions) & §8.1 (aggregation into DB, audit scoring); FS-002 §1.4 **rule 6** (provider neutrality). |
| `02-complete-schema-catalog.md` | Unified schema catalog | FS-001 §9.1–§9.4 (**25 baseline tables**, relationships, indexes, first-run init) + FS-002 **Appendix A** (**15 additive tables/views**, activated `url_embeddings`, additive columns/element groups/metric keys/rule keys, all required indexes). |
| `03-content-store-and-retention.md` | Content store & retention | FS-001 §9.5 (transactional retention pruning) & §9.6 (compressed body/DOM/screenshot store, content-hash addressing, archive import/export); FS-002 Appendix A (retention of additive tables). |

### 03 · Crawl Engine

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-limits-and-aimd-politeness.md` | Limits resolver & AIMD politeness | FS-001 §4.1–§4.4 (Unlimited resolver, config parameters, validation invariants, politeness/robots.txt); FS-002 §3.1–§3.8 (full AIMD control law, observables, parameters, checkpointing, `host_throttle_events`). |
| `02-frontier-fetching-and-waf.md` | Inputs, frontier, fetching, WAF, List Mode | FS-001 §5.1–§5.5, §5.7, §5.8 (audit inputs, canonicalization, frontier, fetching, link discovery, lifecycle, storage); FS-002 §2.1–§2.9 (WAF 200-OK fingerprinting, `waf-challenge`, signature catalogue, corroboration guard, render-resolution) & §10.1–§10.9 (List Mode, regex rewriting, migration redirect matrix, `redirect_mappings`). |
| `03-rendering-and-proxy-routing.md` | Rendering & proxy | FS-001 §5.6 (headless Chromium, infinite-scroll, stealth), §5.9 (upstream + built-in loopback intercepting proxy), §5.10 (signed-request crawler access). |
| `04-scheduled-audits.md` | Scheduled audits | FS-001 §6.1–§6.5 (schedule model, catch-up/no-overlap execution, run history, UI/contract, user-authorized completion actions). |

### 04 · Audit & Analysis Engine

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-pipeline-and-content-scoping.md` | Pipeline & content scoping | FS-001 §7.1–§7.7 (8-step pipeline, element inventory, rule taxonomy, finding model & suppression, rule execution, single-page analysis); FS-002 §4.1–§4.7 (Scoped Visible Text Tree, `content_scope_profiles`, `scope-preview`). |
| `02-core-seo-and-indexability-rules.md` | Core SEO, indexability, directive equivalency | FS-001 §7.8, §7.12–§7.15, §7.18 (spelling, readability/soft-404, structured data, mobile-friendliness, technology detection, response-vs-rendered); FS-002 §15.1–§15.6 (`link_edges.dom_zone` weighting, pixel-width SERP/social measurement, header-vs-HTML equivalency). |
| `03-accessibility-and-cwv-performance.md` | Accessibility & performance | FS-001 §7.10 (accessibility rule families) & §7.11 (rendered Core Web Vitals, performance budgets, code coverage, page composition). |
| `04-custom-extraction-search-js.md` | Custom extraction / search / JS | FS-001 §7.9 (Custom Content Extraction), §7.16 (Custom Content Search), §7.17 (Custom JavaScript Snippets). |
| `05-ngrams-and-vector-semantics.md` | N-grams & vector semantics | FS-002 §5.1–§5.9 (4-zone N-gram/topical-density analyzer, `ngram_frequencies`, `ngram_site_summaries`) & §12.1–§12.10 (local ONNX/loopback embeddings, semantic cannibalization, link opportunities, `semantic_link_opportunities`, `semantic_clusters`). |
| `06-ai-geo-and-martech-auditing.md` | GEO & MarTech auditing | FS-002 §13.1–§13.6 (`llms.txt`/`llms-full.txt` validator, AI-crawler governance matrix, generator tool, `governance_matrix`) & §14.1–§14.6 (GTM/GA4/Ads/Pixel/Clarity inventory, `dataLayer` & Consent Mode v2, `analytics_detections`). |
| `07-log-file-analyzer.md` | Server-log intelligence | FS-002 §11.1–§11.9 (offline streaming ingestion, bot UA + bundled CIDR verification, optional DNS, True Orphans / Crawl Budget Waste / Uncrawled Indexable correlation, `log_imports`, `log_url_aggregates`). |

### 05 · API, Hub & MCP

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-rest-api-contract.md` | Unified REST contract | FS-001 §10.1, §10.2, §10.4 (conventions, full baseline REST mapping, error/edge behavior) + FS-002 **Appendix B** (**37 additive routes** + extended route parameters). |
| `02-realtime-websocket-hub.md` | Unified real-time hub | FS-001 §10.3 (**11 baseline events**) + FS-002 §7.2 & **Appendix C** (**8 additive events** + additive payload fields). |
| `03-local-mcp-server-and-agents.md` | Local MCP server & agents | FS-001 §11.1–§11.6 (mandate, stdio + loopback SSE transports, **8 lifecycle tools**, **7 baseline resources + 3 prompts**, delegated AI flows, credentials) + FS-002 **Appendix D** (**8 additive resources + 2 prompts**). |
| `04-metrics-and-diagnostics.md` | Metrics & diagnostics | FS-001 §10.5 (pull-only loopback `/metrics`, local logs, crash minidumps); FS-002 §3.7 (host-throttle families) & §7.3 (memory/GC/WAL families). |

### 06 · Frontend Shell & UI

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-electron-shell-and-ui-stack.md` | Shell & mandated UI stack | FS-001 §8.3 (chart requirements) & §12 (window catalog & duties); FS-002 §7.2, §7.4, §7.5 (status-bar telemetry, memory hygiene, performance budgets) & §8.1–§8.9 (React, Mantine v7/v8, Zustand, ECharts, Sigma.js + Graphology, Finder picker, state store, packaging). |
| `02-virtualization-and-explorers.md` | Virtualization, surfaces, tools | FS-002 §7.1 (50-row DOM virtualization) & §8.3 (TanStack/AG Grid); FS-001 §8.2, §8.4–§8.7 (16 baseline report surfaces, exports, destinations, standalone tools); FS-002 §10.9, §11.9, §12.10, §13.3, §13.6, §14.6, §15.6 (6 extended surfaces + `scope-preview`/`rewrite-preview`/`llms.txt` tools). |
| `03-local-ui-plugin-system.md` | Local UI plugins | FS-002 §6.1–§6.8 (plugin directories, `plugin.json` manifest, sandbox CSP & capability bridge, `onLoad`/`onTabActivate`/`onDataUpdate`/`onCrawlComplete` hooks, UI contributions, `ui_plugins` registry). |

> **v2.0 addition (CR-018):** `06-frontend-shell-and-ui/04-ui-surface-map.md` holds the UI surface catalogue (UI-01 to UI-24) and the configure/trigger/view binding for every in-scope capability.

### 07 · Quality & Verification

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-master-acceptance-criteria.md` | Master acceptance checklist | FS-001 §14 (**#1–#34**) + FS-002 §9 (**#35–#48**) = **48 mandatory criteria**, unabridged; plus v2.0 §10 end-to-end criteria **#49–#58** (CR-015). |
| `02-open-scope-decisions.md` | Open scope decisions | FS-001 §15 (product-owner decisions **#1–#5**; items 2, 3, 5 resolved in v2.0) plus deferred capabilities (CR-017). |
| `03-fixture-site.md` | Fixture site and end-to-end acceptance | Trap set, JSON expected results, acceptance rule (CR-015). Criteria **#49–#58** are in `01-master-acceptance-criteria.md` §10. |

### 08 · Integrations (v2.0)

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-google-integrations.md` | Search Console, GA4, PageSpeed Insights | Own OAuth client / API key, per-project toggles, sync, local cache, CSV fallback (CR-013). |

### 09 · Distribution (v2.0)

| File | Area of concern | Key contents (tables / contracts) |
|---|---|---|
| `01-packaging-and-release.md` | Packaging and release | Installer, code signing, GitHub Releases, licence audit, platforms (CR-014). |

---

## 3. Bidirectional Traceability & Fidelity Matrix

This matrix proves that **every section and appendix** of FS-CRAWL-001 (§1–§15) and FS-CRAWL-002 (§1–§15, Appendices A–E) — including every subsection and both document-control preambles — is accounted for in a target file, with **zero orphaned requirements**. Section heads of split sections are preserved verbatim in their designated home file (marked *"heading & introduction only"*); their subsections are distributed as shown in the ↳ rows.

### Forward matrix — source section → architecture file(s)

#### FS-CRAWL-001 v1.4

| Source section | Heading | Architecture file(s) |
|---|---|---|
| Preamble (document control) | Document control & revision history | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| §1 | 1. Purpose and Clean-Room Protocol | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.1 | 1.1 Purpose | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.2 | 1.2 Chinese Wall rules | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.3 | 1.3 Product principles | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| §2 | 2. Architectural Foundation | `01-foundation-and-governance/02-process-model-and-lifecycle.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §2.1 | 2.1 Process model | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| ↳ §2.2 | 2.2 Communication channels | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| ↳ §2.3 | 2.3 Startup, shutdown, and recovery | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| ↳ §2.4 | 2.4 Persistence foundation | `02-data-and-persistence/01-database-providers-and-cqrs.md` |
| ↳ §2.5 | 2.5 Feature availability | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| ↳ §2.6 | 2.6 Network boundary | `01-foundation-and-governance/03-network-boundary-and-security.md` |
| ↳ §2.7 | 2.7 Background services | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| §3 | 3. Excluded Subsystems (Negative Requirements) | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| §4 | 4. Crawler Limits Subsystem | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §4.1 | 4.1 Resolver contract | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §4.2 | 4.2 Configuration parameters | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §4.3 | 4.3 Validation and invariants | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §4.4 | 4.4 Politeness (independent of limits) | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| §5 | 5. Crawl Engine | `03-crawl-engine/02-frontier-fetching-and-waf.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §5.1 | 5.1 Audit inputs | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.2 | 5.2 URL canonicalization and deduplication | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.3 | 5.3 Frontier and scheduling | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.4 | 5.4 Fetching | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.5 | 5.5 Link discovery | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.6 | 5.6 Rendering mode (optional) | `03-crawl-engine/03-rendering-and-proxy-routing.md` |
| ↳ §5.7 | 5.7 Lifecycle and control | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.8 | 5.8 Storage during crawl | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §5.9 | 5.9 Local proxy and traffic routing | `03-crawl-engine/03-rendering-and-proxy-routing.md` |
| ↳ §5.10 | 5.10 Signed-request crawler access (optional) | `03-crawl-engine/03-rendering-and-proxy-routing.md` |
| §6 | 6. Scheduled Audits | `03-crawl-engine/04-scheduled-audits.md` |
| ↳ §6.1 | 6.1 Schedule model | `03-crawl-engine/04-scheduled-audits.md` |
| ↳ §6.2 | 6.2 Execution rules | `03-crawl-engine/04-scheduled-audits.md` |
| ↳ §6.3 | 6.3 Run history | `03-crawl-engine/04-scheduled-audits.md` |
| ↳ §6.4 | 6.4 UI and contract | `03-crawl-engine/04-scheduled-audits.md` |
| ↳ §6.5 | 6.5 Completion actions (optional, user-authorized) | `03-crawl-engine/04-scheduled-audits.md` |
| §7 | 7. Audit Engine | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §7.1 | 7.1 Inputs | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.2 | 7.2 Pipeline | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.3 | 7.3 Element extraction inventory | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.4 | 7.4 Rule taxonomy (generic, industry-standard) | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.5 | 7.5 Finding model | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.6 | 7.6 Rule execution architecture | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.7 | 7.7 Single-page analysis | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §7.8 | 7.8 Spelling checks | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §7.9 | 7.9 Custom Content Extraction | `04-audit-and-analysis-engine/04-custom-extraction-search-js.md` |
| ↳ §7.10 | 7.10 Accessibility auditing (optional category) | `04-audit-and-analysis-engine/03-accessibility-and-cwv-performance.md` |
| ↳ §7.11 | 7.11 Rendered performance and Core Web Vitals (optional category) | `04-audit-and-analysis-engine/03-accessibility-and-cwv-performance.md` |
| ↳ §7.12 | 7.12 Readability, content metrics, and soft-404 detection | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §7.13 | 7.13 Structured data validation | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §7.14 | 7.14 Mobile-friendliness (optional category) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §7.15 | 7.15 Technology detection (optional category) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §7.16 | 7.16 Custom content search | `04-audit-and-analysis-engine/04-custom-extraction-search-js.md` |
| ↳ §7.17 | 7.17 Custom JavaScript snippets (optional) | `04-audit-and-analysis-engine/04-custom-extraction-search-js.md` |
| ↳ §7.18 | 7.18 Response-vs-rendered comparison | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| §8 | 8. Local Reporting and Visualization | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §8.1 | 8.1 Aggregation into the database | `02-data-and-persistence/01-database-providers-and-cqrs.md` |
| ↳ §8.2 | 8.2 Report surfaces | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §8.3 | 8.3 Chart requirements | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.4 | 8.4 Export | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §8.5 | 8.5 Optional export destinations | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §8.6 | 8.6 Access | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §8.7 | 8.7 Standalone diagnostic and authoring tools | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §9 | 9. Data Model (Abstract Schema) | `02-data-and-persistence/02-complete-schema-catalog.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §9.1 | 9.1 Conventions | `01-foundation-and-governance/03-network-boundary-and-security.md`, `02-data-and-persistence/02-complete-schema-catalog.md` |
| ↳ §9.2 | 9.2 Table catalog | `02-data-and-persistence/02-complete-schema-catalog.md` |
| ↳ §9.3 | 9.3 Relationships and indexes | `02-data-and-persistence/02-complete-schema-catalog.md` |
| ↳ §9.4 | 9.4 First-run initialization (mandatory) | `02-data-and-persistence/02-complete-schema-catalog.md` |
| ↳ §9.5 | 9.5 Retention | `02-data-and-persistence/03-content-store-and-retention.md` |
| ↳ §9.6 | 9.6 Content store | `02-data-and-persistence/03-content-store-and-retention.md` |
| §10 | 10. API and Real-Time Contract | `05-api-hub-and-mcp/01-rest-api-contract.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §10.1 | 10.1 Conventions | `05-api-hub-and-mcp/01-rest-api-contract.md` |
| ↳ §10.2 | 10.2 REST mapping | `05-api-hub-and-mcp/01-rest-api-contract.md` |
| ↳ §10.3 | 10.3 Real-time hub | `05-api-hub-and-mcp/02-realtime-websocket-hub.md` |
| ↳ §10.4 | 10.4 Error and edge behavior | `05-api-hub-and-mcp/01-rest-api-contract.md` |
| ↳ §10.5 | 10.5 Local metrics and diagnostics | `05-api-hub-and-mcp/04-metrics-and-diagnostics.md` |
| §11 | 11. Local Automation Interface — Local MCP Server (Optional) | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.1 | 11.1 Mandate and boundary | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.2 | 11.2 Transport layers | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.3 | 11.3 Tools — lifecycle control | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.4 | 11.4 Resources and prompts — data consumption | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.5 | 11.5 Delegated AI control flows | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| ↳ §11.6 | 11.6 Credentials and lifecycle | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| §12 | 12. Desktop Shell Requirements | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| §13 | 13. Non-Functional Requirements | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| §14 | 14. Acceptance Criteria | `07-quality-and-verification/01-master-acceptance-criteria.md` |
| §15 | 15. Open Scope Decisions | `07-quality-and-verification/02-open-scope-decisions.md` |

#### FS-CRAWL-002 v1.1

| Source section | Heading | Architecture file(s) |
|---|---|---|
| Preamble (document control) | Document control & revision history | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| §1 | 1. Document Control and Integration Map | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.1 | 1.1 Purpose and relationship to the parent specification | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.2 | 1.2 Clean-room protocol (Chinese Wall maintenance) | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.3 | 1.3 Integration map | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.4 | 1.4 Precedence, compatibility, and invariants | `01-foundation-and-governance/03-network-boundary-and-security.md`, `02-data-and-persistence/01-database-providers-and-cqrs.md` |
| ↳ §1.5 | 1.5 Conventions and terminology | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| ↳ §1.6 | 1.6 Extension scope exclusions (negative requirements) | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| §2 | 2. Soft-Block and WAF Challenge Detection on 200 OK Responses | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.1 | 2.1 Problem and objective | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.2 | 2.2 Pipeline placement — pre-audit WAF & Challenge Fingerprinting Pass | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.3 | 2.3 Signature catalogue | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.4 | 2.4 Decision logic and state transition | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.5 | 2.5 Interaction with other modules and parent flows | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.6 | 2.6 Data model additions (extends §9.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.7 | 2.7 REST additions (extends §10.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.8 | 2.8 Real-time hub and MCP additions (extends §10.3, §11.4) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §2.9 | 2.9 UI requirements | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| §3 | 3. AIMD Auto-Adaptive Politeness Controller | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.1 | 3.1 Problem and placement | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.2 | 3.2 Observables | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.3 | 3.3 Parameters | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.4 | 3.4 Control law (mathematical contract) | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.5 | 3.5 Lifecycle, checkpointing, and recovery | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.6 | 3.6 Persistence (extends §9.2) | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| ↳ §3.7 | 3.7 Telemetry and contracts (extends §10.2, §10.3, §10.5, §11.4) | `05-api-hub-and-mcp/04-metrics-and-diagnostics.md` |
| ↳ §3.8 | 3.8 UI requirements | `03-crawl-engine/01-limits-and-aimd-politeness.md` |
| §4 | 4. Content Area Scoping and Boilerplate Stripping | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.1 | 4.1 Purpose and parent-contract gap | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.2 | 4.2 Configuration model | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.3 | 4.3 Scoped Visible Text Tree | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.4 | 4.4 Storage, diagnostics, and re-audit (extends §9.2) | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.5 | 4.5 Reporting behavior | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.6 | 4.6 REST and tool additions (extends §10.2, §8.7) | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| ↳ §4.7 | 4.7 UI requirements | `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` |
| §5 | 5. Site-Wide N-Gram and Topical Density Analyzer | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.1 | 5.1 Purpose and pipeline placement | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.2 | 5.2 Tokenization and stop-word pipeline | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.3 | 5.3 Zones | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.4 | 5.4 Metrics | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.5 | 5.5 Cannibalization and topical-gap detection | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.6 | 5.6 Mandatory bounds and schema additions (extends §9.2) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.7 | 5.7 REST additions (extends §10.2) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.8 | 5.8 MCP additions (extends §11.4) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §5.9 | 5.9 UI requirements — N-Gram Explorer | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| §6 | 6. Local UI Plugin Architecture and Lifecycle Hooks | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.1 | 6.1 Principles | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.2 | 6.2 Plugin directories and discovery | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| ↳ §6.3 | 6.3 Manifest contract (`plugin.json`) | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.4 | 6.4 Sandbox and capability contract | `01-foundation-and-governance/03-network-boundary-and-security.md` |
| ↳ §6.5 | 6.5 Lifecycle hook contract (deterministic, exposed by the frontend state store) | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.6 | 6.6 UI contribution contracts | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.7 | 6.7 Registry, management, and REST (extends §9.2, §10.2, §12) | `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` |
| ↳ §6.8 | 6.8 Security and privacy invariants | `01-foundation-and-governance/03-network-boundary-and-security.md` |
| §7 | 7. Real-Time Memory Profiling and UI Virtualization Mandates | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` *(heading & introduction only — subsections distributed, see ↳ rows)* |
| ↳ §7.1 | 7.1 Strict DOM virtualization | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §7.2 | 7.2 Live status-bar telemetry | `05-api-hub-and-mcp/02-realtime-websocket-hub.md`, `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §7.3 | 7.3 Metrics integration (extends §10.5) | `05-api-hub-and-mcp/04-metrics-and-diagnostics.md` |
| ↳ §7.4 | 7.4 Frontend memory hygiene | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §7.5 | 7.5 Performance budgets | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| §8 | 8. Mandated Frontend Technology Stack | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.1 | 8.1 Renderer architecture | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.2 | 8.2 UI component system — Mantine UI (v7/v8) | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.3 | 8.3 Virtualized data grids — TanStack Table + TanStack Virtual (or AG Grid Community) | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §8.4 | 8.4 High-density charting — Apache ECharts | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.5 | 8.5 Network and link graph visualization — Sigma.js + Graphology | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.6 | 8.6 Visual CSS/XPath selector picker — Finder (`@medv/finder`) | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.7 | 8.7 Stack summary | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.8 | 8.8 State store contract (plugin hook foundation) | `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` |
| ↳ §8.9 | 8.9 Offline, packaging, and licensing mandates | `01-foundation-and-governance/02-process-model-and-lifecycle.md` |
| §9 | 9. Additional Acceptance Criteria | `07-quality-and-verification/01-master-acceptance-criteria.md` |
| §10 | 10. List Mode, URL Rewriting & Migration Redirect Matrix Validator | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.1 | 10.1 Purpose | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.2 | 10.2 List Mode ingestion (extends §5.1) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.3 | 10.3 Redirect Migration Matrix Validator | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.4 | 10.4 On-the-fly URL rewriting (extends §5.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.5 | 10.5 Staging-vs-production host mapping (extends §8.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.6 | 10.6 Schema additions (extends §9.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.7 | 10.7 REST additions (extends §10.2) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.8 | 10.8 Hub and MCP additions (extends §10.3, §11.4) | `03-crawl-engine/02-frontier-fetching-and-waf.md` |
| ↳ §10.9 | 10.9 UI requirements | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §11 | 11. Local Server Log File Analyzer & True Orphan Discovery | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.1 | 11.1 Purpose and network boundary | `01-foundation-and-governance/03-network-boundary-and-security.md` |
| ↳ §11.2 | 11.2 Formats and streaming ingestion | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.3 | 11.3 Bot verification and classification | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.4 | 11.4 Aggregation | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.5 | 11.5 Crawl-vs-log correlation (extends §7.4, §8.1) | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.6 | 11.6 Schema additions (extends §9.2) | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.7 | 11.7 REST additions (extends §10.2) | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.8 | 11.8 Hub additions (extends §10.3) | `04-audit-and-analysis-engine/07-log-file-analyzer.md` |
| ↳ §11.9 | 11.9 UI requirements — Log File Analyzer | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §12 | 12. Local Vector Embeddings & Semantic Internal Linking Pipeline | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.1 | 12.1 Purpose — activating the dormant schema | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.2 | 12.2 Embedding providers (strictly local) | `01-foundation-and-governance/03-network-boundary-and-security.md` |
| ↳ §12.3 | 12.3 Input source and chunking | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.4 | 12.4 Storage and query | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.5 | 12.5 Semantic audit passes | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.6 | 12.6 Execution and bounds | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.7 | 12.7 Schema additions (extends §9.2) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.8 | 12.8 REST additions (extends §10.2) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.9 | 12.9 Hub additions (extends §10.3) | `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` |
| ↳ §12.10 | 12.10 UI requirements | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §13 | 13. AI Search (GEO) Readiness, `llms.txt` Validator & Bot Governance Matrix | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §13.1 | 13.1 `llms.txt` and `llms-full.txt` discovery and validation | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §13.2 | 13.2 AI crawler governance matrix | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §13.3 | 13.3 Standalone tool — `llms.txt` Generator & Validator (extends §8.7) | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| ↳ §13.4 | 13.4 Schema additions (extends §9.2) | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §13.5 | 13.5 REST additions (extends §10.2) | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §13.6 | 13.6 UI requirements | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §14 | 14. MarTech, Analytics & Tag Integrity Auditor | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.1 | 14.1 Automated tag and container inventory | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.2 | 14.2 `dataLayer` and Consent Mode v2 inspection (rendering mode) | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.3 | 14.3 Built-in audit rules — new category `Analytics & Tagging` | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.4 | 14.4 Schema additions (extends §9.2) | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.5 | 14.5 REST and MCP additions (extends §10.2, §11.4) | `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| ↳ §14.6 | 14.6 UI requirements — Analytics & Tagging Explorer | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| §15 | 15. Contextual Link Weighting, Pixel-Width SERP Measurement & Header/HTML Equivalency | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.1 | 15.1 DOM-zone link classification | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.2 | 15.2 Weighted link equity (extends §8.1) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.3 | 15.3 Pixel-width SERP and social card measurement (extends §7.3, §7.4) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.4 | 15.4 HTTP header vs HTML directive equivalency (extends §7.3, §7.4) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.5 | 15.5 Schema and API additions (extends §9.2, §10.2, §11.4) | `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| ↳ §15.6 | 15.6 UI requirements | `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` |
| Appendix A | Appendix A — Additive Data Model Catalog (extends FS-CRAWL-001 §9.2) | `02-data-and-persistence/02-complete-schema-catalog.md`, `02-data-and-persistence/03-content-store-and-retention.md` |
| Appendix B | Appendix B — Additive REST Contract (extends FS-CRAWL-001 §10.2) | `05-api-hub-and-mcp/01-rest-api-contract.md` |
| Appendix C | Appendix C — Additive Real-Time Hub Events (extends FS-CRAWL-001 §10.3) | `05-api-hub-and-mcp/02-realtime-websocket-hub.md` |
| Appendix D | Appendix D — Additive MCP Surface (extends FS-CRAWL-001 §11.4) | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| Appendix E | Appendix E — Cross-Reference Index | `README.md` |

### Reverse matrix — architecture file → source sections

| Architecture file | Source sections contained (verbatim) |
|---|---|
| `01-foundation-and-governance/01-clean-room-and-exclusions.md` | FS-CRAWL-001 Document control & revision history (preamble); FS-CRAWL-001 §1 (complete section); FS-CRAWL-001 §3 (complete section); FS-CRAWL-002 Document control & revision history (preamble); FS-CRAWL-002 §1 (complete section) |
| `01-foundation-and-governance/02-process-model-and-lifecycle.md` | FS-CRAWL-001 §2 heading & introduction; FS-CRAWL-001 §2.1; FS-CRAWL-001 §2.2; FS-CRAWL-001 §2.3; FS-CRAWL-001 §2.5; FS-CRAWL-001 §2.7; FS-CRAWL-001 §13 (complete section); FS-CRAWL-002 §6.2; FS-CRAWL-002 §8.9 |
| `01-foundation-and-governance/03-network-boundary-and-security.md` | FS-CRAWL-001 §2.6; FS-CRAWL-001 §9.1; FS-CRAWL-002 §1.4 rule 9; FS-CRAWL-002 §6.4; FS-CRAWL-002 §6.8; FS-CRAWL-002 §11.1; FS-CRAWL-002 §12.2 |
| `02-data-and-persistence/01-database-providers-and-cqrs.md` | FS-CRAWL-001 §2.4; FS-CRAWL-001 §8.1; FS-CRAWL-002 §1.4 rule 6 |
| `02-data-and-persistence/02-complete-schema-catalog.md` | FS-CRAWL-001 §9 heading & introduction; FS-CRAWL-001 §9.1; FS-CRAWL-001 §9.2; FS-CRAWL-001 §9.3; FS-CRAWL-001 §9.4; FS-CRAWL-002 Appendix A (complete) |
| `02-data-and-persistence/03-content-store-and-retention.md` | FS-CRAWL-001 §9.5; FS-CRAWL-001 §9.6; FS-CRAWL-002 Appendix A (complete) |
| `03-crawl-engine/01-limits-and-aimd-politeness.md` | FS-CRAWL-001 §4 (complete section); FS-CRAWL-002 §3 (complete section) |
| `03-crawl-engine/02-frontier-fetching-and-waf.md` | FS-CRAWL-001 §5 heading & introduction; FS-CRAWL-001 §5.1; FS-CRAWL-001 §5.2; FS-CRAWL-001 §5.3; FS-CRAWL-001 §5.4; FS-CRAWL-001 §5.5; FS-CRAWL-001 §5.7; FS-CRAWL-001 §5.8; FS-CRAWL-002 §2 (complete section); FS-CRAWL-002 §10 (complete section) |
| `03-crawl-engine/03-rendering-and-proxy-routing.md` | FS-CRAWL-001 §5.6; FS-CRAWL-001 §5.9; FS-CRAWL-001 §5.10 |
| `03-crawl-engine/04-scheduled-audits.md` | FS-CRAWL-001 §6 (complete section) |
| `04-audit-and-analysis-engine/01-pipeline-and-content-scoping.md` | FS-CRAWL-001 §7 heading & introduction; FS-CRAWL-001 §7.1; FS-CRAWL-001 §7.2; FS-CRAWL-001 §7.3; FS-CRAWL-001 §7.4; FS-CRAWL-001 §7.5; FS-CRAWL-001 §7.6; FS-CRAWL-001 §7.7; FS-CRAWL-002 §4 (complete section) |
| `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` | FS-CRAWL-001 §7.8; FS-CRAWL-001 §7.12; FS-CRAWL-001 §7.13; FS-CRAWL-001 §7.14; FS-CRAWL-001 §7.15; FS-CRAWL-001 §7.18; FS-CRAWL-002 §15 (complete section) |
| `04-audit-and-analysis-engine/03-accessibility-and-cwv-performance.md` | FS-CRAWL-001 §7.10; FS-CRAWL-001 §7.11 |
| `04-audit-and-analysis-engine/04-custom-extraction-search-js.md` | FS-CRAWL-001 §7.9; FS-CRAWL-001 §7.16; FS-CRAWL-001 §7.17 |
| `04-audit-and-analysis-engine/05-ngrams-and-vector-semantics.md` | FS-CRAWL-002 §5 (complete section); FS-CRAWL-002 §12 (complete section) |
| `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` | FS-CRAWL-002 §13 (complete section); FS-CRAWL-002 §14 (complete section) |
| `04-audit-and-analysis-engine/07-log-file-analyzer.md` | FS-CRAWL-002 §11 (complete section) |
| `05-api-hub-and-mcp/01-rest-api-contract.md` | FS-CRAWL-001 §10 heading & introduction; FS-CRAWL-001 §10.1; FS-CRAWL-001 §10.2; FS-CRAWL-001 §10.4; FS-CRAWL-002 Appendix B (complete) |
| `05-api-hub-and-mcp/02-realtime-websocket-hub.md` | FS-CRAWL-001 §10.3; FS-CRAWL-002 §7.2; FS-CRAWL-002 Appendix C (complete) |
| `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` | FS-CRAWL-001 §11 (complete section); FS-CRAWL-002 Appendix D (complete) |
| `05-api-hub-and-mcp/04-metrics-and-diagnostics.md` | FS-CRAWL-001 §10.5; FS-CRAWL-002 §3.7; FS-CRAWL-002 §7.3 |
| `06-frontend-shell-and-ui/01-electron-shell-and-ui-stack.md` | FS-CRAWL-001 §8.3; FS-CRAWL-001 §12 (complete section); FS-CRAWL-002 §7 heading & introduction; FS-CRAWL-002 §7.2; FS-CRAWL-002 §7.4; FS-CRAWL-002 §7.5; FS-CRAWL-002 §8 (complete section) |
| `06-frontend-shell-and-ui/02-virtualization-and-explorers.md` | FS-CRAWL-001 §8 heading & introduction; FS-CRAWL-001 §8.2; FS-CRAWL-001 §8.4; FS-CRAWL-001 §8.5; FS-CRAWL-001 §8.6; FS-CRAWL-001 §8.7; FS-CRAWL-002 §7.1; FS-CRAWL-002 §8.3; FS-CRAWL-002 §10.9; FS-CRAWL-002 §11.9; FS-CRAWL-002 §12.10; FS-CRAWL-002 §13.3; FS-CRAWL-002 §13.6; FS-CRAWL-002 §14.6; FS-CRAWL-002 §15.6 |
| `06-frontend-shell-and-ui/03-local-ui-plugin-system.md` | FS-CRAWL-002 §6 (complete section) |
| `07-quality-and-verification/01-master-acceptance-criteria.md` | FS-CRAWL-001 §14 (complete section); FS-CRAWL-002 §9 (complete section) |
| `07-quality-and-verification/02-open-scope-decisions.md` | FS-CRAWL-001 §15 (complete section) |
| `README.md` | FS-CRAWL-002 Appendix E (verbatim); traceability & fidelity ledger |

### Coverage assertion

- **FS-CRAWL-001 v1.4:** 1 preamble + 15 top-level sections + 71 subsections = **87 blocks**, all assigned.
- **FS-CRAWL-002 v1.1:** 1 preamble + 20 top-level blocks (15 sections + Appendices A–E) + 107 subsections = **128 blocks**, all assigned.
- **Orphaned requirements: 0.** Every block is reproduced verbatim in at least one domain file (or, for Appendix E, in this README). Cross-cutting fragments (e.g. FS-002 §1.4 rules 6 and 9, §9.1, §7.2) are intentionally reproduced in each file whose area of concern requires them, so each domain file is self-contained.

---

## 4. Fidelity Verification Ledger

### 4.1 Master-file integrity (immutable sources)

Both masters were hashed **before** decomposition and re-hashed **after**; they are byte-identical (never opened for writing).

| Master file | SHA-256 (before = after) | Lines |
|---|---|---|
| `functional-specification.md` (FS-CRAWL-001 v1.4) | `B4C7D79290D65EA5419118E167B2138BCD0EFAB5AD9D099D552D0B4CD6099948` | 895 |
| `crawlforge-extensions-and-ui-stack.md` (FS-CRAWL-002 v1.1) | `578BCCD15C5D8C02C4C75F4685A98D7E4AAB6B907E71CED4B5A9C72D4B581FE4` | 1405 |

### 4.2 Contract inventory (preserved in full)

| Contract class | Baseline (FS-CRAWL-001) | Additive (FS-CRAWL-002) | Total | Home file(s) |
|---|---|---|---|---|
| Database tables / views | 25 (§9.2) | 15 new (Appendix A) + activated `url_embeddings` | 40 + activation | `02-data-and-persistence/02-complete-schema-catalog.md` |
| REST routes | full mapping (§10.2) | 37 additive + extended params (Appendix B) | unified | `05-api-hub-and-mcp/01-rest-api-contract.md` |
| Real-time hub events | 11 (§10.3) | 8 additive + payload fields (Appendix C) | 19 | `05-api-hub-and-mcp/02-realtime-websocket-hub.md` |
| MCP lifecycle tools | 8 (§11.3) | 0 (unchanged) | 8 | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| MCP resources | 7 (§11.4) | 8 additive (Appendix D) | 15 | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| MCP prompts | 3 (§11.4) | 2 additive (Appendix D) | 5 | `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` |
| Acceptance criteria | #1–#34 (§14) | #35–#48 (§9) | 48 | `07-quality-and-verification/01-master-acceptance-criteria.md` |
| Open scope decisions | #1–#5 (§15) | — | 5 | `07-quality-and-verification/02-open-scope-decisions.md` |
| Precedence invariants | — | rules 1–9 (§1.4) | 9 | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| Negative requirements | #1–#8 (§3) | E1–E9 (§1.6) | 17 | `01-foundation-and-governance/01-clean-room-and-exclusions.md` |

> Note on counts: the additive data-model catalog comprises **15** new tables/views — 8 introduced in FS-CRAWL-002 v1.0 (`waf_detections`, `waf_user_signatures`, `host_throttle_events`, `content_scope_profiles`, `ngram_frequencies`, `ngram_site_summaries`, `user_stopwords`, `ui_plugins`) and 7 in v1.1 (`redirect_mappings`, `log_imports`, `log_url_aggregates`, `semantic_link_opportunities`, `semantic_clusters`, `governance_matrix`, `analytics_detections`) — plus the **activated** baseline `url_embeddings`. All are reproduced verbatim in Appendix A.

### 4.3 Decomposition method & guarantees

The hierarchy was produced by a deterministic, verbatim extraction (no hand-transcription), with four machine-checked guarantees:

1. **Exact partition / reconstruction.** Each master was split into a preamble + top-level section blocks (+ subsection blocks). Re-concatenating the partition reproduces the source **byte-for-byte** (modulo line-ending normalization). Result: `reconstruction EXACT` for both masters.
2. **Full coverage.** Every preamble, top-level section, and subsection is assigned to ≥1 target file. The coverage check reports **0 orphans**. Section heads of split sections are explicitly preserved so no heading/introduction text is lost.
3. **Verbatim substring check.** Every emitted block is asserted to be an exact substring of its master before writing.
4. **Write-then-re-read verification.** Each generated file is re-opened and every one of its blocks re-asserted present verbatim. All 26 files passed.

**Placeholder / truncation scan:** no `TODO`, `TBD`, `PLACEHOLDER`, `FIXME`, `[insert…]`, lorem-ipsum, or summarization markers exist in any domain file. (Two substring matches — the schema field `invalid/truncated flags` and the phrase `summarized into metric_summaries` — are legitimate verbatim source text, not placeholders.)

**Acceptance completeness:** the master checklist contains exactly the integers **1…48 with no gaps**.

### 4.4 Notation preserved

Cross-references inside reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. Use the forward matrix above to resolve any `§n` to its file(s) in this hierarchy. Each domain file carries a provenance header listing its exact source sections and a per-block *"Verbatim source"* banner.

---

## 5. Source Cross-Reference Index (FS-CRAWL-002 Appendix E — verbatim)

The extension specification's own cross-reference index is preserved here in full (it is the only appendix whose natural home is this ledger). It maps each FS-CRAWL-001 contract to the FS-CRAWL-002 sections that extend it.

### Appendix E — Cross-Reference Index (verbatim)

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

---

*End of FS-CRAWL-ARCH README. This hierarchy decomposes but never modifies FS-CRAWL-001 v1.4 (`functional-specification.md`) or FS-CRAWL-002 v1.1 (`crawlforge-extensions-and-ui-stack.md`), which remain the immutable sources of truth in `E:\Crawlforge\`.*
