# Custom Extraction, Search & JavaScript Snippets

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §7.9, §7.16, §7.17 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.9 Custom Content Extraction — verbatim and unabridged**

### 7.9 Custom Content Extraction

- **Purpose:** beyond the fixed built-in inventory (section 7.3), the user may define **custom extraction rules** that pull arbitrary content from crawled pages into named, typed datasets for reporting and export.
- **Configuration:** custom extraction is an optional, off-by-default part of the audit configuration (section 5.1). It consists of an ordered list of rules; each rule defines:
  - A user-chosen **name**, which becomes the dataset column label.
  - An **extraction method**: an element-selector path evaluated against the document tree, or a pattern expression evaluated against element text or raw markup.
  - A **capture target**: element text, a named attribute, inner markup, or a pattern capture group (group selection is configurable for pattern rules).
  - An **output type**: text, integer, decimal, or URL; URL values are resolved to absolute form against the document base.
  - Optional **path scoping**: include/exclude URL-path directives restricting the rule to matching pages.
- **Execution:** rules run during per-URL crawl processing, after DOM parsing, against the same parsed document as the built-in inventory (no additional fetches); in rendering mode they run against the post-render DOM. Selector rules capture one value per matched element; pattern rules capture one value per match (or per configured capture group). Values are coerced to the configured type; conversion failures are recorded as invalid markers and never fail the crawl.
- **Mandatory bounds:**
  - A per-rule, per-page **cap on stored values**; excess matches are discarded and counted.
  - A per-value **length cap** with safe truncation that never splits a multi-byte character; truncations are counted.
  - Rule evaluation is **time-bounded** so pathological patterns cannot stall fetch workers.
  - Extraction errors, discards, and truncations are logged locally and surfaced as rule-level diagnostics; they never fail the audit.
- **Storage:** each captured value persists as a row keyed by (audit, URL, rule, ordinal) in the extracted-content table (section 9.2); per-rule aggregates (pages matched, values captured, discards, truncations) are computed for reporting.
- **Reporting and export:** a dedicated **custom extraction explorer** presents the dataset with dynamic columns — one column per rule, multi-valued rules expanding to as many columns as the busiest page — with filtering, sorting, and pagination (section 8.2). The dataset is included in spreadsheet and CSV exports (section 8.4). Extracted values are also available as inputs to declarative audit rules (section 7.6).
- **Rule management and preview:** rules are managed in the audit configuration surface (add, edit, reorder, delete) with a live preview against a sample URL before the crawl starts.
- **Re-audit behavior:** changing extraction rules and re-auditing re-extracts from stored bodies in the content store (section 9.6) without re-fetching.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.16 Custom content search — verbatim and unabridged**

### 7.16 Custom content search

- The user defines search items combining plain terms and/or regular expressions, with optional **negative terms** that must be absent for a match.
- Per-item URL-path include/exclude directives scope where the item runs; each item targets visible text (content-area-scoped when configured) and/or raw markup.
- Execution records per-URL match counts and matched snippets; an overview joins items with per-item affected-URL counts.
- Results are exposed through an explorer, CSV export, and the automation interface; the feature has an enable flag and re-runs on re-audit from stored bodies without network access.
- Mandatory bounds: regular-expression execution timeout and a maximum matches-per-page cap; failures are isolated and never fail the audit.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.17 Custom JavaScript snippets (optional) — verbatim and unabridged**

### 7.17 Custom JavaScript snippets (optional)

- Requires rendering mode. The user defines scripts executed in the rendered page, each with an execution timing, a timeout, URL-path matching, and declared result columns.
- Scripts run inside an isolated wrapper exposing a minimal host API (submit a data value, report an error, load an external script); return values are captured as typed results.
- A built-in example library and a single-URL test mode are provided.
- Results join URL data for a report surface and appear as dynamic columns in the query builder (section 8.7), with bounded row counts.
- Safety: execution is time-bounded, has no access to the local backend beyond the scoped wrapper API, and a snippet failure on one page never fails the audit or other pages.
