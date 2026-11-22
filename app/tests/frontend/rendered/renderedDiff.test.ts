/**
 * Raw-vs-rendered diff reader proofs (WP-10-03, UI-13; §7.18 / CR-009).
 *
 * Feeds the ACTUAL WP-10-02 comparer evidence shapes into the real diff
 * extractor and asserts the dual evidence (response + rendered) for title,
 * canonical, noindex (critical, either direction) and word count.
 */

import { describe, expect, it } from 'vitest';
import {
  extractRenderedDiff,
  fieldDiffFromFinding,
  noindexDiffFromFinding,
  wordCountDiffFromFinding,
  evidenceString,
  evidenceNumber,
  evidenceBoolean
} from '../../../src/frontend/src/surfaces/explorers/rendered/renderedDiff';
import {
  lateMetaTitle,
  lateMetaCanonical,
  lateMetaNoindex,
  jsContentText,
  serviceWorkerRegistered,
  renderFinding,
  allRenderFindings
} from './helpers';

describe('extractRenderedDiff — dual evidence (UI-13)', () => {
  it('reads the late-meta title change with both raw and rendered values', () => {
    const diff = extractRenderedDiff([lateMetaTitle, lateMetaCanonical, lateMetaNoindex]);
    expect(diff.hasDifferences).toBe(true);
    expect(diff.title?.responseValue).toBe('Raw title before script');
    expect(diff.title?.renderedValue).toBe('Rendered title after script');
    expect(diff.title?.severity).toBe('error');
  });

  it('reads the late-meta canonical change with dual evidence', () => {
    const diff = extractRenderedDiff([lateMetaTitle, lateMetaCanonical, lateMetaNoindex]);
    expect(diff.canonical?.responseValue).toBe('http://127.0.0.1:47311/pages/late-meta.html');
    expect(diff.canonical?.renderedValue).toBe('http://127.0.0.1:47311/pages/late-meta-other.html');
  });

  it('reads the critical rendered-only noindex with full dual evidence', () => {
    const diff = extractRenderedDiff([lateMetaNoindex]);
    expect(diff.noindex).not.toBeNull();
    expect(diff.noindex?.critical).toBe(true);
    expect(diff.noindex?.direction).toBe('rendered-only');
    expect(diff.noindex?.rawNoindex).toBe(false);
    expect(diff.noindex?.renderedNoindex).toBe(true);
    expect(diff.noindex?.rawDirectives).toBe('');
    expect(diff.noindex?.renderedDirectives).toBe('noindex');
    expect(diff.criticalCount).toBe(1);
    expect(diff.highestSeverity).toBe('error');
  });

  it('reads a raw-only noindex (the other critical direction)', () => {
    const rawOnly = renderFinding({
      id: 'f-raw-noindex',
      ruleKey: 'render.noindex-delta',
      severity: 'error',
      urlId: 'url-x',
      evidence: {
        details: {
          field: 'noindex',
          critical: true,
          direction: 'raw-only',
          rawNoindex: true,
          renderedNoindex: false,
          rawDirectives: 'noindex',
          renderedDirectives: '',
          responseValue: 'noindex',
          renderedValue: 'index'
        }
      }
    });
    const diff = extractRenderedDiff([rawOnly]);
    expect(diff.noindex?.direction).toBe('raw-only');
    expect(diff.noindex?.rawNoindex).toBe(true);
    expect(diff.noindex?.renderedNoindex).toBe(false);
    expect(diff.noindex?.critical).toBe(true);
  });

  it('reads the js-content word counts (~0 raw vs ~200 rendered)', () => {
    const diff = extractRenderedDiff([jsContentText]);
    expect(diff.wordCount?.rawWordCount).toBe(3);
    expect(diff.wordCount?.renderedWordCount).toBe(203);
    expect(diff.wordCount?.wordCountDelta).toBe(200);
    expect(diff.wordCount?.wordCountChangeFraction).toBeCloseTo(0.9852, 3);
  });

  it('falls back to totalWordCount/words aliases for the rendered count', () => {
    const aliasOnly = renderFinding({
      id: 'f-alias',
      ruleKey: 'render.text-delta',
      urlId: 'url-x',
      evidence: { details: { field: 'text', rawWordCount: 0, totalWordCount: 200, words: 200 } }
    });
    const diff = extractRenderedDiff([aliasOnly]);
    expect(diff.wordCount?.rawWordCount).toBe(0);
    expect(diff.wordCount?.renderedWordCount).toBe(200);
  });

  it('projects every delta field (links included) and excludes service-worker', () => {
    const diff = extractRenderedDiff(allRenderFindings());
    const fields = diff.fields.map((f) => f.field);
    expect(fields).toContain('title');
    expect(fields).toContain('canonical');
    expect(fields).toContain('noindex');
    expect(fields).toContain('text');
    expect(fields).toContain('links');
    expect(fields).toContain('status');
    expect(fields).toContain('structured-data');
    expect(fields).toContain('resources');
    // service-worker is informational, not a response-vs-rendered difference.
    expect(diff.fields.some((f) => f.ruleKey === 'render.service-worker-registered')).toBe(false);
  });

  it('reports no differences for a clean page or non-render findings', () => {
    expect(extractRenderedDiff([]).hasDifferences).toBe(false);
    expect(extractRenderedDiff([serviceWorkerRegistered]).hasDifferences).toBe(false);
    const unrelated = renderFinding({ id: 'f-x', ruleKey: 'content.duplicate-title', urlId: 'url-x' });
    expect(extractRenderedDiff([unrelated]).hasDifferences).toBe(false);
  });
});

describe('per-finding readers', () => {
  it('fieldDiffFromFinding returns null for non-delta keys', () => {
    expect(fieldDiffFromFinding(serviceWorkerRegistered)).toBeNull();
    expect(fieldDiffFromFinding(lateMetaTitle)?.field).toBe('title');
  });

  it('noindexDiffFromFinding only reads the noindex key', () => {
    expect(noindexDiffFromFinding(lateMetaTitle)).toBeNull();
    expect(noindexDiffFromFinding(lateMetaNoindex)?.critical).toBe(true);
  });

  it('wordCountDiffFromFinding only reads the text key', () => {
    expect(wordCountDiffFromFinding(lateMetaTitle)).toBeNull();
    expect(wordCountDiffFromFinding(jsContentText)?.renderedWordCount).toBe(203);
  });
});

describe('evidence value helpers', () => {
  it('evidenceString coerces scalars and nullish to null', () => {
    expect(evidenceString('x')).toBe('x');
    expect(evidenceString(5)).toBe('5');
    expect(evidenceString(true)).toBe('true');
    expect(evidenceString(null)).toBeNull();
    expect(evidenceString(undefined)).toBeNull();
  });

  it('evidenceNumber reads finite numbers and numeric strings', () => {
    expect(evidenceNumber(203)).toBe(203);
    expect(evidenceNumber('203')).toBe(203);
    expect(evidenceNumber('abc')).toBeNull();
    expect(evidenceNumber(undefined)).toBeNull();
  });

  it('evidenceBoolean is true only for literal true', () => {
    expect(evidenceBoolean(true)).toBe(true);
    expect(evidenceBoolean(false)).toBe(false);
    expect(evidenceBoolean('true')).toBe(false);
    expect(evidenceBoolean(undefined)).toBe(false);
  });
});
