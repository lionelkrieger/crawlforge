// @vitest-environment jsdom
/**
 * Rendered comparison aggregation proofs (WP-10-03, UI-16; §7.18 / §8.2).
 *
 * Pure aggregation: per-URL grouping, severity sorting (critical noindex
 * first), delta-only rows. Loader: queries the locked issue-explorer query
 * surface (one findings query per render.* key) + the URL list route through
 * the real typed client, and lists both acceptance pages.
 */

import { describe, expect, it } from 'vitest';
import {
  buildRenderedComparisonRows,
  loadRenderedComparison
} from '../../../src/frontend/src/surfaces/explorers/rendered/renderedComparison';
import { RENDER_COMPARISON_RULE_KEYS } from '../../../src/frontend/src/surfaces/explorers/rendered/renderedKeys';
import {
  AUDIT,
  allRenderFindings,
  fixtureUrlRecords,
  renderedClient,
  callsTo,
  serviceWorkerRegistered
} from './helpers';

describe('buildRenderedComparisonRows — grouping + severity sort', () => {
  const rows = buildRenderedComparisonRows(allRenderFindings(), fixtureUrlRecords());

  it('lists every page with a response-vs-rendered delta', () => {
    const ids = rows.map((r) => r.urlId);
    expect(ids).toContain('url-late-meta');
    expect(ids).toContain('url-js-content');
    expect(ids).toContain('url-js-link');
    expect(ids).toContain('url-status');
    expect(ids).toContain('url-structured-data');
    expect(ids).toContain('url-images');
  });

  it('excludes pages with no delta (service-worker-only and clean pages)', () => {
    const ids = rows.map((r) => r.urlId);
    expect(ids).not.toContain('url-service-worker');
    expect(ids).not.toContain('url-clean');
  });

  it('sorts the critical noindex page first, then warning, then notices', () => {
    expect(rows[0].urlId).toBe('url-late-meta');
    expect(rows[0].critical).toBe(true);
    expect(rows[0].severity).toBe('error');
    expect(rows[1].urlId).toBe('url-status');
    expect(rows[1].severity).toBe('warning');
    // remaining rows are notices, ordered by canonical URL
    const noticeRanks = rows.slice(2).map((r) => r.severityRank);
    expect(noticeRanks.every((rank) => rank === 2)).toBe(true);
    const noticeUrls = rows.slice(2).map((r) => r.canonicalUrl);
    expect(noticeUrls).toEqual([...noticeUrls].sort((a, b) => a.localeCompare(b)));
  });

  it('joins capture source and changed-field labels from the URL records', () => {
    const lateMeta = rows.find((r) => r.urlId === 'url-late-meta')!;
    expect(lateMeta.captureSource).toBe('both');
    expect(lateMeta.changedFields).toContain('Title');
    expect(lateMeta.changedFields).toContain('Canonical');
    expect(lateMeta.changedFields).toContain('Noindex');
    const jsContent = rows.find((r) => r.urlId === 'url-js-content')!;
    expect(jsContent.captureSource).toBe('rendered');
    expect(jsContent.changedFields).toContain('Visible text');
  });

  it('a service-worker finding alone never creates a row', () => {
    const onlySw = buildRenderedComparisonRows([serviceWorkerRegistered], fixtureUrlRecords());
    expect(onlySw).toHaveLength(0);
  });
});

describe('loadRenderedComparison — locked query surface', () => {
  it('queries findings by every render.* key and lists both acceptance pages', async () => {
    const { api, calls } = renderedClient();
    const result = await loadRenderedComparison(api, AUDIT);
    expect(result.error).toBeNull();
    const ids = result.rows.map((r) => r.urlId);
    expect(ids).toContain('url-late-meta');
    expect(ids).toContain('url-js-content');

    // One findings query per locked render.* key (ruleKey filter).
    const findingsCalls = callsTo(calls, '/findings');
    for (const key of RENDER_COMPARISON_RULE_KEYS) {
      expect(findingsCalls.some((call) => call.url.includes(`ruleKey=${encodeURIComponent(key)}`))).toBe(true);
    }
    // URL inventory fetched for capture-source / canonical identity.
    expect(callsTo(calls, '/urls?').length).toBeGreaterThan(0);
  });

  it('degrades to an honest empty result offline (null api)', async () => {
    const result = await loadRenderedComparison(null, AUDIT);
    expect(result.rows).toHaveLength(0);
    expect(result.error).toContain('offline');
  });

  it('propagates an envelope error without fabricating rows', async () => {
    const { api } = renderedClient({ failWith: { code: 'internal', message: 'boom' } });
    const result = await loadRenderedComparison(api, AUDIT);
    expect(result.rows).toHaveLength(0);
    expect(result.error).toBe('boom');
  });
});
