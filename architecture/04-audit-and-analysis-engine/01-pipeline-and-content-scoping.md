# Audit Pipeline & Content Scoping

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §7 heading & introduction, §7.1, §7.2, §7.3, §7.4, §7.5, §7.6, §7.7 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §4 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7. Audit Engine — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 7. Audit Engine

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.1 Inputs — verbatim and unabridged**

### 7.1 Inputs

- The crawl dataset (complete or in progress).
- The audit rule configuration: enabled categories and per-rule thresholds, all stored locally.
- Scope context: seed URL(s) and target domain.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.2 Pipeline — verbatim and unabridged**

### 7.2 Pipeline

1. **Fetch / reuse HTML** — the stored response body is reused from the content store (section 9.6); re-fetching occurs only on explicit refresh.
2. **Parse the DOM** — a tolerant parser produces a document tree even for malformed markup.
3. **Extract the element inventory** — structured facts are pulled from the tree (section 7.3); when enabled, user-defined custom extraction rules run in the same stage (section 7.9).
4. **Per-URL rule passes** — independent rules evaluate each URL's inventory.
5. **Cross-URL aggregation passes** — site-wide rules evaluate indexed views over the whole dataset (duplicates, orphans, patterns).
6. **Finding emission** — judgments are persisted with severity and evidence.
7. **Metric aggregation** — summary metrics are computed for reporting (section 8.1).
8. **Report readiness** — the audit is marked reportable; the UI is notified via the hub.

Execution modes: **incremental** (per-URL passes run during the crawl) and **full** (adds cross-URL passes after completion). A **re-audit** recomputes findings without re-fetching unless a refresh is requested.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.3 Element extraction inventory — verbatim and unabridged**

### 7.3 Element extraction inventory
> Capabilities: CAP-A-007, CAP-A-009, CAP-F-141

| Element group | Extracted facts |
|---|---|
| Document head | Title; meta description, robots, viewport, charset; canonical target; language/region alternates; social-graph cards; favicon relations |
| Headings | Ordered outline with levels and text |
| Anchors | Target, anchor text, relation flags, target-window behavior, position |
| Images | Source(s), alternative text, declared dimensions, lazy-loading attributes |
| Structured data | Embedded blocks, vocabulary, parsed fields |
| Resources | Scripts and stylesheets with placement and loading flags; counts and sizes |
| Text statistics | Visible word count, declared language |
| Tables | Detected data tables with row/column structure and header cells |
| Link relations | Pagination, alternate formats, and other declared relations |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.4 Rule taxonomy (generic, industry-standard) — verbatim and unabridged**

### 7.4 Rule taxonomy (generic, industry-standard)
> Capabilities: CAP-A-007, CAP-B-036, CAP-B-037, CAP-B-038, CAP-B-039, CAP-B-040, CAP-B-041, CAP-B-042, CAP-B-043, CAP-B-044, CAP-B-045, CAP-B-046, CAP-B-047, CAP-B-048, CAP-B-049, CAP-B-050, CAP-B-051, CAP-B-052, CAP-B-053, CAP-B-054, CAP-B-055, CAP-B-056, CAP-B-057, CAP-B-058, CAP-B-059, CAP-B-060, CAP-B-061, CAP-B-062, CAP-B-063, CAP-C-087, CAP-C-090, CAP-C-091, CAP-C-092, CAP-C-095, CAP-C-096, CAP-C-097, CAP-D2-111, CAP-D2-113, CAP-D2-114, CAP-D2-116, CAP-E-122, CAP-E-125, CAP-F-139, CAP-F-148

| Category | Example checks (abstract) |
|---|---|
| Indexability & status | Error statuses; redirect chains and loops; canonical missing/mismatched; robots meta directives; robots-blocked URLs; orphan pages; sitemap coverage; soft-404 detection (section 7.12) |
| Content quality | Missing, duplicate, or length-out-of-range titles and descriptions; heading-order violations; thin content; near-duplicate bodies; misspelled words in visible text (section 7.8); readability and content metrics (section 7.12) |
| Linking | Broken internal and external links; excessive redirect depth; uninformative anchor text; excessive outbound links; very deep pages; link-equity distribution (section 8.1) |
| Images | Missing or empty alternative text; broken sources; oversized payloads; missing dimensions |
| Performance heuristics | Total page weight; resource counts; render-blocking resources; uncompressed text resources; missing or short cache lifetimes; large document trees; slow responses. Rendered lab metrics and Core Web Vitals: section 7.11 |
| Security & protocol | Mixed content; non-HTTPS pages; insecure form actions; missing security headers; absent HTTP-to-HTTPS redirection |
| Structured data | Parse errors; missing required properties per vocabulary; deprecated types; rich-result eligibility (section 7.13) |
| Internationalization | Invalid language/region codes; missing return tags; missing self-reference; default-language handling |
| Mobile readiness | Viewport presence and validity; responsive-design signals; tap-target sizing and font legibility (section 7.14) |
| Accessibility | Automated accessibility-rule violations; ARIA attribute/role validity; form labels and landmarks; color-contrast signals (section 7.10) |
| Technology detection | Detected platform/framework/analytics signatures and categories from a bundled fingerprint catalogue (section 7.15) |

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.5 Finding model — verbatim and unabridged**

