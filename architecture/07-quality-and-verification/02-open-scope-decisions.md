# Open Scope Decisions

> **CrawlForge modular architecture — area of concern: `07-quality-and-verification`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §15 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 15. Open Scope Decisions — complete section, verbatim and unabridged**

## 15. Open Scope Decisions

The following questions are recorded as unresolved and are **not required by this version**; each must be resolved by the product owner and recorded here as an explicit scope statement either way:

1. **Unattended/headless execution** — whether audits can be started from a command line without opening any window. (Scheduled audits per section 6 already run without user interaction once configured, and the local MCP interface per section 11 allows programmatic control while the application is running.)
2. **Manual product-update path** — whether a local, user-initiated update installation mechanism is in scope. No update checking of any kind exists in this version.
3. **Third-party search/analytics/backlink data enrichment** — whether to import the user's own search-console, analytics, or backlink-provider data (and optional field-performance data) into reports using the user's own credentials. These are license-independent, user-owned integrations but reintroduce outbound traffic to third parties, so they are deliberately left as an explicit product-owner choice rather than mandated. If adopted, they follow the optional/off-by-default, user-credential, encrypted-at-rest pattern of section 8.5.
4. **AMP analysis** — whether Accelerated Mobile Pages discovery and validation are in scope. Not required by this version.
5. **Raw-SQL administration tool** — whether a direct query interface over the local database is offered as a power-user/debug tool (the interactive query builder per section 8.7 is already specified). Not required by this version.
