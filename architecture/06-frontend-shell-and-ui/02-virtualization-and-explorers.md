# Virtualization, Report Surfaces & Tools

> **CrawlForge modular architecture — area of concern: `06-frontend-shell-and-ui`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §8 heading & introduction, §8.2, §8.4, §8.5, §8.6, §8.7 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §7.1, §8.3, §10.9, §11.9, §12.10, §13.3, §13.6, §14.6, §15.6 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8. Local Reporting and Visualization — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 8. Local Reporting and Visualization

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.2 Report surfaces — verbatim and unabridged**

### 8.2 Report surfaces
> Capabilities: CAP-C-098, CAP-E-119, CAP-E-130, CAP-E-137, CAP-E-138, CAP-H-168, CAP-H-169, CAP-H-170, CAP-H-178, CAP-H-180, CAP-I-193, CAP-I-199, CAP-I-200, CAP-I-201

| Surface | Content |
|---|---|
| Dashboard | Summary cards, severity/category charts, crawl-health panel, top-issue lists |
| Issue explorer | Filterable, sortable, groupable finding lists; drill-down to affected URLs; bulk export |
| URL explorer | Per-URL detail: status and timing, extracted elements, findings, inbound/outbound links, redirect chain, optional rendered screenshot |
| Visualizations | Site-structure tree by path; crawl-depth map; internal-link network graph (force-directed layout); redirect-chain diagrams; section treemap by URL count or weight |
| Audit comparison | Any two audits of a project: new/resolved findings and metric deltas |
| Content comparison | Any two audits of a project, per shared URL: side-by-side differences of title, headings, and visible text, plus status and metadata changes; rendered offline from the content store (section 9.6) |
| Custom extraction explorer | User-defined extraction dataset with dynamic per-rule columns (multi-valued rules expand to the busiest page's column count); filter, sort, paginate; per-rule match counts and diagnostics (section 7.9) |
| Accessibility | Violations grouped by rule family and severity, with implicated elements and remediation (section 7.10) |
| Performance / Core Web Vitals | Lab-metric distributions, worst pages, budget pass-rate, code-coverage summary (section 7.11) |
| Structured data | Every block with type, parsed fields, and validation/rich-result outcome (section 7.13) |
| Technology | Detected technologies by category with confidence and affected-URL counts (section 7.15) |
| Custom search | Per-item match overview and per-URL matched snippets (section 7.16) |
| Snippet results | Custom JavaScript snippet outputs joined to URLs, dynamic columns (section 7.17) |
| Rendered comparison | Response-vs-rendered differences per URL (section 7.18) |
| Scores | Per-category and overall audit scores with cross-audit trend (section 8.1) |
| Suppressed findings | User-suppressed findings and rules, reversible (section 7.5) |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.4 Export — verbatim and unabridged**

### 8.4 Export
> Capabilities: CAP-G-167, CAP-H-173, CAP-H-174, CAP-H-177, CAP-H-175, CAP-G-165, CAP-G-164 · Decisions: D-014, D-017 · Change records: CR-011, CR-017

- Local file exports: a multi-sheet spreadsheet workbook (summary, URLs, findings, links, and custom extraction when enabled), CSV per dataset, a self-contained HTML report bundle, and an optional PDF summary. Custom extraction exports use the same dynamic-column expansion as the explorer (section 7.9).
- Exports may additionally be delivered to optional user-configured destinations (section 8.5).
- Agent dataset exports: CSV, JSON Lines and Markdown datasets, and a read-only SQLite audit snapshot (section 11.8). **[Added by CR-011, D-014]**
- Full page text export per page as plain text, alongside the Markdown export. **[Added by CR-017]**

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.5 Optional export destinations — verbatim and unabridged**

### 8.5 Optional export destinations
> Capabilities: CAP-H-X02 · Decisions: D-013 · Change records: CR-016

- **Off by default;** no destination exists until the user creates one. Destination classes: a local or network folder, an FTP server and an SFTP server, addressed with the user's own credentials, never vendor accounts. Cloud drive, spreadsheet-service and object-storage destinations, and notification webhooks, are out of scope for now (section 1.5 of `01-clean-room-and-exclusions.md`). **[Amended by CR-016, D-006, D-013]**
- Each destination stores: a user-chosen name; class; endpoint details; credential payload **encrypted at rest** (section 9.1).
- A **test-connection** action verifies reachability and credentials before first use.
- Transfers occur only when the user initiates an export to the destination, or pre-authorizes it as a schedule completion action (section 6.5).
- Delivery failures are retried with bounded backoff, logged locally, and surfaced in the UI; a destination failure **never fails the audit** or the local export.
- Disabling or deleting all destinations restores the guarantee of zero non-crawl outbound traffic.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.6 Access — verbatim and unabridged**

### 8.6 Access

- Every report is viewable at any time, fully offline. **No authorization check of any kind gates report access.**

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.7 Standalone diagnostic and authoring tools — verbatim and unabridged**

### 8.7 Standalone diagnostic and authoring tools
> Capabilities: CAP-G-166, CAP-G-X01, CAP-H-170, CAP-H-X03, CAP-H-171 · Decisions: D-017 · Change records: CR-017

A set of self-contained tools operates without requiring a full audit; each is reachable from the shell and, where stateless, from the automation interface:

- **Robots tester:** the user supplies a robots.txt body (or points at a live one) and a list of test URLs; the tool returns the allow/disallow verdict per URL under the same parser used by the crawler (section 4.4), including the matched rule.
- **URL checker:** fetches a single URL and reports status, full redirect chain, selected response headers, and indexability signals.
- **Structured-data checker:** accepts pasted markup or a URL and returns parsed blocks with the validation and rich-result outcome (section 7.13).
- **Render tester:** renders a single URL and returns a screenshot, the post-render document, and console messages — a preview of rendering-mode behavior.
- **XML sitemap generator:** builds a sitemap from an audit's URL set, filterable by indexability and status, with optional change-frequency and priority hints; the result is exported as a standards-conformant sitemap file (and sitemap index when split by size).
- **Interactive query builder:** composes nested AND/OR filter rules over URL columns (operators such as contains, starts/ends-with, equals, in-set, between, regex, and numeric comparison), including dynamic custom-extraction and snippet columns; saved queries apply to the explorers and exports. A raw-SQL administration tool is an open scope decision (section 15) and is not required by this version.
- **Segments:** a saved query can be applied as a named segment, a URL subset that filters every report surface, chart and export; segment counts are shown on the dashboard. **[Added by CR-017]**

### 8.8 Configuration editor and project settings
> Capabilities: CAP-K-X01, CAP-K-X02, CAP-K-X04 · Decisions: D-004, D-005 · Change records: CR-012

**[Added by CR-012]**

- Every project lists its **configurations** (named crawl profiles, for example "Full crawl", "Quick sample", "Mobile rendered", "URL list check"). Each can be viewed, created, duplicated, edited and deleted. App-level configuration templates can be copied into a project. A running audit keeps the configuration snapshot it started with (section 4.3).
- The editor exposes every section 5.1 input, grouped: start URLs and scope; "crawl as" preset and capture mode; limits; include/exclude rules; directive handling; resources and external scope; authentication and cookies; click discovery and site-type options; consent states; analysis categories; thresholds; extraction and search; advanced.
- **"Crawl as" presets** bundle user agent, viewport and default capture mode: Googlebot Smartphone, Googlebot Desktop, Chrome mobile, Chrome desktop, using current user-agent strings.
- **Analysis categories** are enabled **per configuration**, not application-wide; a disabled category contributes no score (section 8.1).
- **Clients.** A client entity can be shared by several projects. It holds: expected GA4 measurement IDs and GTM container IDs (used by analytics coverage checks, FS-CRAWL-002 §14), the expected consent platform, brand name variants, target locale and language (driving the spelling dictionary and expected hreflang), and report branding (client name, logo). A project may override any client value.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.1 Strict DOM virtualization — verbatim and unabridged**

### 7.1 Strict DOM virtualization

**Mandate:** every tabular explorer renders **only visible viewport rows plus a small overscan buffer**. The mounted DOM row count shall never exceed **50 rows** (target band 30–50 including overscan), with row recycling. This applies to:

- URL explorer, Issue explorer, Custom extraction explorer (§8.2);
- **Link explorer** — formalized here as the first-class tabular surface over the parent link dataset (GET `/api/audits/{id}/links`, §10.2; inbound/outbound views in URL detail);
- **N-Gram Explorer** (FS-CRAWL-002 §5) and WAF detection lists (FS-CRAWL-002 §2);
- plugin report tabs with tabular content (FS-CRAWL-002 §6).

Performance and data-windowing contract:

| Requirement | Contract |
|---|---|
| Scroll performance | Sustained **60 FPS at 500,000+ rows** on commodity desktop hardware (§13), verified by the composite gate of criterion #40 (median ≤ 16.7 ms, dropped ≤ 1%, single-vsync ≥ 95%, DOM rows ≤ 50) | **[Amended by CR-019, D-019]**
| Server-side windowing | Rows fetched via offset/limit pagination (§10.1); default page size 200; resident client cache ≤ 20 pages (≈ 4,000 rows) with LRU eviction; total row count from pagination metadata; the full dataset is never held client-side |
| Row geometry | Estimated row heights with a bounded dynamic-measurement cache; scroll-position ↔ offset mapping stays stable under measurement updates |
| Horizontal virtualization | Wide dynamic-column datasets (§7.9 busiest-page expansion) render only visible columns plus overscan; pinned columns always mounted, pin count bounded (default ≤ 4) |
| Sorting & filtering | Multi-column sorting and filtering execute server-side (§10.2); "instant" filtering is debounced (150–300 ms); query-builder rules (§8.7) compose into the same server filters |
| Accessibility | Virtualization preserves keyboard navigation, focus retention, and ARIA row semantics (`aria-rowcount` / `aria-rowindex`) per §13 |
| Charts & graphs | No per-datum SVG DOM: canvas/WebGL rendering only (FS-CRAWL-002 §8.4, §8.5) |

---

> **Verbatim source — FS-CRAWL-002 v1.1: 8.3 Virtualized data grids — TanStack Table + TanStack Virtual (or AG Grid Community) — verbatim and unabridged**

### 8.3 Virtualized data grids — TanStack Table + TanStack Virtual (or AG Grid Community)

**Mandate:** TanStack Table + TanStack Virtual (headless pair, primary choice) **or** AG Grid Community with its server-side row model. Either implementation must satisfy the full capability checklist and the FS-CRAWL-002 §7.1 virtualization contract:

| Capability | Requirement |
|---|---|
| Scale | 1M+ rows via the windowed server-side row model (FS-CRAWL-002 §7.1); never a full client dataset |
| Column pinning | Left/right pinning with bounded pin count |
| Dynamic columns | Custom-extraction per-rule columns, multi-valued expansion to the busiest page (§7.9, §8.2) |
| Sorting | Multi-column sorting, executed server-side (§10.2) |
| Filtering | Instant (debounced) filtering; query-builder integration (§8.7) |
| Interaction | Row selection, column resize/reorder, density modes, keyboard grid navigation (§13) |
| Export handoff | Selection/filter state handed to the export flow (§8.4) |

---

> **Verbatim source — FS-CRAWL-002 v1.1: 10.9 UI requirements — verbatim and unabridged**

### 10.9 UI requirements

- Audit configuration: list-ingestion panel (file picker / clipboard paste, preview table with per-line validation diagnostics, discovery-mode selector).
- Migration Matrix surface with verdict badges and hop-chain visualization; matrix upload wizard.
- Rewrite-rule editor with live preview; host-mapping editor embedded in the comparison surfaces.
- All tabular content obeys FS-CRAWL-002 §7.1.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #43.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 11.9 UI requirements — Log File Analyzer — verbatim and unabridged**

### 11.9 UI requirements — Log File Analyzer

A new report surface, additive to the §8.2 catalog:

- Import manager: drag-drop / native file dialog, format auto-detect, window picker, verification-tier badges, progress display.
- Correlation tabs (True Orphans / Budget Waste / Uncrawled Indexable), each virtualized per FS-CRAWL-002 §7.1, with evidence drill-down to URL detail.
- Bot-family breakdown charts and hit-timeline histogram (Apache ECharts, FS-CRAWL-002 §8.4).
- DNS verification action with an explicit consent dialog showing exactly what will be queried.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #44.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 12.10 UI requirements — verbatim and unabridged**

### 12.10 UI requirements

A new **Semantic** report surface, additive to the §8.2 catalog:

- Link-opportunities grid (source, target, similarity, anchor candidates), virtualized per FS-CRAWL-002 §7.1, with one-click navigation to source/target URL detail.
- Cluster view with a similarity-edge graph overlay (Sigma.js + Graphology, FS-CRAWL-002 §8.5) under the same LOD budgets.
- Per-URL "semantically related" panel in URL detail; cannibalization findings cross-linked to the N-gram cannibalization view (FS-CRAWL-002 §5.9).
- Provider settings with connection-test feedback and explicit loopback-only validation errors.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #45.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 13.3 Standalone tool — `llms.txt` Generator & Validator (extends §8.7) — verbatim and unabridged**

### 13.3 Standalone tool — `llms.txt` Generator & Validator (extends §8.7)

- **Generate:** from an audit's top-scoring indexable pages — ranking contract: indexable, no Error findings, descending content-quality composite (word count, readability in range, title/description present; exact composite an implementation choice) — grouped by directory/category. Output structure: H1 site name; blockquote summary (site meta description or user input); H2 sections per group with markdown link lists (page title + absolute URL). Optional `llms-full.txt` variant adds bounded content excerpts.
- **Validate:** pasted/uploaded body or a URL → structural verdict (FS-CRAWL-002 §13.1 rules) plus link verification.
- Output is a **local file export** (§8.4 pattern); nothing is ever auto-published or submitted to any vendor (FS-CRAWL-002 §1.6 E9).

---

> **Verbatim source — FS-CRAWL-002 v1.1: 13.6 UI requirements — verbatim and unabridged**

### 13.6 UI requirements

A new **GEO Readiness** report surface, additive to the §8.2 catalog:

- `llms.txt` / `llms-full.txt` status cards per host with structural evidence and broken-link drill-down.
- Governance matrix grid (agent columns, purpose toggles, conflict badges), virtualized per FS-CRAWL-002 §7.1.
- Generator & Validator entry in the standalone tools window (§12), with preview before export.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #46.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 14.6 UI requirements — Analytics & Tagging Explorer — verbatim and unabridged**

### 14.6 UI requirements — Analytics & Tagging Explorer

A new report surface, additive to the §8.2 catalog:

- Container inventory grid (vendor, ID, coverage, placement), virtualized per FS-CRAWL-002 §7.1.
- Per-URL tag/consent timeline: default consent declarations, updates, and first tag fire on one axis; `gcs`/`gcd` hit markers.
- Coverage treemap by directory (Apache ECharts, FS-CRAWL-002 §8.4); bounded `dataLayer` event inspector.
- Configuration forms for consent auditing, primary-container override, JSON schema, and custom beacon patterns (Mantine forms, FS-CRAWL-002 §8.2).

**Acceptance hook:** FS-CRAWL-002 §9, criterion #47.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 15.6 UI requirements — verbatim and unabridged**

### 15.6 UI requirements

- Link explorer: zone badges and zone filter; weighted/unweighted equity toggle in Visualizations (node sizing, FS-CRAWL-002 §8.5).
- URL detail: **SERP & Social Card Preview** panel with desktop/mobile toggle and live truncation highlight; header/HTML conflict panel with side-by-side directive diff.
- All tabular content obeys FS-CRAWL-002 §7.1.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #48.

---

---

## Amendment history

### CR-016 (2026-11-17; D-006, D-013)

Replaced text in §8.5 (destination classes):

> - **Off by default;** no destination exists until the user creates one. Destination classes: a cloud drive/spreadsheet service, an object-storage endpoint, and a notification webhook — all addressed with the **user's own credentials**, never vendor accounts.
### CR-019 (2026-11-17; D-019)

Replaced text in virtualization scroll-performance row:

> | Scroll performance | Sustained **60 FPS at 500,000+ rows** (p95 frame time ≤ 16.7 ms during continuous scrolling) on commodity desktop hardware (§13) |
