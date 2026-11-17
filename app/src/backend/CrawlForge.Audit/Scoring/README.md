# Scoring area — handoff notes (WP-03-08)

Owner: cf-wp-01-03. Implements `IAuditScoringService` (§8.1, criterion #32) and
`IFindingSuppressionService` (§7.5), suppression-aware aggregation, the offline
cross-audit comparison read model (§8.2), and zero-fetch recomputation on
re-audit and suppression change.

## ENABLED-CATEGORIES CONVENTION — REQUIRED READING for phase-05 (API) and phase-06 (UI)

A **disabled category contributes no score row** and is excluded from the
overall mean (§8.1). Category enablement is read from the audit's **frozen
configuration snapshot** (`audits.configuration_snapshot_json`) via the
optional JSON property:

```json
{ "enabledCategories": ["Indexability", "Content", "StructuredData"] }
```

- Accepted spellings: `enabledCategories` or `EnabledCategories`
  (case-insensitive property match).
- Entries: `AuditCategory` **names** (case-insensitive) or **numeric** values.
  Unknown entries are ignored (forward compatibility).
- **ABSENT property = ALL categories enabled** (backward-compatible default).
- Malformed snapshot JSON also falls back to all-enabled.
- **Phase-05's API and phase-06's UI MUST write this key into the configuration
  snapshot when category toggles are configured.** No locked DTO or schema
  object carries category enablement; this additive JSON convention is the
  single source of truth. Orchestrator-approved (WP-03-08 dispatch); recorded
  in the operating manual for later phases.

Implementation: `AuditScoringService.ParseEnabledCategories` (public, tested).

## Scoring formula (stable — do not change without a comparability migration)

- Severity deduction weights: **Error = 10, Warning = 3, Notice = 1, Pass = 0**.
- Per-category penalty = Σ(weight × non-suppressed finding count).
- Coverage denominator = `max(1, indexable URL count) × 10`
  (`url_records.is_indexable = true`). One error per indexable URL exhausts
  the category budget.
- Category score = `100 × (1 − min(1, penalty / denominator))`, rounded to 2
  decimals. No findings → 100.
- Overall = arithmetic mean of enabled category scores (2 decimals); overall
  counts are the category sums. Overall row carries `category = NULL`.
- Scores persist per audit in `audit_scores` (transactional DELETE+INSERT
  replace) → comparable across audits, drives the score-trend surface.

## Suppression semantics — keystone parity (mandatory)

`SuppressionMatcher` mirrors the accepted pipeline keystone
(`Pipeline\FindingEmitter.IsSuppressed`, READ-ONLY reference) **exactly**:

1. Inactive rows (`IsSuppressed = false`) cover nothing.
2. Rows with **no declared dimension** cover nothing (blanket-suppression guard).
3. Rule dimension: null = any rule, else **ordinal** equality.
4. URL dimension: null = any URL, else equality.
5. Free-form `FindingScope` dimension: null = any, else **ordinal equality
   against the RULE KEY** (keystone subtlety).
6. All declared dimensions must match.

A parity test matrix asserts `SuppressionMatcher.Covers ==
FindingEmitter.IsSuppressed` across suppression shapes.

- Suppression keys are deterministic per dimension set
  (`rule=…;url=…;scope=…`, `*` for null; SHA-256 fallback beyond 191 chars).
- Re-suppressing the same target **reactivates** the existing row (idempotent).
- Unsuppress = reversible **flag flip**; the row persists for history.
- Suppressed findings stay persisted and inspectable (`findings.is_suppressed`)
  but are excluded from default counts, scores, aggregates, and comparisons.
- Scoring consumes BOTH the persisted emission-time flag (keystone
  `FindingEmitter`) AND the current active suppression set: flagged findings
  never leak into scores; new suppressions apply immediately even before the
  coordinator re-applies flags.

## Zero-fetch recomputation

`ScoringRecomputeCoordinator.RecomputeForAuditAsync / RecomputeForProjectAsync`:
re-flag persisted findings from the CURRENT project suppression set, recompute
scores, rewrite scoring aggregates. Pure SQL over stored rows — serves both
the re-audit path and the suppression-change path. No URL fetch, no content
artifact required. `FindingSuppressionService` invokes it on every state change
when constructed with a coordinator.

## Metric key ownership

The recorder rewrites ONLY `scoring.*` keys in `metric_summaries`
(`ScoringMetricKeys.All`): findings total / by-severity / by-category,
suppressed total, URLs total / indexable. Sibling metric rows (e.g.
`waf-blocks`, `scoped-word-count`) are never deleted or written.

## Cross-audit comparison

`AuditComparisonReader.CompareAsync(baseAuditId, targetAuditId)` →
`AuditComparisonResult`: new/resolved findings keyed by
**(rule_key, canonical_url)** via `url_records` join (URL surrogate keys differ
across audits; site-wide findings use empty URL identity), metric deltas keyed
by (metric_key, dimensions_json), score deltas per category + overall.
Suppressed findings excluded by default. Fully offline.