### 7.5 Finding model
> Capabilities: CAP-B-063, CAP-F-140, CAP-I-192, CAP-I-194, CAP-I-196, CAP-I-X01, CAP-J-204, CAP-J-207, CAP-J-208, CAP-K-X03 · Decisions: D-004 · Change records: CR-012

Each finding carries:

- A **rule key** from the product's own taxonomy, plus its category.
- A **severity**: Error, Warning, Notice, or Pass — assigned by the rule definition, with thresholds configurable locally.
- Thresholds (title and description lengths, thin-content word count, slow-response cut-off, readability target and others) are configurable **per project**, falling back to application defaults taken from the rule definitions. **[Added by CR-012]**
- The **affected URL(s)**, or a site-wide scope marker.
- **Evidence**: the extracted values that support the judgment.
- A **recommendation**: generic remediation guidance.
- A **first-seen audit** reference for delta tracking.
- An optional **remediation difficulty** indicator (effort estimate), independent of severity and priority.

**Near-duplicate detection principle:** page bodies are fingerprinted using overlapping token sequences (shingles); set-similarity estimation between fingerprints identifies clusters of substantially similar content. The mathematical principle is specified; the concrete hashing and estimation techniques are an implementation choice.

**Finding suppression and prioritization:** the user may suppress (ignore) an individual finding or an entire rule for a project. Suppressions are persisted, reversible, and listed in a dedicated suppressed view; suppressed findings are excluded from default counts and from scoring (section 8.1) but remain inspectable. Each rule may also carry a local **priority/importance** ranking independent of severity, used to order remediation lists. Suppression state is project-scoped and carries across re-audits of the same project.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.6 Rule execution architecture — verbatim and unabridged**

### 7.6 Rule execution architecture
> Capabilities: CAP-H-172

- Rules are **declarative conditions** over the element inventory and aggregate views; a generic evaluator applies them. Adding a rule is a data change, not an engine change.
- Per-URL passes are independent and parallelizable. Aggregation passes run once over indexed views (for example, grouping by title text, or computing reachability from seeds over the link graph).

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.7 Single-page analysis — verbatim and unabridged**

### 7.7 Single-page analysis
> Capabilities: CAP-G-X01

- An ad-hoc mode fetches and audits a single URL without creating a project or crawl. The same pipeline applies; results are shown immediately and may optionally be saved as a lightweight local record.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 4. Content Area Scoping and Boilerplate Stripping — complete section, verbatim and unabridged**

## 4. Content Area Scoping and Boilerplate Stripping
> Capabilities: CAP-G-162

**Extends FS-CRAWL-001 §7.2, §7.8, §7.9, §7.12, §7.16.** Additional hooks: §5.1, §7.3, §7.5, §8.2, §9.2, §9.6, §10.2.

### 4.1 Purpose and parent-contract gap

FS-CRAWL-001 §7.12 already states: *"All text metrics operate on content-area-scoped text when a content-area selector is configured (section 7.9)"*, and §7.16 targets *"visible text (content-area-scoped when configured)"*. The parent, however, defines custom extraction rules (§7.9) without a first-class scoping configuration. This module supplies that missing contract: **project-level and audit-level Include / Exclude Content Area selectors** and the **Scoped Visible Text Tree** they produce, applied before every text-sensitive pass.

### 4.2 Configuration model

- **Two configuration levels**, merged through the §4.1 priority order: an audit-level scope overrides the project-level default; an absent audit scope inherits the project default; no configured scope anywhere → baseline behavior (full visible text).
- A **content scope** consists of:
  - an ordered **Include list** of selectors,
  - an ordered **Exclude list** of selectors,
  - a per-entry **selector language**: CSS selector or XPath (declared per entry; both may be mixed within one scope).
- Example exclude entries (boilerplate): `nav`, `header`, `footer`, `aside`, `.cookie-banner`, `.mega-menu`, `[role=navigation]`, `[role=banner]`, `[role=contentinfo]`, `.sidebar`, `.newsletter-signup`, `.social-share`.
- **Bundled boilerplate preset:** a named, read-only preset containing the common boilerplate selectors above. Applying it is an explicit user action; nothing is stripped automatically, preserving baseline behavior (FS-CRAWL-002 §1.4 rule 4).
- **Evaluation semantics (deterministic):**
  1. Include list empty → the whole document body is the candidate region.
  2. Include list non-empty → the candidate region is the union of subtrees matched by include selectors, in document order.
  3. Exclude selectors are applied **after** inclusion, subtracting matched subtrees from the candidate region; **exclude wins** on any conflict.
  4. Nested matches are deduplicated: an ancestor match subsumes its descendants; text is never counted twice.
  5. The result preserves document order.
