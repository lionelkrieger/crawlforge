# N-Gram Analysis & Vector Semantics

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §5 (complete section), §12 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 5. Site-Wide N-Gram and Topical Density Analyzer — complete section, verbatim and unabridged**

## 5. Site-Wide N-Gram and Topical Density Analyzer
> Capabilities: CAP-F-143, CAP-F-144

**Extends FS-CRAWL-001 §7.2, §8.2, §9.2, §10.2, §11.4.** Additional hooks: §7.3, §7.4, §7.5, §8.1, §8.4, §9.6, §13.

### 5.1 Purpose and pipeline placement

The analyzer extracts 1-gram, 2-gram, and 3-gram phrase frequencies across four distinct zones per URL, computes per-URL term frequency (TF) and site-wide document frequency (DF), and surfaces **keyword cannibalization** and **topical gaps** as findings and report surfaces.

Pipeline placement (§7.2):

- **Per-URL tokenization and counting** run in the per-URL pass (step 4) during incremental execution, over the same parsed document as the built-in inventory — no additional fetches.
- **Site-wide DF, cannibalization, and gap passes** are cross-URL aggregation passes (step 5) over indexed views, executed in full mode after completion.
- **Re-audit** recomputes everything from stored bodies (§9.6) without network access.
- URLs blocked as `waf-challenge` (FS-CRAWL-002 §2) and non-HTML records are excluded.

### 5.2 Tokenization and stop-word pipeline

Per page, in order:

1. **Language selection:** the page's declared language (§7.3 Text statistics); fallback to the user's preferred checking language — the same selection rule as spelling dictionaries (§7.8).
2. **Tokenization:** Unicode-aware word tokenization (letter/digit sequences); locale-aware handling of apostrophes and hyphens; case folding; configurable minimum token length (default 2); optional numeric-token exclusion.
3. **Sentence segmentation:** n-grams never cross sentence boundaries or zone boundaries.
4. **Stop-word filtering:** bundled per-language stop-word lists, shipped as offline locale assets (§13 packaging, the §7.8 dictionary pattern); never fetched remotely. **User stop-word additions** are stored locally per language (`user_stopwords` table) and applied alongside the bundled list. Stop words are removed **before** n-gram construction: contiguity is defined over the filtered token stream.
5. **N-gram construction:** contiguous 1-, 2-, and 3-grams within each zone (FS-CRAWL-002 §5.3), keyed by their normalized joined form.

### 5.3 Zones

Four distinct zones per URL, counted separately:

| Zone key | Source | Scoping |
|---|---|---|
| `body` | Scoped Visible Text Tree body text | FS-CRAWL-002 §4 scope applied |
| `headings` | H1–H6 text within the scoped tree | FS-CRAWL-002 §4 scope applied |
| `title` | Full document title | Never scoped (head fact) |
| `inbound-anchor` | Anchor text of internal link edges targeting the URL (`link_edges`, §9.2) | As recorded; scoping does not apply (anchors belong to source pages) |

### 5.4 Metrics

- **Raw count:** occurrences of an n-gram in a (URL, zone, order).
- **TF (term frequency):** count ÷ total n-grams of the same order in that zone for the URL (normalized 0–1).
- **DF (document frequency):** number of distinct URLs containing the n-gram — computed per zone and as an any-zone union.
- **Site-wide total frequency:** sum of raw counts across URLs.
- **Topical density:** TF × log(1 + N / DF), where N is the audit's analyzed-URL count — a TF-IDF-class weighting; the mathematical principle is specified, exact smoothing is an implementation choice.
- **Primary terms:** per URL, the highest-density 2-gram and 3-gram in the `title` zone and in H1 text (from `headings`), using configurable zone weights (defaults: title 2.0, H1 1.5).

### 5.5 Cannibalization and topical-gap detection
> Capabilities: CAP-F-149, CAP-F-X01

- **Keyword cannibalization:** cluster **indexable** URLs (indexability flags, §9.2 `url_records`; §7.4) whose primary 2-gram or 3-gram in Titles/H1s is identical after normalization. A cluster of ≥ 2 URLs emits a finding:
  - Rule key `content.keyword-cannibalization`, category *Content quality* (§7.4), severity **Warning**.
  - Evidence: shared n-gram, order, zone, competing URL list with per-URL TF, site DF, and canonical targets.
  - When every cluster member declares the same canonical target, severity downgrades to **Notice** (consolidation already signaled).
