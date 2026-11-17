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
> Decisions: D-005, D-010, D-012 · Change records: CR-003

The following questions are recorded as unresolved and are **not required by this version**; each must be resolved by the product owner and recorded here as an explicit scope statement either way:

1. **Unattended/headless execution** — whether audits can be started from a command line without opening any window. (Scheduled audits per section 6 already run without user interaction once configured, and the local MCP interface per section 11 allows programmatic control while the application is running.)
2. **Manual product-update path** — **Resolved (CR-003, CR-014, D-010):** a user-initiated release check against GitHub Releases and manual or user-confirmed update installation are in scope; no automatic update checking exists. The release check is specified now and built once public releases exist. **[Amended by CR-003, D-005, D-012, D-010]**
3. **Third-party search/analytics/backlink data enrichment** — **Resolved (CR-003, CR-013, D-005, D-012):** the user's own Google Search Console, Google Analytics 4 and PageSpeed Insights data (including field-performance data) are in scope through user-connected integrations (`08-integrations`). Backlink-provider enrichment is out of scope; it may be proposed later in its own change record. **[Amended by CR-003, D-005, D-012, D-010]**
4. **AMP analysis** — whether Accelerated Mobile Pages discovery and validation are in scope. Not required by this version.
5. **Raw-SQL administration tool** — whether a direct query interface over the local database is offered as a power-user/debug tool (the interactive query builder per section 8.7 is already specified). Not required by this version.

---

## Amendment history

### CR-003 (2026-11-17; D-005, D-012, D-010)

Replaced text in §15 item 2:

> 2. **Manual product-update path** — whether a local, user-initiated update installation mechanism is in scope. No update checking of any kind exists in this version.
### CR-003 (2026-11-17; D-005, D-012, D-010)

Replaced text in §15 item 3:

> 3. **Third-party search/analytics/backlink data enrichment** — whether to import the user's own search-console, analytics, or backlink-provider data (and optional field-performance data) into reports using the user's own credentials. These are license-independent, user-owned integrations but reintroduce outbound traffic to third parties, so they are deliberately left as an explicit product-owner choice rather than mandated. If adopted, they follow the optional/off-by-default, user-credential, encrypted-at-rest pattern of section 8.5.
