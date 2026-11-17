/**
 * Bundled SERP font-metric engine tests (criterion #48): deterministic
 * measurement, word-aware truncation with a width-consuming ellipsis,
 * line wrapping, and budget analysis. Pure — node environment.
 */

import { describe, expect, it } from 'vitest';
import {
  CARD_TITLE_FONT,
  DESKTOP_BUDGET,
  ELLIPSIS,
  MOBILE_BUDGET,
  SOCIAL_BUDGETS,
  TITLE_FONT,
  analyzeSerp,
  glyphWidthPx,
  measureTextPx,
  truncateToWidthPx,
  wrapToLines
} from '../../../src/frontend/src/tools/serp/fontMetrics';

describe('glyphWidthPx / measureTextPx', () => {
  it('uses the bundled Arial advance table (units per 1000 em)', () => {
    expect(glyphWidthPx('i', TITLE_FONT)).toBeCloseTo(4.44, 5); // 222/1000 * 20
    expect(glyphWidthPx('W', TITLE_FONT)).toBeCloseTo(18.88, 5); // 944/1000 * 20
    expect(glyphWidthPx(' ', TITLE_FONT)).toBeCloseTo(5.56, 5); // 278/1000 * 20
  });

  it('applies the semibold weight factor for card titles', () => {
    expect(glyphWidthPx('a', CARD_TITLE_FONT)).toBeCloseTo((556 / 1000) * 16 * 1.06, 5);
  });

  it('falls back for glyphs outside the table', () => {
    expect(glyphWidthPx('☃', TITLE_FONT)).toBeCloseTo((556 / 1000) * 20, 5);
  });

  it('sums advances and is deterministic', () => {
    expect(measureTextPx('ii', TITLE_FONT)).toBeCloseTo(8.88, 5);
    expect(measureTextPx('', TITLE_FONT)).toBe(0);
    const a = measureTextPx('CrawlForge — local-first', TITLE_FONT);
    const b = measureTextPx('CrawlForge — local-first', TITLE_FONT);
    expect(a).toBe(b);
  });
});

describe('truncateToWidthPx', () => {
  const font = { family: 'arial', sizePx: 10 } as const; // a=5.56, space=2.78, …=10

  it('returns short text unchanged', () => {
    const result = truncateToWidthPx('Hi', 580, TITLE_FONT);
    expect(result).toEqual({ text: 'Hi', overflow: '', truncated: false, widthPx: measureTextPx('Hi', TITLE_FONT) });
  });

  it('prefers a whole-word boundary and keeps the ellipsis inside budget', () => {
    // Budget fits "aaaa" + space + ellipsis exactly; the cut lands on the word boundary.
    const maxPx = 5.56 * 4 + 2.78 + 10;
    const result = truncateToWidthPx('aaaa bbbb cccc', maxPx, font);
    expect(result.truncated).toBe(true);
    expect(result.text).toBe(`aaaa${ELLIPSIS}`);
    expect(result.overflow).toBe('bbbb cccc');
    expect(result.widthPx).toBeLessThanOrEqual(maxPx + 1e-9);
  });

  it('hard-breaks a single overlong word at glyph level', () => {
    const result = truncateToWidthPx('aaaaaaaaaa', 30, font);
    expect(result.truncated).toBe(true);
    expect(result.text).toBe(`aaa${ELLIPSIS}`);
    expect(result.overflow).toBe('aaaaaaa');
    expect(result.widthPx).toBeLessThanOrEqual(30 + 1e-9);
  });

  it('never exceeds the budget and always marks the cut with an ellipsis', () => {
    const long = 'The quick brown fox jumps over the lazy dog. '.repeat(4);
    for (const budget of [120, 200, 320, 580]) {
      const result = truncateToWidthPx(long, budget, TITLE_FONT);
      expect(result.truncated).toBe(true);
      expect(result.text.endsWith(ELLIPSIS)).toBe(true);
      expect(result.widthPx).toBeLessThanOrEqual(budget + 1e-9);
      expect(result.overflow.length).toBeGreaterThan(0);
    }
  });
});

describe('wrapToLines', () => {
  it('wraps into the budgeted line count and truncates the tail', () => {
    const text = 'one two three four five six seven eight nine ten';
    const result = wrapToLines(text, 120, 2, { family: 'arial', sizePx: 14 });
    expect(result.lines.length).toBeLessThanOrEqual(2);
    expect(result.truncated).toBe(true);
    expect(result.overflow.length).toBeGreaterThan(0);
    expect(result.lines[result.lines.length - 1].text.endsWith(ELLIPSIS)).toBe(true);
    for (const line of result.lines) {
      expect(line.widthPx).toBeLessThanOrEqual(120 + 1e-9);
    }
  });

  it('keeps short text whole without truncation', () => {
    const result = wrapToLines('Short description.', 920, 2, { family: 'arial', sizePx: 14 });
    expect(result.truncated).toBe(false);
    expect(result.overflow).toBe('');
    expect(result.lines.length).toBe(1);
    expect(result.lines[0].text).toBe('Short description.');
  });

  it('reports total width across lines', () => {
    const result = wrapToLines('alpha beta gamma delta', 100, 2, { family: 'arial', sizePx: 14 });
    const sum = result.lines.reduce((acc, line) => acc + line.widthPx, 0);
    expect(result.totalWidthPx).toBeCloseTo(sum, 5);
  });
});

describe('analyzeSerp + budgets', () => {
  it('splits the description budget across lines', () => {
    const analysis = analyzeSerp('Title', 'A description.', DESKTOP_BUDGET);
    expect(analysis.descriptionLineMaxPx).toBe(460);
    expect(analysis.title.truncated).toBe(false);
    expect(analysis.description.truncated).toBe(false);
  });

  it('truncates a long title against the desktop budget', () => {
    const analysis = analyzeSerp('X'.repeat(120), 'desc', DESKTOP_BUDGET);
    expect(analysis.title.truncated).toBe(true);
    expect(analysis.title.widthPx).toBeLessThanOrEqual(DESKTOP_BUDGET.titleMaxPx + 1e-9);
  });

  it('mobile budgets are narrower than desktop', () => {
    expect(MOBILE_BUDGET.titleMaxPx).toBeLessThan(DESKTOP_BUDGET.titleMaxPx);
    expect(MOBILE_BUDGET.descriptionMaxPx).toBeLessThan(DESKTOP_BUDGET.descriptionMaxPx);
  });

  it('social budgets carry platform-specific line counts', () => {
    expect(SOCIAL_BUDGETS.twitter.descriptionLines).toBe(2);
    expect(SOCIAL_BUDGETS.facebook.descriptionLines).toBe(1);
    expect(SOCIAL_BUDGETS.facebook.titleLines).toBe(2);
  });
});