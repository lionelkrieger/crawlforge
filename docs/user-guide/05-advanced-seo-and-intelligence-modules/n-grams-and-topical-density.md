# N-Grams and Topical Density

The N-Gram Analyzer extracts **1-gram, 2-gram, and 3-gram phrase frequencies**
across four distinct zones of every analyzed page, computes per-URL term
frequency (TF) and site-wide document frequency (DF), and turns the result into
two actionable diagnostics: **keyword cannibalization** (pages competing for
the same phrase) and **topical gaps** (phrases your internal links demand but
no page supplies).

Everything runs locally over the parsed documents the crawl already stored —
no additional fetches, no remote services, no telemetry. Re-audits recompute
the full analysis offline from the content store.

> **Note:** The analyzer runs by default within mandatory bounds. Excess data
> beyond a bound is **discarded and counted** — surfaced as diagnostics, never
> an audit failure.

---

## Where the analysis runs

| Stage | When | What |
|---|---|---|
| Per-URL tokenization and counting | Per-URL pass, during incremental execution | N-gram frequencies over the same parsed document as the built-in inventory |
| Site-wide DF, cannibalization, and gap passes | Cross-URL aggregation, full mode after completion | Indexed views over the whole dataset |
| Re-audit | On demand | Everything recomputed from stored bodies, without network access |

URLs blocked as `waf-challenge` and non-HTML records are **excluded** from the
analysis.

---

## The four zones

Each URL is counted separately in four zones:

| Zone key | Source | Scoping |
|---|---|---|
| `body` | Scoped Visible Text Tree body text | Content scope applied |
| `headings` | H1–H6 text within the scoped tree | Content scope applied |
| `title` | Full document title | Never scoped (head fact) |
| `inbound-anchor` | Anchor text of internal link edges targeting the URL | As recorded; scoping does not apply (anchors belong to source pages) |

The `inbound-anchor` zone is what makes topical-gap detection possible: it
captures how the rest of the site *talks about* a page, independent of the
page's own text.

> **Tip:** Configure a content scope before analyzing n-grams if your templates
> carry heavy boilerplate — the `body` and `headings` zones then measure
> content phrases, not menu labels. See the scoping guide.

---

## Tokenization pipeline

Per page, in order:

1. **Language selection.** The page's declared language; fallback to your
   preferred checking language — the same selection rule as spelling
   dictionaries.
2. **Tokenization.** Unicode-aware word tokenization (letter/digit sequences),
   locale-aware handling of apostrophes and hyphens, case folding,
   configurable minimum token length (default **2**), optional numeric-token
   exclusion.
3. **Sentence segmentation.** N-grams **never cross sentence boundaries or
   zone boundaries**.
4. **Stop-word filtering.** Bundled per-language stop-word lists ship as
   offline locale assets — never fetched remotely. Your **user stop-word
   additions** are stored locally per language and applied alongside the
   bundled list. Stop words are removed **before** n-gram construction:
   contiguity is defined over the filtered token stream.
5. **N-gram construction.** Contiguous 1-, 2-, and 3-grams within each zone,
   keyed by their normalized joined form.

> **Note:** Because stop words are removed first, "guide to internal linking"
> can become the 2-gram `internal linking` — contiguity is measured on the
> filtered stream, not the raw text.

---

## Metrics

| Metric | Definition |
|---|---|
| Raw count | Occurrences of an n-gram in a (URL, zone, order) |
| TF (term frequency) | Count ÷ total n-grams of the same order in that zone for the URL (normalized 0–1) |
| DF (document frequency) | Number of distinct URLs containing the n-gram — computed per zone and as an any-zone union |
| Site-wide total frequency | Sum of raw counts across URLs |
| Topical density | TF × log(1 + N / DF), where N is the audit's analyzed-URL count — a TF-IDF-class weighting |

**Primary terms:** per URL, the highest-density **2-gram and 3-gram** in the
`title` zone and in **H1 text** (from `headings`), using configurable zone
weights (defaults: title **2.0**, H1 **1.5**). Primary terms drive
cannibalization clustering.

---

## Worked example: from counts to density

Suppose an audit analyzes **N = 1,000** URLs, and the 2-gram
`internal linking` appears:

- **12 times** in the `body` zone of page A, which has **600** body 2-grams
  in total.
- On **40** distinct URLs site-wide (body-zone DF = 40).

Then for page A, body zone, order 2:

| Metric | Calculation | Result |
|---|---|---|
| Raw count | occurrences in (A, body, 2) | 12 |
| TF | 12 ÷ 600 | 0.02 |
| DF | distinct URLs containing the term (body zone) | 40 |
| Topical density | 0.02 × log(1 + 1000 / 40) | ≈ 0.065 |

