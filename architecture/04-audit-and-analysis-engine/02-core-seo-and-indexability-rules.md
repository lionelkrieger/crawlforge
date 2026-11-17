# Core SEO, Indexability & Directive Equivalency Rules

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §7.8, §7.12, §7.13, §7.14, §7.15, §7.18 |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | §15 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.8 Spelling checks — verbatim and unabridged**

### 7.8 Spelling checks

- Visible page text is checked against bundled per-language dictionaries; the dictionary is chosen from the page's declared language, falling back to the user's preferred checking language.
- A locally stored **user dictionary** (per language) lets the user add accepted words; user-dictionary entries suppress findings.
- Findings carry the offending tokens and their positions as evidence; exclusion of proper nouns and code-like tokens is handled by generic heuristics (implementation choice).
- Dictionary assets ship with the product per locale (section 13); checking works fully offline.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.12 Readability, content metrics, and soft-404 detection — verbatim and unabridged**

### 7.12 Readability, content metrics, and soft-404 detection

- **Readability:** a standard reading-ease score and grade-level estimate (Flesch-class formulas over sentence, word, and syllable counts) are computed from visible text, per page and in aggregate.
- **Reading time:** estimated from word count and a configurable words-per-minute rate.
- **Text-to-HTML ratio:** visible-text length relative to markup size, as a thin-content signal.
- **Sentiment (optional):** polarity scoring against a bundled affect lexicon; disabled by default and enabled per audit.
- **Soft-404 detection:** pages returning a success status whose content matches error-like patterns (a configurable phrase list plus structural heuristics) are flagged as soft-404 findings, distinct from hard HTTP errors.
- All text metrics operate on content-area-scoped text when a content-area selector is configured (section 7.9).

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.13 Structured data validation — verbatim and unabridged**

### 7.13 Structured data validation

- Embedded structured data is parsed from JSON-LD and Microdata into a normalized graph resolved against a bundled open vocabulary.
- Validation covers syntax (well-formedness, context and type presence) and semantics (required and expected properties per type).
- **Rich-result eligibility:** for each supported search-gallery feature type, the engine checks the property set required for eligibility and reports eligible / ineligible / missing-required-field, naming the specific fields involved.
- Errors use a structured, human-readable taxonomy (e.g. missing type, invalid value, missing required field, malformed block) rather than free text only.
- Findings are emitted per block; a structured-data explorer lists every block, its type, and its validation outcome.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.14 Mobile-friendliness (optional category) — verbatim and unabridged**

### 7.14 Mobile-friendliness (optional category)

- Uses the rendered document and the configured device-emulation profile (section 5.6).
- Checks: viewport meta presence and validity; tap-target sizing and spacing; font legibility; content-to-viewport fit; and responsive-design signals.
- Violations emit findings and contribute to a mobile-readiness aggregate and to audit scoring (section 8.1).

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.15 Technology detection (optional category) — verbatim and unabridged**

### 7.15 Technology detection (optional category)

- A bundled, versioned fingerprint catalogue is matched against DOM markers, script/source patterns, response headers, and meta generators to detect platforms, frameworks, analytics, and other technologies, grouped by category with a confidence indicator.
- Detections are stored per URL and surfaced in a technology explorer and an aggregate "technologies in use" view.
- The catalogue is a bundled asset; it is never fetched from a remote service at runtime and updates only with product updates.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.18 Response-vs-rendered comparison — verbatim and unabridged**

### 7.18 Response-vs-rendered comparison

- When rendering mode is enabled, both the raw HTTP response body and the post-render document are retained for each page.
- The engine compares status, title and metadata, link sets, visible text, structured data, and resource sets between the two.
- Differences surface as findings (e.g. content or links present only after rendering, metadata altered by script) and as a side-by-side diff in the URL detail view, supporting a "rendered" report surface.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 15. Contextual Link Weighting, Pixel-Width SERP Measurement & Header/HTML Equivalency — complete section, verbatim and unabridged**

## 15. Contextual Link Weighting, Pixel-Width SERP Measurement & Header/HTML Equivalency

**Extends FS-CRAWL-001 §7.3, §7.4, §8.1, §8.2 and FS-CRAWL-002 §4.** Additional hooks: §5.4, §9.2, §9.3, §10.2, §11.4.

### 15.1 DOM-zone link classification

- Every extracted `link_edge` (§9.2) is classified by the DOM zone of its source anchor: **`in-content`, `navigation`, `header`, `footer`, `sidebar`, `breadcrumb`**.
- Classification uses landmark elements/roles and the Content Area Scoping rules (FS-CRAWL-002 §4): anchors inside the scoped content region classify as `in-content`; boilerplate landmarks classify by deterministic precedence — breadcrumb markers > `nav`/`role=navigation` > `header`/`role=banner` > `footer`/`role=contentinfo` > `aside`/`role=complementary` (sidebar) > default `in-content` for body anchors matching no boilerplate landmark.
- Persisted as the additive column **`link_edges.dom_zone`** with zone evidence; classification runs during link inventory and is re-derived from stored bodies on re-audit (§9.6).