- **Topical gaps:** n-grams with inbound-anchor DF ≥ a configurable threshold (default 3) and zero DF in the `title` and `headings` zones site-wide — anchor demand without on-page supply. Emits rule key `content.topical-gap`, severity **Notice**, evidence: term, anchor DF, sample source URLs.
- Thresholds (minimum cluster size, minimum DF, minimum TF, indexable-only toggle) are local audit configuration; suppression follows §7.5.

### 5.6 Mandatory bounds and schema additions (extends §9.2)

Bounds (the §7.9 philosophy — excess is discarded and counted, never fatal):

- Per URL, per zone, per order: retain the **top K terms by count** (default 250, configurable); tail discards are counted and surfaced as diagnostics.
- Site summaries retain terms with DF ≥ 2 plus the top M terms by total frequency (default 100,000) — bounded storage at 500,000+ URL scale.

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `ngram_frequencies` | New table | Audit reference; URL reference; zone (`body`, `headings`, `title`, `inbound-anchor`); order (1, 2, 3); term text; raw count; TF | Unique (audit, URL, zone, order, term); indexes (audit, zone, order, term) and (audit, URL); pruned with retention (§9.5) |
| `ngram_site_summaries` | New aggregate view | Audit reference; zone; order; term; DF; total frequency; mean TF; primary-term URL count | Logical aggregate view, materialized as a precomputed read-model table on both providers (§2.4 command/query separation, §8.1 pattern) so explorer reads are constant-cost; index (audit, zone, order, DF descending) |
| `user_stopwords` | New table | Language; term; added-at timestamp | Configuration data; mirrors the `user_dictionary` pattern (§9.2); not pruned by audit retention |
| `metric_summaries` | New metric keys | `ngram-top` (dimensions: zone, order); `cannibalization-clusters`; `topical-gaps` | Dashboard cards and charts |

### 5.7 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| N-gram frequencies | GET `/api/audits/{id}/ngrams` | `scope=site\|url`; zone; order; term search; URL filter; min DF; sorting (TF, DF, frequency); pagination | Site-summary rows (`scope=site`) or per-URL rows (`scope=url`) |
| Cannibalization clusters | GET `/api/audits/{id}/ngrams/cannibalization` | Order; zone; minimum cluster size; pagination | Clusters: shared primary n-gram, competing indexable URLs with TF/DF/canonical evidence |
| Topical gaps | GET `/api/audits/{id}/ngrams/gaps` | Minimum anchor DF; pagination | Gap terms with anchor DF and sample source URLs |

Extended existing routes: POST `/api/exports` accepts an `ngrams` dataset selection (site summaries, per-URL frequencies, cannibalization clusters — §8.4 spreadsheet/CSV pattern); GET `/api/audits/{id}/charts/{metric}` accepts `ngram-top`; GET `/api/audits/{id}/findings` filters the new rule keys without contract change.

### 5.8 MCP additions (extends §11.4)

- **New resource `ngram-analysis`** (address pattern: resource kind + audit reference + optional filters): filters `scope`, `zone`, `order`, `term`, `urlId`, `minDf`, pagination. Content: site-summary rows, per-URL rows, and top cannibalization clusters — a bounded, read-only projection of the REST endpoints.
- **Optional new prompt `ngram-topical-briefing`** (parameter: audit reference): composes `ngram-analysis` with `audit-summary` into an agent-ready topical briefing; executes no side effects.
- The MCP boundary is preserved (§11.1): no stop-word or configuration management via MCP; `url-detail` gains top primary terms as an additive field.

### 5.9 UI requirements — N-Gram Explorer

A new report surface, additive to the §8.2 catalog:

