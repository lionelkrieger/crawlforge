# List Mode and the Migration Redirect Matrix

List Mode crawls a URL list you supply instead of discovering links from seeds. The Redirect Migration Matrix Validator verifies old→new URL mappings hop-by-hop — the tool you reach for during a site migration, a domain change, or a URL-structure overhaul. Together with on-the-fly URL rewriting and staging↔production host mapping, they form CrawlForge's migration toolkit.

This guide covers each capability and then walks through a complete migration setup, step by step. Everything runs locally: the only outbound traffic is the crawl of the URLs you supplied (optionally through a proxy you configured).

## List Mode

### Supplying a URL list

The URL list seed accepts three input forms:

| Form | Format details |
|---|---|
| `.txt` file | One URL per line |
| `.csv` file | Configurable URL column, with automatic header detection |
| Clipboard paste | Paste rows directly into the ingestion panel |

Files are selected via the native file dialog. The ingestion panel shows a preview table with per-line validation diagnostics before anything is crawled.

### The ingestion pipeline

Every entry passes through the same deterministic pipeline:

1. **Parse** the file or paste into entries.
2. **Validate per entry**: entries must be absolute `http(s)` URLs. Malformed entries are rejected into a bounded, per-line diagnostic list — they never fail the audit.
3. **Canonicalize** each entry (lowercased scheme/host, default ports removed, fragments discarded, trailing-slash policy, normalized percent-encoding, query policy applied). Any configured rewrite rules apply here too.
4. **Deduplicate** against the seen-set keyed by canonical URL.
5. **Enqueue as seeds** with discovery source `list`.

