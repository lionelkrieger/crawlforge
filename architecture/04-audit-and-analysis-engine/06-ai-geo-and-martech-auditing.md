# AI Search (GEO) & MarTech Auditing

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §13 (complete section), §14 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 13. AI Search (GEO) Readiness, `llms.txt` Validator & Bot Governance Matrix — complete section, verbatim and unabridged**

## 13. AI Search (GEO) Readiness, `llms.txt` Validator & Bot Governance Matrix

**Extends FS-CRAWL-001 §4.4, §7.4, §8.2, §8.7.** Additional hooks: §5.1, §5.3, §7.3, §9.2, §10.2, §11.4.

### 13.1 `llms.txt` and `llms-full.txt` discovery and validation
> Capabilities: CAP-J-X01

- **At crawl start, alongside the robots.txt fetch (§4.4):** probe `/llms.txt` and `/llms-full.txt` on each in-scope host. Both are recorded as ordinary `url_records` (§9.2) with their status and content type.
- **Structural validation** of a retrieved `llms.txt`: H1 title present; blockquote summary present; H2 sections containing link lists. Structural failures emit `geo.llms-txt-malformed` (**Warning**) with per-element evidence.
- **Linked documentation/markdown URLs:** every link in the file is enqueued as a discovered URL (§5.3) when in scope and robots-allowed, otherwise verified for status; broken or non-200 targets emit `geo.llms-txt-broken-link` (**Warning**) per target.
- **Missing files:** `geo.llms-txt-missing` (**Notice** — the convention is optional); an absent `llms-full.txt` while `llms.txt` exists is a Notice annotation.
- All `geo.*` rule keys live in a new additive category **AI Search (GEO)** (additive to the §7.4 taxonomy); suppression follows §7.5.

### 13.2 AI crawler governance matrix
> Capabilities: CAP-J-X02

- **Bundled, versioned agent-profile catalogue** (the §7.15 asset pattern), each profile carrying an agent token and a purpose class:

| Agent | Purpose class |
|---|---|
| `Googlebot`, `Bingbot` | `search-index` |
| `OAI-SearchBot`, `PerplexityBot` | `ai-rag-citation` |
| `GPTBot`, `ClaudeBot`, `Google-Extended`, `CCBot`, `Applebot-Extended` | `llm-training` |

- **Inputs evaluated per agent:** robots.txt rules (the §4.4 parser), `X-Robots-Tag` response headers (including agent-specific directives and `noai` / `noimageai`), and HTML `<meta name="robots">` plus agent-specific meta tags (§7.3).
- **Evaluation:** for each agent × path prefix (prefixes derived from robots rules and the URL tree) × sampled URLs, compute allow/deny/unknown verdicts for three purposes: **search indexing**, **AI RAG citation**, **LLM training**.
- **Contradictions** (e.g. robots allows an agent while `X-Robots-Tag` or meta denies it) emit `geo.governance-conflict` (**Notice**) with both evidence values.
- **Site-wide matrix surface:** rows = path prefixes / URLs, columns = agents, purpose toggles (indexing / RAG / training); virtualized per FS-CRAWL-002 §7.1. The matrix is a report, not a gate — it never alters crawl behavior.

### 13.3 Standalone tool — `llms.txt` Generator & Validator (extends §8.7)
> Capabilities: CAP-J-X01

- **Generate:** from an audit's top-scoring indexable pages — ranking contract: indexable, no Error findings, descending content-quality composite (word count, readability in range, title/description present; exact composite an implementation choice) — grouped by directory/category. Output structure: H1 site name; blockquote summary (site meta description or user input); H2 sections per group with markdown link lists (page title + absolute URL). Optional `llms-full.txt` variant adds bounded content excerpts.
- **Validate:** pasted/uploaded body or a URL → structural verdict (FS-CRAWL-002 §13.1 rules) plus link verification.
- Output is a **local file export** (§8.4 pattern); nothing is ever auto-published or submitted to any vendor (FS-CRAWL-002 §1.6 E9).

### 13.4 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `url_elements` | New element group | `llms_structure` payload: H1/blockquote/section presence, linked-URL count, broken count | Additive to the §7.3 inventory; payload JSON |
| `governance_matrix` | New table | Audit reference; agent key; scope kind (`prefix` \| `url`); scope value; purpose (`search-index` \| `ai-rag-citation` \| `llm-training`); verdict; evidence JSON | Indexes (audit, agent), (audit, scope value); pruned with retention (§9.5) |
| `audits` | Additive snapshot fields | Agent-catalogue version; GEO toggles | Snapshot pattern (§9.2) |
| `metric_summaries` | New metric keys | `geo-readiness` (dimension: check); `governance-coverage` | Dashboard cards |

### 13.5 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| llms.txt results | GET `/api/audits/{id}/geo/llms-txt` | Host filter | Per-host file status, structural verdict, linked/broken counts |
| Governance matrix | GET `/api/audits/{id}/geo/governance` | Agent; purpose; scope filters; pagination | Matrix rows with verdicts and evidence |
| Generate llms.txt | POST `/api/tools/llms-generate` | Audit reference; grouping options; site metadata; full variant flag | Generated file (local export) |
| Validate llms.txt | POST `/api/tools/llms-validate` | Markup body or URL | Structural verdict + link-check results |

**New MCP resource `geo-readiness`** (audit reference; agent filter): read-only llms.txt validation results plus governance summary. Tool invocation and configuration are not exposed via MCP (§11.1 boundary).

### 13.6 UI requirements

A new **GEO Readiness** report surface, additive to the §8.2 catalog:

- `llms.txt` / `llms-full.txt` status cards per host with structural evidence and broken-link drill-down.
- Governance matrix grid (agent columns, purpose toggles, conflict badges), virtualized per FS-CRAWL-002 §7.1.
- Generator & Validator entry in the standalone tools window (§12), with preview before export.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #46.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 14. MarTech, Analytics & Tag Integrity Auditor — complete section, verbatim and unabridged**

## 14. MarTech, Analytics & Tag Integrity Auditor
> Capabilities: CAP-H-X06

**Extends FS-CRAWL-001 §5.6, §5.9, §7.3, §7.4, §8.2.** Additional hooks: §7.2, §7.17, §9.2, §10.2, §11.4.

### 14.1 Automated tag and container inventory

- **Sources:** HTML parsing (§7.2 step 3) and local proxy network capture (§5.6 rendering capture, §5.9 proxy routing) — no additional fetches beyond the parent's existing capture paths.
- **Bundled, versioned detection catalogue** (the §7.15 asset pattern) covering: **Google Tag Manager** (`GTM-*` container IDs), **Google Analytics 4** (`G-*` measurement IDs), **Google Ads** (`AW-*` conversion IDs), **Meta Pixel**, **LinkedIn Insight**, **Microsoft Clarity** — plus **custom user-defined beacon patterns** (pattern expressions over script sources, inline markers, and network hit URLs; the §7.16 pattern discipline with time bounds).
- Each detection records: vendor; container/pixel ID; detection source (`dom` | `network` | `both`); placement (head/body); load flags (async/defer); first-party vs third-party classification.
- The catalogue is a packaged asset updated only with product updates; user patterns are local configuration. No remote tag registry or vendor service is consulted.

### 14.2 `dataLayer` and Consent Mode v2 inspection (rendering mode)

- **`dataLayer` capture:** in rendering mode (§5.6), the post-render `window.dataLayer` array is snapshotted with mandatory bounds — event count plus the first N events (default 100, configurable); capture is engine-level with the same isolation and time-bound discipline as custom snippets (§7.17).
- **Consent Mode v2 state:** default and update commands for `ad_storage`, `analytics_storage`, `ad_user_data`, `ad_personalization` are extracted into an ordered timeline (defaults first, then updates with sequence markers).
- **Network correlation:** consent hit parameters (`gcs`, `gcd`) observed in proxy-captured network requests (§5.9) are correlated against the consent timeline; tags firing **before or without** a default consent declaration are detected.
- Without rendering mode, inventory still runs from static HTML and any proxy capture; consent/dataLayer passes are marked unavailable with a diagnostic.

### 14.3 Built-in audit rules — new category `Analytics & Tagging`

| Rule key | Condition | Severity |
|---|---|---|
| `analytics.missing-container` | Indexable HTML pages missing the site's primary GTM/GA4 ID (primary = most frequent across indexable pages; user override configurable) | **Warning** |
| `analytics.duplicate-container` | The same container ID injected multiple times on a page, or duplicate `page_view` requests on initial load (network capture) | **Warning** |
| `analytics.datalayer-malformed` | Syntax errors or missing required keys in `dataLayer` events, validated against an optional user-supplied JSON schema (stored in audit configuration; bounded validation) | **Warning** |
| `analytics.consent-mode-missing` | Tags firing prior to or without default consent state declarations, where Consent Mode auditing is enabled | **Warning** |
| `analytics.no-hits-observed` (optional diagnostic) | Container present in DOM but zero corresponding network hits observed while capture is enabled | **Notice** |

The category **Analytics & Tagging** is additive to the §7.4 taxonomy; suppression follows §7.5; evidence carries container IDs, event excerpts (bounded), and consent-timeline markers.

### 14.4 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `analytics_detections` | New table | Audit reference; URL reference; vendor; container/pixel ID; detection source; placement; load flags; evidence JSON | Indexes (audit, vendor), (audit, container ID); pruned with retention (§9.5) |
| `url_elements` | New element groups | `consent_state` payload (defaults/updates timeline, `gcs`/`gcd` observations); `datalayer_snapshot` payload (bounded event count + first N events) | Additive to the §7.3 inventory; payload JSON |
| `crawl_configurations` | Additive fields | Consent-auditing toggle; primary-container override; `dataLayer` JSON schema; custom beacon patterns | §5.1 input-set extension |
| `metric_summaries` | New metric keys | `analytics-coverage` (dimension: vendor); `consent-compliance` | Dashboard cards |

### 14.5 REST and MCP additions (extends §10.2, §11.4)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Analytics detections | GET `/api/audits/{id}/analytics` | `view=inventory\|consent\|findings`; vendor/container filters; pagination | Detection rows, consent timelines, or findings |
| Container coverage | GET `/api/audits/{id}/analytics/coverage` | Vendor filter | Per-container coverage across indexable pages (present/missing counts) |

**New MCP resource `analytics-coverage`** (audit reference; vendor filter): read-only coverage projection. Hub: detections ride the existing `findings-updated` and `url-batch` events (§10.3) — no new event required.

### 14.6 UI requirements — Analytics & Tagging Explorer

A new report surface, additive to the §8.2 catalog:

- Container inventory grid (vendor, ID, coverage, placement), virtualized per FS-CRAWL-002 §7.1.
- Per-URL tag/consent timeline: default consent declarations, updates, and first tag fire on one axis; `gcs`/`gcd` hit markers.
- Coverage treemap by directory (Apache ECharts, FS-CRAWL-002 §8.4); bounded `dataLayer` event inspector.
- Configuration forms for consent auditing, primary-container override, JSON schema, and custom beacon patterns (Mantine forms, FS-CRAWL-002 §8.2).

**Acceptance hook:** FS-CRAWL-002 §9, criterion #47.

---
