/**
 * Bundled SERP font-metric engine (FS-CRAWL-002 §15.6, criterion #48).
 *
 * Pixel widths come from a bundled Arial/Helvetica advance-width table
 * (units per 1000 em, the classic hmtx normalization) — never from network
 * font loads or canvas measurement of remote fonts. Budgets are configured
 * pixel values with documented desktop/mobile defaults; truncation is
 * word-aware with a glyph-level hard break and an ellipsis that itself
 * consumes width.
 *
 * Pure functions: deterministic, offline, unit-testable.
 *
 * @module tools/serp/fontMetrics
 */

/** Arial advance widths, units per 1000 em (bundled; no remote fonts). */
const ARIAL_WIDTHS_1000: Readonly<Record<string, number>> = {
  ' ': 278, '!': 278, '"': 354, '#': 556, '$': 556, '%': 889, '&': 667, "'": 191,
  '(': 333, ')': 333, '*': 389, '+': 584, ',': 278, '-': 333, '.': 278, '/': 278,
  '0': 556, '1': 556, '2': 556, '3': 556, '4': 556, '5': 556, '6': 556, '7': 556,
  '8': 556, '9': 556, ':': 278, ';': 278, '<': 584, '=': 584, '>': 584, '?': 556,
  '@': 1015, A: 667, B: 667, C: 722, D: 722, E: 667, F: 611, G: 778, H: 722,
  I: 278, J: 500, K: 667, L: 556, M: 833, N: 722, O: 778, P: 667, Q: 778, R: 722,
  S: 667, T: 611, U: 722, V: 667, W: 944, X: 667, Y: 667, Z: 611, '[': 278,
  '\\': 278, ']': 278, '^': 469, '_': 556, '`': 333, a: 556, b: 556, c: 500,
  d: 556, e: 556, f: 278, g: 556, h: 556, i: 222, j: 222, k: 500, l: 222,
  m: 833, n: 556, o: 556, p: 556, q: 556, r: 333, s: 500, t: 278, u: 556,
  v: 500, w: 722, x: 500, y: 500, z: 500, '{': 334, '|': 260, '}': 334, '~': 584,
  '…': 1000, '–': 556, '—': 1000, '‘': 222, '’': 222, '“': 333, '”': 333,
  '•': 350, '·': 278, '©': 737, '®': 737, '™': 1000, '€': 556, '£': 556, '¥': 556
};

/** Fallback advance for glyphs outside the table (average lowercase width). */
const FALLBACK_WIDTH_1000 = 556;

/** The ellipsis appended to truncated text. */
export const ELLIPSIS = '…';

/** One font configuration: family key + rendered size. */
export interface SerpFont {
  /** Bundled family key (currently arial; the table is Helvetica-compatible). */
  readonly family: 'arial';
  /** Rendered size in CSS pixels. */
  readonly sizePx: number;
  /** Optional weight multiplier (semibold approximation for card titles). */
  readonly weightFactor?: number;
}

/** The SERP title font (Google-class desktop rendering). */
export const TITLE_FONT: SerpFont = { family: 'arial', sizePx: 20 };
/** The SERP description font. */
export const DESCRIPTION_FONT: SerpFont = { family: 'arial', sizePx: 14 };
/** The SERP display-URL font. */
export const URL_FONT: SerpFont = { family: 'arial', sizePx: 14 };
/** Social-card title font (semibold approximation). */
export const CARD_TITLE_FONT: SerpFont = { family: 'arial', sizePx: 16, weightFactor: 1.06 };
/** Social-card description font. */
export const CARD_DESCRIPTION_FONT: SerpFont = { family: 'arial', sizePx: 14 };

/** Pixel budgets for one device profile. */
export interface SerpBudget {
  /** Maximum title width before truncation. */
  readonly titleMaxPx: number;
  /** Total description width across all lines. */
  readonly descriptionMaxPx: number;
  /** Number of description lines. */
  readonly descriptionLines: number;
}

/** Documented desktop defaults (Google-class snippet, approximate). */
export const DESKTOP_BUDGET: SerpBudget = { titleMaxPx: 580, descriptionMaxPx: 920, descriptionLines: 2 };
/** Documented mobile defaults (narrower viewport). */
export const MOBILE_BUDGET: SerpBudget = { titleMaxPx: 400, descriptionMaxPx: 600, descriptionLines: 2 };

/** Social platform card budgets (bundled, documented approximations). */
export const SOCIAL_BUDGETS = {
  /** X/Twitter summary card: one title line, two description lines. */
  twitter: { titleMaxPx: 560, descriptionMaxPx: 920, descriptionLines: 2 },
  /** Facebook link card: two title lines, one description line. */
  facebook: { titleMaxPx: 1000, descriptionMaxPx: 500, descriptionLines: 1, titleLines: 2 }
} as const;

/** Advance width of one glyph at the configured font, in pixels. */
export function glyphWidthPx(char: string, font: SerpFont): number {
  const units = ARIAL_WIDTHS_1000[char] ?? FALLBACK_WIDTH_1000;
  return (units / 1000) * font.sizePx * (font.weightFactor ?? 1);
}

/** Pixel width of a string under the bundled metrics. */
export function measureTextPx(text: string, font: SerpFont): number {
  let total = 0;
  for (const char of text) {
    total += glyphWidthPx(char, font);
  }
  return total;
}

