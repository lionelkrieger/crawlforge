# Semantic Internal Linking

Exact-phrase analysis (n-grams) finds pages that compete for the *same words*.
**Semantic analysis** goes further: it embeds every indexable page into a local
vector and measures *meaning* similarity, producing three passes:

- **Semantic cannibalization** — pages that compete for the same topic even
  when their wording differs.
- **Automated internal link opportunities** — semantically related pages that
  don't link to each other yet, with candidate anchor phrases drawn from the
  target's top n-grams.
- **Low-relevance links** — existing internal links whose source and target
  are semantically unrelated.

The pipeline is **optional** and **strictly local**. Until you configure a
provider and run an embedding job, no vectors are generated. If the provider
is unavailable, the semantic passes are marked **unavailable** with a local
diagnostic — audits never fail because of embeddings.

---

## Privacy guarantees

| Guarantee | How it is enforced |
|---|---|
| No remote embedding API, vector store, or hosted LLM | Only two provider classes exist: the bundled offline ONNX model and a user-configured **loopback** daemon |
| Loopback-only daemon endpoints | Configuration validation **rejects any non-loopback address** |
| No cloud fallback, no API keys to remote services | The daemon is your own local infrastructure |
| Air-gapped capability | The bundled ONNX model runs fully offline (CPU/GPU); no network of any kind |
| Zero telemetry | Progress and diagnostics stay on the loopback hub; nothing is transmitted off-machine |

> **Note:** A loopback daemon such as Ollama or LM Studio is *your* service,
> running on *your* machine. CrawlForge talks to it only at the loopback
> endpoint you configure (for example `http://127.0.0.1:11434`) — that traffic
> never leaves the machine.

---

## How the pipeline works

### Input and chunking

- Per **indexable** URL, the embedding input is the **Scoped Visible Text Tree
  + Title + H1**, assembled through a deterministic template: title, then H1,
  then scoped body. Configure a content scope first if your templates carry
  heavy boilerplate.
- Long documents are split into bounded chunks (maximum chunk length with
  optional overlap); chunk vectors are pooled by a deterministic rule —
  **mean pooling** is the default.

### Incremental, resumable, model-consistent

- On re-audit, **only URLs whose stored content hash changed** are
  re-embedded. The pass is idempotent and resumable.
- **Mixing model tags within one audit is prohibited.** Changing the model
  requires a full re-embed of that audit's vectors (use the force re-embed
  flag).
- The active provider and model tag are **snapshotted per audit**; the model
  tag (provider + model + dimension) is recorded with every stored embedding.

### Similarity

- **Cosine similarity**, executed through the database provider's
  vector-similarity capability on both the embedded and MySQL providers.
- Nearest-neighbour queries are indexed and bounded (top-K).
- Opportunity output is bounded: top-K per source URL; excess is discarded
  and counted.

---

## The three semantic passes

| Pass | Definition | Deliverable |
|---|---|---|
| Semantic cannibalization | Pairs/clusters of indexable URLs whose cosine similarity ≥ a configurable high threshold (default **0.88**) | Finding `content.semantic-cannibalization`, category *Content quality*, severity **Warning** (**Notice** when all members share one canonical target); evidence: similarity scores, cluster members, canonical hints |
| Automated internal link opportunities | For each indexable URL, top-K semantically related pages (default K = **10**) within a configurable similarity band (default **0.75–0.87**) that **do not currently share a link in either direction** | Persisted recommendations: source URL, target URL, similarity score, and candidate anchor phrases drawn from the target's top n-grams. Report dataset + export — **not findings by default** |
| Semantic relevance of existing links | Internal edges whose source/target cosine similarity falls below a configurable low-relevance floor (default **0.35**) | Finding `linking.low-relevance-link`, category *Linking*, severity **Notice**; evidence: similarity, anchor text, DOM zone |

Semantic cannibalization **complements** exact n-gram cannibalization: when
both passes fire for the same URL set, their evidence cross-references the
other pass.

> **Tip:** The band matters. Similarity ≥ 0.88 means "probably the same topic"
> (cannibalization territory); 0.75–0.87 means "related enough to link, distinct
> enough to keep both pages"; below 0.35 means "why are these pages linked?"

---

## Step-by-step: activate the bundled ONNX model

The bundled model is a packaged product asset — nothing to download, no
network required. This is the air-gapped path.

1. Open **Settings** and locate the **embedding provider** section.
2. Select the **bundled local ONNX** provider. No endpoint is needed — the
   model ships with the product and executes offline on CPU/GPU.
3. (Optional) Leave the model tag at the provider default; the model tag and
   vector dimension are recorded with every embedding automatically.
4. Run the **connection test**. The verdict reports reachability and model
   availability.