- **N-Gram Explorer:** zone tabs (Body / Headings / Title / Inbound Anchor), order toggle (1/2/3), term search, sortable TF / DF / frequency / density columns, drill-down to the URLs carrying a term, and a per-URL n-gram panel in URL detail.
- **Cannibalization view:** cluster cards listing competing URLs with canonical hints and one-click navigation to the URL explorer filtered to the cluster.
- **Topical gaps view:** gap terms with anchor-demand evidence.
- **Charts:** top-terms bar charts, DF distribution histogram, topical treemap (Apache ECharts, FS-CRAWL-002 §8); chart filters propagate to the explorer (§8.3).
- The grid obeys the virtualization mandate (FS-CRAWL-002 §7) at site-summary scale.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #38.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 12. Local Vector Embeddings & Semantic Internal Linking Pipeline — complete section, verbatim and unabridged**

## 12. Local Vector Embeddings & Semantic Internal Linking Pipeline

**Activates FS-CRAWL-001 §2.4 (vector-similarity capability) and §9.2 (`url_embeddings`).** Additional hooks: §2.6, §2.7, §5.8, §7.4, §7.5, §8.1, §8.2, §9.6, §10.2, §10.3, §11.4, §13.

### 12.1 Purpose — activating the dormant schema

FS-CRAWL-001 §2.4 mandates vector-similarity search as a required database capability, and §9.2 defines `url_embeddings` (audit reference; URL reference; embedding payload; model tag) — but the parent specifies no pipeline that populates or queries it. This module is that pipeline: **strictly local** embedding generation and three semantic audit passes over the resulting vectors.

### 12.2 Embedding providers (strictly local)

| Provider class | Contract |
|---|---|
| **Bundled local ONNX runtime model** | Packaged model asset (§13 packaging manifest); offline CPU/GPU execution; model tag and vector dimension recorded with every embedding; no network of any kind |
| **Loopback local LLM/embedding daemon** | Ollama / LM Studio-class daemon at a **user-configured loopback endpoint** (e.g. `http://127.0.0.1:11434` or any configurable loopback port). Configuration validation **rejects any non-loopback address** (§4.3 pattern). Connection-test action mirrors the §8.5 / §2.4 test-connection pattern. The daemon is user-owned local infrastructure; no API keys to remote services, no cloud fallback |

- Provider selection lives in settings (`application_settings`); the active provider and model tag are snapshotted per audit.
- If the provider is unavailable, semantic passes are marked **unavailable** with a local diagnostic; audits never fail because of embeddings.
- No remote embedding API, remote vector store, or hosted LLM service is permitted (FS-CRAWL-002 §1.6 E7; §2.6 boundary preserved — loopback stays on-machine, FS-CRAWL-002 §1.4 rule 9).

### 12.3 Input source and chunking

- Per **indexable** URL, the embedding input is the **Scoped Visible Text Tree (FS-CRAWL-002 §4.3) + Title + H1**, assembled through a deterministic template (title, H1, then scoped body).
- Bounded chunking: maximum chunk length with optional overlap (implementation choice within bounds); long documents produce multiple chunks whose vectors are pooled by a deterministic rule (mean pooling specified as the default).
- **Incremental re-embedding:** on re-audit, only URLs whose stored content hash changed (`content_artifacts`, §9.2) are re-embedded; the pass is idempotent and resumable.
- Mixing model tags within one audit is prohibited: changing the model requires a full re-embed of that audit's vectors.

### 12.4 Storage and query

- `url_embeddings` (§9.2) is populated in transactional batches (§5.8); the embedding payload is the provider's vector serialization, the model tag identifies provider + model + dimension.
- Similarity metric: **cosine similarity**, executed through the database provider's vector-similarity capability (§2.4) on both providers; nearest-neighbour queries are indexed and bounded (top-K).

### 12.5 Semantic audit passes
> Capabilities: CAP-E-129, CAP-F-146

