# Complete Schema Catalog

> **CrawlForge modular architecture — area of concern: `02-data-and-persistence`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §9 heading & introduction, §9.1, §9.2, §9.3, §9.4 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | Appendix A (complete) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9. Data Model (Abstract Schema) — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 9. Data Model (Abstract Schema)

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.1 Conventions — verbatim and unabridged**

### 9.1 Conventions

- One logical installation database, served by the configured provider (section 2.4); all crawl data is project-scoped.
- Surrogate integer keys; UTC timestamps; JSON text columns for flexible payloads.
- Projects and audits support soft delete with transactional pruning.
- **Secret protection (mandatory):** stored site credentials, destination credentials, external-database credentials, proxy credentials, and any local token or signing secrets are encrypted at rest using app-generated entropy persisted locally; key material is never derived from hardware or OS identity (section 3, row 1).

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.2 Table catalog — verbatim and unabridged**

### 9.2 Table catalog

| Table | Purpose | Key fields (abstract) | Notes |
|---|---|---|---|
| application_settings | Single-row preferences and defaults | UI preferences; default crawl limits; default audit toggles; storage options; database provider selection and connection settings (credentials encrypted); theme/locale | Seeded at first run with the default file-based provider. **No identity, license, or account columns exist.** |
| projects | Grouping of audits | Name; description; user tags/labels; created/updated timestamps; scope summary | |
| crawl_configurations | Reusable input sets | Project reference; seeds; scope rules; depth; limit overrides; rendering mode; politeness; proxy routing; custom extraction rules; optional headers/cookies | Sensitive optional values are encrypted at rest (section 9.1) |
| audits | One crawl-and-audit run | Project reference; configuration snapshot; status; started/finished timestamps; counters; engine version | Configuration is snapshotted so later edits never mutate history |
| url_records | Per-URL crawl facts | Audit reference; canonical URL; host; path; depth; discovery source; status class; final status; redirect target; content type; size; timing; robots state; indexability flags | Unique per (audit, canonical URL) |
| url_elements | Extracted DOM inventory | URL reference; element group; payload JSON; ordinal | One row per extracted element group instance |
| link_edges | Directed link graph | Audit reference; source URL reference; canonical target; target URL reference (nullable for external); anchor text; relation flags; internal/external marker | Supports orphan and graph analysis |
| findings | Audit judgments | Audit reference; rule key; category; severity; URL reference (nullable for site-wide); evidence JSON; recommendation key | |
| metric_summaries | Precomputed aggregates | Audit reference; metric key; dimensions JSON; numeric value | Powers dashboards and charts |
| crawl_checkpoints | Resume state | Audit reference; serialized frontier; seen-set reference; counters; saved-at timestamp | Written on pause and periodically during runs |
| schema_migrations | Applied versions | Version; provider dialect; applied-at timestamp | Idempotent migration ledger, per provider |
| saved_views (optional) | User filter presets | Name; target surface; filter JSON | Local convenience only |
| schedule_definitions | Recurring audit rules | Project/configuration references; recurrence rule; enabled flag; limit overrides; completion actions JSON; next-due timestamp | Local only (section 6) |
| schedule_runs | Schedule firing history | Schedule reference; audit reference (nullable on failure); trigger type; outcome; timestamps | Pruned by retention |
| export_destinations | User-configured delivery targets | Name; class; endpoint details; encrypted credential payload; enabled flag | Off by default; none seeded |
| user_dictionary | Accepted spellings | Language; word; added-at timestamp | Suppresses spelling findings (section 7.8) |
| content_artifacts | Content-store index | Audit reference; URL reference; artifact kind (body, screenshot, rendered DOM); storage key; size; content hash | Points into the content store (section 9.6) |
| url_embeddings (optional) | Similarity artifacts | Audit reference; URL reference; embedding payload; model tag | Supports vector-similarity analysis (section 2.4) |
| extracted_content | Custom extraction values | Audit reference; URL reference; rule name; ordinal; typed value payload; value type; invalid/truncated flags | One row per captured value (section 7.9); pruned with retention |
| content_search_results | Custom search matches (section 7.16) | Audit reference; URL reference; search-item reference; match count; matched snippets | Per-item aggregates for the overview |
| snippet_results | Custom JavaScript snippet outputs (section 7.17) | Audit reference; URL reference; snippet reference; typed result columns | Dynamic columns; bounded rows |
| technology_detections | Detected technologies (section 7.15) | Audit reference; URL reference; technology key; category; confidence | Feeds technology explorer and aggregate |
| audit_scores | Category and overall scores (section 8.1) | Audit reference; category (or overall); score; contributing counts | Comparable across audits; drives score trend |
| finding_suppressions | User-suppressed findings/rules (section 7.5) | Project reference; rule key and/or finding scope; suppressed flag; timestamp | Project-scoped; reversible; excluded from counts/scores |
| automation_action_log | Local agent invocation history | Timestamp; transport; tool/resource name; argument summary; outcome | User-viewable and purgeable (section 11); pruned by retention |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.3 Relationships and indexes — verbatim and unabridged**

### 9.3 Relationships and indexes

- Projects have many configurations; configurations have many audits; audits have many URL records, link edges, findings, and metric summaries; URL records have many element rows; internal link edges reference URL records on both ends.
- Schedules reference configurations and produce run-history rows linked to audits; content artifacts and embeddings belong to (audit, URL) pairs and are addressable across audits of the same project for comparison.
- Required indexes: unique (audit, canonical URL); (audit, status class); (audit, severity, category); (audit, rule key); link edges by (audit, source) and (audit, target); metrics by (audit, metric key); schedules by next-due time; run history by (schedule, started-at); content artifacts by (audit, URL, kind); extracted content by (audit, rule name) and (audit, URL); action log by timestamp.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.4 First-run initialization (mandatory) — verbatim and unabridged**

### 9.4 First-run initialization (mandatory)

- Migrations create all tables on the configured provider, then seed **exactly one application-settings row** with deterministic defaults: the local file-based database provider, unlimited crawl ceilings, all audit categories enabled, local-only feature toggles on, default politeness on.
- First-run state additionally includes: no schedules, no export destinations, no external database configured, an empty user dictionary, and the local MCP automation interface disabled (section 11).
- Initialization completes with **zero network calls**; the product is fully usable immediately after first launch, including on an air-gapped machine.

---

> **Verbatim source — FS-CRAWL-002 v1.1: Appendix A — Additive Data Model Catalog (extends FS-CRAWL-001 §9.2) — complete section, verbatim and unabridged**

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
