# Semantic Links — Read Contract for Phase-05 (REST + MCP)

| Document control | Value |
|---|---|
| Workpiece | WP-04-02 (phase-04 wave-4A) |
| Authority | FS-CRAWL-002 §12.8 (REST), §12.8 (MCP resource), §11.1 (MCP boundary) |
| Status | Documentation only — phase-05 implements the routes against this contract |
| Data source | `semantic_link_opportunities`, `semantic_clusters`, `url_embeddings` (locked entities) |

## 1. REST surface

The dispatch route `/api/audits/{id}/semantic-links` is the read projection of
the spec §12.8 opportunities endpoint
(`/api/audits/{id}/semantics/link-opportunities`). Phase-05 may expose either
path; both serve the identical read model below. All routes are loopback-only,
read-only, and wrapped in the uniform `ApiEnvelope<T>` + `PaginationMetadata`.

### GET `/api/audits/{id}/semantic-links`

| Query parameter | Type | Default | Notes |
|---|---|---|---|
| `sourceUrlId` | long? | null | Filter to one source URL |
| `minSimilarity` | double? | 0.75 | Band lower bound (inclusive) |
| `maxSimilarity` | double? | 0.87 | Band upper bound (inclusive); cannibalization pairs live above it |
| `offset` | int | 0 | Pagination |
| `limit` | int | 50 | Pagination (bounded) |
| `sortBy` | string | `similarity` | `similarity` \| `sourceUrlId` \| `targetUrlId` |
| `sortDescending` | bool | true | Similarity descending by default |

Response items map 1:1 to `SemanticLinkOpportunityDto`:

```json
{
  "id": 123,
  "auditId": 7,
  "sourceUrlId": 41,
  "targetUrlId": 88,
  "similarity": 0.8123,
  "anchorCandidates": ["semantic linking", "internal link candidates"]
}
```

`anchorCandidatesJson` is a JSON string array of candidate anchor phrases drawn
from the target's top n-grams (empty array when the n-gram pass has not run).

**Suggestions only:** these rows are recommendations. No route in this contract
applies a suggestion; applying links is a user action outside the semantic
module (the pipeline never mutates `link_edges`, `url_records`, or content).

### Companion reads (spec §12.8, same data layer)

- `GET /api/audits/{id}/semantics/similar` — `urlId`, `topK` (default 10),
  `minSimilarity`: ranked similar URLs with cosine scores, computed from
  `url_embeddings` payloads (provider vector-similarity capability, §2.4).
- `GET /api/audits/{id}/semantics/clusters` — `minClusterSize` (default 2),
  pagination: `SemanticClusterDto` rows grouped by `clusterId` with centroid
  similarities.
- `POST /api/audits/{id}/semantics/run` — accepts provider override + force
  re-embed flag; returns immediately (job accepted); progress over the hub
  (`embedding-progress` ≤ 1/s, `embedding-completed` once). Implementation:
  `ISemanticLinkAnalyzer.RunAsync` on a background service (§2.7 pattern).
- `POST /api/settings/embedding/test` — provider configuration in,
  `EmbeddingProviderTestResult` out. **Loopback validation is enforced before
  any connection attempt** (`EmbeddingProviderFactory.TryValidateSettings` /
  `LoopbackEndpointValidator`); non-loopback endpoints are rejected with a
  configuration error, never contacted.

## 2. MCP resource

**Resource `semantic-links`** (spec name `semantic-link-opportunities`; address
pattern: resource kind + audit reference + optional filters):

| Filter | Maps to |
|---|---|
| `auditId` (required) | audit reference |
| `sourceUrlId` | source filter |
| `minSimilarity` / `maxSimilarity` | similarity band |
| `offset` / `limit` | pagination |

Content: a bounded, read-only projection of the REST rows above (same DTO
shape). Optional prompt `semantic-topical-briefing` (parameter: audit
reference) composes this resource with `audit-summary`; no side effects.

**MCP boundary (§11.1):** provider configuration is NOT exposed via MCP; no
run/apply mutations; read-only data only.

## 3. Provider switching (no data migration)

`application_settings` carries `EmbeddingProviderSettings`
(`Kind` = `BundledOnnx` | `LoopbackDaemon`, `LoopbackEndpoint`, `ModelTag`).
Switching providers changes the model tag; the pipeline detects foreign tags
in `url_embeddings` and fully re-embeds the audit (§12.3 prohibits mixing
tags). The table schema never changes — SQLite and MySQL parity is preserved
(precendence rule 6). The active provider + model tag are snapshotted on
`audits` (`EmbeddingProvider`, `EmbeddingModelTag`,
`EmbeddingSimilarityThreshold` additive fields).

## 4. Bounds and failure posture

- Opportunity output: top-K per source (default 10); excess discarded and
  counted (§12.6).
- Analyzed set bounded (`SemanticPipelineOptions.MaxAnalyzedUrls`, default
  5000); per-URL failures isolated and surfaced as a Notice diagnostic.
- Provider unavailable → `intelligence.embeddings-unavailable` Notice; the
  audit never fails because of embeddings (§12.2).
- Metric keys: `semantic-opportunities`, `semantic-clusters` (locked
  `MetricKeys`; §12.7).
- Findings: `content.semantic-cannibalization` (Warning; Notice when all
  members share one declared canonical target), `linking.low-relevance-link`
  (Notice). Suppression semantics match the keystone `FindingEmitter`.
