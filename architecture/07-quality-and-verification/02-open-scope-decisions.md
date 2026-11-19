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
> Decisions: D-005, D-010, D-012, D-014 · Change records: CR-003, CR-011

The following questions are recorded as unresolved and are **not required by this version**; each must be resolved by the product owner and recorded here as an explicit scope statement either way:

1. **Unattended/headless execution** — whether audits can be started from a command line without opening any window. (Scheduled audits per section 6 already run without user interaction once configured, and the local MCP interface per section 11 allows programmatic control while the application is running.)
2. **Manual product-update path** — **Resolved (CR-003, CR-014, D-010):** a user-initiated release check against GitHub Releases and manual or user-confirmed update installation are in scope; no automatic update checking exists. The release check is specified now and built once public releases exist. **[Amended by CR-003, D-005, D-012, D-010]**
3. **Third-party search/analytics/backlink data enrichment** — **Resolved (CR-003, CR-013, D-005, D-012):** the user's own Google Search Console, Google Analytics 4 and PageSpeed Insights data (including field-performance data) are in scope through user-connected integrations (`08-integrations`). Backlink-provider enrichment is out of scope; it may be proposed later in its own change record. **[Amended by CR-003, D-005, D-012, D-010]**
4. **AMP analysis** — whether Accelerated Mobile Pages discovery and validation are in scope. Not required by this version.
5. **Raw-SQL administration tool** — **Resolved (CR-011, D-014):** a read-only SQLite snapshot of an audit with a documented schema (section 11.8) serves direct-query needs; no raw-SQL interface over the live database is provided. **[Amended by CR-011, D-014]**

---

## Amendment history

### CR-003 (2026-11-17; D-005, D-012, D-010)

Replaced text in §15 item 2:

> 2. **Manual product-update path** — whether a local, user-initiated update installation mechanism is in scope. No update checking of any kind exists in this version.
### CR-003 (2026-11-17; D-005, D-012, D-010)

Replaced text in §15 item 3:

> 3. **Third-party search/analytics/backlink data enrichment** — whether to import the user's own search-console, analytics, or backlink-provider data (and optional field-performance data) into reports using the user's own credentials. These are license-independent, user-owned integrations but reintroduce outbound traffic to third parties, so they are deliberately left as an explicit product-owner choice rather than mandated. If adopted, they follow the optional/off-by-default, user-credential, encrypted-at-rest pattern of section 8.5.
### CR-011 (2026-11-17; D-014)

Replaced text in §15 item 5:

> 5. **Raw-SQL administration tool** — whether a direct query interface over the local database is offered as a power-user/debug tool (the interactive query builder per section 8.7 is already specified). Not required by this version.

### 15.1 Deferred capabilities (CR-017)
> Decisions: D-017, D-020 · Change records: CR-017, CR-028

**[Added by CR-017]** Recorded as deferred: not specified now and not built in the next build-plan extension; revisited after the core end-to-end flows (#49 to #58) pass acceptance.

| Capability | Title |
|---|---|
| CAP-B2-077 | Detect conflicting entity information |
| CAP-B2-078 | Compare structured data against visible page content |
| CAP-B2-079 | Inconsistent organisation identity across a domain |
| CAP-D1-106 | Keyboard accessibility |
| CAP-E-131 | Topic and entity relationships |
| CAP-E-133 | Important pages with insufficient internal links |
| CAP-E-134 | Pages receiving internal links from irrelevant sections |
| CAP-E-135 | Navigation inconsistencies |
| CAP-E-136 | Pages with no route from the main navigation |
| CAP-F-145 | Heading-to-content consistency |
| CAP-F-147 | Content freshness indicators |
| CAP-F-150 | Title and body-content alignment |
| CAP-F-151 | Entity extraction |
| CAP-F-152 | Content-template comparison |
| CAP-I-195 | Prioritise by confidence |
| CAP-I-198 | Root-cause grouping |
| CAP-H-X01 | Offline website copy export |
| CAP-L-X03 | MySQL provider parity and schema authority (external MySQL-class provider; deferred by CR-028) |

