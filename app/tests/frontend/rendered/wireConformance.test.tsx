// @vitest-environment jsdom
/**
 * Wire-conformance contract tests (WP-10-03 YAML contract_tests:
 * RenderedTabWireConformanceTests, RenderedComparisonSurfaceTests).
 *
 * These feed the REAL producer output shapes into the REAL consumers and
 * assert the exact wire field names bind end to end:
 *  - the WP-10-01 `RenderedNetworkEntry` waterfall keys (url / resourceType /
 *    method / statusCode / mimeType / transferredBytes / encodedDataLength /
 *    timingJson / fromCache / fromServiceWorker) → waterfall reader + viewer;
 *  - the WP-10-02 comparer `evidence.details` dual-evidence keys (field /
 *    responseValue / renderedValue / critical / direction / rawNoindex /
 *    renderedNoindex / rawWordCount / renderedWordCount) → diff extractor;
 *  - the locked `render.*` rule-key wire tokens + `captureSource` URL wire
 *    field → comparison aggregation + the issue-explorer query surface.
 */

import { describe, expect, it } from 'vitest';
import type { Finding } from '../../../src/frontend/src/contracts/api/audits';
import { FINDING_RULE_KEYS } from '../../../src/frontend/src/contracts/api/vocabulary';
import { parseNetworkWaterfall, totalTransferredBytes } from '../../../src/frontend/src/surfaces/explorers/rendered/renderedArtifacts';
import { extractRenderedDiff } from '../../../src/frontend/src/surfaces/explorers/rendered/renderedDiff';
import { buildRenderedComparisonRows, loadRenderedComparison } from '../../../src/frontend/src/surfaces/explorers/rendered/renderedComparison';
import { NetworkWaterfallViewer } from '../../../src/frontend/src/surfaces/explorers/rendered/NetworkWaterfallViewer';
import { RenderedOutputPanel } from '../../../src/frontend/src/surfaces/explorers/rendered/RenderedOutputPanel';
import { renderTool, byTestId, textOf, fakeServices } from '../tools/helpers';
import {
  AUDIT,
  fixtureWaterfall,
  fixtureUrlRecords,
  allRenderFindings,
  lateMetaNoindex,
  jsContentText,
  renderTestResponse,
  renderedClient,
  callsTo
} from './helpers';

describe('RenderedTabWireConformanceTests', () => {
  it('parses the producer waterfall JSON (exact wire keys) and sums transferredBytes', () => {
    // The persisted artifact is the producer array serialized as JSON.
    const json = JSON.stringify(fixtureWaterfall());
    const entries = parseNetworkWaterfall(json);
    expect(entries).not.toBeNull();
    expect(entries).toHaveLength(3);
    expect(entries![0].url).toBe('http://127.0.0.1:47311/pages/late-meta.html');
    expect(entries![0].resourceType).toBe('document');
    expect(entries![0].method).toBe('GET');
    expect(entries![0].statusCode).toBe(200);
    expect(entries![0].mimeType).toBe('text/html');
    expect(entries![0].transferredBytes).toBe(1480);
    expect(entries![2].fromCache).toBe(true);
    expect(totalTransferredBytes(entries)).toBe(1480 + 5200 + 95);
  });

  it('tolerates the { networkWaterfall | entries } wrapper shapes', () => {
    const wrapped = parseNetworkWaterfall({ networkWaterfall: fixtureWaterfall() });
    expect(wrapped).toHaveLength(3);
    const entriesWrapped = parseNetworkWaterfall({ entries: fixtureWaterfall() });
    expect(entriesWrapped).toHaveLength(3);
    expect(parseNetworkWaterfall(null)).toBeNull();
    expect(parseNetworkWaterfall('not json')).toBeNull();
  });

  it('renders the locked waterfall fields in the viewer (producer → consumer)', async () => {
    const view = await renderTool(<NetworkWaterfallViewer entries={fixtureWaterfall()} />, fakeServices().services);
    expect(byTestId(view.container, 'rendered-waterfall-row-0')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-waterfall-status-0'))).toBe('200');
    expect(textOf(byTestId(view.container, 'rendered-waterfall-bytes-0'))).toBe('1.4 KB');
    expect(textOf(byTestId(view.container, 'rendered-waterfall-total'))).toContain('6.6 KB');
  });

  it('binds RenderTestResponse.networkWaterfall in the output panel (locked tools.ts shape)', async () => {
    const view = await renderTool(<RenderedOutputPanel result={renderTestResponse()} />, fakeServices().services);
    expect(byTestId(view.container, 'rendered-output-waterfall-row-0')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-output-waterfall-count'))).toContain('3 network requests');
  });

  it('projects the comparer dual-evidence keys (responseValue/renderedValue/critical)', () => {
    const diff = extractRenderedDiff([lateMetaNoindex, jsContentText]);
    // noindex dual evidence wire keys
    expect(diff.noindex?.critical).toBe(true);
    expect(diff.noindex?.direction).toBe('rendered-only');
    expect(diff.noindex?.rawNoindex).toBe(false);
    expect(diff.noindex?.renderedNoindex).toBe(true);
    // text word-count wire keys
    expect(diff.wordCount?.rawWordCount).toBe(3);
    expect(diff.wordCount?.renderedWordCount).toBe(203);
  });

  it('reads a finding produced with the locked rule-key constant (no key drift)', () => {
    const finding: Finding = {
      id: 'f-conf',
      ruleKey: FINDING_RULE_KEYS.RenderTitleDelta,
      severity: 'error',
      category: 'indexability',
      urlId: 'url-x',
      message: 'title delta',
      evidence: { details: { field: 'title', responseValue: 'A', renderedValue: 'B' } }
    };
    expect(finding.ruleKey).toBe('render.title-delta');
    const diff = extractRenderedDiff([finding]);
    expect(diff.title?.responseValue).toBe('A');
    expect(diff.title?.renderedValue).toBe('B');
  });
});

describe('RenderedComparisonSurfaceTests', () => {
  it('aggregates real comparer findings + URL captureSource wire field into rows', () => {
    const rows = buildRenderedComparisonRows(allRenderFindings(), fixtureUrlRecords());
    const lateMeta = rows.find((r) => r.urlId === 'url-late-meta')!;
    expect(lateMeta.captureSource).toBe('both');
    expect(lateMeta.critical).toBe(true);
    expect(lateMeta.severity).toBe('error');
    const jsContent = rows.find((r) => r.urlId === 'url-js-content')!;
    expect(jsContent.captureSource).toBe('rendered');
    expect(jsContent.severity).toBe('notice');
  });

  it('orders rows by the locked severity wire tokens (error > warning > notice)', () => {
    const rows = buildRenderedComparisonRows(allRenderFindings(), fixtureUrlRecords());
    expect(rows[0].severity).toBe('error');
    expect(rows[1].severity).toBe('warning');
    expect(rows.slice(2).every((r) => r.severity === 'notice')).toBe(true);
  });

  it('queries the issue-explorer surface with the locked render.* rule-key tokens', async () => {
    const { api, calls } = renderedClient();
    await loadRenderedComparison(api, AUDIT);
    const findingsCalls = callsTo(calls, '/findings');
    const queriedKeys = findingsCalls.map((call) => new URL(call.url).searchParams.get('ruleKey'));
    expect(queriedKeys).toContain('render.title-delta');
    expect(queriedKeys).toContain('render.canonical-delta');
    expect(queriedKeys).toContain('render.noindex-delta');
    expect(queriedKeys).toContain('render.text-delta');
    expect(queriedKeys).toContain('render.service-worker-registered');
  });
});
