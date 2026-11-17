# REST API Contract

> **CrawlForge modular architecture — area of concern: `05-api-hub-and-mcp`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §10 heading & introduction, §10.1, §10.2, §10.4 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | Appendix B (complete) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10. API and Real-Time Contract — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 10. API and Real-Time Contract

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10.1 Conventions — verbatim and unabridged**

### 10.1 Conventions

- Base URL: loopback HTTP. All bodies are JSON. Paths below are **new design artifacts**, not reproductions of any existing surface.
- List endpoints accept pagination (offset/limit), filtering, and sorting.
- Uniform response envelope: a data payload, optional pagination metadata, and an optional error object (machine-readable code plus human-readable message).
- No authentication headers are required for the shell-facing API. The only local credential concepts are the optional per-launch session token (section 2.2) and the automation-interface token (section 11); destination, proxy, and external-database credentials (sections 8.5, 5.9, 2.4) are user-owned secrets used solely for their respective outbound connections.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10.2 REST mapping — verbatim and unabridged**

### 10.2 REST mapping

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Health probe | GET /api/health | — | Readiness flag, uptime |
| Version info | GET /api/version | — | Product version, schema version, active runtime profile |
| Local metrics | GET /metrics | — | Standardized text exposition (Prometheus-class format) of local technical metric families (section 10.5) |
| Read settings | GET /api/settings | — | Full settings object (defaults guaranteed; secrets never returned in clear) |
| Update settings | PUT /api/settings | Any subset of settings fields | Updated settings object |
| Test database connection | POST /api/settings/database/test | Provider selection and connection fields | Reachability, authentication, and migration-compatibility verdict |
| Diagnostic artifacts | GET, DELETE /api/diagnostics/artifacts | Kind filter on list | Local log/minidump metadata (kind, created-at, size) or purge confirmation |
| List projects | GET /api/projects | Pagination, search text | Project summaries |
| Create project | POST /api/projects | Name, description, tags, scope summary | Created project |
| Read / update / delete project | GET, PUT, DELETE /api/projects/{id} | Project fields on update | Project or deletion confirmation |
| List / create configurations | GET, POST /api/projects/{id}/configurations | Configuration fields per section 5.1 | Configuration summaries / created configuration |
| Read / update / delete configuration | GET, PUT, DELETE /api/configurations/{id} | Configuration fields on update | Configuration or confirmation |
| Start audit | POST /api/audits | Configuration reference, optional limit overrides | Audit identifier and initial state (returns immediately; progress via hub) |
| List audits | GET /api/audits | Project filter, status filter, pagination | Audit summaries with counters |
| Read audit | GET /api/audits/{id} | — | Audit detail, counters, phase |
| Pause / resume / cancel | POST /api/audits/{id}/pause, /resume, /cancel | — | New state |
| Re-audit | POST /api/audits/{id}/reaudit | Optional refresh flag | New audit identifier linked to the same dataset |
| List URLs | GET /api/audits/{id}/urls | Filters (status class, content type, depth, indexability, text search), sorting, pagination | URL record summaries |
| Read URL detail | GET /api/audits/{id}/urls/{urlId} | — | Full record, element inventory, findings, inbound/outbound links, redirect chain |
| List links | GET /api/audits/{id}/links | Source/target filters, internal/external, pagination | Link edge rows |
| List findings | GET /api/audits/{id}/findings | Severity, category, rule key, URL filters; sorting; pagination | Finding rows with evidence |
| Dashboard summary | GET /api/audits/{id}/summary | — | Metric cards: counts by status/severity/category, crawl health |
| Chart series | GET /api/audits/{id}/charts/{metric} | Dimension filters, bucketing options | Series points (label, value, dimensions) |
| Graph data | GET /api/audits/{id}/graph | Graph type (structure tree, link network, redirect chains), depth/size limits | Nodes and edges |
| Compare audits | GET /api/audits/compare | Two audit identifiers | New/resolved findings, metric deltas |
| Single-page analysis | POST /api/analysis/single-page | Target URL, optional rendering flag, optional rule filters | Findings and element inventory for the page |
| Create export | POST /api/exports | Audit reference, format, dataset selection | Export job identifier |
| Download export | GET /api/exports/{id}/file | — | Local file stream (spreadsheet, CSV, HTML bundle, or PDF) |
| List / create schedules | GET, POST /api/projects/{id}/schedules | Schedule fields per section 6.1 | Schedule summaries / created schedule |
| Read / update / delete schedule | GET, PUT, DELETE /api/schedules/{id} | Schedule fields on update | Schedule or confirmation |
| Enable / disable schedule | POST /api/schedules/{id}/enable, /disable | — | New state and next-due time |
| Run schedule now | POST /api/schedules/{id}/run-now | — | Audit identifier (manual trigger) |
| Schedule run history | GET /api/schedules/{id}/runs | Pagination | Run-history rows |
| List / create destinations | GET, POST /api/destinations | Destination fields per section 8.5 | Destination summaries (credentials never returned in clear) / created destination |
| Update / delete destination | PUT, DELETE /api/destinations/{id} | Destination fields | Destination or confirmation |
| Test destination | POST /api/destinations/{id}/test | — | Reachability and credential verdict |
| User dictionary | GET, POST, DELETE /api/dictionary | Language filter; word on add | Dictionary entries or confirmation |
| Compare page content | GET /api/audits/compare/content | Two audit identifiers; canonical URL | Side-by-side title/heading/text differences and metadata changes |
| List extracted content | GET /api/audits/{id}/extracted-content | Rule filter, URL filter, column selection, sorting, pagination | Dynamic-column rows of custom extraction values (section 7.9) |
| Extraction summary | GET /api/audits/{id}/extracted-content/summary | — | Per-rule pages matched, values captured, discards, truncations |
| List search results | GET /api/audits/{id}/search-results | Item filter, URL filter, pagination | Custom search matches and per-item overview (section 7.16) |
| List snippet results | GET /api/audits/{id}/snippet-results | Snippet filter, URL filter, column selection, pagination | Dynamic-column snippet outputs (section 7.17) |
| List technologies | GET /api/audits/{id}/technologies | Category filter, pagination | Technology detections with confidence (section 7.15) |
| Get audit scores | GET /api/audits/{id}/scores | — | Per-category and overall scores (section 8.1) |
| Suppress / unsuppress finding | POST /api/projects/{id}/suppressions, DELETE /api/projects/{id}/suppressions/{key} | Rule key and/or finding scope | Suppression state (section 7.5) |
| Robots test | POST /api/tools/robots-test | Robots body or URL; test URLs; user-agent | Allow/disallow verdict and matched rule per URL (section 8.7) |
| URL check | POST /api/tools/url-check | Target URL | Status, redirect chain, headers, indexability (section 8.7) |
| Structured-data check | POST /api/tools/structured-data-check | Markup or URL | Parsed blocks with validation/rich-result outcome (section 7.13) |
| Render test | POST /api/tools/render-test | Target URL; device profile | Screenshot, rendered document, console messages (section 8.7) |
| Generate sitemap | POST /api/tools/sitemap-generate | Audit reference; filters; change-frequency/priority options | Sitemap file (or index) (section 8.7) |
| Saved queries | GET, POST, PUT, DELETE /api/queries | Query rule tree; name | Saved query-builder definitions (section 8.7) |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10.4 Error and edge behavior — verbatim and unabridged**