> **Note:** Because rewrite rules run during ingestion canonicalization, the list you upload is not necessarily the list that gets crawled — preview rewrites first (see [URL rewriting](#on-the-fly-url-rewriting)).

### Discovery modes

| Mode | Behavior |
|---|---|
| `list-only` | Depth ceiling forced to **0**; link discovery and sitemap expansion disabled; **only uploaded URLs are crawled** |
| `list-bounded` | Discovery runs, but only URLs present in the uploaded list (after canonicalization) are enqueued; off-list discoveries are recorded as `skipped` with reason `out-of-list` |

Choose `list-only` for targeted audits and migration validation (you want exactly your rows, nothing else). Choose `list-bounded` when you want the link graph context around your list without crawling beyond it.

Limits resolution is unchanged in List Mode: total/per-domain ceilings, timeouts, and retry budgets still apply. Pre-crawl validation probes a **bounded sample** of list entries, and WAF challenge verdicts apply as usual.

### Starting a list-mode audit

On the REST surface, `POST /api/audits` accepts the additive `urlList` and `listMode` fields alongside the configuration reference. In the UI, the audit configuration's list-ingestion panel handles file picker / clipboard paste, the preview table, and the discovery-mode selector.

> **Tip:** A high WAF block rate in the pre-crawl sample warns you before a full run. If many list entries sit behind a challenge, resolve access with the site owner first — see [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md).

### Reading the ingestion diagnostics

The preview table's per-line diagnostics tell you exactly what happened to each uploaded row:

| Outcome | Meaning |
|---|---|
| Accepted | Absolute `http(s)` URL; canonicalized and enqueued as a `list` seed |
| Rejected (malformed) | Not an absolute `http(s)` URL; listed in the bounded per-line diagnostic list |
| Deduplicated | Canonical form already present in the seen-set; not enqueued twice |
| Rewritten | A rewrite rule changed the canonical form before keying — the preview shows the effective URL |

Malformed entries never fail the audit: the valid rows crawl, and the diagnostics list the rest. Because deduplication is keyed by canonical URL, two rows that differ only by fragment, default port, casing, or stripped query parameters collapse into one fetch.

> **Note:** In `list-bounded` mode, discovered URLs that are not in your list (after canonicalization) are recorded as `skipped` with reason `out-of-list`. That record is your evidence that discovery ran and what it found beyond the list — useful for spotting pages the migration plan missed.

## The Redirect Migration Matrix

The matrix validator answers one question per row: *does this old URL actually land where the migration plan says it should?*

### Mapping upload

A mapping is two columns:

| Column | Meaning |
|---|---|
| `Source_URL` | The old URL to fetch and follow |
| `Expected_Target_URL` | Where the migration plan says it should end up |

Upload from `.csv`, `.txt`, or clipboard. Rows persist in `redirect_mappings` attached to the audit (unique per audit + source URL, with an ordinal and upload-batch reference). The upload **auto-configures a `list-only` audit seeded from the Source column** — or attaches to an existing list-mode audit if you prefer.

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Upload migration matrix | `POST /api/audits/{id}/redirect-matrix` | Mapping rows (`Source_URL`, `Expected_Target_URL`); strictness; seed-mode flag | Created mapping count; audit seeding result |
| Matrix results | `GET /api/audits/{id}/redirect-matrix/results` | Verdict filter; URL search; sorting; pagination | Per-row verdict, hop chain, final status, canonical evidence |

### How a row is evaluated

1. Every `Source_URL` is fetched.
2. The **full redirect hop chain** is recorded — every hop, so chains and loops can be reconstructed.
3. The **final destination** and the final page's **canonical target** are compared against `Expected_Target_URL`.
4. Comparison is canonicalized (the standard canonicalization plus the audit's query policy). Strictness is configurable:

| Strictness | Comparison |
|---|---|
| `exact` | Exact match after canonicalization |
| `normalized` | Normalized match after canonicalization |

### The five-tier verdict system

Each row receives **one primary verdict**. Precedence is fixed — **first failure wins** — and all evidence is retained regardless of which tier fired:

| Precedence | Rule key | Condition | Severity |
|---|---|---|---|
| 1 | `redirect-matrix.chain-exceeded` | Hop count exceeds the redirect-hop ceiling, or a loop is detected | Error |
| 2 | `redirect-matrix.final-non-200` | Final status outside 2xx | Error |
| 3 | `redirect-matrix.target-mismatch` | Final destination ≠ expected target | Error |
| 4 | `redirect-matrix.canonical-mismatch` | Final page declares a canonical that differs from the expected target | Warning |
| 5 | `redirect-matrix.pass` | All checks satisfied | Pass |

All five rule keys live in the *Indexability & status* category. Evidence carries the full hop chain, the final status, and the observed canonical — so a failing row tells you exactly where the chain went wrong, not just that it failed.

> **Note:** A row can fail at only one tier. A chain that both exceeds the hop ceiling *and* lands on the wrong target reports `chain-exceeded` (the first failure); the mismatch evidence is still retained for inspection.

> **Warning:** Sources blocked as `waf-challenge` are marked **unverifiable** (blocked annotation) and excluded from pass/fail rates. A matrix run against a challenge-protected site measures nothing until access is resolved — the verdict mix would be misleading otherwise.

### A worked verdict example

Consider a mapping row `Source_URL = https://old.example.com/pricing`, `Expected_Target_URL = https://www.example.com/plans` (illustrative):

| Observed chain | Verdict | Why |
|---|---|---|
| `/pricing` → 301 `/plans` → 200, page canonical `https://www.example.com/plans` | `pass` | Destination and canonical both match the expected target |
| `/pricing` → 301 `/plans` → 200, page canonical `https://www.example.com/pricing-v2` | `canonical-mismatch` (Warning) | The redirect lands correctly, but the declared canonical differs |
| `/pricing` → 301 `/old-pricing` → 200 | `target-mismatch` (Error) | Final destination is not the expected target |
| `/pricing` → 301 `/plans` → 404 | `final-non-200` (Error) | The chain ends outside 2xx; precedence 2 beats the mismatch |
| `/pricing` → 301 `/a` → 301 `/b` → 301 `/a` (loop) | `chain-exceeded` (Error) | Loop detected; precedence 1 wins over everything downstream |

In every failing case the full hop chain, final status, and observed canonical remain in the evidence — the verdict tells you the first failure, the drill-down tells you the whole story.

### Matrix runs and WAF-protected sites

The WAF & Challenge Fingerprinting Pass runs on every fetched response, including matrix sources. When a source is challenge-blocked:

- The row is annotated blocked (`waf-challenge`) and marked **unverifiable**.
- It is excluded from pass/fail rate aggregates — it counts as neither.
- The block is surfaced as a diagnostic; the remediation is to verify crawler access with the site owner, or adjust scope or supplied credentials.

If the pre-crawl sample shows a high block rate, resolve access before the full matrix run. Signed-request access and site credentials are covered in [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md).

### Reviewing results

The Migration Matrix surface provides verdict columns, hop-chain drill-down, verdict filters, and spreadsheet/CSV export. Aggregates are materialized under the `redirect-matrix` metric key, dimensioned by verdict. While a matrix is attached, crawl progress carries matrix counters (validated/total), and the hub emits `matrix-row-validated` events in throttled batches (at most once per second).

The local automation interface exposes a read-only `redirect-matrix-results` resource (audit reference, verdict filter, pagination). Matrix *management* (uploading mappings, configuring strictness) is not exposed via the automation interface — it stays in the shell and REST surface.

## On-the-fly URL rewriting

Rewrite rules are **ordered regex find/replace rules** applied during canonicalization, before seen-set keying. They let you normalize URL families in flight — for example, stripping legacy session paths or collapsing old parameter forms.

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Pattern | — | Valid regex | Invalid patterns are rejected at configuration time |
| Replacement | — | Replacement text | A rewrite may never produce a non-`http(s)` scheme |
| Scope | — | `seeds-only` \| `all-discovered` | Which URLs the rule applies to |
| Enabled flag | — | On/off per rule | Disabled rules are skipped |

Guarantees:

- **Deterministic evaluation order** — rules run in their configured order.
- **Time-bounded pattern evaluation** — a pathological pattern cannot stall the crawl.
- **Per-rule rewrite counters** are recorded as diagnostics, so you can see which rules actually fired.
- The rule set is **snapshotted per audit**: later edits never mutate history.

Preview before committing: `POST /api/tools/rewrite-preview` takes sample URLs and rewrite rules and returns before/after canonicalization per rule. The rewrite-rule editor includes a live preview.

> **Tip:** Keep rewrite rules few and surgical. Because they run before deduplication, an aggressive rule can collapse distinct pages into one canonical URL — the preview tool shows exactly that before you crawl.

### A worked rewriting example

Suppose legacy URLs carry a redundant print prefix (illustrative):

1. Rule: pattern `^https://example\.com/print/`, replacement `https://example.com/`, scope `all-discovered`, enabled.
2. `POST /api/tools/rewrite-preview` with sample URLs shows `https://example.com/print/about` → `https://example.com/about` per rule.
3. During the crawl, both forms canonicalize to the same key — the seen-set fetches the page once, and the per-rule rewrite counter records how many URLs the rule transformed.

Order matters when rules interact: an earlier rule's output is the next rule's input. The preview evaluates the same ordered pipeline, so what you see is what the crawl does.

## Staging-vs-production host mapping

Host-mapping rules map a **source host → target host**, with an optional path-prefix transform (for example, `staging.example.com` → `www.example.com`).

Where mapping applies:

- **Audit comparison and content comparison** align URLs by **path** (plus query per policy) after host mapping. `GET /api/audits/compare` and `GET /api/audits/compare/content` accept an additive `hostMapping` parameter. Surfaces label the mapped hosts; without a mapping, comparison behavior is exactly the default.
- **The matrix validator** may use the same mapping when expected targets live on the production host while the crawl runs against staging.

This is what makes a staging crawl comparable to a production baseline: the staging audit's URLs are aligned to their production counterparts by path, and the matrix can expect production targets while fetching staging sources.

### A worked host-mapping example

Illustrative setup: staging lives at `staging.example.com`, production at `www.example.com`.

1. Define the mapping: source host `staging.example.com` → target host `www.example.com` (add a path-prefix transform only if the staging tree lives under a prefix).
2. Crawl staging with your normal scope; the audit records staging URLs.
3. Compare against the production baseline audit with `hostMapping` supplied: `/about` on staging aligns with `/about` on production (query alignment follows the query policy). The comparison surfaces label the mapped hosts so the alignment is explicit.
4. For matrix validation, expected targets may point at `www.example.com` while the sources are fetched on staging — the validator applies the same mapping when comparing.

Without a mapping, comparison behavior is exactly the default: URLs align only when their hosts already match.

## Step-by-step: validating a site migration

A complete migration validation, chunked into phases. The scenario: `old.example.com` is migrating to `www.example.com/new-site/`, and you have a mapping spreadsheet from the migration plan.

### Phase 1 — Prepare the mapping

1. Export the migration plan as two columns: `Source_URL` (old URLs) and `Expected_Target_URL` (planned new URLs). `.csv`, `.txt`, or clipboard all work.
2. Make sure every row is an absolute `http(s)` URL on both sides. Malformed rows will be rejected into per-line diagnostics at upload — clean them now to keep the matrix complete.
3. Decide the comparison **strictness**: `exact` for a lift-and-shift migration, `normalized` when query parameters or casing may legitimately differ.

### Phase 2 — Set up the project and access

4. Create a project for the migration (see [Creating projects and scopes](creating-projects-and-scopes.md)).
5. If the old site requires authentication or signed access, configure the custom headers/cookies or signature material first — [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md) covers the credential classes and the in-scope-only routing rules.
6. If the crawl must egress through a corporate proxy, configure the upstream proxy (and its bypass list) before running.

### Phase 3 — Upload the matrix

7. Open the matrix upload wizard and supply the mapping rows.
8. Choose the seeding behavior: let the upload **auto-configure a `list-only` audit seeded from the Source column**, or attach the matrix to an existing list-mode audit.
9. Set the strictness (`exact` or `normalized`).
10. Review the upload result: the created mapping count and the audit seeding result. Fix any rejected rows and re-upload them.

### Phase 4 — Align hosts and normalize (staging scenarios)

11. If you are crawling **staging** while expected targets live on **production**, add a host-mapping rule (staging host → production host, with any path-prefix transform). The matrix uses the mapping when comparing.
12. If legacy URL forms need normalization before comparison, add rewrite rules and run `POST /api/tools/rewrite-preview` on a handful of representative URLs. Verify the before/after output matches your intent.

### Phase 5 — Run and monitor

13. Start the audit. Pre-crawl validation probes a bounded sample of the list; resolve any protocol, redirect, TLS, login-gate, or WAF blocker it surfaces before the full run.
14. Watch the matrix counters (validated/total) in crawl progress. Rows are validated as their sources are fetched; `matrix-row-validated` events stream to the surface in throttled batches.
15. If the WAF-blocked share climbs, stop and resolve access — blocked sources are *unverifiable*, not failures, and a challenge-heavy run produces no usable pass/fail signal.

### Phase 6 — Review verdicts and iterate

16. Open the Migration Matrix surface and filter by verdict. Work the Errors first, in precedence order:
    - `chain-exceeded` → inspect the hop-chain drill-down for loops or over-long chains; fix the redirect configuration on the server.
    - `final-non-200` → the chain lands on an error status; check the final hop's target exists.
    - `target-mismatch` → the chain lands somewhere other than planned; compare the recorded final destination with `Expected_Target_URL`.
17. Review `canonical-mismatch` Warnings separately: the redirect lands correctly, but the final page declares a different canonical. Decide whether the canonical or the mapping row is wrong.
18. Export the matrix (spreadsheet/CSV) for the migration report; the `redirect-matrix` metric aggregates give you verdict counts at a glance.
19. After server-side fixes, run a fresh matrix audit. Compare the two audits (`GET /api/audits/compare`, with `hostMapping` when hosts differ) to see resolved versus new findings.

> **Note:** Each audit snapshots its rewrite rules, host mapping, list mode, and matrix attachment. Re-running with edited settings creates a new, independently reproducible audit — history is never mutated.

## Troubleshooting migration runs

| Symptom | What it means | What to do |
|---|---|---|
| Many `chain-exceeded` verdicts | Redirect chains are longer than the redirect-hop ceiling, or loop | Inspect hop chains; fix server-side redirect loops; the ceiling itself is a limit override if chains are legitimately long |
| Many `final-non-200` verdicts | Chains land on error statuses | Check the final hop's target exists on the new site |
| `target-mismatch` with correct-looking chains | Final destination differs from the plan after canonicalization | Check query policy and strictness; a stripped/kept parameter can change the comparison |
| `canonical-mismatch` Warnings only | Redirects land correctly but pages declare other canonicals | Decide whether the page canonical or the mapping row is wrong; this tier is a Warning, not an Error |
| Rows marked unverifiable | Sources were WAF-challenge blocked | Resolve access with the site owner; blocked rows are excluded from pass/fail rates |
| Fewer fetched URLs than uploaded rows | Malformed rows rejected, or canonical duplicates collapsed | Read the per-line ingestion diagnostics; dedup is by canonical URL |
| `out-of-list` skips in `list-bounded` | Discovery found URLs beyond your list | Expected behavior; review the skipped record for pages the plan may have missed |
| Rewrite produced unexpected URLs | Rule order or pattern breadth | Run `POST /api/tools/rewrite-preview`; remember rules evaluate in order, before dedup |

## Related guides

- [Creating projects and scopes](creating-projects-and-scopes.md)
- [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md)
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md)
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md)
- [WAF and soft-block detection](../05-advanced-seo-and-intelligence-modules/waf-and-soft-block-detection.md)
- [Exports and secure destinations](../04-exploring-reports-and-visualizations/exports-and-secure-destinations.md)

[← Documentation portal](../README.md)