A term that is frequent on one page but rare across the site scores high —
that is the TF-IDF-class intuition: density rewards *distinctive* topical
focus, not mere repetition. The any-zone DF union additionally counts URLs
where the term appears in *any* zone, which is what the site explorer's
DF column can show alongside per-zone DF.

**Primary terms** apply the same density ranking with zone weights: the
highest-density 2-gram and 3-gram in the `title` zone (weight 2.0) and in H1
text (weight 1.5) become the page's primary terms — the phrases cannibalization
clustering compares across pages.

---

## Keyword cannibalization

The analyzer clusters **indexable** URLs whose primary 2-gram or 3-gram in
Titles/H1s is **identical after normalization**. A cluster of ≥ 2 URLs emits a
finding:

| Property | Value |
|---|---|
| Rule key | `content.keyword-cannibalization` |
| Category | Content quality |
| Severity | **Warning** |
| Severity downgrade | **Notice** when every cluster member declares the same canonical target (consolidation already signaled) |
| Evidence | Shared n-gram, order, zone, competing URL list with per-URL TF, site DF, and canonical targets |

Thresholds — minimum cluster size, minimum DF, minimum TF, and the
indexable-only toggle — are local audit configuration. Suppression follows the
standard finding model (project-scoped, reversible, excluded from default
counts and scoring while suppressed).

> **Tip:** A Warning cluster with *different* canonical targets is the classic
> cannibalization case: decide which URL should own the phrase, consolidate or
> differentiate the others, and re-audit to verify the cluster resolves.

---

## Topical gaps

A topical gap is **anchor demand without on-page supply**: an n-gram that
internal links use frequently, but no page targets in its title or headings.

Detection rule: n-grams with **inbound-anchor DF ≥ a configurable threshold
(default 3)** and **zero DF in the `title` and `headings` zones site-wide**.

| Property | Value |
|---|---|
| Rule key | `content.topical-gap` |
| Severity | **Notice** |
| Evidence | Term, anchor DF, sample source URLs |

> **Tip:** Read a gap as a content brief: the site already links with that
> phrase — create or retarget a page whose title/H1 supplies it, then point
> the existing anchors at it.

---

## Managing stop words

- **Bundled lists** ship per language as offline locale assets — the same
  packaging pattern as spelling dictionaries. They are never fetched
  remotely, so analysis works air-gapped.
- **Your additions** are stored locally per language (`user_stopwords`) and
  applied alongside the bundled list. Add site-specific noise tokens
  (brand boilerplate, UI labels that survive scoping) to sharpen phrase
  extraction.
- Stop-word changes take effect on the next analysis run; re-audit recomputes
  everything offline from stored bodies.
- Stop-word and configuration management is intentionally **not** exposed via
  MCP — the automation boundary stays read-only for analysis data.

> **Tip:** If a high-frequency 2-gram in the explorer is pure template noise,
> first check whether a content scope would remove it (scoping fixes the
> source); add a stop word only when the token is genuinely linguistic noise
> across pages.

---

## Bounds: discard-and-count

Mandatory bounds keep the analysis tractable at 500,000+ URL scale. Excess is
**discarded and counted**, never fatal:

| Bound | Default | Behavior |
|---|---|---|
| Top K terms per URL, per zone, per order | **250** (configurable) | Retain the top K by count; tail discards are counted and surfaced as diagnostics |
| Site-summary retention | DF ≥ **2** plus the top M terms by total frequency | M default **100,000**; bounded storage |

Discard counters (`TailTermsDiscarded`, `SiteRowsDiscarded`) appear in audit
diagnostics. If you see heavy tail discards on a content-rich site, raise the
top-K bound locally and re-audit — the analysis recomputes offline.

---

## Configuration reference

All values are local audit configuration:

| Field | Default | Notes |
|---|---|---|
| `TopKPerUrlZoneOrder` | 250 | Top K terms by count retained per URL/zone/order |
| `SiteMinDocumentFrequency` | 2 | Site summaries retain terms with DF at least this value |
| `TopMSiteTerms` | 100,000 | Plus the top M terms by total frequency |
| `MinTokenLength` | 2 | Minimum token length |
| `ExcludeNumericTokens` | Off | Exclude purely numeric tokens |
| `TitleZoneWeight` | 2.0 | Primary-term density weight for the title zone |
| `HeadingsZoneWeight` | 1.5 | Primary-term density weight for H1/headings text |
| `CannibalizationEnabled` | On | Keyword-cannibalization detection |
| `TopicalGapEnabled` | On | Topical-gap detection |
| `MinAnchorDf` | 3 | Minimum inbound-anchor DF for a topical gap |
| `MinClusterSize` | 2 | Minimum cannibalization cluster size |
| `MinTermFrequency` | 0 | Minimum per-URL TF for primary-term candidacy |
| `IndexableOnly` | On | Restrict detection to indexable URLs |
| `PreferredLanguage` | `en` | Fallback analysis language when the page declares none |