| Pass | Definition | Deliverable |
|---|---|---|
| **Semantic cannibalization** | Pairs/clusters of indexable URLs whose cosine similarity ≥ a configurable high threshold (default **0.88**); clustering over the similarity graph (connected-components-class; implementation choice) | Finding `content.semantic-cannibalization`, category *Content quality* (§7.4), severity **Warning** (**Notice** when all members share one canonical target); evidence: similarity scores, cluster members, canonical hints. Complements exact N-gram cannibalization (FS-CRAWL-002 §5.5); when both fire for the same URL set, evidence cross-references the other pass |
| **Automated internal link opportunities** | For each indexable URL, top-K semantically related pages (default K = 10) within a configurable similarity band (default **0.75–0.87**) that **do not currently share a link** in `link_edges` (§9.2) in either direction | Persisted recommendations: source URL, target URL, similarity score, and **candidate anchor phrases drawn from the target's top N-grams** (FS-CRAWL-002 §5); report dataset + export, not findings by default |
| **Semantic relevance of existing links** | Internal edges whose source/target cosine similarity falls below a configurable low-relevance floor (default **0.35**) | Finding `linking.low-relevance-link`, category *Linking* (§7.4), severity **Notice**; evidence: similarity, anchor text, DOM zone (FS-CRAWL-002 §15) |

### 12.6 Execution and bounds

- Runs as a background service (§2.7 pattern) after crawl completion or during incremental idle; bounded concurrency; provider request timeouts; per-URL failures isolated, counted, and surfaced as diagnostics — never fatal (§7.9 bound philosophy).
- Progress streams over the hub (FS-CRAWL-002 §12.9); the job is resumable via content-hash comparison (FS-CRAWL-002 §12.3).
- Opportunity output is bounded: top-K per source URL; excess discarded and counted.

### 12.7 Schema additions (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `url_embeddings` | **Activated** (parent table) | Audit reference; URL reference; embedding payload; model tag | Populated and queried per FS-CRAWL-002 §12.4; no schema change |
| `semantic_link_opportunities` | New table | Audit reference; source URL reference; target URL reference; similarity; anchor candidates JSON; computed-at | Unique (audit, source, target); indexes (audit, source), (audit, similarity descending); pruned with retention (§9.5) |
| `semantic_clusters` | New table | Audit reference; cluster reference; URL reference; similarity to cluster centroid; primary-member flag | Index (audit, cluster); pruned with retention (§9.5) |
| `audits` | Additive snapshot fields | Embedding provider, model tag, thresholds (cannibalization, band, floor) | Snapshot pattern (§9.2) |
| `metric_summaries` | New metric keys | `semantic-clusters`; `semantic-opportunities` | Dashboard cards |

### 12.8 REST additions (extends §10.2)

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Run / re-run embeddings | POST `/api/audits/{id}/semantics/run` | Provider override (optional); force re-embed flag | Job accepted (immediate; progress over hub) |
| Similar pages | GET `/api/audits/{id}/semantics/similar` | URL reference; top-K; minimum similarity | Ranked similar URLs with scores |
| Link opportunities | GET `/api/audits/{id}/semantics/link-opportunities` | Source filter; similarity band; pagination | Source, target, similarity, anchor candidates |
| Semantic clusters | GET `/api/audits/{id}/semantics/clusters` | Minimum cluster size; pagination | Clusters with members and centroid similarities |
| Provider test | POST `/api/settings/embedding/test` | Provider configuration | Reachability + model/dimension probe (loopback validation enforced) |

**New MCP resource `semantic-link-opportunities`** (audit reference; source filter; band; pagination): read-only projection of the opportunities endpoint. **Optional new prompt `semantic-topical-briefing`** (audit reference): composes `semantic-link-opportunities` with `audit-summary`; no side effects. Provider configuration is not exposed via MCP (§11.1 boundary).

### 12.9 Hub additions (extends §10.3)

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| `embedding-progress` | Server → shell | Audit identifier; embedded count; total eligible; provider; phase | At most once per second while running |
| `embedding-completed` | Server → shell | Audit identifier; final counts; pass summaries (clusters, opportunities, low-relevance links) | Once per job finalization |

### 12.10 UI requirements

A new **Semantic** report surface, additive to the §8.2 catalog:

- Link-opportunities grid (source, target, similarity, anchor candidates), virtualized per FS-CRAWL-002 §7.1, with one-click navigation to source/target URL detail.
- Cluster view with a similarity-edge graph overlay (Sigma.js + Graphology, FS-CRAWL-002 §8.5) under the same LOD budgets.
- Per-URL "semantically related" panel in URL detail; cannibalization findings cross-linked to the N-gram cannibalization view (FS-CRAWL-002 §5.9).
- Provider settings with connection-test feedback and explicit loopback-only validation errors.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #45.

---