### 15.2 Weighted link equity (extends §8.1)

- **Zone weights** are configurable per audit; built-in defaults: `in-content` **1.0**, `breadcrumb` **0.6**, `navigation` **0.3**, `sidebar` **0.3**, `header` **0.2**, `footer` **0.1** — editorial in-content links transfer significantly more weight than boilerplate.
- The internal PageRank / link-equity computation (§8.1) runs in a **weighted variant**: each edge contributes its zone weight (standard iterative computation with configurable damping; mathematical principle specified, implementation choice). The unweighted distribution is retained for comparability.
- Outputs: per-URL weighted equity score, percentile, and concentration, persisted under `metric_summaries` key `link-equity-weighted`; graph node sizing offers a weighted/unweighted toggle (FS-CRAWL-002 §8.5).

### 15.3 Pixel-width SERP and social card measurement (extends §7.3, §7.4)

- **Measurement:** rendered pixel widths of `<title>` and `<meta name="description">` are computed from **bundled per-locale font-metric tables** (standard search-result font metrics; packaged offline assets, no web-font fetch). Width = summed glyph advances with the search engine's ellipsis behavior.
- **Configurable budgets** (defaults are approximations of standard search-result rendering): title desktop **~580 px**; title mobile **~920 px** with two-line wrap; description desktop **~920 px** two-line; description mobile **~920 px** two-line.
- **Findings:** `title-pixel-truncation` and `description-pixel-truncation`, category *Content quality* (§7.4), severity **Warning**; evidence: computed pixels, budget, overflow, estimated truncation point. Character-count checks (§7.4) remain; pixel checks are additive.
- **Live SERP & Social Card Preview** in the URL explorer: desktop/mobile SERP snippet (title, URL, description with ellipsis at the measured truncation point) plus social card from the Open Graph/Twitter facts (§7.3). Rendered locally; preview images come from the content store when captured (§9.6); no remote fetch.
- Measurements persist as the `url_elements` group `serp_metrics`.

### 15.4 HTTP header vs HTML directive equivalency (extends §7.3, §7.4)

Reconcile response-header directives (recorded per §5.4) against their HTML counterparts in the inventory (§7.3):

| Header source | HTML counterpart | Conflict finding (severity **Error**) |
|---|---|---|
| `X-Robots-Tag` (per directive, per agent) | `<meta name="robots">` / agent-specific meta | `indexability.header-html-conflict` — category *Indexability & status* |
| `Link: rel="canonical"` | `<link rel="canonical">` | `canonical.header-html-conflict` — category *Indexability & status* |
| `Link: rel="alternate"; hreflang="..."` | `<link rel="alternate" hreflang>` | `hreflang.header-html-conflict` — category *Internationalization* |

- Evidence carries both values and a directive-level diff; agreement may record a Pass annotation. Parsed header directives persist as the `url_elements` group `header_directives`.
- Conflicts are high-severity because search engines may honor either source; the finding never auto-resolves the conflict.

### 15.5 Schema and API additions (extends §9.2, §10.2, §11.4)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `link_edges` | Additive column | `dom_zone` (six-zone enum); zone evidence | Parent link indexes (§9.3) unchanged; additive index (audit, dom_zone) |
| `url_elements` | New element groups | `serp_metrics` (pixel widths, budgets, truncation flags); `header_directives` (parsed header directives) | Additive to the §7.3 inventory; payload JSON |
| `crawl_configurations` | Additive fields | Zone weights; SERP pixel budgets | §5.1 input-set extension |
| `metric_summaries` | New metric keys | `link-equity-weighted` (dimensions: URL); `serp-truncation` | §8.1 aggregate pattern |

REST: GET `/api/audits/{id}/links` gains a `domZone` filter and weighted-equity fields; GET `/api/audits/{id}/urls/{urlId}` gains `serp_metrics` and header-conflict detail; GET `/api/audits/{id}/charts/{metric}` accepts `link-equity-weighted`. MCP `url-detail` gains the same additive fields. No new hub events (aggregates ride existing events).

### 15.6 UI requirements

- Link explorer: zone badges and zone filter; weighted/unweighted equity toggle in Visualizations (node sizing, FS-CRAWL-002 §8.5).
- URL detail: **SERP & Social Card Preview** panel with desktop/mobile toggle and live truncation highlight; header/HTML conflict panel with side-by-side directive diff.
- All tabular content obeys FS-CRAWL-002 §7.1.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #48.

---
