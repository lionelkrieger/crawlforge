# Server Log File Analyzer

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §11 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 11. Local Server Log File Analyzer & True Orphan Discovery — complete section, verbatim and unabridged**

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
