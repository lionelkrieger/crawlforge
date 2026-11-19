# Database Providers, CQRS & Aggregation

> **CrawlForge modular architecture — area of concern: `02-data-and-persistence`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §2.4, §8.1 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §1.4 rule 6 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.4 Persistence foundation — verbatim and unabridged**

### 2.4 Persistence foundation
> Capabilities: CAP-L-X02 · Decisions: D-020 · Change records: CR-028

- **Database provider.** Persistence is abstracted behind a provider contract. **This version ships one provider: a local file-based embedded database (SQLite-class)** in the per-user application data directory, with zero configuration, zero network and full offline operation. The provider contract is kept, so an **external client-server provider (MySQL-class) can be added later**; it is deferred (section 15.1 of `07-quality-and-verification/02-open-scope-decisions.md`). **[Amended by CR-028, D-020]**
- The **logical schema, read models, and migration ledger are provider-agnostic**; each provider supplies a dialect-specific migration path to the same logical schema version. Provider selection and connection settings live in application settings. Switching providers is an explicit, user-initiated operation using the archive export/import mechanism — never automatic.
- **Durability and integrity:** the file-based provider uses write-ahead logging; the external provider uses its native transactional durability. Foreign keys are enforced; all multi-row writes occur inside transactions on either provider.
- Schema evolution is handled by **versioned migrations** applied automatically at backend startup, before the API begins serving, on the configured provider.
- First-run initialization is deterministic and requires **zero network calls** on the default provider (section 9.4); an external provider is never contacted unless the user has configured it.
- Optional per-project archive export/import produces a self-contained local bundle and doubles as the provider-migration vehicle.
- **Command/query separation:** all crawl and audit ingestion flows through a transactional write path; every report, dashboard, and list read is served from precomputed, indexed read models. Read queries must never require full scans over raw crawl data at the target scale (section 13).
- **Required database capabilities** (implementation-agnostic; the configured provider must supply them natively or through loadable extensions): regular-expression predicates in queries; in-database delimited-text import/export; vector-similarity search over stored embeddings; extended mathematical and statistical functions. For the embedded provider, loading native extensions per platform is part of the self-contained packaging requirement (section 13).
- Response bodies and rendered artifacts persist in a dedicated **content store** (section 9.6) on the local filesystem beside the database, independent of the chosen provider, enabling re-audit without re-fetching and cross-audit content comparison.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.1 Aggregation into the database — verbatim and unabridged**

### 8.1 Aggregation into the database
> Capabilities: CAP-B-048, CAP-E-119, CAP-E-120, CAP-E-121, CAP-E-122, CAP-E-123, CAP-E-124, CAP-E-132, CAP-E-X03, CAP-H-179, CAP-H-180, CAP-I-202, CAP-I-X02

- During and after an audit, the backend computes summary metrics into dedicated aggregate tables on the configured provider (section 2.4):
  - URL counts by status class, content type, depth, and indexability.
  - Finding counts by severity and category.
  - Crawl health: errors, redirects, blocked and skipped counts.
  - Distributions: response time, page weight, word count.
  - Link-graph statistics: orphan pages, hub pages, and reachability, computed by standard graph traversal from the seeds; **link-equity distribution** (inlink counts, percentile ranking, and concentration across pages).
  - **Audit scoring:** a per-category score and an overall score derived from finding counts and severities weighted by indexable-URL coverage; a disabled category contributes no score; suppressed findings are excluded (section 7.5). Scores are stored per audit so they are comparable across audits and drive a score-trend surface.
- Aggregates are keyed by audit and metric with a dimension payload, so dashboard reads are constant-cost lookups rather than full scans.

---

> **Verbatim source — FS-CRAWL-002 v1.1: §1.4 rule 6 — verbatim excerpt (the complete §1.4 invariant set lives in 01-foundation-and-governance/01-clean-room-and-exclusions.md)**

6. **Provider agnosticism.** Every new schema object is logical (§2.4, §9.1); both the embedded file-based and the optional external client-server provider implement it. Aggregate views are materialized as precomputed read models where the provider lacks native view support (§8.1 pattern).

---

## Amendment history

### CR-028 (2026-11-17; D-020)

Replaced text in §2.4 (configurable database providers):

> - **Configurable database providers.** Persistence is abstracted behind a provider contract; the application is not restricted to a single storage engine. Two provider classes are supported:
>   - **Default — local file-based embedded database (SQLite-class):** resides in the per-user application data directory; zero configuration, zero network, fully offline.
>   - **Optional — external client-server relational database (MySQL-class):** for high-volume deployments. Addressed by a user-configured connection (host, port, database name, credentials). The server is the **user's own infrastructure**; credentials are encrypted at rest (section 9.1). No vendor-hosted database exists or is contacted.