5. Open a completed audit and **run the embedding job** (see "Running and
   monitoring the job" below).
6. Watch the progress stream; when the job finalizes, the Semantic surface
   populates with clusters, opportunities, and low-relevance findings.

> **Note:** The bundled model's tag and dimension are snapshotted into the
> audit. Switching providers later requires a full re-embed of that audit.

---

## Step-by-step: configure a loopback daemon (Ollama / LM Studio)

Use this path when you prefer your own local embedding daemon. The daemon is
user-owned infrastructure: you install it, you load models into it, you keep
it running. CrawlForge only connects to it at a loopback address.

### Step 1 — Prepare your daemon

1. Install and start your local daemon (Ollama or an LM Studio-class server)
   using its own documentation.
2. Load an **embedding model** into the daemon with the daemon's own tools.
3. Note the daemon's loopback endpoint — for example
   `http://127.0.0.1:11434`, or any loopback port you configured.

### Step 2 — Point CrawlForge at it

1. Open **Settings → embedding provider** and select the **loopback daemon**
   provider.
2. Enter the **loopback endpoint**. Validation rejects any non-loopback
   address with an explicit error — a LAN or public address cannot be saved.
3. Enter the **model tag** identifying the embedding model you loaded.
4. Run the **connection test**: it probes reachability *and* model/dimension
   availability at your endpoint. Fix any reported issue before continuing.
5. Save. The provider configuration lives in application settings; the active
   provider and model tag are snapshotted per audit.

### Step 3 — Run the job

1. Open a completed audit and run the embedding job.
2. Verify progress and the final pass summaries (below).

> **Warning:** If the daemon stops or the model becomes unavailable, the
> semantic passes are marked **unavailable** with a local diagnostic. The
> audit itself never fails — restart your daemon and re-run the job.

> **Tip:** Keep the daemon on a fixed loopback port so saved configurations
> remain valid across sessions. There are no API keys to remote services and
> no cloud fallback — if the loopback endpoint is unreachable, nothing is
> sent anywhere else.

---

## Running and monitoring the job

- The job runs as a **background service** after crawl completion or during
  incremental idle, with bounded concurrency and provider request timeouts.
- Starting a job returns immediately; **progress streams over the hub**:
  - `embedding-progress` — at most once per second while running: embedded
    count, total eligible, provider, phase.
  - `embedding-completed` — once per job finalization: final counts and pass
    summaries (clusters, opportunities, low-relevance links).
- **Per-URL failures are isolated, counted, and surfaced as diagnostics —
  never fatal.**
- The job is **resumable** via content-hash comparison: unchanged pages reuse
  their stored vectors.
- A **force re-embed** flag recomputes every vector (required after a model
  change); an optional provider override can be supplied per run.

---

### Job phases

The progress stream reports a phase label as the job advances:

| Phase | What is happening |
|---|---|
| `embedding` | Eligible indexable URLs are embedded (changed content hashes only, on re-runs) |
| `similarity` | Cosine nearest-neighbour queries and the three passes execute |
| `persistence` | Opportunities, clusters, and findings are written in transactional batches |
| `completed` | Final counts and pass summaries are emitted (`embedding-completed`) |
| `unavailable` | The provider could not be reached; a local diagnostic explains why |

> **Note:** Reused vectors count toward the embedded total — a re-run over
> unchanged content finishes quickly because only changed pages are
> re-embedded.

---

## Worked example: reading the similarity bands

Three page pairs from one audit, with default thresholds:

| Pair | Cosine | Classification | What you do |
|---|---|---|---|
| "Internal linking guide" ↔ "Internal link audit checklist" | 0.91 | ≥ 0.88 → semantic cannibalization cluster | Decide the owning URL; consolidate or differentiate |
| "Internal linking guide" ↔ "Anchor text best practices" | 0.81 | Within 0.75–0.87, no existing link → link opportunity | Add a link with a candidate anchor from the target's top n-grams |
| "Internal linking guide" ↔ "Cookie policy" | 0.22 | < 0.35 with an existing link → low-relevance Notice | Review whether the link serves users; remove or recontextualize |

The same pair can move between categories as content changes: re-embedding is
triggered by content-hash changes, so editing a page updates its vectors and
its relationships on the next job run.

> **Note:** Clusters group indexable URLs only; non-indexable pages (blocked,
> noindex, error states) never participate in cannibalization or opportunity
> passes.

---

## Reading the Semantic surface

The **Semantic** report surface provides:

- **Link-opportunities grid:** source, target, similarity, and anchor
  candidates — virtualized at scale, with one-click navigation to the
  source/target URL detail.
- **Cluster view:** semantic cannibalization clusters with a similarity-edge
  graph overlay, under the same level-of-detail budgets as other graph
  surfaces.
- **Per-URL "semantically related" panel** in URL detail.
- **Cross-links:** semantic cannibalization findings link to the n-gram
  cannibalization view, so exact-phrase and semantic evidence sit side by
  side.
- **Provider settings** with connection-test feedback and explicit
  loopback-only validation errors.

---

## Storage

| Object | Role |
|---|---|
| `url_embeddings` | The activated vector table: audit, URL, embedding payload, model tag. Populated in transactional batches; no schema change |
| `semantic_link_opportunities` | Persisted recommendations: source, target, similarity, anchor candidates, computed-at. Unique per (audit, source, target); pruned with audit retention |
| `semantic_clusters` | Cluster membership: cluster reference, URL, similarity to cluster centroid, primary-member flag; pruned with audit retention |
| Audit snapshot fields | Embedding provider, model tag, and the three thresholds — later settings edits never mutate history |

---

## Troubleshooting

| Symptom | Cause | Resolution |
|---|---|---|
| Semantic passes marked **unavailable** | Provider unreachable or model missing | Run the connection test; restart your loopback daemon or switch to the bundled ONNX model; re-run the job. The audit itself is unaffected |
| Endpoint rejected at save | Non-loopback address | Use a loopback address such as `http://127.0.0.1:11434`; LAN/public addresses are refused by validation |
| Connection test reports model unavailable | Model tag does not match a loaded model | Load the embedding model in your daemon and correct the model tag |
| Results look stale after editing pages | Vectors reused by content hash | Re-run the job; changed content hashes re-embed automatically |
| Results inconsistent after a model switch | Model tags must not mix within an audit | Run with the **force re-embed** flag to recompute every vector under the new model |
| Some URLs missing from results | Per-URL failures are isolated and counted | Check the job diagnostics; failures never abort the job — re-run to retry |

---

## Configuration reference

| Field | Default | Notes |
|---|---|---|
| Provider | Bundled ONNX available; selection in settings | `onnx` (bundled) or `loopback-daemon`; snapshotted per audit |
| Loopback endpoint | — (daemon only) | Any loopback address/port; non-loopback rejected at validation |
| Model tag | Provider default | Identifies provider + model + dimension; mixing tags in one audit prohibited |
| Cannibalization threshold | **0.88** | Cosine similarity at/above which indexable URLs cluster |
| Opportunity similarity band | **0.75–0.87** | Related-but-distinct range for link recommendations |
| Opportunity top-K | **10** | Maximum recommendations per source URL; excess discarded and counted |
| Low-relevance floor | **0.35** | Existing internal links below this similarity emit a Notice |

Thresholds are snapshotted into the audit alongside the provider and model
tag, so later settings edits never mutate history.

---

## Workflow: act on link opportunities

1. Run the embedding job on a completed audit.
2. Open the **link-opportunities grid**; sort by similarity within the band.
3. For a source page, review the recommended targets and the **candidate
   anchor phrases** (drawn from each target's top n-grams).
4. Add the internal link on the source page using a natural anchor from the
   candidates — recommendations are a report dataset, not findings, so there
   is no severity pressure; prioritize by traffic and topical strategy.
5. Re-crawl or re-audit the affected pages; the opportunity disappears once
   the link exists (the pass excludes pairs that already share a link in
   either direction).
6. Export the `semantic-opportunity` dataset for editorial handoff.

## Workflow: resolve semantic cannibalization

1. Open the **cluster view**; inspect members and centroid similarities.
2. Cross-check the n-gram cannibalization view (evidence cross-references
   both passes when they agree).
3. Decide the owning URL; consolidate via canonical/redirect or differentiate
   the competing pages' topics.
4. Re-audit and re-run embeddings — changed content hashes trigger
   re-embedding of exactly the edited pages; resolved clusters drop out.

---

## Exports, metrics, and API

- **Exports:** the `semantic-opportunity` dataset selection.
- **Metric keys:** `semantic-clusters`, `semantic-opportunities` — dashboard
  cards.

| Operation | Method & path |
|---|---|
| Run / re-run embeddings (optional provider override; force re-embed flag) | POST `/api/audits/{id}/semantics/run` |
| Similar pages (URL reference, top-K, minimum similarity) | GET `/api/audits/{id}/semantics/similar` |
| Link opportunities (source filter, similarity band, pagination) | GET `/api/audits/{id}/semantics/link-opportunities` |
| Semantic clusters (minimum cluster size, pagination) | GET `/api/audits/{id}/semantics/clusters` |
| Provider test (reachability + model/dimension probe; loopback validation enforced) | POST `/api/settings/embedding/test` |

**MCP:** the read-only `semantic-link-opportunities` resource projects the
opportunities endpoint (audit reference, source filter, band, pagination); the
optional `semantic-topical-briefing` prompt composes it with `audit-summary`,
with no side effects. Provider configuration is intentionally **not** exposed
via MCP.

---

## Related guides

- [N-grams and topical density](n-grams-and-topical-density.md)
- [Content scoping and boilerplate stripping](content-scoping-and-boilerplate-stripping.md)
- [WAF and soft-block detection](waf-and-soft-block-detection.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)

[← Documentation portal](../README.md)
