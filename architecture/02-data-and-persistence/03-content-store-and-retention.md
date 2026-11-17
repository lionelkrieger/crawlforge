# Content Store & Retention

> **CrawlForge modular architecture — area of concern: `02-data-and-persistence`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §9.5, §9.6 |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | Appendix A (complete) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.5 Retention — verbatim and unabridged**

### 9.5 Retention

- User-configurable retention per project (keep the last N audits, or an age limit). Pruning removes dependent rows transactionally, including content artifacts, embeddings, extracted content, schedule run history, and automation action-log entries; orphaned content-store files are reclaimed. Periodic integrity checks and compaction keep the database healthy on either provider. Diagnostic artifacts (logs, minidumps) have their own bounded local retention (section 10.5).

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.6 Content store — verbatim and unabridged**

### 9.6 Content store

- Response bodies and rendered artifacts (screenshots, rendered DOM) are persisted as **compressed, managed files** in a dedicated directory on the local filesystem beside the database, independent of the database provider, indexed by the content-artifacts table; they are never held wholly in memory.
- Artifacts are keyed by (audit, URL, kind) and are addressable **across audits** of the same project, enabling re-audit without re-fetching (section 7.2) and content comparison (section 8.2).
- The store is pruned transactionally with audit retention (section 9.5); its disk usage is included in the user-visible storage reporting (section 13).

---

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
