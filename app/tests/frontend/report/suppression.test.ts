/**
 * Suppression helper proofs (WP-09-08): the client coverage matcher mirrors
 * the accepted backend keystone (SuppressionMatcher.Covers) dimension-for-
 * dimension; request building, server-query projection, score deltas, and
 * state upserts behave exactly as the REST surface requires.
 */

import { describe, expect, it } from 'vitest';
import type { SuppressionState } from '../../../src/frontend/src/contracts/api/projects';
import type { AuditScores } from '../../../src/frontend/src/contracts/api/audits';
import {
  coversFinding,
  isFindingSuppressed,
  findCoveringSuppression,
  findingSuppression,
  buildSuppressionRequest,
  suppressionServerQuery,
  suppressionLabel,
  computeScoreDelta,
  formatDelta,
  upsertSuppression
} from '../../../src/frontend/src/surfaces/explorers/issues/suppression';
import {
  structuredDataInvalid,
  hreflangNoReturn,
  duplicateTitle,
  suppressionFixture,
  ruleSuppressionFixture,
  scoresBefore,
  scoresAfter
} from './helpers';

/** Builds a suppression state with overrides. */
function suppression(overrides: Partial<SuppressionState> = {}): SuppressionState {
  return { ...suppressionFixture, ...overrides };
}

describe('coversFinding — backend SuppressionMatcher.Covers parity', () => {
  it('covers when rule key and URL scope both match', () => {
    expect(coversFinding(suppressionFixture, 'structured-data.invalid', '7')).toBe(true);
  });

  it('does not cover a different rule key', () => {
    expect(coversFinding(suppressionFixture, 'structured-data.malformed-block', '7')).toBe(false);
  });

  it('does not cover a different URL', () => {
    expect(coversFinding(suppressionFixture, 'structured-data.invalid', '8')).toBe(false);
  });

  it('does not cover a site-wide finding when URL-scoped', () => {
    expect(coversFinding(suppressionFixture, 'structured-data.invalid', null)).toBe(false);
  });

  it('null rule key matches any rule (URL-scoped)', () => {
    const anyRule = suppression({ ruleKey: null });
    expect(coversFinding(anyRule, 'content.duplicate-title', '7')).toBe(true);
    expect(coversFinding(anyRule, 'content.duplicate-title', '8')).toBe(false);
  });

  it('null URL scope matches any URL including site-wide', () => {
    expect(coversFinding(ruleSuppressionFixture, 'content.duplicate-title', '3')).toBe(true);
    expect(coversFinding(ruleSuppressionFixture, 'content.duplicate-title', null)).toBe(true);
    expect(coversFinding(ruleSuppressionFixture, 'other-rule', '3')).toBe(false);
  });

  it('inactive suppressions cover nothing', () => {
    expect(coversFinding(suppression({ suppressed: false }), 'structured-data.invalid', '7')).toBe(false);
  });

  it('dimension-less suppressions cover nothing (blanket guard)', () => {
    const blanket = suppression({ ruleKey: null, scope: null });
    expect(coversFinding(blanket, 'structured-data.invalid', '7')).toBe(false);
  });

  it('finding scope matches only against the rule key (ordinal)', () => {
    const scoped = suppression({ ruleKey: null, scope: { urlPattern: 'content.duplicate-title' } });
    expect(coversFinding(scoped, 'content.duplicate-title', '3')).toBe(true);
    expect(coversFinding(scoped, 'content.duplicate-title-x', '3')).toBe(false);
    expect(coversFinding(scoped, 'Content.Duplicate-Title', '3')).toBe(false);
  });

  it('matching is ordinal (case-sensitive rule keys)', () => {
    expect(coversFinding(suppressionFixture, 'Structured-Data.Invalid', '7')).toBe(false);
  });

  it('all declared dimensions must match together', () => {
    const both = suppression({ scope: { urlId: '7', urlPattern: 'structured-data.invalid' } });
    expect(coversFinding(both, 'structured-data.invalid', '7')).toBe(true);
    expect(coversFinding(both, 'structured-data.invalid', '9')).toBe(false);
    expect(coversFinding(both, 'other', '7')).toBe(false);
  });
});

describe('set-level helpers', () => {
  it('isFindingSuppressed is true when any active suppression covers', () => {
    const set = [suppression({ suppressed: false }), ruleSuppressionFixture];
    expect(isFindingSuppressed('content.duplicate-title', '3', set)).toBe(true);
    expect(isFindingSuppressed('structured-data.invalid', '7', set)).toBe(false);
  });

  it('findCoveringSuppression returns the first active cover', () => {
    const set = [suppression({ suppressed: false }), suppressionFixture];
    expect(findCoveringSuppression('structured-data.invalid', '7', set)?.key).toBe(suppressionFixture.key);
    expect(findCoveringSuppression('no-such-rule', null, set)).toBeNull();
  });

  it('findingSuppression projects a locked finding row', () => {
    expect(findingSuppression(structuredDataInvalid, [suppressionFixture])?.key).toBe(suppressionFixture.key);
    expect(findingSuppression(hreflangNoReturn, [suppressionFixture])).toBeNull();
  });
});