User stop-word additions are stored per language (`user_stopwords`) alongside
the bundled offline lists; they are configuration data and are never pruned by
audit retention.

---

## Storage and read models

| Object | Role |
|---|---|
| `ngram_frequencies` | Per-URL rows: audit, URL, zone, order, term, raw count, TF. Unique per (audit, URL, zone, order, term); pruned with audit retention |
| `ngram_site_summaries` | Site-level aggregate: zone, order, term, DF, total frequency, mean TF, primary-term URL count. Materialized as a precomputed read-model table on **both** database providers, so explorer reads are constant-cost |
| `user_stopwords` | Your per-language stop-word additions — configuration data, never pruned by audit retention |

Because site summaries are precomputed, sorting and filtering the site-wide
explorer stays fast even at hundreds of thousands of URLs; per-URL drill-downs
read the indexed frequency rows.

---

## Reading the N-Gram Explorer

The **N-Gram Explorer** is a dedicated report surface:

- **Zone tabs:** Body / Headings / Title / Inbound Anchor.
- **Order toggle:** 1 / 2 / 3.
- **Term search** plus sortable **TF / DF / frequency / density** columns.
- **Drill-down** from any term to the URLs carrying it.
- A **per-URL n-gram panel** in URL detail (top primary terms are also exposed
  in the MCP `url-detail` resource).

Companion views:

- **Cannibalization view:** cluster cards listing competing URLs with
  canonical hints and one-click navigation to the URL explorer filtered to
  the cluster.
- **Topical gaps view:** gap terms with anchor-demand evidence.
- **Charts:** top-terms bar charts, DF distribution histogram, and a topical
  treemap. Chart filters propagate to the explorer.

The grid is virtualized, so site-summary scale stays responsive.

### Per-URL vs site-level results

| View | Answers | Use it for |
|---|---|---|
| Site-level (`scope=site`) | Which phrases dominate the site? How widely is each phrase used (DF)? | Topical authority, cannibalization triage, gap discovery |
| Per-URL (`scope=url`) | Which phrases does this page actually carry, and how strongly (TF)? | On-page optimization, verifying a retargeted title/H1 |

---

## Workflow: find and fix cannibalization

1. Run a full audit (cross-URL passes execute after completion).
2. Open the **Cannibalization view**; sort by cluster size.
3. Open a cluster card: inspect the shared primary n-gram, the competing
   URLs' TF, site DF, and canonical targets.
4. Decide the owning URL. Consolidate (canonical/redirect) or differentiate
   (retarget title/H1 to a distinct primary phrase).
5. Re-audit — the analysis recomputes offline; resolved clusters disappear
   from the finding list, and audit comparison shows the delta.

## Workflow: close a topical gap

1. Open the **Topical gaps view**; sort by anchor DF.
2. Pick a gap term with strong anchor demand; inspect the sample source URLs
   to understand the linking context.
3. Create a page (or retarget an existing one) whose title/H1 supplies the
   phrase — the scoping guide helps keep the analysis clean.
4. Point the existing anchors at the supplying page.
5. Re-audit and verify: the gap closes when title/headings DF becomes
   non-zero.

---

## Exports, metrics, and API

- **Exports:** the `ngrams` dataset selection covers site summaries, per-URL
  frequencies, and cannibalization clusters (spreadsheet/CSV).
- **Metric keys:** `ngram-top` (dimensions: zone, order),
  `cannibalization-clusters`, `topical-gaps` — dashboard cards and charts
  (`ngram-top` chart series).
- **Findings:** the standard findings endpoint filters the two rule keys
  without contract change.

| Operation | Method & path |
|---|---|
| N-gram frequencies (`scope=site\|url`, zone, order, term search, URL filter, min DF, sorting, pagination) | GET `/api/audits/{id}/ngrams` |
| Cannibalization clusters (order, zone, minimum cluster size, pagination) | GET `/api/audits/{id}/ngrams/cannibalization` |
| Topical gaps (minimum anchor DF, pagination) | GET `/api/audits/{id}/ngrams/gaps` |

**MCP:** the read-only `ngram-analysis` resource projects the same data
(filters: `scope`, `zone`, `order`, `term`, `urlId`, `minDf`, pagination); the
optional `ngram-topical-briefing` prompt composes it with `audit-summary` into
an agent-ready topical briefing with no side effects. Stop-word and
configuration management is intentionally **not** exposed via MCP.

---

## Related guides

- [Content scoping and boilerplate stripping](content-scoping-and-boilerplate-stripping.md)
- [Semantic internal linking](semantic-internal-linking.md)
- [WAF and soft-block detection](waf-and-soft-block-detection.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)

[← Documentation portal](../README.md)
