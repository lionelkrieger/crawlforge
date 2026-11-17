# GEO area — handoff notes (WP-04-03)

Owner: cf-wp-01-03. Implements the locked `GeoContracts.cs` surface:
`IGeoReadinessAnalyzer` (+ `ValidateLlmsTxt` pure function),
`IAiBotGovernanceMatrix`, `ILlmsTxtGenerator` (FS-CRAWL-002 §13, criterion #46).

## ADVISORY-ONLY INVARIANT (criterion #46)

The governance matrix is a **report, never a gate**. Nothing in this area
writes `robots.txt`, alters crawl behavior, or contacts any vendor. All inputs
are stored artifacts (url_records, url_elements inventory, content-store
bodies); all outputs are findings, `governance_matrix` rows, and metric rows.
The bundled agent-profile asset is loaded with `File.ReadAllText` only —
**zero runtime fetch, zero remote refresh** (exclusions E2/E9). Generator
output is local files only; nothing is auto-published.

## Locked surfaces implemented

| Contract | Implementation | Notes |
|---|---|---|
| `IGeoReadinessAnalyzer.AnalyzeAsync` | `GeoReadinessAnalyzer` | `GeoReadinessResult(score, hostResults, findings)` |
| `IGeoReadinessAnalyzer.ValidateLlmsTxt` | same | pure structural validation |
| `IAiBotGovernanceMatrix.EvaluateAsync` | `AiBotGovernanceMatrix` | rows ordered agent/scope/purpose |
| `ILlmsTxtGenerator.GenerateAsync` | `LlmsTxtGenerator` | local files only |

Extra public helpers (composition-time): `AnalyzeWithChecksAsync` (per-check
scores), `EvaluateWithFindingsAsync` (conflict findings),
`GovernanceMatrixRecorder` (replace-semantics persistence),
`GeoAggregateRecorder` (metric writes), `AiAgentProfileCatalogue.Load /
ResolveDefaultAssetPath`, `LlmsTxtParser`.

## Findings (category AI Search (GEO), suppression per §7.5)

| Rule key | Severity | Trigger |
|---|---|---|
| `geo.llms-txt-missing` | Notice | host lacks llms.txt (or non-200); also the llms-full.txt-absent annotation (`annotation: true` in evidence) |
| `geo.llms-txt-malformed` | Warning | missing H1 / blockquote / H2 link sections (per-element issues in evidence) |
| `geo.llms-txt-broken-link` | Warning | per target: structurally unresolvable, non-success crawled status, or not in the crawled set |
| `geo.governance-conflict` | Notice | robots vs X-Robots-Tag/meta contradiction; evidence carries BOTH values |

## Readiness scoring (stable formula)

Five checks, each 0–1; `ReadinessScore = 100 × mean(checks)` (2 dp). Empty
audit (no URLs) scores 0.

- `llms-txt`: hosts with present + well-formed llms.txt / hosts.
- `llms-links`: 1 − broken/total links across llms files (1 when no links).
- `answer-first`: sampled indexable pages with a ≥40-char paragraph among the
  first 8 body elements.
- `passage-citability`: sampled pages with a 30–120-word paragraph.
- `entity-clarity`: sampled pages with JSON-LD `@type` or `og:site_name`.

Sample bound: 50 pages; body reads bounded at 2 MiB. Metric `geo-readiness`
rows carry dimension `{"check":"<name>"}` plus `{"check":"overall"}` (0–100).

## Governance evaluation model

- Agents: bundled catalogue (`assets\ai-agent-profiles`, 14 profiles incl. the
  nine §13.2-mandated agents). Purpose classes map to `AiAgentPurpose`.
- Scopes: path prefixes (URL-tree directories + robots rule prefixes, ≤20,
  always `/`) and sampled URLs (≤50, indexable HTML). Scope KIND rides in
  evidence JSON (`scopeKind: "prefix" | "url"`) — the locked schema has no
  scope_kind column (orchestrator-approved convention).
- Verdicts: robots base via the locked `IRobotsTxtParser` (accepted Crawler
  implementation; injected, never duplicated). Overlays: `noindex`/`none` →
  Search Deny; `noai`/`none` → Rag + Training Deny; `noimageai` → Training
  Deny; `index`/`all` → Search Allow. Agent-qualified directives
  (`GPTBot: noai`) apply only to that agent. Deny overlays win; conflicts
  with the robots base emit `geo.governance-conflict`.
- `GovernanceVerdict.Unspecified` = no explicit directive observed.
- Metric `governance-coverage`: share of cells with explicit verdicts,
  dimension `{"purpose":"Search|Rag|Training|overall"}`.

## llms.txt generation ranking (§13.3)

Indexable + success + HTML, excluding pages with Error findings; composite =
0.6 × min(words, 2000)/2000 + 0.2 × title + 0.2 × description; top 100 of 200
candidates; grouped by first path segment; H1 host / blockquote summary
(most frequent meta description or local fallback) / H2 groups with markdown
links. `llms-full.txt` adds 300-char excerpts. LOCAL files only.

## READ CONTRACTS FOR PHASE-05 (API) — documentation obligation

The following surfaces are served FROM this area's stored outputs; phase-05
implements the endpoints, this area owns the data shape:

- `GET /api/audits/{id}/geo` — readiness projection: `GeoReadinessResult`
  (score, per-host `LlmsTxtValidation`, findings) + `geo-readiness` metric
  rows. Sub-routes per §13.5: `/geo/llms-txt` (host filter → per-host file
  status, structural verdict, linked/broken counts) and `/geo/governance`
  (agent/purpose/scope filters + pagination → `governance_matrix` rows).
- `GET /api/projects/{id}/bot-governance` — project-level advisory matrix
  aggregation across the project's audits (latest audit per host by default):
  `governance_matrix` rows + `governance-coverage` metrics + agent profile
  policy notes from the bundled catalogue. Advisory only — no mutation route.
- MCP resource `geo-readiness` (audit reference; agent filter) — read-only
  llms.txt validation results plus governance summary (§13.5). Tool
  invocation/configuration NOT exposed via MCP (§11.1 boundary).
- `POST /api/tools/llms-generate` / `POST /api/tools/llms-validate` (§13.5)
  map to `ILlmsTxtGenerator.GenerateAsync` / `ValidateLlmsTxt`.
