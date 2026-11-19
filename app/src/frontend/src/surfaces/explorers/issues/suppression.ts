/**
 * Suppression + scoring helpers for the Issue Explorer (WP-09-08,
 * FS-CRAWL-001 §7.5 / §8.1).
 *
 * The coverage matcher mirrors the accepted backend keystone EXACTLY
 * (`CrawlForge.Audit.Scoring.SuppressionMatcher.Covers`, aligned with
 * `FindingEmitter.IsSuppressed`): a suppression covers a finding when every
 * declared dimension matches — rule key (null = any rule), URL scope
 * (null = any URL), and free-form finding scope (matched against the rule
 * key). Inactive rows and rows with no declared dimension cover nothing.
 * Matching is ordinal. Keeping this parity client-side lets the explorer
 * badge rows and drive the reversible suppressed view without a wire flag
 * (the findings list currently returns suppressed rows unflagged — see the
 * mounting handoff for the missing backend affordances).
 *
 * Pure module: no DOM, no API calls — exhaustively unit-tested in node.
 *
 * @module surfaces/explorers/issues/suppression
 */

import type { Finding, AuditScores, CategoryScore } from '../../../contracts/api/audits';
import type { SuppressionState, SuppressionCreateRequest } from '../../../contracts/api/projects';

/** The suppression status filter values exposed by the explorer. */
export type IssueStatusFilter = 'all' | 'suppressed';

/**
 * Tests whether one suppression covers a finding. Mirrors
 * `SuppressionMatcher.Covers` dimension-for-dimension (ordinal equality).
 */
export function coversFinding(
  suppression: SuppressionState,
  ruleKey: string,
  urlId: string | null
): boolean {
  if (!suppression.suppressed) {
    return false;
  }

  const scopeUrlId = suppression.scope?.urlId ?? null;
  const scopePattern = suppression.scope?.urlPattern ?? null;
  const hasDimension =
    suppression.ruleKey !== null || scopeUrlId !== null || scopePattern !== null;
  if (!hasDimension) {
    // Accidental blanket-suppression guard (backend parity).
    return false;
  }

  const ruleMatches = suppression.ruleKey === null || suppression.ruleKey === ruleKey;
  const urlMatches = scopeUrlId === null || scopeUrlId === urlId;
  const scopeMatches = scopePattern === null || scopePattern === ruleKey;
  return ruleMatches && urlMatches && scopeMatches;
}

/** True when any active suppression covers the finding (set-level parity). */
export function isFindingSuppressed(
  ruleKey: string,
  urlId: string | null,
  suppressions: readonly SuppressionState[]
): boolean {
  return suppressions.some((suppression) => coversFinding(suppression, ruleKey, urlId));
}

/** The first active suppression covering a finding (the unsuppress target). */
export function findCoveringSuppression(
  ruleKey: string,
  urlId: string | null,
  suppressions: readonly SuppressionState[]
): SuppressionState | null {
  return suppressions.find((suppression) => coversFinding(suppression, ruleKey, urlId)) ?? null;
}

/** Convenience overload over a locked finding row. */
export function findingSuppression(
  finding: Finding,
  suppressions: readonly SuppressionState[]
): SuppressionState | null {
  return findCoveringSuppression(finding.ruleKey, finding.urlId, suppressions);
}

/**
 * Builds the narrowest suppression request for one finding: the rule key
 * scoped to the affected URL when the finding carries one, otherwise the
 * rule key alone. `scope: 'rule'` suppresses the rule across every URL.
 */
export function buildSuppressionRequest(
  finding: Finding,
  scope: 'finding' | 'rule' = 'finding'
): SuppressionCreateRequest {
  if (scope === 'rule' || finding.urlId === null) {
    return { ruleKey: finding.ruleKey };
  }
  return { ruleKey: finding.ruleKey, scope: { urlId: finding.urlId } };
}

/**
 * Server-expressible domain filters for the suppressed view. The findings
 * route filters by ruleKey/urlId; a suppression's dimensions map onto those
 * filters so the suppressed view stays SERVER-windowed (honest totals).
 * A pattern-only suppression covers findings whose rule key equals the
 * pattern (backend scope semantics), so it maps to a ruleKey filter.
 */
export function suppressionServerQuery(
  suppression: SuppressionState
): Record<string, unknown> {
  const query: Record<string, unknown> = {};
  const scopeUrlId = suppression.scope?.urlId ?? null;
  const scopePattern = suppression.scope?.urlPattern ?? null;
  if (suppression.ruleKey !== null) {
    query.ruleKey = suppression.ruleKey;
  } else if (scopePattern !== null && scopeUrlId === null) {
    query.ruleKey = scopePattern;
  }
  if (scopeUrlId !== null) {
    query.urlId = scopeUrlId;
  }
  return query;
}

/** Human-readable label for a suppression (picker + badges). */
export function suppressionLabel(suppression: SuppressionState): string {
  const rule = suppression.ruleKey ?? 'any rule';
  const scopeUrlId = suppression.scope?.urlId ?? null;
  const scopePattern = suppression.scope?.urlPattern ?? null;
  if (scopeUrlId !== null) {
    return `${rule} · URL ${scopeUrlId}`;
  }
  if (scopePattern !== null) {
    return `${rule} · scope ${scopePattern}`;
  }
  return `${rule} · all URLs`;
}

/** One category score delta between two score snapshots. */
export interface CategoryScoreDelta {
  readonly category: string;
  readonly before: number;
  readonly after: number;
  readonly delta: number;
}

/** Overall + per-category score deltas (after − before). */
export interface ScoreDelta {
  readonly overallBefore: number;
  readonly overallAfter: number;
  readonly overallDelta: number;
  readonly categories: readonly CategoryScoreDelta[];
}

/**
 * Computes the score movement between two `audits.scores` snapshots. The
 * backend recomputes synchronously on every suppression state change
 * (`ScoringRecomputeCoordinator.RecomputeForProjectAsync`), so the snapshot
 * fetched after the REST call already reflects the change. Categories are
 * unioned by key; a missing side reads as 0.
 */
export function computeScoreDelta(
  before: AuditScores | null,
  after: AuditScores | null
): ScoreDelta | null {
  if (!before || !after) {
    return null;
  }
  const beforeByCategory = new Map<string, CategoryScore>(
    before.categories.map((entry) => [entry.category, entry])
  );
  const afterByCategory = new Map<string, CategoryScore>(
    after.categories.map((entry) => [entry.category, entry])
  );
  const keys = [...new Set([...beforeByCategory.keys(), ...afterByCategory.keys()])].sort();
  const categories: CategoryScoreDelta[] = keys.map((category) => {
    const b = beforeByCategory.get(category)?.score ?? 0;
    const a = afterByCategory.get(category)?.score ?? 0;
    return { category, before: b, after: a, delta: round2(a - b) };
  });
  return {
    overallBefore: before.overall,
    overallAfter: after.overall,
    overallDelta: round2(after.overall - before.overall),
    categories
  };
}

/** Rounds to two decimals (score wire precision). */
function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

/** Formats a signed score delta for display (+8 / −3.5 / 0). */
export function formatDelta(delta: number): string {
  if (delta > 0) {
    return `+${delta}`;
  }
  return `${delta}`;
}

/**
 * Upserts a suppression state into a list by its stable key (create returns
 * the active row; delete returns the inactive row, which is removed).
 */
export function upsertSuppression(
  suppressions: readonly SuppressionState[],
  next: SuppressionState
): readonly SuppressionState[] {
  const without = suppressions.filter((entry) => entry.key !== next.key);
  return next.suppressed ? [...without, next] : without;
}
