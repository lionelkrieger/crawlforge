/**
 * Resilient-selector capture engine (criterion #42). Wraps @medv/finder
 * (bundled, offline) to produce a unique CSS selector for a clicked
 * element, pairs it with a positional XPath alternative, and previews the
 * match count against the sandbox root — the count the crawl will see.
 *
 * @module tools/picker/selectorEngine
 */

import { finder } from '@medv/finder';
import { buildXPath, countXPathMatches } from './xpath';

/** One captured selector with both syntaxes and its match preview. */
export interface SelectorCapture {
  /** Resilient CSS selector (finder output). */
  readonly css: string;
  /** Positional XPath alternative (structure-encoded). */
  readonly xpath: string;
  /** Number of elements the CSS selector matches under the root. */
  readonly matchCount: number;
  /** Number of nodes the XPath matches under the root. */
  readonly xpathMatchCount: number;
  /** Tag of the captured element (display aid). */
  readonly tag: string;
  /** Short text preview of the captured element. */
  readonly textPreview: string;
}

/** CSS match-count result with syntax-error surfacing. */
export interface CssCount {
  readonly count: number;
  readonly error: string | null;
}

/** Counts CSS matches under root; invalid selectors report the syntax error. */
export function countCssMatches(root: Element, css: string): CssCount {
  try {
    return { count: root.querySelectorAll(css).length, error: null };
  } catch (error) {
    return { count: 0, error: error instanceof Error ? error.message : 'Invalid CSS selector.' };
  }
}

/** Short, single-line text preview of an element (<= 80 chars). */
export function textPreviewOf(element: Element): string {
  const text = (element.textContent ?? '').replace(/\s+/g, ' ').trim();
  return text.length > 80 ? `${text.slice(0, 79)}…` : text;
}

/**
 * Captures a resilient selector for `element` within `root`.
 * Finder is constrained to the sandbox root so selectors never escape it;
 * the timeout keeps pathological documents responsive.
 */
export function captureSelector(element: Element, root: Element): SelectorCapture {
  const css = finder(element, {
    root,
    timeoutMs: 1200,
    seedMinLength: 2,
    optimizedMinLength: 2
  });
  const xpath = buildXPath(element, root);
  return {
    css,
    xpath,
    matchCount: countCssMatches(root, css).count,
    xpathMatchCount: countXPathMatches(root, xpath).count,
    tag: element.tagName.toLowerCase(),
    textPreview: textPreviewOf(element)
  };
}