/** Result of fitting text into a pixel budget. */
export interface TruncationResult {
  /** The text that fits (ellipsis included when truncated). */
  readonly text: string;
  /** The overflow tail that would be cut (empty when not truncated). */
  readonly overflow: string;
  /** Whether any content was cut. */
  readonly truncated: boolean;
  /** Pixel width of the returned text. */
  readonly widthPx: number;
}

/**
 * Word-aware truncation to a pixel budget. Words are kept whole when
 * possible; a word that cannot fit on its own is hard-broken at glyph
 * level. The ellipsis consumes width like any other glyph.
 */
export function truncateToWidthPx(text: string, maxPx: number, font: SerpFont): TruncationResult {
  const fullWidth = measureTextPx(text, font);
  if (fullWidth <= maxPx) {
    return { text, overflow: '', truncated: false, widthPx: fullWidth };
  }

  const ellipsisWidth = glyphWidthPx(ELLIPSIS, font);
  const target = Math.max(0, maxPx - ellipsisWidth);
  let used = 0;
  let cutIndex = 0;
  const chars = [...text];

  // Walk glyphs; remember the last word boundary that still fits so the
  // cut prefers whole words.
  let lastBoundary = -1;
  let lastBoundaryWidth = 0;
  for (let i = 0; i < chars.length; i += 1) {
    const char = chars[i];
    const width = glyphWidthPx(char, font);
    if (used + width > target) break;
    used += width;
    cutIndex = i + 1;
    if (char === ' ') {
      lastBoundary = i;
      lastBoundaryWidth = used - width;
    }
  }

  // Prefer the word boundary when it keeps at least half the budget used
  // (avoids pathologically short cuts on long words).
  let finalIndex = cutIndex;
  let finalWidth = used;
  if (lastBoundary >= 0 && lastBoundaryWidth >= target * 0.5) {
    finalIndex = lastBoundary;
    finalWidth = lastBoundaryWidth;
  }

  const kept = chars.slice(0, finalIndex).join('').trimEnd();
  const overflow = chars.slice(finalIndex).join('').trimStart();
  return {
    text: kept + ELLIPSIS,
    overflow,
    truncated: true,
    widthPx: finalWidth + ellipsisWidth
  };
}

/** One wrapped description line with its pixel width. */
export interface DescriptionLine {
  readonly text: string;
  readonly widthPx: number;
}

/** Result of wrapping a description into the budgeted line count. */
export interface WrapResult {
  readonly lines: readonly DescriptionLine[];
  readonly truncated: boolean;
  /** The overflow tail beyond the last line (empty when not truncated). */
  readonly overflow: string;
  /** Total pixel width across lines. */
  readonly totalWidthPx: number;
}

/**
 * Wraps text into at most `lines` lines of `lineMaxPx` each; content beyond
 * the last line is truncated with an ellipsis on that line.
 */
export function wrapToLines(text: string, lineMaxPx: number, lines: number, font: SerpFont): WrapResult {
  const words = text.split(/\s+/).filter(Boolean);
  const out: DescriptionLine[] = [];
  let current = '';
  let currentWidth = 0;
  let wordIndex = 0;

  const pushLine = () => {
    out.push({ text: current, widthPx: currentWidth });
    current = '';
    currentWidth = 0;
  };

  while (wordIndex < words.length && out.length < lines) {
    const word = words[wordIndex];
    const spaceWidth = current.length > 0 ? glyphWidthPx(' ', font) : 0;
    const wordWidth = measureTextPx(word, font);

    if (currentWidth + spaceWidth + wordWidth <= lineMaxPx) {
      current = current.length > 0 ? `${current} ${word}` : word;
      currentWidth += spaceWidth + wordWidth;
      wordIndex += 1;
      continue;
    }

    if (current.length > 0) {
      pushLine();
      continue;
    }

    // Single word longer than a line: hard-break it.
    const broken = truncateToWidthPx(word, lineMaxPx, font);
    current = broken.text;
    currentWidth = broken.widthPx;
    words[wordIndex] = broken.overflow;
    if (broken.overflow.length === 0) wordIndex += 1;
    pushLine();
  }

  if (current.length > 0 && out.length < lines) pushLine();

  const remaining = words.slice(wordIndex).join(' ');
  const truncated = remaining.length > 0;
  if (truncated && out.length > 0) {
    const lastIndex = out.length - 1;
    const last = out[lastIndex];
    const refit = truncateToWidthPx(`${last.text} ${remaining}`, lineMaxPx, font);
    out[lastIndex] = { text: refit.text, widthPx: refit.widthPx };
    return {
      lines: out,
      truncated: true,
      overflow: refit.overflow,
      totalWidthPx: out.reduce((sum, line) => sum + line.widthPx, 0)
    };
  }

  return {
    lines: out,
    truncated: false,
    overflow: '',
    totalWidthPx: out.reduce((sum, line) => sum + line.widthPx, 0)
  };
}

/** Complete SERP analysis for one device budget. */
export interface SerpAnalysis {
  readonly title: TruncationResult;
  readonly description: WrapResult;
  /** Per-line description budget (descriptionMaxPx / lines). */
  readonly descriptionLineMaxPx: number;
}

/** Analyzes title + description against a device budget. */
export function analyzeSerp(title: string, description: string, budget: SerpBudget): SerpAnalysis {
  const lineMaxPx = budget.descriptionMaxPx / Math.max(1, budget.descriptionLines);
  return {
    title: truncateToWidthPx(title, budget.titleMaxPx, TITLE_FONT),
    description: wrapToLines(description, lineMaxPx, budget.descriptionLines, DESCRIPTION_FONT),
    descriptionLineMaxPx: lineMaxPx
  };
}