- **Validation and bounds:** selector syntax is validated at configuration time; invalid selectors are rejected with a descriptive local error (§4.3 pattern). Selector evaluation is time-bounded per page (the §7.9 bound philosophy); evaluation failures are isolated, logged as rule-level diagnostics, and never fail the audit. A live preview against a sample URL is provided before the crawl starts (mirroring the §7.9 rule-preview pattern).

### 4.3 Scoped Visible Text Tree
> Capabilities: CAP-G-163

**Definition:** a derived, scope-filtered visible-text view of the parsed document, produced in the §7.2 pipeline **after DOM parsing (step 2) and before text-sensitive inventory and rule passes (steps 3–4)**.

Construction rules:

- The FS-CRAWL-002 §4.2 scope is applied to the parsed tree; in rendering mode (§5.6) it is applied to the **post-render** tree.
- Visibility extraction follows standard semantics: script, style, noscript, and template content is excluded; elements hidden by standard markup/attribute semantics are excluded; in rendering mode, computed visibility is used when available.
- The tree is a **derived view, not a persisted artifact**: it is re-derived from stored bodies (§9.6) on re-audit without network access.

**Consumers (all operate on the Scoped Visible Text Tree when a scope is configured):**

| Pass | Parent hook |
|---|---|
| Visible word count / text statistics | §7.3 (Text statistics group) |
| Flesch-class readability, grade level, reading time, text-to-HTML ratio | §7.12 |
| Spelling checks | §7.8 |
| Shingle near-duplicate detection | §7.5 |
| Custom content search — visible-text target | §7.16 |
| N-gram analysis — body and headings zones | FS-CRAWL-002 §5 |

**Non-consumers (unchanged by scoping):** document-head facts (title, meta, canonical, alternates), the full heading outline structure, link discovery (§5.5), anchors, images, structured data, resources, and tables (§7.3). Scoping affects **text metrics only**. The text-to-HTML ratio keeps the **full markup size as denominator** (scoped visible-text length relative to full document markup), so boilerplate removal raises the ratio as intended. Raw-markup search targets (§7.16) are never scoped.

### 4.4 Storage, diagnostics, and re-audit (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `content_scope_profiles` | New table | Project reference; profile name; include selectors JSON (entry: language, expression, order); exclude selectors JSON; preset reference (nullable); created/updated timestamps | Reusable project-level scopes; configuration data, not pruned by audit retention |
| `crawl_configurations` | Additive field | Content scope (inline override or profile reference) | §5.1 input set extension |
| `audits` | Additive snapshot field | Resolved content scope | Snapshot pattern (§9.2): later edits never mutate history |
| `url_elements` | New element group | `content_scope` payload: include-match count, exclude-match count, full word count, scoped word count, removed word count | Additive to the §7.3 inventory; no schema change (payload JSON) |
| `metric_summaries` | New metric key | `scoped-word-count` distribution | §8.1 aggregate pattern |

Re-audit (§7.2) re-derives scoped trees and all dependent metrics from the content store (§9.6) under the audit's snapshotted scope — no re-fetching. Changing the scope and re-auditing recomputes findings; deltas surface through audit comparison (§8.2).

### 4.5 Reporting behavior

- **URL explorer (§8.2):** scoped and full word-count columns; a scope-diagnostics panel in URL detail (matched regions, removed counts).
- **Finding evidence (§7.5):** text-based findings note the scoped basis in evidence so judgments are reproducible.
- **Content comparison (§8.2):** each audit uses its own snapshotted scope; the comparison surface labels which scope produced each side.
- **Custom extraction (§7.9):** extraction rules continue to run against the full parsed document (their own path scoping is unchanged); scoping and extraction are orthogonal.

### 4.6 REST and tool additions (extends §10.2, §8.7)

- POST `/api/projects/{id}/configurations` and PUT `/api/configurations/{id}` accept the additive `contentScope` field; PUT `/api/projects/{id}` accepts project-level scope defaults and profile references.
- **New tool endpoint:** POST `/api/tools/scope-preview` — input: target URL (or stored artifact reference), include/exclude selector lists; output: matched-region summary, word counts before/after, sample scoped text, per-selector match counts. A stateless member of the §8.7 tool family, also embedded in the single-page analyzer (§7.7).
- POST `/api/analysis/single-page` results include scope diagnostics when a scope is supplied.
- MCP `url-detail` resource gains the `content_scope` diagnostics payload (additive).

### 4.7 UI requirements

- **Scope editor** in project settings and audit configuration: include/exclude lists with per-entry CSS/XPath language toggle, drag reorder, preset chips, syntax validation, and live preview against a sample URL.
- **Point-and-click capture:** the Finder selector picker (FS-CRAWL-002 §8.6) sends captured selectors directly into the include or exclude list.
- Scope state is visible in the audit header ("Scoped: 2 include / 7 exclude") so reports are never silently misread.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #37.

---