describe('buildSuppressionRequest', () => {
  it('scopes to the affected URL by default', () => {
    expect(buildSuppressionRequest(structuredDataInvalid)).toEqual({
      ruleKey: 'structured-data.invalid',
      scope: { urlId: '7' }
    });
  });

  it('rule scope omits the URL dimension', () => {
    expect(buildSuppressionRequest(structuredDataInvalid, 'rule')).toEqual({
      ruleKey: 'structured-data.invalid'
    });
  });

  it('site-wide findings suppress the rule alone', () => {
    const siteWide = { ...duplicateTitle, urlId: null };
    expect(buildSuppressionRequest(siteWide)).toEqual({ ruleKey: 'content.duplicate-title' });
  });
});

describe('suppressionServerQuery — server-expressible suppressed view', () => {
  it('maps rule + URL dimensions onto the locked findings filters', () => {
    expect(suppressionServerQuery(suppressionFixture)).toEqual({
      ruleKey: 'structured-data.invalid',
      urlId: '7'
    });
  });

  it('rule-only suppression maps to a ruleKey filter', () => {
    expect(suppressionServerQuery(ruleSuppressionFixture)).toEqual({ ruleKey: 'content.duplicate-title' });
  });

  it('pattern-only suppression maps to ruleKey (scope matches the rule key)', () => {
    const patternOnly = suppression({ ruleKey: null, scope: { urlPattern: 'content.duplicate-title' } });
    expect(suppressionServerQuery(patternOnly)).toEqual({ ruleKey: 'content.duplicate-title' });
  });

  it('URL-only suppression maps to a urlId filter', () => {
    const urlOnly = suppression({ ruleKey: null, scope: { urlId: '7' } });
    expect(suppressionServerQuery(urlOnly)).toEqual({ urlId: '7' });
  });
});

describe('suppressionLabel', () => {
  it('labels rule + URL, rule-only, and pattern scopes', () => {
    expect(suppressionLabel(suppressionFixture)).toBe('structured-data.invalid · URL 7');
    expect(suppressionLabel(ruleSuppressionFixture)).toBe('content.duplicate-title · all URLs');
    const pattern = suppression({ ruleKey: null, scope: { urlPattern: 'x' } });
    expect(suppressionLabel(pattern)).toBe('any rule · scope x');
  });
});

describe('computeScoreDelta', () => {
  it('computes overall and per-category movement (after − before)', () => {
    const delta = computeScoreDelta(scoresBefore, scoresAfter);
    expect(delta).not.toBeNull();
    expect(delta!.overallBefore).toBe(72);
    expect(delta!.overallAfter).toBe(80);
    expect(delta!.overallDelta).toBe(8);
    const sd = delta!.categories.find((entry) => entry.category === 'structureddata');
    expect(sd).toEqual({ category: 'structureddata', before: 60, after: 75, delta: 15 });
    const content = delta!.categories.find((entry) => entry.category === 'content');
    expect(content!.delta).toBe(0);
  });

  it('unions categories and reads a missing side as 0', () => {
    const before: AuditScores = { overall: 50, categories: [{ category: 'content', score: 50, findingCount: 1 }] };
    const after: AuditScores = { overall: 60, categories: [{ category: 'linking', score: 60, findingCount: 1 }] };
    const delta = computeScoreDelta(before, after)!;
    expect(delta.categories.map((entry) => entry.category)).toEqual(['content', 'linking']);
    expect(delta.categories[0]).toEqual({ category: 'content', before: 50, after: 0, delta: -50 });
    expect(delta.categories[1]).toEqual({ category: 'linking', before: 0, after: 60, delta: 60 });
  });

  it('rounds to two decimals', () => {
    const before: AuditScores = { overall: 70.125, categories: [] };
    const after: AuditScores = { overall: 70.137, categories: [] };
    expect(computeScoreDelta(before, after)!.overallDelta).toBe(0.01);
  });

  it('returns null when either snapshot is unavailable', () => {
    expect(computeScoreDelta(null, scoresAfter)).toBeNull();
    expect(computeScoreDelta(scoresBefore, null)).toBeNull();
  });
});

describe('formatDelta + upsertSuppression', () => {
  it('formats signed deltas', () => {
    expect(formatDelta(8)).toBe('+8');
    expect(formatDelta(-3.5)).toBe('-3.5');
    expect(formatDelta(0)).toBe('0');
  });

  it('upserts by stable key and removes inactive rows', () => {
    const added = upsertSuppression([], suppressionFixture);
    expect(added).toHaveLength(1);
    const replaced = upsertSuppression(added, { ...suppressionFixture, createdAt: 'later' });
    expect(replaced).toHaveLength(1);
    expect(replaced[0].createdAt).toBe('later');
    const removed = upsertSuppression(replaced, { ...suppressionFixture, suppressed: false });
    expect(removed).toHaveLength(0);
  });
});