### 10.4 Error and edge behavior

- Validation errors return field-level reasons in the error envelope.
- Long operations return immediately with an identifier; progress flows over the hub.
- Starting an audit that is already running is rejected idempotently with the current state.
- All core endpoints remain functional with no network connectivity; only destination delivery, test-connection actions (sections 8.5, 2.4), and proxied crawls touch user-configured endpoints, and none consults any vendor service.

---

> **Verbatim source — FS-CRAWL-002 v1.1: Appendix B — Additive REST Contract (extends FS-CRAWL-001 §10.2) — complete section, verbatim and unabridged**

## Appendix B — Additive REST Contract (extends FS-CRAWL-001 §10.2)

All routes follow §10.1 conventions (JSON envelope, offset/limit pagination, filtering, sorting; loopback only). The "Defined in" column references **FS-CRAWL-002** sections.

| Method & path | Defined in | Purpose |
|---|---|---|
| GET `/api/audits/{id}/waf-blocks` | §2.7 | WAF detection list with evidence |
| GET `/api/audits/{id}/waf-blocks/summary` | §2.7 | Provider/class counts, block rate |
| GET `/api/waf-signatures` | §2.7 | Effective catalogue (bundled version + user entries) |
| POST `/api/waf-signatures` | §2.7 | Create user signature |
| PUT `/api/waf-signatures/{key}` | §2.7 | Update user signature |
| DELETE `/api/waf-signatures/{key}` | §2.7 | Delete user entry / restore bundled default |
| GET `/api/audits/{id}/hosts` | §3.7 | Per-host AIMD state; optional adjustment history |
| POST `/api/audits/{id}/hosts/{host}/override` | §3.7 | Pin/clear manual host rate |
| GET `/api/audits/{id}/ngrams` | §5.7 | Site-summary or per-URL n-gram rows |
| GET `/api/audits/{id}/ngrams/cannibalization` | §5.7 | Cannibalization clusters |
| GET `/api/audits/{id}/ngrams/gaps` | §5.7 | Topical gaps |
| POST `/api/tools/scope-preview` | §4.6 | Scope preview against a sample URL/artifact |
| GET `/api/plugins` | §6.7 | Plugin registry and load state |
| PUT `/api/plugins/{id}` | §6.7 | Enable/disable; grant/revoke permissions |
| POST `/api/plugins/rescan` | §6.7 | Rescan plugin directories |
| DELETE `/api/plugins/{id}` | §6.7 | Remove registry entry |
| GET `/api/system/health` | §7.2 | On-demand memory/WAL snapshot |
| POST `/api/system/compact` | §7.2 | Compact / Trim Working Set action |
| POST `/api/audits/{id}/redirect-matrix` | §10.7 | Upload migration mapping; auto-seed list-mode audit |
| GET `/api/audits/{id}/redirect-matrix/results` | §10.7 | Per-row verdicts, hop chains, canonical evidence |
| POST `/api/tools/rewrite-preview` | §10.7 | Sample URL rewriting before/after |
| POST `/api/projects/{id}/logs/import` | §11.7 | Import log file (immediate identifier; hub progress) |
| GET `/api/projects/{id}/logs` | §11.7 | List log imports |
| DELETE `/api/logs/{id}` | §11.7 | Purge import + aggregates |
| GET `/api/audits/{id}/logs/correlation` | §11.7 | Orphans / budget waste / uncrawled indexable views |
| POST `/api/logs/{id}/verify-dns` | §11.7 | Optional user-triggered DNS bot verification |
| POST `/api/audits/{id}/semantics/run` | §12.8 | Run / re-run local embedding job |
| GET `/api/audits/{id}/semantics/similar` | §12.8 | Top-K similar URLs by cosine |
| GET `/api/audits/{id}/semantics/link-opportunities` | §12.8 | Semantic internal-link recommendations |
| GET `/api/audits/{id}/semantics/clusters` | §12.8 | Semantic cannibalization clusters |
| POST `/api/settings/embedding/test` | §12.8 | Loopback provider connection/model test |
| GET `/api/audits/{id}/geo/llms-txt` | §13.5 | Per-host llms.txt validation results |
| GET `/api/audits/{id}/geo/governance` | §13.5 | Bot-governance matrix rows |
| POST `/api/tools/llms-generate` | §13.5 | Generate llms.txt / llms-full.txt (local export) |
| POST `/api/tools/llms-validate` | §13.5 | Validate llms.txt body or URL |
| GET `/api/audits/{id}/analytics` | §14.5 | Tag inventory / consent / findings views |
| GET `/api/audits/{id}/analytics/coverage` | §14.5 | Per-container coverage across indexable pages |

Extended existing routes (additive fields/filters only): GET `/api/audits/{id}/urls` (blockReason, wafProvider filters); GET `/api/audits/{id}/urls/{urlId}` (WAF detection, scope diagnostics, top n-grams, `serp_metrics`, header-conflict detail, semantic relations); GET `/api/audits/{id}/summary` (WAF counts, throttle summary); GET `/api/audits/{id}/charts/{metric}` (`waf-blocks`, `ngram-top`, `link-equity-weighted` metrics); GET `/api/audits/{id}/links` (`domZone` filter, weighted-equity fields); POST `/api/analysis/single-page` and POST `/api/tools/url-check` (WAF verdict, scope diagnostics); POST `/api/tools/render-test` (post-render WAF verdict, picker support); POST `/api/exports` (`waf-blocks`, `ngrams`, `redirect-matrix`, semantic-opportunity, analytics datasets); POST `/api/audits` (`urlList`, `listMode`); POST/PUT configuration routes and PUT `/api/projects/{id}` (contentScope, rewrite/host-mapping, consent/beacon, zone-weight/SERP fields); GET `/api/audits/compare` and GET `/api/audits/compare/content` (`hostMapping`); GET `/metrics` (memory/GC/WAL families).
